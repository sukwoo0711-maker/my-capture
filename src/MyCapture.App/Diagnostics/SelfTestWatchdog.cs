using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MyCapture.App.Diagnostics;

/// <summary>Where an in-process self-test stopped making progress.</summary>
internal sealed record SelfTestStall(string Phase, int Step, TimeSpan Elapsed, TimeSpan Budget);

/// <summary>
/// Bounds each phase of an in-process self-test from a background thread.
/// </summary>
/// <remarks>
/// A self-test drives the real UI on its dispatcher thread, so a wait that never ends there (a
/// modal loop, a blocked native call, a dispatcher frame whose exit never runs) cannot time itself
/// out. The packaged runner would only notice at its whole-test limit, with no record of where the
/// process stopped. The watchdog calls its stall handler once, on its own thread, as soon as the
/// current phase outlives its budget.
/// </remarks>
internal sealed class SelfTestWatchdog : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Action<SelfTestStall> _stalled;
    private readonly TimeSpan _poll;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private string _phase;
    private TimeSpan _budget;
    private long _started;
    private int _step;
    private bool _fired;

    /// <param name="phase">The first phase; it starts now.</param>
    /// <param name="budget">How long the first phase may take.</param>
    /// <param name="stalled">Runs once on the watchdog thread when a phase outlives its budget.</param>
    /// <param name="poll">How often the budget is checked; tests shorten it.</param>
    /// <param name="clock">A <see cref="Stopwatch"/> timestamp source; tests replace it.</param>
    internal SelfTestWatchdog(
        string phase,
        TimeSpan budget,
        Action<SelfTestStall> stalled,
        TimeSpan? poll = null,
        Func<long>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(stalled);
        _clock = clock ?? Stopwatch.GetTimestamp;
        _stalled = stalled;
        _poll = poll ?? TimeSpan.FromMilliseconds(250);
        _phase = phase;
        _budget = budget;
        _started = _clock();
        _thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "MyCapture self-test watchdog",
        };
        _thread.Start();
    }

    internal string Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>True once the stall handler has been called.</summary>
    internal bool HasStalled
    {
        get
        {
            lock (_gate)
            {
                return _fired;
            }
        }
    }

    /// <summary>Starts <paramref name="phase"/>, which must end within <paramref name="budget"/>.</summary>
    internal void Enter(string phase, TimeSpan budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        lock (_gate)
        {
            _phase = phase;
            _budget = budget;
            _started = _clock();
            _step = 0;
        }
    }

    internal int CurrentStep => Volatile.Read(ref _step);

    /// <summary>Records progress inside a phase (a sample index) without allocating.</summary>
    internal void Step(int step) => Volatile.Write(ref _step, step);

    private void Watch()
    {
        while (!_stop.Wait(_poll))
        {
            SelfTestStall? stall = null;
            lock (_gate)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(_started, _clock());
                if (!_fired && elapsed > _budget)
                {
                    _fired = true;
                    stall = new SelfTestStall(_phase, Volatile.Read(ref _step), elapsed, _budget);
                }
            }

            if (stall is not null)
            {
                _stalled(stall);
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _stop.Dispose();
    }
}

/// <summary>
/// What a stalled self-test records about its own process before it ends itself: where each
/// thread is (busy or waiting), which windows are open, and a dump for the full stacks.
/// </summary>
internal static class SelfTestStallEvidence
{
    /// <summary>Exit code of a self-test that ended itself after a stall.</summary>
    internal const int StalledExitCode = 3;

