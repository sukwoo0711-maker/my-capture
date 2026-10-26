using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Options of the resident memory and responsiveness probe (<c>--selftest-resident &lt;output&gt;</c>):
/// the whole resident app runs against an isolated data root under the output folder, beside any
/// normal instance, and a script drives its own capture, card, editor, library, OCR and pin paths.
/// </summary>
/// <remarks>
/// The probe never registers global shortcuts, never writes the Run key, never checks for updates,
/// never imports the legacy library and never writes the clipboard (the clipboard writer is replaced
/// before any product code runs). Desktop frames are synthetic, at the virtual desktop's size, so no
/// screen content is read or stored.
/// </remarks>
internal sealed class ResidentProbeOptions
{
    internal const string CommandLineSwitch = "--selftest-resident";
    internal const string ScenarioSwitch = "--resident-scenarios";
    internal const string QuickSwitch = "--resident-quick";
    internal const string AuditSwitch = "--resident-audit";
    internal const string NoIndexingSwitch = "--resident-no-indexing";
    internal const string SoftwareRenderSwitch = "--resident-software-render";
    internal const string IdleReleaseSwitch = "--resident-idle-release";
    internal const string IdleSecondsSwitch = "--resident-idle-seconds";

    internal static readonly string[] AllScenarios = ["capture", "editor-4k", "capture-30", "library", "ocr", "pins-10"];

    private ResidentProbeOptions(string output, IReadOnlyList<string> scenarios, bool quick, bool audit)
    {
        Output = output;
        Scenarios = scenarios;
        Quick = quick;
        Audit = audit;
    }

    /// <summary>
    /// Diagnostic only: after each step's natural samples, a forced full collection and a list of the
    /// step's windows and images still reachable. Changes every later natural sample, so the
    /// release table never uses it.
    /// </summary>
    internal bool Audit { get; }

    /// <summary>Diagnostic only: the library's automatic OCR indexing never starts.</summary>
    internal bool SkipIndexing { get; private init; }

    /// <summary>Diagnostic only: WPF renders in software, to tell render-device memory from the app's own.</summary>
    internal bool SoftwareRender { get; private init; }

    internal ResidentIdleReleaseVariant IdleRelease { get; private init; }
    internal int IdleSeconds { get; private init; } = 180;
    internal bool MeasureIdleRelease { get; private init; }

    internal string Output { get; }

    internal string DataRoot => DiagnosticOutputPaths.Child(Output, "profile");

    /// <summary>Appended to the single-instance names so the probe never meets a normal instance.</summary>
    internal string InstanceSuffix { get; } = ".ResidentProbe." + Guid.NewGuid().ToString("N");

    internal IReadOnlyList<string> Scenarios { get; }

    /// <summary>Short settle windows for development; the release table always uses the full ones.</summary>
    internal bool Quick { get; }

    internal int[] SettleSeconds => Quick ? [0, 2, 4] : [0, 10, 30];

    internal int[] StartSeconds => Quick ? [0, 2, 4] : [0, 10, 30, 60];

    internal static ResidentProbeOptions? TryParse(string[] args)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, CommandLineSwitch, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        string output = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : Path.Combine(Path.GetTempPath(), "mycapture-resident-probe-" + Guid.NewGuid().ToString("N"));
        int scenarioIndex = Array.FindIndex(args, argument => string.Equals(argument, ScenarioSwitch, StringComparison.OrdinalIgnoreCase));
        string[] scenarios = scenarioIndex >= 0 && scenarioIndex + 1 < args.Length
            ? args[scenarioIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : AllScenarios;
        bool quick = Array.Exists(args, argument => string.Equals(argument, QuickSwitch, StringComparison.OrdinalIgnoreCase));
        bool audit = Array.Exists(args, argument => string.Equals(argument, AuditSwitch, StringComparison.OrdinalIgnoreCase));
        var idle = ParseIdleRelease(args);
        bool software = Array.Exists(args, argument => string.Equals(argument, SoftwareRenderSwitch, StringComparison.OrdinalIgnoreCase));
        if (software && idle.Variant != ResidentIdleReleaseVariant.None)
            throw new ArgumentException("Do not combine --resident-software-render with an idle-release candidate.");
        return new ResidentProbeOptions(DiagnosticOutputPaths.Create(output), scenarios, quick, audit)
        {
            SkipIndexing = Array.Exists(args, argument => string.Equals(argument, NoIndexingSwitch, StringComparison.OrdinalIgnoreCase)),
            SoftwareRender = software,
            IdleRelease = idle.Variant,
            IdleSeconds = idle.Seconds,
            MeasureIdleRelease = idle.Requested,
        };
    }

