using System.Runtime.InteropServices;

namespace MyCapture.App.Tests;

/// <summary>
/// Polls this process's loaded-module list, so the probe can tell which DLLs an open brought in
/// and when. The list is read the way a debugger reads it, without taking the loader lock, so a
/// load in progress never blocks the poll and the poll never delays a load.
/// </summary>
internal sealed class ModuleTracker : IDisposable
{
    private readonly ProbeRecorder _rec;
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, ModuleEntry> _known = [];
    private readonly List<ModuleEntry> _loaded = [];
    private readonly List<ModuleEntry> _unloaded = [];
    private readonly Thread _thread;
    private IntPtr[] _buffer = new IntPtr[1024];
    private double _lastScan;
    private volatile bool _stop;

    private ModuleTracker(ProbeRecorder rec)
    {
        _rec = rec;
        _thread = new Thread(Run) { IsBackground = true, Name = "probe module tracker", Priority = ThreadPriority.AboveNormal };
    }

    internal int BaselineCount { get; private set; }

    internal static ModuleTracker Start(ProbeRecorder rec)
    {
        var tracker = new ModuleTracker(rec);
        tracker.Scan(baseline: true);
        tracker._thread.Start();
        return tracker;
    }

    internal List<Dictionary<string, object?>> Loaded(double zero)
    {
        lock (_gate) return [.. _loaded.Select(entry => entry.ToReport(zero))];
    }

    internal List<Dictionary<string, object?>> Unloaded(double zero)
    {
        lock (_gate) return [.. _unloaded.Select(entry => entry.ToReport(zero))];
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                Scan(baseline: false);
            }
            catch (Exception ex)
            {
                _rec.Event("module scan failed: " + ex.Message);
                Thread.Sleep(500);
            }

            Thread.Sleep(15);
        }
    }

    private void Scan(bool baseline)
    {
        IntPtr process = NativeProbe.GetCurrentProcess();
        int count;
        while (true)
        {
            if (!NativeProbe.K32EnumProcessModulesEx(process, _buffer, _buffer.Length * IntPtr.Size, out int needed, 3))
                return;   // the list changed mid-read; the next poll sees it
            count = needed / IntPtr.Size;
            if (count <= _buffer.Length) break;
            _buffer = new IntPtr[count * 2];
        }

        double now = _rec.Now;
        var present = new HashSet<IntPtr>(count);
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr module = _buffer[i];
                present.Add(module);
                if (_known.ContainsKey(module)) continue;
                var entry = new ModuleEntry(Describe(process, module), baseline ? 0 : _lastScan, now);
                _known[module] = entry;
                if (!baseline) _loaded.Add(entry);
            }

            foreach (KeyValuePair<IntPtr, ModuleEntry> gone in _known.Where(pair => !present.Contains(pair.Key)).ToList())
            {
                _known.Remove(gone.Key);
                gone.Value.UnloadedBy = now;
                _unloaded.Add(gone.Value);
            }

            if (baseline) BaselineCount = _known.Count;
        }

        _lastScan = now;
    }

    private static string Describe(IntPtr process, IntPtr module)
    {
        char[] name = new char[1024];
        int length = NativeProbe.K32GetModuleFileNameExW(process, module, name, name.Length);
        string path = length > 0 ? new string(name, 0, length) : $"<module 0x{module.ToInt64():X}>";
        return NativeProbe.K32GetModuleInformation(process, module, out NativeProbe.ModuleInfo info, Marshal.SizeOf<NativeProbe.ModuleInfo>())
            ? $"{path} ({info.SizeOfImage / 1024} KB)"
            : path;
    }

    private sealed class ModuleEntry(string path, double after, double by)
    {
        internal double? UnloadedBy { get; set; }

        internal Dictionary<string, object?> ToReport(double zero) => new()
        {
            ["module"] = path,
            // The load happened after the previous poll and before the poll that saw it.
            ["after"] = Math.Round(after - zero, 1),
            ["by"] = Math.Round(by - zero, 1),
            ["unloadedBy"] = UnloadedBy is double gone ? Math.Round(gone - zero, 1) : null,
        };
    }
}

/// <summary>
/// Samples, every 250 ms, this process's CPU and I/O, the machine's busy share, the audio and
/// media services, and every process's CPU and reads, so the probe can see whether the first
/// open waits on a service start, on another process (such as the antivirus), or on itself.
/// </summary>
internal sealed class SystemSampler : IDisposable
{
    private static readonly string[] ServiceNames = ["Audiosrv", "AudioEndpointBuilder", "WMPNetworkSvc"];
    private static readonly string[] StateNames = ["?", "stopped", "start-pending", "stop-pending", "running", "continue-pending", "pause-pending", "paused"];