    /// <summary>
    /// Describes the process's threads over a short interval. A thread whose CPU time grows is
    /// spinning (a dispatcher that never reaches a low priority, a busy loop); one whose time
    /// stays flat is blocked in a wait.
    /// </summary>
    internal static string DescribeThreads(int uiThreadId, TimeSpan interval)
    {
        var text = new StringBuilder();
        try
        {
            Dictionary<int, TimeSpan> before = ReadThreadTimes();
            ulong? cyclesBefore = ReadThreadCycles(uiThreadId);
            Thread.Sleep(interval);
            ulong? cyclesAfter = ReadThreadCycles(uiThreadId);
            // Thread times advance only on clock ticks; cycles also count a thread that wakes
            // briefly many times a second, as a dispatcher re-arming a 1 ms timer does.
            text.AppendLine(cyclesBefore is { } first && cyclesAfter is { } last
                ? string.Create(CultureInfo.InvariantCulture, $"UI thread cycles: +{last - first} over {interval.TotalMilliseconds:0}ms")
                : "UI thread cycles: unavailable");
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Threads: {process.Threads.Count}, sampled over {interval.TotalMilliseconds:0}ms (UI thread {uiThreadId})"));
            foreach (ProcessThread thread in process.Threads)
            {
                TimeSpan now = SafeTotal(thread);
                TimeSpan delta = before.TryGetValue(thread.Id, out TimeSpan earlier) ? now - earlier : TimeSpan.Zero;
                bool busy = delta > TimeSpan.Zero;
                if (thread.Id != uiThreadId && !busy)
                {
                    continue;
                }

                string wait = SafeState(thread);
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  thread {thread.Id}{(thread.Id == uiThreadId ? " (UI)" : string.Empty)}: {wait}, " +
                    $"cpu +{delta.TotalMilliseconds:0}ms, total {now.TotalMilliseconds:0}ms"));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            text.AppendLine("Threads: unavailable (" + ex.Message + ")");
        }