    internal static (ResidentIdleReleaseVariant Variant, int Seconds, bool Requested) ParseIdleRelease(string[] args)
    {
        string? Value(string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(name.Length + 1)..];
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                        ? args[i + 1] : throw new ArgumentException("Missing value for " + name);
            }
            return null;
        }
        string? variant = Value(IdleReleaseSwitch);
        ResidentIdleReleaseVariant parsed = variant?.ToLowerInvariant() switch
        {
            null or "none" => ResidentIdleReleaseVariant.None,
            "processmode" => ResidentIdleReleaseVariant.ProcessMode,
            "keepalive" => ResidentIdleReleaseVariant.KeepAlive,
            _ => throw new ArgumentException("Unknown resident idle-release variant: " + variant),
        };
        string? seconds = Value(IdleSecondsSwitch);
        int threshold = 180;
        if (seconds is not null && (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out threshold)
            || threshold is < 1 or > 3600))
            throw new ArgumentException("Resident idle seconds must be between 1 and 3600.");
        return (parsed, threshold, variant is not null);
    }
}

/// <summary>Synthetic desktop pixels with the native back-buffer ownership of a real screen grab.</summary>
internal static class ResidentProbeImages
{
    internal static FrozenFrame DesktopFrame(RectD screenBounds, int seed)
    {
        int width = Math.Max(1, (int)screenBounds.Width), height = Math.Max(1, (int)screenBounds.Height);
        return new FrozenFrame(Pattern(width, height, seed), screenBounds, null, 0);
    }

    internal static BitmapSource Pattern(int width, int height, int seed)
    {
        int stride = width * 4;
        byte[] row = new byte[stride];
        var image = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        image.Lock();
        try
        {
            int band = -1;
            for (int y = 0; y < height; y++)
            {
                if (y / 8 != band)
                {
                    band = y / 8;
                    for (int x = 0; x < width; x++)
                    {
                        row[x * 4] = (byte)(70 + (x / 24 + y / 24 + seed) % 150);
                        row[x * 4 + 1] = (byte)(90 + (x / 48 + seed) % 120);
                        row[x * 4 + 2] = (byte)(120 + (y / 40) % 110);
                        row[x * 4 + 3] = 255;
                    }
                }
                Marshal.Copy(row, 0, IntPtr.Add(image.BackBuffer, y * image.BackBufferStride), stride);
            }
            image.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally { image.Unlock(); }
        image.Freeze();
        return image;
    }

    /// <summary>Dark text on white, the shape of a document or code capture, for OCR and text pins.</summary>
    internal static BitmapSource Text(int width, int height, string[] lines)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var typeface = new Typeface("Segoe UI");
            for (int index = 0; index < lines.Length; index++)
            {
                var text = new FormattedText(lines[index], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 28, Brushes.Black, 1.0);
                context.DrawText(text, new Point(24, 24 + index * 42));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Process counters at fixed points of the probe script, the UI thread's input-priority round trip
/// while idle, and the named timings of the responsiveness paths.
/// </summary>
internal sealed class ResidentProbeRecorder : IDisposable
{
    private readonly ResidentProbeOptions _options;
    private readonly Dispatcher _dispatcher;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<double> _latency = [];
    private readonly System.Threading.Timer _latencyTimer;
    private readonly List<Row> _rows = [];
    private readonly List<Timing> _timings = [];
    private readonly List<string> _failures = [];
    private readonly List<string> _notes = [];
    private readonly List<(string Step, string Kind, WeakReference Reference)> _tracked = [];
    private TimeSpan _lastCpu;
    private double _lastWallMs;
    private int _pending;

    internal ResidentProbeRecorder(ResidentProbeOptions options, Dispatcher dispatcher)
    {
        _options = options;
        _dispatcher = dispatcher;
        _process.Refresh();
        _lastCpu = _process.TotalProcessorTime;
        _lastWallMs = _clock.Elapsed.TotalMilliseconds;
        // One input-priority round trip every 100 ms, never more than one outstanding: the delay a
        // key press would meet on the UI thread at that moment.
        _latencyTimer = new System.Threading.Timer(_ => PostLatencyProbe(), null, 100, 100);
    }

    internal sealed record Row(string Step, int AtSeconds, double ElapsedSeconds, double WorkingSetMb, double PrivateMb,
        double ManagedMb, double GcHeapMb, double GcCommittedMb, int GdiHandles, int UserHandles, int Handles, int Threads,
        double CpuPercent, double UiP95Ms, double UiMaxMs, int UiSamples, int Gen0, int Gen1, int Gen2, string? Detail);

    internal sealed record Timing(string Name, double Milliseconds);

    private void PostLatencyProbe()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        long posted = Stopwatch.GetTimestamp();
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                double ms = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                lock (_gate) _latency.Add(ms);
                Volatile.Write(ref _pending, 0);
            }));
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    internal void Time(string name, double milliseconds)
    {
        lock (_gate) _timings.Add(new Timing(name, milliseconds));
    }