    private readonly ProbeRecorder _rec;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly IntPtr _scm;
    private readonly Dictionary<string, IntPtr> _services = [];
    private readonly Dictionary<string, string> _serviceStates = [];
    private readonly List<(double At, string Text)> _serviceTransitions = [];
    private readonly List<(double At, string Text)> _started = [];
    private readonly List<(double At, string Text)> _exited = [];
    private readonly List<Dictionary<string, object?>> _own = [];
    private readonly Dictionary<string, (double At, Dictionary<(long Pid, long Created), ProcessSample> Processes)> _marks = [];
    private Dictionary<(long Pid, long Created), ProcessSample> _processes;
    private IntPtr _processBuffer;
    private int _processBufferSize = 4 << 20;
    private (long Idle, long Kernel, long User) _lastSystem;
    private volatile bool _stop;

    private SystemSampler(ProbeRecorder rec)
    {
        _rec = rec;
        _processBuffer = Marshal.AllocHGlobal(_processBufferSize);
        _scm = NativeProbe.OpenSCManagerW(null, null, 0x0001);
        foreach (string name in ServiceNames)
        {
            IntPtr service = _scm == IntPtr.Zero ? IntPtr.Zero : NativeProbe.OpenServiceW(_scm, name, 0x0004);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Transition(name, $"cannot query (Win32 error {error})");
                continue;
            }

            _services[name] = service;
        }

