using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
            Thread.Sleep(interval);
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
            _ = GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == processId)
            {
                count++;
                IntPtr ownerWindow = GetWindow(hwnd, GwOwner);
                text.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  window 0x{hwnd.ToInt64():X}: {Describe(hwnd)}, visible={IsWindowVisible(hwnd)}, " +
                    $"enabled={IsWindowEnabled(hwnd)}, owner=0x{ownerWindow.ToInt64():X}"));
            }

            return true;
        }, IntPtr.Zero);
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Process windows: {count}"));
        return text.ToString();
    }

    /// <summary>
    /// Writes a dump of this process with the runtime's createdump tool, which ships beside the
    /// runtime (the app folder of a self-contained package). Returns what happened, for the report.
    /// </summary>
    internal static string WriteDump(string path, TimeSpan timeout)
    {
        string tool = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump.exe");
        if (!File.Exists(tool))
        {
            return "Dump: not written; createdump.exe is not beside the runtime";
        }

        try
        {
            var start = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // A heap dump keeps the runtime's own data, so managed stacks can be read from it.
            start.ArgumentList.Add("--withheap");
            start.ArgumentList.Add("--name");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            var clock = Stopwatch.StartNew();
            using Process dumper = Process.Start(start)
                ?? throw new InvalidOperationException("createdump did not start");
            Task<string> output = dumper.StandardOutput.ReadToEndAsync();
            Task<string> error = dumper.StandardError.ReadToEndAsync();
            if (!dumper.WaitForExit(timeout))
            {
                dumper.Kill();
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Dump: createdump did not finish within {timeout.TotalSeconds:0}s");
            }

            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            string detail = (output.Result + " " + error.Result).ReplaceLineEndings(" ").Trim();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Dump: {path} ({bytes} bytes, exit {dumper.ExitCode}, {clock.Elapsed.TotalSeconds:0.0}s) {detail}");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException)
        {
            return "Dump: not written; " + ex.Message;
        }
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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