    /// <summary>Remembers <paramref name="target"/> weakly for the forced-collection audit.</summary>
    internal void Track(string step, string kind, object? target)
    {
        if (target is null) return;
        lock (_gate) _tracked.Add((step, kind, new WeakReference(target)));
    }

    /// <summary>
    /// Diagnostic only (<see cref="ResidentProbeOptions.Audit"/>): a forced, compacting full collection,
    /// then the counters and every tracked window or image still reachable.
    /// </summary>
    internal async Task AuditAsync(string step)
    {
        if (!_options.Audit) return;
        for (int pass = 0; pass < 2; pass++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(500);
        }
        string[] alive;
        lock (_gate) alive = _tracked.Where(item => item.Reference.IsAlive).Select(item => item.Step + ":" + item.Kind).ToArray();
        Sample(step + " after forced GC", -1, (alive.Length == 0 ? "nothing tracked alive" : "alive: " + string.Join(", ", alive))
            + " | " + CommittedRegions());
    }

    /// <summary>
    /// Committed memory by kind (private, mapped, image) and the largest private allocations, from
    /// VirtualQuery: which native blocks a closed window left behind.
    /// </summary>
    internal static string CommittedRegions()
    {
        var allocations = new Dictionary<long, (long Committed, uint Type)>();
        long address = 0;
        int size = Marshal.SizeOf<MemoryBasicInformation>();
        while (VirtualQuery(new IntPtr(address), out MemoryBasicInformation info, (UIntPtr)size) != UIntPtr.Zero)
        {
            long regionSize = (long)info.RegionSize;
            if (info.State == 0x1000) // MEM_COMMIT
            {
                long key = info.AllocationBase.ToInt64();
                allocations.TryGetValue(key, out var current);
                allocations[key] = (current.Committed + regionSize, info.Type);
            }
            address = info.BaseAddress.ToInt64() + regionSize;
            if (address <= 0 || regionSize <= 0) break;
        }
        static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        long privateBytes = allocations.Values.Where(a => a.Type == 0x20000).Sum(a => a.Committed);
        long mapped = allocations.Values.Where(a => a.Type == 0x40000).Sum(a => a.Committed);
        long image = allocations.Values.Where(a => a.Type == 0x1000000).Sum(a => a.Committed);
        var large = allocations.Where(a => a.Value.Type == 0x20000).Select(a => a.Value.Committed).OrderDescending().ToArray();
        string buckets = string.Join(" ", new (long Min, string Name)[] { (16L << 20, ">=16MB"), (4L << 20, "4-16MB"), (1L << 20, "1-4MB"), (0, "<1MB") }
            .Select((bucket, index) =>
            {
                long upper = index == 0 ? long.MaxValue : new[] { long.MaxValue, 16L << 20, 4L << 20, 1L << 20 }[index];
                long[] inBucket = large.Where(value => value >= bucket.Min && value < upper).ToArray();
                return $"{bucket.Name}:{inBucket.Length}/{Mb(inBucket.Sum())}";
            }));
        return $"committed private {Mb(privateBytes)} mapped {Mb(mapped)} image {Mb(image)}; private allocations {buckets}; largest "
            + string.Join(",", large.Take(12).Select(Mb));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

    internal void Note(string note)
    {
        lock (_gate) _notes.Add(note);
    }

    internal void Fail(Exception error)
    {
        lock (_gate) _failures.Add(error.ToString());
    }

    /// <summary>Counters now. CPU and UI latency cover the interval since the previous sample.</summary>
    internal Row Sample(string step, int atSeconds, string? detail = null)
    {
        _process.Refresh();
        double wall = _clock.Elapsed.TotalMilliseconds;
        TimeSpan cpu = _process.TotalProcessorTime;
        double cpuPercent = wall > _lastWallMs
            ? (cpu - _lastCpu).TotalMilliseconds / (wall - _lastWallMs) / Environment.ProcessorCount * 100 : 0;
        _lastCpu = cpu;
        _lastWallMs = wall;
        double[] latency;
        lock (_gate)
        {
            latency = _latency.Order().ToArray();
            _latency.Clear();
        }
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        var row = new Row(step, atSeconds, wall / 1000, Mb(_process.WorkingSet64), Mb(_process.PrivateMemorySize64),
            Mb(GC.GetTotalMemory(false)), Mb(gc.HeapSizeBytes), Mb(gc.TotalCommittedBytes),
            (int)GetGuiResources(_process.Handle, 0), (int)GetGuiResources(_process.Handle, 1), _process.HandleCount,
            _process.Threads.Count, Math.Round(cpuPercent, 3),
            latency.Length == 0 ? 0 : Math.Round(latency[(int)Math.Floor((latency.Length - 1) * 0.95)], 2),
            latency.Length == 0 ? 0 : Math.Round(latency[^1], 2), latency.Length,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), detail);
        lock (_gate) _rows.Add(row);
        return row;
    }