        NativeProbe.GetSystemTimes(out _lastSystem.Idle, out _lastSystem.Kernel, out _lastSystem.User);
        _processes = ReadProcesses();
        _thread = new Thread(Run) { IsBackground = true, Name = "probe system sampler" };
    }

    internal static SystemSampler Start(ProbeRecorder rec)
    {
        var sampler = new SystemSampler(rec);
        sampler.SampleServices();
        sampler._thread.Start();
        return sampler;
    }

    /// <summary>Keeps a snapshot of every process's counters under <paramref name="name"/>.</summary>
    internal void Mark(string name)
    {
        Dictionary<(long, long), ProcessSample> snapshot = ReadProcesses();
        lock (_gate) _marks[name] = (_rec.Now, snapshot);
    }

    internal List<string> ServiceTransitions(double zero)
    {
        lock (_gate) return [.. _serviceTransitions.Select(e => $"{e.At - zero,9:F0}  {e.Text}")];
    }

    internal List<string> ProcessStarts(double zero)
    {
        lock (_gate) return [.. _started.Select(e => $"{e.At - zero,9:F0}  {e.Text}")];
    }

    internal List<string> ProcessExits(double zero)
    {
        lock (_gate) return [.. _exited.Select(e => $"{e.At - zero,9:F0}  {e.Text}")];
    }

    internal List<Dictionary<string, object?>> OwnSamples(double zero)
    {
        lock (_gate)
        {
            return [.. _own.Select(sample => sample.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == "t" ? Math.Round((double)pair.Value! - zero, 0) : pair.Value))];
        }
    }

    /// <summary>The processes that used the most CPU, or read the most, between two marks.</summary>
    internal Dictionary<string, object?>? Delta(string from, string to)
    {
        lock (_gate)
        {
            if (!_marks.TryGetValue(from, out var a) || !_marks.TryGetValue(to, out var b)) return null;
            List<ProcessDelta> rows = [.. b.Processes.Where(pair => pair.Key.Pid != 0).Select(pair =>
            {
                bool isNew = !a.Processes.TryGetValue(pair.Key, out ProcessSample before);
                return new ProcessDelta(
                    $"{pair.Value.Name} ({pair.Key.Pid}{(isNew ? ", new" : "")})",
                    (pair.Value.Cpu - before.Cpu) / 10_000.0,
                    (pair.Value.ReadBytes - before.ReadBytes) / 1024,
                    (pair.Value.OtherBytes - before.OtherBytes) / 1024);
            })];
            return new Dictionary<string, object?>
            {
                ["windowMs"] = Math.Round(b.At - a.At, 0),
                ["topCpu"] = rows.OrderByDescending(r => r.CpuMs).Take(15).Select(r => r.ToString()).ToList(),
                ["topRead"] = rows.OrderByDescending(r => r.ReadKb).Take(10).Select(r => r.ToString()).ToList(),
            };
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        foreach (IntPtr service in _services.Values) NativeProbe.CloseServiceHandle(service);
        if (_scm != IntPtr.Zero) NativeProbe.CloseServiceHandle(_scm);
        Marshal.FreeHGlobal(_processBuffer);
        _processBuffer = IntPtr.Zero;
    }

    private void Run()
    {
        int tick = 0;
        while (!_stop)
        {
            Thread.Sleep(250);
            try
            {
                SampleServices();
                SampleOwn();
                if (++tick % 2 == 0) SampleProcesses();
            }
            catch (Exception ex)
            {
                _rec.Event("system sample failed: " + ex.Message);
            }
        }
    }

    private void SampleServices()
    {
        foreach ((string name, IntPtr service) in _services)
        {
            string state = NativeProbe.QueryServiceStatusEx(service, 0, out NativeProbe.ServiceStatusProcess status, Marshal.SizeOf<NativeProbe.ServiceStatusProcess>(), out _)
                ? $"{(status.CurrentState < StateNames.Length ? StateNames[status.CurrentState] : status.CurrentState.ToString())} pid {status.ProcessId}"
                : $"query failed (Win32 error {Marshal.GetLastWin32Error()})";
            Transition(name, state);
        }
    }

    private void Transition(string name, string state)
    {
        lock (_gate)
        {
            if (_serviceStates.TryGetValue(name, out string? last) && last == state) return;
            _serviceStates[name] = state;
            _serviceTransitions.Add((_rec.Now, $"{name}: {state}"));
        }

        _rec.Event($"service {name}: {state}");
    }

    private void SampleOwn()
    {
        IntPtr process = NativeProbe.GetCurrentProcess();
        NativeProbe.GetProcessTimes(process, out _, out _, out long kernel, out long user);
        NativeProbe.GetProcessIoCounters(process, out NativeProbe.IoCounters io);
        NativeProbe.GetSystemTimes(out long idle, out long sysKernel, out long sysUser);
        long total = (sysKernel - _lastSystem.Kernel) + (sysUser - _lastSystem.User);
        double busy = total > 0 ? 100.0 * (total - (idle - _lastSystem.Idle)) / total : 0;
        _lastSystem = (idle, sysKernel, sysUser);
        lock (_gate)
        {
            _own.Add(new Dictionary<string, object?>
            {
                ["t"] = _rec.Now,
                ["cpuMs"] = Math.Round((kernel + user) / 10_000.0, 0),
                ["readKb"] = (long)(io.ReadTransferCount / 1024),
                ["readOps"] = (long)io.ReadOperationCount,
                ["otherKb"] = (long)(io.OtherTransferCount / 1024),
                ["threads"] = _processes.FirstOrDefault(pair => pair.Key.Pid == Environment.ProcessId).Value.Threads,
                ["machineBusyPct"] = Math.Round(busy, 0),
            });
        }
    }

    private void SampleProcesses()
    {
        Dictionary<(long Pid, long Created), ProcessSample> now = ReadProcesses();
        double at = _rec.Now;
        lock (_gate)
        {
            foreach (var pair in now.Where(pair => !_processes.ContainsKey(pair.Key)))
            {
                _started.Add((at, $"{pair.Value.Name} pid {pair.Key.Pid}"));
            }

            foreach (var pair in _processes.Where(pair => !now.ContainsKey(pair.Key)))
            {
                _exited.Add((at, $"{pair.Value.Name} pid {pair.Key.Pid}"));
            }

            _processes = now;
        }
    }

    /// <summary>Every process's counters from one SystemProcessInformation snapshot.</summary>
    private Dictionary<(long Pid, long Created), ProcessSample> ReadProcesses()
    {
        lock (_gate)
        {
            int status;
            while ((status = NativeProbe.NtQuerySystemInformation(5, _processBuffer, _processBufferSize, out int needed)) == unchecked((int)0xC0000004))
            {
                Marshal.FreeHGlobal(_processBuffer);
                _processBufferSize = Math.Max(needed, _processBufferSize) * 2;
                _processBuffer = Marshal.AllocHGlobal(_processBufferSize);
            }

            var result = new Dictionary<(long, long), ProcessSample>();
            if (status != 0) return result;
            int offset = 0;
            while (true)
            {
                IntPtr entry = _processBuffer + offset;
                int next = Marshal.ReadInt32(entry, 0x00);
                int threads = Marshal.ReadInt32(entry, 0x04);
                long created = Marshal.ReadInt64(entry, 0x20);
                long cpu = Marshal.ReadInt64(entry, 0x28) + Marshal.ReadInt64(entry, 0x30);
                int nameBytes = (ushort)Marshal.ReadInt16(entry, 0x38);
                IntPtr nameBuffer = Marshal.ReadIntPtr(entry, 0x40);
                long pid = Marshal.ReadIntPtr(entry, 0x50).ToInt64();
                long read = Marshal.ReadInt64(entry, 0xE8);
                long other = Marshal.ReadInt64(entry, 0xF8);
                string name = nameBuffer == IntPtr.Zero ? (pid == 0 ? "Idle" : "?") : Marshal.PtrToStringUni(nameBuffer, nameBytes / 2);
                result[(pid, created)] = new ProcessSample(name, cpu, read, other, threads);
                if (next == 0) break;
                offset += next;
            }

            return result;
        }
    }

    private readonly record struct ProcessSample(string Name, long Cpu, long ReadBytes, long OtherBytes, int Threads);

    private sealed record ProcessDelta(string Process, double CpuMs, long ReadKb, long OtherKb)
    {
        public override string ToString() => $"{Process}: cpu {CpuMs:F0} ms, read {ReadKb} KB, other io {OtherKb} KB";
    }
}

internal static class NativeProbe
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ModuleInfo
    {
        public IntPtr BaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool K32EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, int size, out int needed, uint filter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int K32GetModuleFileNameExW(IntPtr process, IntPtr module, [Out] char[] name, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool K32GetModuleInformation(IntPtr process, IntPtr module, out ModuleInfo info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("ntdll.dll")]
    internal static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);
}