        return text.ToString();
    }

    /// <summary>Lists this process's top-level windows; a disabled owner marks a modal loop.</summary>
    internal static string DescribeWindows()
    {
        var text = new StringBuilder();
        uint processId = (uint)Environment.ProcessId;
        IntPtr foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        text.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Foreground: 0x{foreground.ToInt64():X} pid {foregroundProcess} {Describe(foreground)}"));
        int count = 0;
        _ = EnumWindows((hwnd, lParam) =>
        {
            uint thread = GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == processId)
            {
                count++;
                IntPtr ownerWindow = GetWindow(hwnd, GwOwner);
                uint ownerThread = ownerWindow == IntPtr.Zero ? 0 : GetWindowThreadProcessId(ownerWindow, out _);
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  window 0x{hwnd.ToInt64():X} thread {thread}: {Describe(hwnd)}, visible={IsWindowVisible(hwnd)}, " +
                    $"enabled={IsWindowEnabled(hwnd)}, owner=0x{ownerWindow.ToInt64():X} (thread {ownerThread})"));
                // A child window created by another thread shares that thread's input queue.
                _ = EnumChildWindows(hwnd, (child, childParam) =>
                {
                    uint childThread = GetWindowThreadProcessId(child, out _);
                    text.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    child 0x{child.ToInt64():X} thread {childThread}: {Describe(child)}"));
                    return true;
                }, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Process windows: {count}"));
        return text.ToString();
    }

    /// <summary>
    /// The UI thread's GUI state as Windows sees it: active, focus and capture windows, and
    /// whether it is in a menu, move or size loop, each of which runs its own message loop.
    /// </summary>
    internal static string DescribeGuiThread(int threadId)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo((uint)threadId, ref info))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"GUI thread {threadId}: unavailable (Win32 {Marshal.GetLastWin32Error()})");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"GUI thread {threadId}: flags=0x{info.Flags:X} (1 caret, 2 in move/size, 4 menu mode, 8 system menu, 0x10 popup menu), " +
            $"active=0x{info.Active.ToInt64():X}, focus=0x{info.Focus.ToInt64():X}, capture=0x{info.Capture.ToInt64():X}, " +
            $"menu-owner=0x{info.MenuOwner.ToInt64():X}, move-size=0x{info.MoveSize.ToInt64():X}");
    }

    /// <summary>
    /// Reads the WPF dispatcher's private scheduling state by reflection: whether it believes a
    /// processing message or background timer is outstanding, the highest queued priority and the
    /// first queued operations, its DispatcherTimers and Win32 timer, and any recorded failure to
    /// post. A dispatcher that has work queued but no outstanding wake-up waits for a message that
    /// never comes; this tells that apart from one starved by pending input.
    /// </summary>
    internal static string DescribeDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        var text = new StringBuilder();
        try
        {
            Type type = typeof(System.Windows.Threading.Dispatcher);
            object? Field(object target, string name) =>
                target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.GetValue(target);
            object? Static(string name) =>
                type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            object? Property(object target, string name) =>
                target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    ?.GetValue(target);

            int tick = Environment.TickCount;
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Dispatcher: posted-processing={Field(dispatcher, "_postedProcessingType")} (0 none, 1 background timer, 2 message), " +
                $"frame-depth={Field(dispatcher, "_frameDepth")}, disable-processing={Field(dispatcher, "_disableProcessingCount")}, " +
                $"request-failed={Field(dispatcher, "_hasRequestProcessingFailed")}, tsf-pump={Field(dispatcher, "_isTSFMessagePumpEnabled")}, " +
                $"shutdown={dispatcher.HasShutdownStarted}"));
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Dispatcher timers: count={(Field(dispatcher, "_timers") as System.Collections.ICollection)?.Count}, " +
                $"due-found={Field(dispatcher, "_dueTimeFound")}, due-in={(Field(dispatcher, "_dueTimeInTicks") is int due ? due - tick : 0)}ms, " +
                $"win32-timer-set={Field(dispatcher, "_isWin32TimerSet")}"));
            if (Field(dispatcher, "_reservedPtsCache") is Tuple<object, List<string>> failures)
            {
                foreach (string failure in failures.Item2.Take(20))
                {
                    text.AppendLine("  request failure: " + failure);
                }
            }

            if (Field(dispatcher, "_queue") is { } queue)
            {
                text.AppendLine("Dispatcher queue: max-priority=" + Property(queue, "MaxPriority"));
                object? item = Field(queue, "_head");
                for (int i = 0; item is not null && i < 30; i++)
                {
                    object? operation = Property(item, "Data");
                    if (operation is System.Windows.Threading.DispatcherOperation op)
                    {
                        text.AppendLine($"  queued: {op.Priority} {Property(op, "Name")}");
                    }

                    item = Property(item, "SequentialNext");
                }
            }

            if (Field(dispatcher, "_window") is { } window && Property(window, "Handle") is IntPtr handle
                && Static("_msgProcessQueue") is { } processMessage)
            {
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Dispatcher window: 0x{handle.ToInt64():X}, process-queue message 0x{Convert.ToInt32(processMessage, CultureInfo.InvariantCulture):X}"));
            }
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException
            or ArgumentException or NullReferenceException or InvalidCastException)
        {
            text.AppendLine("Dispatcher: state unavailable (" + ex.GetType().Name + ": " + ex.Message + ")");
        }

        return text.ToString();
    }

    /// <summary>
    /// Posts the dispatcher's own process-queue message once, as WPF does when it requests
    /// processing, so the report can say whether a lost wake-up was all that held the queue.
    /// Diagnostic only: the run has already failed when this is called.
    /// </summary>
    internal static bool PostDispatcherProcessMessage(System.Windows.Threading.Dispatcher dispatcher)
    {
        try
        {
            Type type = typeof(System.Windows.Threading.Dispatcher);
            object? window = type.GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dispatcher);
            object? handle = window?.GetType().GetProperty("Handle")?.GetValue(window);
            object? message = type.GetField("_msgProcessQueue", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            return handle is IntPtr hwnd && message is not null
                && PostMessage(hwnd, (uint)Convert.ToInt32(message, CultureInfo.InvariantCulture), IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException
            or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks this process's runtime, through its diagnostics port (as <c>dotnet-dump collect</c>
    /// does), to write a dump with heap. The runtime writes it from a separate process, so a
    /// stalled thread holding the loader lock cannot stop it, and the heap keeps the runtime data
    /// that managed stacks are read from. Returns what happened, for the report.
    /// </summary>
    internal static string WriteDump(string path, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                "dotnet-diagnostic-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                PipeDirection.InOut);
            pipe.Connect((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            pipe.Write(DumpRequest(path));
            pipe.Flush();

            using var cancel = new CancellationTokenSource(timeout);
            byte[] header = new byte[DiagnosticsHeaderSize];
            pipe.ReadExactlyAsync(header, cancel.Token).AsTask().GetAwaiter().GetResult();
            byte[] payload = new byte[Math.Max(0, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)) - DiagnosticsHeaderSize)];
            pipe.ReadExactlyAsync(payload, cancel.Token).AsTask().GetAwaiter().GetResult();
            uint status = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Dump: {path} ({bytes} bytes, {(header[17] == 0 ? "ok" : "error")} 0x{status:X8}, {clock.Elapsed.TotalSeconds:0.0}s)");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or UnauthorizedAccessException or EndOfStreamException)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Dump: not written after {clock.Elapsed.TotalSeconds:0.0}s; {ex.GetType().Name}: {ex.Message}");
        }
    }

    private const int DiagnosticsHeaderSize = 20;

    /// <summary>
    /// A diagnostics IPC "generate core dump" request: the 20-byte header (magic, total size,
    /// command set Dump = 1, command GenerateCoreDump = 1, reserved), then the dump path as a
    /// length-prefixed null-terminated UTF-16 string, the dump type (2 = with heap) and the
    /// diagnostics-logging flag.
    /// </summary>
    internal static byte[] DumpRequest(string path)
    {
        ReadOnlySpan<byte> magic = "DOTNET_IPC_V1\0"u8;
        int size = DiagnosticsHeaderSize + 4 + ((path.Length + 1) * 2) + 4 + 4;
        if (size > ushort.MaxValue)
        {
            throw new IOException("The dump path is too long for a diagnostics request.");
        }

        byte[] message = new byte[size];
        Span<byte> span = message;
        magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)size);
        span[16] = 0x01; // Dump command set.
        span[17] = 0x01; // GenerateCoreDump.
        int offset = DiagnosticsHeaderSize;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], path.Length + 1);
        offset += 4;
        foreach (char c in path)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], c);
            offset += 2;
        }

        offset += 2; // The terminating null.
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 2); // WithHeap.
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], 0); // No createdump logging.
        return message;
    }

    /// <summary>
    /// Ends this process at once with <paramref name="exitCode"/>. A stalled UI thread may hold
    /// what an orderly shutdown waits for, so no shutdown handler runs.
    /// </summary>
    internal static void Terminate(int exitCode)
    {
        _ = TerminateProcess(GetCurrentProcess(), (uint)exitCode);
    }

    /// <summary>The native id of the calling thread, as <see cref="ProcessThread.Id"/> reports it.</summary>
    internal static int CurrentNativeThreadId() => (int)GetCurrentThreadId();

    private static ulong? ReadThreadCycles(int threadId)
    {
        IntPtr handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return QueryThreadCycleTime(handle, out ulong cycles) ? cycles : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static Dictionary<int, TimeSpan> ReadThreadTimes()
    {
        var times = new Dictionary<int, TimeSpan>();
        using Process process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            times[thread.Id] = SafeTotal(thread);
        }

        return times;
    }

    private static TimeSpan SafeTotal(ProcessThread thread)
    {
        try
        {
            return thread.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private static string SafeState(ProcessThread thread)
    {
        try
        {
            return thread.ThreadState == System.Diagnostics.ThreadState.Wait
                ? "Wait/" + thread.WaitReason
                : thread.ThreadState.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return "exited";
        }
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(none)";
        }

        var className = new StringBuilder(256);
        _ = GetClassName(hwnd, className, className.Capacity);
        var title = new StringBuilder(256);
        _ = GetWindowText(hwnd, title, title.Capacity);
        return $"[{className}] \"{title}\"";
    }

    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private const uint ThreadQueryLimitedInformation = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