    /// <summary>Samples at each of <paramref name="seconds"/> after now, idling naturally between them.</summary>
    internal async Task SettleAsync(string step, int[] seconds, Func<string?>? detail = null)
    {
        var started = Stopwatch.StartNew();
        foreach (int at in seconds)
        {
            TimeSpan wait = TimeSpan.FromSeconds(at) - started.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            Sample(step, at, detail?.Invoke());
        }
    }

    internal void Write()
    {
        _latencyTimer.Dispose();
        string build = typeof(ResidentProbeRecorder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
        object report;
        lock (_gate)
        {
            report = new
            {
                Utc = DateTimeOffset.UtcNow,
                Build = build,
                Os = Environment.OSVersion.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                LogicalProcessors = Environment.ProcessorCount,
                RenderingTier = RenderCapability.Tier >> 16,
                VirtualDesktop = $"{desktop.Width}x{desktop.Height}",
                Monitors = MonitorEnumerator.GetAll().Select(monitor =>
                    $"{monitor.Bounds.Width}x{monitor.Bounds.Height}@{monitor.Dpi}{(monitor.IsPrimary ? " primary" : "")}").ToArray(),
                _options.Quick,
                IdleReleaseVariant = _options.IdleRelease.ToString().ToLowerInvariant(),
                _options.IdleSeconds,
                _options.MeasureIdleRelease,
                Scenarios = _options.Scenarios,
                Rows = _rows,
                Timings = _timings,
                Notes = _notes,
                Failures = _failures,
            };
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(_options.Output, "resident-probe.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var text = new StringBuilder();
        text.AppendLine(_failures.Count == 0 ? "RESULT: PASS (measurement only; read resident-probe.json)" : "RESULT: FAIL");
        foreach (string failure in _failures) text.AppendLine(failure);
        File.WriteAllText(DiagnosticOutputPaths.Child(_options.Output, "resident-probe-report.txt"), text.ToString());
    }

    internal bool Failed
    {
        get { lock (_gate) return _failures.Count > 0; }
    }

    public void Dispose()
    {
        _latencyTimer.Dispose();
        _process.Dispose();
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
