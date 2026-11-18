using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Real DPI changes on this machine's monitors. Runs only when at least two attached monitors have
/// different scale settings; it never changes them. Real chrome HWNDs are placed with real
/// SetWindowPos on both sides of every seam; only the pointer position is injected, the real cursor
/// never moves. Set MYCAPTURE_DPI_SEAM_LOG to a folder to keep the record of every placement; each line
/// is written through as it happens, so a test process that dies leaves its last step behind.
/// </summary>
public sealed class RealDpiSeamHarnessTests
{
    /// <summary>
    /// WM_DPICHANGED messages one HWND may receive for one placement before the harness stops WPF from
    /// applying the suggestion. Past it the DPI is trading between two monitors; the harness ends the loop
    /// itself so the record survives instead of the stack overflowing.
    /// </summary>
    private const int TradeLimit = 12;

    /// <summary>
    /// The strict placement the selector chrome uses, on a plain per-monitor WPF window, moved from inside
    /// one monitor to offsets across every seam. Each SetWindowPos request, the rectangle read right after
    /// it, every WM_DPICHANGED with its suggested rectangle and nesting, and the attempt count are logged.
    /// A record, not a requirement on Windows: the strict API keeps throwing by design.
    /// </summary>
    [RealMixedDpiFact]
    public void PositionerAcrossRealDpiSeams() => StaTestHost.Run(() =>
    {
        using var dpi = TestDpiContext.AtPerMonitorDpi();
        MonitorInfo[] monitors = MonitorEnumerator.GetAll().ToArray();
        using var log = new SeamLog("positioner");
        Describe(monitors, log);
        int failures = 0, placements = 0;
        foreach ((double width, double height) in new[] { (160d, 90d), (330d, 48d), (156d, 194d), (600d, 400d) })
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                ShowActivated = false, Topmost = true, Width = width, Height = height,
                Left = monitors[0].WorkArea.Left / monitors[0].ScaleFactor + 50,
                Top = monitors[0].WorkArea.Top / monitors[0].ScaleFactor + 50,
            };
            var api = new RecordingNativeApi(log);
            try
            {
                window.Show();
                Pump();
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                HwndSource.FromHwnd(hwnd)!.AddHook(api.Hook);
                foreach ((MonitorInfo from, MonitorInfo to) in Seams(monitors))
                    foreach (RectD target in Straddles(from, to, width * to.ScaleFactor, height * to.ScaleFactor))
                    {
                        // Start fully on the source monitor at its own scale, as chrome following the pointer does.
                        RectD start = Inside(from, width * from.ScaleFactor, height * from.ScaleFactor);
                        api.Begin($"reset on {from.DeviceName} {start}", hwnd);
                        try { PhysicalWindowPositioner.MoveAndResize(hwnd, start, api); }
                        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            api.Note("reset threw: " + error.Message);
                        }
                        Pump();
                        placements++;
                        api.Begin($"{from.DeviceName}->{to.DeviceName} {width}x{height} DIP, request {target}", hwnd);
                        try { PhysicalWindowPositioner.MoveAndResize(hwnd, target, api); api.Note("exact"); }
                        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            failures++;
                            api.Note("THREW: " + error.Message);
                        }
                        Pump();
                        _ = GetWindowRect(hwnd, out NativeRect r);
                        api.Note($"after layout {Text(r)} dpi {GetDpiForWindow(hwnd)}, WM_DPICHANGED {api.Messages}, deepest nesting {api.Deepest}");
                    }
            }
            finally { window.Close(); }
        }
        log.Line($"strict placements that threw: {failures} of {placements}");
    });

    /// <summary>
    /// The native selector over the real desktop, pointer moved through every mixed-DPI seam without a
    /// button press, chrome included (hover tab, magnifier, size chip, key panel). Fails when the selection
    /// ends (the reported crash path) or when one HWND's DPI keeps trading for a single pointer sample.
    /// </summary>
    [RealMixedDpiFact]
    public void SelectorChromeAcrossRealDpiSeamsKeepsTheSelection() => StaTestHost.Run(() =>
    {
        using var dpi = TestDpiContext.AtPerMonitorDpi();
        MonitorInfo[] monitors = MonitorEnumerator.GetAll().ToArray();
        double left = monitors.Min(m => m.Bounds.Left), top = monitors.Min(m => m.Bounds.Top);
        var desktop = new RectD(left, top, monitors.Max(m => m.Bounds.Right) - left, monitors.Max(m => m.Bounds.Bottom) - top);
        using var log = new SeamLog("selector");
        Describe(monitors, log);
        var problems = new List<string>();
        foreach (bool recording in new[] { false, true })
            problems.AddRange(SweepSelector(desktop, monitors, recording, log));
        log.Line(problems.Count == 0 ? "selector: no problems" : "selector problems: " + string.Join(" || ", problems));
        Assert.Empty(problems);
    });

    private static List<string> SweepSelector(RectD desktop, MonitorInfo[] monitors, bool recording, SeamLog log)
    {
        NativeSelectorChromeCache.Trim();
        var problems = new List<string>();
        PointD cursor = Center(monitors[0].Bounds);
        int width = (int)desktop.Width, height = (int)desktop.Height;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        using var overlay = new NativeCaptureOverlay(desktop, false, true)
        {
            PlaceCursor = _ => false,
            ReadCursor = () => cursor,
            SourceWindows = monitors.Select((m, i) => new WindowCandidate(new IntPtr(100 + i),
                new RectD(m.Bounds.Left + 40, m.Bounds.Top + 40, m.Bounds.Width / 2, m.Bounds.Height / 2), m.DeviceName))
                .Append(new WindowCandidate(new IntPtr(99), desktop, "Desktop", ClassName: "Progman")).ToList(),
            DetectWindows = true,
            ShowKeyPanel = !recording,
            OffersOtherActions = true,
            RecordingSelection = recording,
        };
        overlay.AttachFrame(new FrozenFrame(bitmap, desktop, null, 0));
        var failures = new List<Exception>();
        overlay.Failed += failures.Add;
        var hooks = new List<HwndSource>();
        var counter = new DpiMessageCounter();
        int deepest = 0, mostPerSample = 0;
        bool traded = false;
        IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x02E0) return IntPtr.Zero;
            (int count, int depth) = counter.Next(hwnd);
            deepest = Math.Max(deepest, depth);
            mostPerSample = Math.Max(mostPerSample, count);
            NativeRect suggested = Marshal.PtrToStructure<NativeRect>(lParam);
            _ = GetWindowRect(hwnd, out NativeRect now);
            log.Line($"recording {recording} pointer {cursor} hwnd {hwnd:X} WM_DPICHANGED {wParam.ToInt64() & 0xffff} #{count} nesting {depth} suggested {Text(suggested)} window now {Text(now)}");
            if (count > TradeLimit)
            {
                traded = true;
                handled = true;
                log.Line($"recording {recording} hwnd {hwnd:X} TRADE LIMIT: {count} WM_DPICHANGED for one pointer sample; the suggestion is not applied");
            }
            return IntPtr.Zero;
        }
        try
        {
            overlay.Show();
            PumpUntil(() => overlay.IsReady && overlay.IsChromeReady);
            var lastDpi = new Dictionary<IntPtr, uint>();
            foreach ((MonitorInfo from, MonitorInfo to) in Seams(monitors))
            {
                log.Line($"recording {recording} crossing {from.DeviceName} (dpi {from.Dpi}) -> {to.DeviceName} (dpi {to.Dpi})");
                foreach (PointD point in Crossing(from, to))
                {
                    cursor = point;
                    if (overlay.Chrome is { } chrome)
                        foreach (Window window in chrome.Windows)
                            if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is { } source && !hooks.Contains(source))
                            {
                                source.AddHook(Hook);
                                hooks.Add(source);
                            }
                    counter.NewSample();
                    _ = SendMessage(overlay.Handle, 0x0200, IntPtr.Zero, IntPtr.Zero);
                    var elapsed = Stopwatch.StartNew();
                    do Pump(); while (elapsed.ElapsedMilliseconds < 8);
                    foreach (Window window in overlay.Chrome?.Windows ?? [])
                    {
                        IntPtr hwnd = new WindowInteropHelper(window).Handle;
                        if (!window.IsVisible || !GetWindowRect(hwnd, out NativeRect r)) continue;
                        uint windowDpi = GetDpiForWindow(hwnd);
                        if (lastDpi.TryGetValue(hwnd, out uint previous) && previous == windowDpi) continue;
                        lastDpi[hwnd] = windowDpi;
                        log.Line($"recording {recording} pointer {point} hwnd {hwnd:X} at dpi {windowDpi} {Text(r)}");
                    }
                    if (overlay.IsClosed || failures.Count > 0)
                    {
                        problems.Add($"recording {recording}: selection ended at {point} crossing {from.DeviceName}->{to.DeviceName}: {string.Join(" | ", failures)}");
                        return problems;
                    }
                }
            }
            if (traded) problems.Add($"recording {recording}: chrome DPI traded between monitors (most WM_DPICHANGED for one sample {mostPerSample})");
        }
        finally
        {
            foreach (HwndSource source in hooks) if (!source.IsDisposed) source.RemoveHook(Hook);
            log.Line($"recording {recording}: closed {overlay.IsClosed}, most WM_DPICHANGED for one sample {mostPerSample}, deepest nesting {deepest}, failures: {string.Join(" | ", failures.Select(f => f.GetType().Name + ": " + f.Message))}");
            overlay.Close();
            NativeSelectorChromeCache.Trim();
        }
        return problems;
    }

    private static RectD Inside(MonitorInfo monitor, double width, double height)
    {
        RectD b = monitor.WorkArea;
        return new RectD(Math.Round(b.Left + (b.Width - width) / 2), Math.Round(b.Top + (b.Height - height) / 2), width, height);
    }

    /// <summary>
    /// Counts WM_DPICHANGED per HWND since the last sample. Nesting is how many earlier messages of the
    /// sample are still on the managed stack below this one: a message delivered inside WPF's handling of
    /// the previous one (its suggested rectangle moved the HWND again) arrives deeper.
    /// </summary>
    private sealed class DpiMessageCounter
    {
        private readonly Dictionary<IntPtr, int> _sample = [];
        private readonly List<int> _frames = [];

        internal void NewSample()
        {
            _sample.Clear();
            _frames.Clear();
        }

        internal (int Count, int Depth) Next(IntPtr hwnd)
        {
            int frames = new StackTrace().FrameCount;
            int count = _sample.TryGetValue(hwnd, out int seen) ? seen + 1 : 1;
            _sample[hwnd] = count;
            while (_frames.Count > 0 && _frames[^1] >= frames) _frames.RemoveAt(_frames.Count - 1);
            int depth = _frames.Count;
            _frames.Add(frames);
            return (count, depth);
        }
    }

    /// <summary>The real Win32 calls, logged attempt by attempt; WM_DPICHANGED arrives in between.</summary>
    private sealed class RecordingNativeApi(SeamLog log) : IPhysicalWindowNativeApi
    {
        private readonly IPhysicalWindowNativeApi _real = Win32PhysicalWindowNativeApi.Instance;
        private readonly DpiMessageCounter _counter = new();
        private int _attempt;
        private IntPtr _hwnd;

        internal int Messages { get; private set; }
        internal int Deepest { get; private set; }

        internal void Begin(string label, IntPtr hwnd)
        {
            _attempt = 0;
            _hwnd = hwnd;
            Messages = 0;
            Deepest = 0;
            _counter.NewSample();
            log.Line($"== {label} (window dpi {GetDpiForWindow(hwnd)})");
        }

        internal void Note(string text) => log.Line("   " + text);

        public bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
        {
            _attempt++;
            Note($"attempt {_attempt}: SetWindowPos [{x},{y} {width}x{height}]");
            bool result = _real.SetWindowPos(hwnd, insertAfter, x, y, width, height, flags);
            _ = _real.GetWindowRect(hwnd, out PhysicalWindowBounds after);
            Note($"attempt {_attempt}: returned {result}, right after {after}, dpi {GetDpiForWindow(hwnd)}");
            return result;
        }

        public bool GetWindowRect(IntPtr hwnd, out PhysicalWindowBounds bounds) => _real.GetWindowRect(hwnd, out bounds);

        public int GetLastError() => _real.GetLastError();

        internal IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x02E0 || hwnd != _hwnd) return IntPtr.Zero;
            (int count, int depth) = _counter.Next(hwnd);
            Messages = count;
            Deepest = Math.Max(Deepest, depth);
            NativeRect suggested = Marshal.PtrToStructure<NativeRect>(lParam);
            _ = RealDpiSeamHarnessTests.GetWindowRect(hwnd, out NativeRect now);
            Note($"WM_DPICHANGED dpi {wParam.ToInt64() & 0xffff} #{count} nesting {depth} suggested {Text(suggested)} window now {Text(now)}");
            if (count > TradeLimit)
            {
                handled = true;
                Note($"TRADE LIMIT: {count} WM_DPICHANGED for one placement; the suggestion is not applied");
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>Writes each line through to MYCAPTURE_DPI_SEAM_LOG as it happens; nothing without it.</summary>
    private sealed class SeamLog : IDisposable
    {
        private readonly System.IO.StreamWriter? _writer;

        internal SeamLog(string name)
        {
            string? folder = Environment.GetEnvironmentVariable("MYCAPTURE_DPI_SEAM_LOG");
            if (string.IsNullOrWhiteSpace(folder)) return;
            string root = MyCapture.App.Diagnostics.DiagnosticOutputPaths.Create(folder);
            _writer = new System.IO.StreamWriter(MyCapture.App.Diagnostics.DiagnosticOutputPaths.Child(root,
                $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt")) { AutoFlush = true };
        }

        internal void Line(string text) => _writer?.WriteLine(text);

        public void Dispose() => _writer?.Dispose();
    }

    /// <summary>Neighbouring monitors of different DPI, both directions.</summary>
    private static IEnumerable<(MonitorInfo From, MonitorInfo To)> Seams(MonitorInfo[] monitors)
    {
        foreach (MonitorInfo a in monitors)
            foreach (MonitorInfo b in monitors)
                if (!ReferenceEquals(a, b) && a.Dpi != b.Dpi && Touch(a.Bounds, b.Bounds)) yield return (a, b);
    }

    private static bool Touch(RectD a, RectD b) =>
        (a.Right == b.Left || b.Right == a.Left) && a.Top < b.Bottom && b.Top < a.Bottom
        || (a.Bottom == b.Top || b.Bottom == a.Top) && a.Left < b.Right && b.Left < a.Right;

    /// <summary>
    /// Rectangles that sit mostly on <paramref name="from"/>, straddle the seam, and lie past it; fraction 0
    /// is fully on <paramref name="to"/> against the seam, where chrome clamped to that monitor sits.
    /// </summary>
    private static IEnumerable<RectD> Straddles(MonitorInfo from, MonitorInfo to, double width, double height)
    {
        RectD f = from.Bounds, t = to.Bounds;
        double y = Math.Max(f.Top, t.Top) + 100, x = Math.Max(f.Left, t.Left) + 100;
        // fraction: the share of the rectangle still on the source monitor.
        foreach (double fraction in new[] { 0.9, 0.6, 0.51, 0.5, 0.49, 0.4, 0.1, 0.0, -0.2 })
        {
            if (t.Left == f.Right) yield return new RectD(Math.Round(t.Left - width * fraction), y, width, height);
            else if (t.Right == f.Left) yield return new RectD(Math.Round(t.Right - width * (1 - fraction)), y, width, height);
            else if (t.Top == f.Bottom) yield return new RectD(x, Math.Round(t.Top - height * fraction), width, height);
            else yield return new RectD(x, Math.Round(t.Bottom - height * (1 - fraction)), width, height);
        }
    }

    /// <summary>Pointer samples two pixels apart through the seam, near its ends and middle.</summary>
    private static IEnumerable<PointD> Crossing(MonitorInfo from, MonitorInfo to)
    {
        RectD f = from.Bounds, t = to.Bounds;
        bool vertical = t.Left == f.Right || t.Right == f.Left;
        double low = vertical ? Math.Max(f.Top, t.Top) : Math.Max(f.Left, t.Left);
        double high = vertical ? Math.Min(f.Bottom, t.Bottom) : Math.Min(f.Right, t.Right);
        double seam = t.Left == f.Right ? t.Left : t.Right == f.Left ? f.Left : t.Top == f.Bottom ? t.Top : f.Top;
        double direction = t.Left == f.Right || t.Top == f.Bottom ? 1 : -1;
        foreach (double along in new[] { low + 4, low + 200, (low + high) / 2, high - 200, high - 4 })
            for (int step = -40; step <= 40; step += 2)
            {
                double across = seam + direction * step - (direction > 0 ? 0 : 1);
                yield return vertical ? new PointD(across, along) : new PointD(along, across);
            }
    }

    private static PointD Center(RectD b) => new(b.Left + b.Width / 2, b.Top + b.Height / 2);

    private static string Text(NativeRect r) => $"[{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}]";

    private static void Describe(MonitorInfo[] monitors, SeamLog log)
    {
        foreach (MonitorInfo m in monitors)
            log.Line($"monitor {m.DeviceName} {m.Bounds} work {m.WorkArea} dpi {m.Dpi} primary {m.IsPrimary}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

/// <summary>Skips unless two attached monitors have different scale settings.</summary>
internal sealed class RealMixedDpiFactAttribute : FactAttribute
{
    public RealMixedDpiFactAttribute()
    {
        uint[] dpis;
        try { dpis = MonitorEnumerator.GetAll().Select(m => m.Dpi).Distinct().ToArray(); }
        catch (Exception) { dpis = []; }
        if (dpis.Length < 2)
            Skip = "Needs two attached monitors with different scale settings; this machine's are all the same.";
    }
}
