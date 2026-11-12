using System.Runtime.InteropServices;
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
/// The pointer crossing between monitors of a shown native selector, driven by synthetic window
/// messages and an injected cursor. The real cursor and keyboard are never touched.
/// </summary>
public sealed class NativeSelectorMonitorCrossingTests
{
    public static TheoryData<int> Layouts => new() { 0, 1, 2, 3, 4, 5, 6 };

    // The selector reports its first frame only after a paint covering the whole HWND, which
    // Windows clips to the real virtual screen (here 4208x1440 at 0,0). Each layout therefore
    // lies inside it; the ones that fit start at (40,20) so screen and DIB coordinates differ.
    private static (RectD Desktop, MonitorInfo[] Monitors) Layout(int index) => index switch
    {
        // Secondary at 150% left of and taller than the primary, gaps above and below the primary.
        0 => Shift(new RectD(0, 0, 3200, 1200), [
            new MonitorInfo("left", new RectD(0, 0, 1600, 1200), new RectD(0, 0, 1600, 1152), 144, false),
            new MonitorInfo("primary", new RectD(1600, 300, 1600, 900), new RectD(1600, 300, 1600, 852), 96, true)]),
        // Secondary at 125% above the primary, narrower: an L-shaped desktop.
        1 => Shift(new RectD(0, 0, 1600, 1400), [
            new MonitorInfo("top", new RectD(0, 0, 1280, 600), new RectD(0, 0, 1280, 600), 120, false),
            new MonitorInfo("primary", new RectD(0, 600, 1600, 800), new RectD(0, 600, 1600, 752), 96, true)]),
        // Portrait 175% on the right, extending above the primary.
        2 => Shift(new RectD(0, 0, 2500, 1400), [
            new MonitorInfo("primary", new RectD(0, 300, 1600, 900), new RectD(0, 300, 1600, 852), 96, true),
            new MonitorInfo("portrait", new RectD(1600, 0, 900, 1400), new RectD(1600, 0, 900, 1352), 168, false)]),
        // Different resolutions at one scale, as reported: a 3440x1440 primary and a 768x1366
        // portrait monitor on its right. Below the portrait (x 3440-4207, y 1366-1439) is no monitor.
        4 => (new RectD(0, 0, 4208, 1440), [
            new MonitorInfo("primary", new RectD(0, 0, 3440, 1440), new RectD(0, 0, 3440, 1392), 96, true),
            new MonitorInfo("portrait", new RectD(3440, 0, 768, 1366), new RectD(3440, 0, 768, 1366), 96, false)]),
        // The small monitor is the primary, bottom-aligned on the right of a large one.
        5 => (new RectD(0, 0, 4208, 1440), [
            new MonitorInfo("large", new RectD(0, 0, 3440, 1440), new RectD(0, 0, 3440, 1440), 96, false),
            new MonitorInfo("primary", new RectD(3440, 74, 768, 1366), new RectD(3440, 74, 768, 1318), 96, true)]),
        // A 1920x600 primary under a wider 2560x800 monitor, centred: gaps left and right of the primary.
        6 => Shift(new RectD(0, 0, 2560, 1400), [
            new MonitorInfo("top", new RectD(0, 0, 2560, 800), new RectD(0, 0, 2560, 800), 96, false),
            new MonitorInfo("primary", new RectD(320, 800, 1920, 600), new RectD(320, 800, 1920, 552), 96, true)]),
        // Three monitors at 100/125/150% with a taskbar on the left of the middle one.
        _ => Shift(new RectD(0, 0, 3600, 900), [
            new MonitorInfo("left", new RectD(0, 0, 1200, 800), new RectD(0, 0, 1200, 800), 96, false),
            new MonitorInfo("middle", new RectD(1200, 0, 1200, 900), new RectD(1260, 0, 1140, 900), 120, true),
            new MonitorInfo("right", new RectD(2400, 0, 1200, 700), new RectD(2400, 0, 1200, 652), 144, false)]),
    };

    private static (RectD, MonitorInfo[]) Shift(RectD desktop, MonitorInfo[] monitors)
    {
        static RectD Move(RectD r) => new(r.Left + 40, r.Top + 20, r.Width, r.Height);
        return (Move(desktop), monitors.Select(m => m with { Bounds = Move(m.Bounds), WorkArea = Move(m.WorkArea) }).ToArray());
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void PointerMovesAndDragsAcrossEveryMonitorKeepTheSelectorAlive(int layout) =>
        StaTestHost.Run(() => Exercise(layout, emulateDpi: false));

    /// <summary>
    /// This machine and CI run every monitor at one DPI, so Windows never sends WM_DPICHANGED to the
    /// chrome HWNDs. The emulator sends it the way Windows does for a per-monitor-aware window: when a
    /// move puts most of the window on a monitor of another DPI, inside that SetWindowPos, with the
    /// requested position and the size scaled by the DPI ratio as the suggested rectangle.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void ChromeWindowsCrossingMixedDpiSeamsKeepTheSelectorAlive(int layout) => StaTestHost.Run(() =>
    {
        using var dpi = TestDpiContext.AtPerMonitorDpi();
        Exercise(layout, emulateDpi: true);
    });

    /// <summary>The monitors of the machine running the test, read but never changed; only the cursor is injected.</summary>
    [Fact]
    public void PointerMovesAcrossThisMachinesMonitorsKeepTheSelectorAlive() => StaTestHost.Run(() =>
    {
        MonitorInfo[] monitors = MonitorEnumerator.GetAll().ToArray();
        double left = monitors.Min(m => m.Bounds.Left), top = monitors.Min(m => m.Bounds.Top);
        double right = monitors.Max(m => m.Bounds.Right), bottom = monitors.Max(m => m.Bounds.Bottom);
        Exercise(new RectD(left, top, right - left, bottom - top), monitors, -1, emulateDpi: false);
    });

    /// <summary>The WPF selector, used when the native one fails, under the same pointer sweep.</summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void WpfSelectorPointerMovesAcrossEveryMonitorKeepItOpen(int layout) => StaTestHost.Run(() =>
    {
        (RectD desktop, MonitorInfo[] monitors) = Layout(layout);
        foreach (bool recording in new[] { false, true })
        {
            PointD cursor = Center(monitors[0].Bounds);
            int width = (int)desktop.Width, height = (int)desktop.Height;
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, new byte[width * height * 4], width * 4);
            bitmap.Freeze();
            var window = new CaptureOverlayWindow(new FrozenFrame(bitmap, desktop, null, 0), abortOnFocusLoss: false, showMagnifier: true)
            {
                DetectWindows = true,
                SourceWindows = Windows(desktop, monitors),
                ShowKeyPanel = !recording,
                OffersOtherActions = true,
                RecordingSelection = recording,
            };
            var view = Assert.IsType<CaptureOverlayView>(window.Content);
            view.ReadCursor = () => cursor;
            view.ReadMonitors = () => monitors;
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show();
                Pump();
                foreach (PointD point in Sweep(monitors))
                {
                    cursor = point;
                    view.SimulatePointerMove();
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    do Pump(); while (elapsed.ElapsedMilliseconds < 3);
                    Assert.False(closed, $"WPF selector closed at {point} (layout {layout}, recording {recording})");
                }
            }
            finally { window.Close(); NativeSelectorChromeCache.Trim(); }
        }
    });

    private static void Exercise(int layout, bool emulateDpi)
    {
        (RectD desktop, MonitorInfo[] monitors) = Layout(layout);
        Exercise(desktop, monitors, layout, emulateDpi);
    }

    private static void Exercise(RectD desktop, MonitorInfo[] monitors, int layout, bool emulateDpi)
    {
        NativeSelectorChromeCache.Trim();
        // Capture with the key panel, capture with the first-use start guide, and recording.
        foreach ((bool recording, bool startGuide) in new[] { (false, false), (false, true), (true, false) })
        {
            PointD cursor = Center(monitors[0].Bounds);
            using NativeCaptureOverlay overlay = Create(desktop, monitors, () => cursor, recording, startGuide);
            var failures = new List<Exception>();
            overlay.Failed += failures.Add;
            var emulator = emulateDpi ? new DpiEmulator(monitors) : null;
            void Move(NativeCaptureOverlay selector, bool paced = false)
            {
                if (selector.Chrome is { } chrome) emulator?.Attach(chrome.Windows);
                _ = SendMessage(selector.Handle, 0x0200, IntPtr.Zero, IntPtr.Zero);
                // A hand-moved pointer leaves the glide (8 ms) and hover-tab (1 ms) timers time to fire.
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                do Pump(); while (paced && elapsed.ElapsedMilliseconds < 6);
                if (selector.Chrome is { } after) emulator?.Attach(after.Windows);
            }
            try
            {
                overlay.Show();
                PumpUntil(() => overlay.IsReady && overlay.IsChromeReady);
                foreach (PointD point in Sweep(monitors))
                {
                    cursor = point;
                    Move(overlay, paced: true);
                    Assert.False(overlay.IsClosed, $"Closed at {point} (layout {layout}, recording {recording}, guide {startGuide}): {string.Join(" | ", failures)}");
                }
                // Drags that start on one monitor and end on every other one.
                foreach (MonitorInfo from in monitors)
                    foreach (MonitorInfo to in monitors)
                    {
                        cursor = Center(from.Bounds);
                        Move(overlay);
                        // BeginSelection is WM_LBUTTONDOWN without SetCapture on the real mouse.
                        overlay.BeginSelection(new PointD(cursor.X - desktop.Left, cursor.Y - desktop.Top));
                        foreach (PointD point in Path(Center(from.Bounds), Corner(to.Bounds)))
                        {
                            cursor = point;
                            Move(overlay);
                        }
                        _ = SendMessage(overlay.Handle, 0x0215, IntPtr.Zero, IntPtr.Zero);
                        Pump();
                        Assert.False(overlay.IsClosed, $"Closed dragging {from.DeviceName}->{to.DeviceName}: {string.Join(" | ", failures)}");
                    }
                Assert.Empty(failures);
                if (emulator is not null && monitors.Select(m => m.Dpi).Distinct().Count() > 1) Assert.True(emulator.Changes > 0, "The emulator never crossed a DPI seam.");
            }
            // The next session reuses these chrome windows from where this one left them, as the
            // retained cache does between real captures.
            finally { overlay.Close(); emulator?.Detach(); }
        }
        NativeSelectorChromeCache.Trim();
    }

    private sealed class DpiEmulator(IReadOnlyList<MonitorInfo> monitors)
    {
        private readonly Dictionary<IntPtr, uint> _dpi = [];
        private readonly List<HwndSource> _sources = [];
        internal int Changes { get; private set; }

        internal void Attach(IEnumerable<System.Windows.Window> windows)
        {
            foreach (System.Windows.Window window in windows)
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero || _dpi.ContainsKey(hwnd) || HwndSource.FromHwnd(hwnd) is not { } source) continue;
                _dpi[hwnd] = (uint)Math.Round(source.CompositionTarget.TransformToDevice.M11 * 96);
                source.AddHook(Hook);
                _sources.Add(source);
            }
        }

        internal void Detach()
        {
            foreach (HwndSource source in _sources) if (!source.IsDisposed) source.RemoveHook(Hook);
            _sources.Clear();
        }

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0047 || !_dpi.TryGetValue(hwnd, out uint current)) return IntPtr.Zero; // WM_WINDOWPOSCHANGED
            WindowPosition position = Marshal.PtrToStructure<WindowPosition>(lParam);
            if ((position.Flags & 0x0003) == 0x0003) return IntPtr.Zero; // Neither moved nor sized.
            var rect = new RectD(position.X, position.Y, Math.Max(1, position.Width), Math.Max(1, position.Height));
            MonitorInfo? best = null;
            double bestArea = 0;
            foreach (MonitorInfo monitor in monitors)
            {
                double w = Math.Min(rect.Right, monitor.Bounds.Right) - Math.Max(rect.Left, monitor.Bounds.Left);
                double h = Math.Min(rect.Bottom, monitor.Bounds.Bottom) - Math.Max(rect.Top, monitor.Bounds.Top);
                if (w > 0 && h > 0 && w * h > bestArea) { bestArea = w * h; best = monitor; }
            }
            if (best is null || best.Dpi == current) return IntPtr.Zero;
            _dpi[hwnd] = best.Dpi;
            Changes++;
            double ratio = best.Dpi / (double)current;
            var suggested = new NativeRect
            {
                Left = position.X, Top = position.Y,
                Right = position.X + (int)Math.Round(position.Width * ratio),
                Bottom = position.Y + (int)Math.Round(position.Height * ratio),
            };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            try
            {
                Marshal.StructureToPtr(suggested, buffer, false);
                _ = SendMessage(hwnd, 0x02E0, new IntPtr((int)(best.Dpi | (best.Dpi << 16))), buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, After; public int X, Y, Width, Height; public uint Flags; }

    /// <summary>Only points the real cursor can reach: GetCursorPos never reports a point off every monitor.</summary>
    private static IEnumerable<PointD> Sweep(IReadOnlyList<MonitorInfo> monitors) =>
        RawSweep(monitors).Where(p => monitors.Any(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y >= m.Bounds.Top && p.Y < m.Bounds.Bottom));

    private static IEnumerable<PointD> RawSweep(IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (MonitorInfo monitor in monitors)
        {
            RectD b = monitor.Bounds;
            yield return Center(b);
            yield return new PointD(b.Left, b.Top);
            yield return new PointD(b.Right - 1, b.Top);
            yield return new PointD(b.Left, b.Bottom - 1);
            yield return new PointD(b.Right - 1, b.Bottom - 1);
            yield return new PointD(b.Left + 1, b.Top + b.Height / 2);
            yield return new PointD(b.Right - 2, b.Top + b.Height / 2);
        }
        for (int a = 0; a < monitors.Count; a++)
            for (int b = 0; b < monitors.Count; b++)
                if (a != b)
                {
                    foreach (PointD point in Path(Center(monitors[a].Bounds), Center(monitors[b].Bounds)))
                        yield return point;
                    foreach (PointD point in Path(Corner(monitors[a].Bounds), Corner(monitors[b].Bounds)))
                        yield return point;
                }
        // Pixel steps across every edge: the seam, the bottom row of a shorter monitor, and the
        // rows of a taller monitor that face no neighbour.
        foreach (MonitorInfo monitor in monitors)
        {
            RectD b = monitor.Bounds;
            foreach (double y in new[] { b.Top, b.Top + 1, b.Top + b.Height / 2, b.Bottom - 2, b.Bottom - 1 })
                for (int d = -16; d <= 16; d++)
                {
                    yield return new PointD(b.Right - 1 + d, y);
                    yield return new PointD(b.Left + d, y);
                }
            foreach (double x in new[] { b.Left, b.Left + b.Width / 2, b.Right - 1 })
                for (int d = -16; d <= 16; d++)
                {
                    yield return new PointD(x, b.Bottom - 1 + d);
                    yield return new PointD(x, b.Top + d);
                }
        }
    }

    private static IEnumerable<PointD> Path(PointD from, PointD to)
    {
        for (int step = 0; step <= 24; step++)
        {
            double t = step / 24d;
            yield return new PointD(Math.Round(from.X + (to.X - from.X) * t), Math.Round(from.Y + (to.Y - from.Y) * t));
        }
    }

    private static PointD Center(RectD bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    private static PointD Corner(RectD bounds) => new(bounds.Right - 3, bounds.Bottom - 3);

    private static NativeCaptureOverlay Create(RectD desktop, MonitorInfo[] monitors, Func<PointD> cursor, bool recording, bool startGuide)
    {
        int width = (int)desktop.Width, height = (int)desktop.Height;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        var overlay = new NativeCaptureOverlay(desktop, false, true)
        {
            PlaceCursor = _ => false,
            ReadCursor = cursor,
            ReadMonitors = () => monitors,
            SourceWindows = Windows(desktop, monitors),
            DetectWindows = true,
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            ShowKeyPanel = !recording,
            ShowStartGuide = startGuide,
            RecordingSelection = recording,
            // As CaptureOverlayCoordinator shows it: the mode bar with its other actions.
            OffersOtherActions = true,
            PinSelectionAvailable = !recording,
        };
        overlay.AttachFrame(new FrozenFrame(bitmap, desktop, null, 0));
        return overlay;
    }

    private static List<WindowCandidate> Windows(RectD desktop, MonitorInfo[] monitors)
    {
        var windows = new List<WindowCandidate>();
        foreach (MonitorInfo monitor in monitors)
        {
            RectD b = monitor.Bounds;
            windows.Add(new WindowCandidate(new IntPtr(100 + windows.Count), new RectD(b.Left + 40, b.Top + 40, b.Width / 3, b.Height / 3), "Small " + monitor.DeviceName));
            windows.Add(new WindowCandidate(new IntPtr(100 + windows.Count), new RectD(b.Left - 8, b.Top - 8, b.Width + 16, monitor.WorkArea.Height + 16), "Maximized " + monitor.DeviceName));
        }
        // A window straddling the first seam and the desktop behind everything.
        RectD first = monitors[0].Bounds;
        windows.Insert(0, new WindowCandidate(new IntPtr(90), new RectD(first.Right - 300, first.Top + 100, 600, 400), "Straddling"));
        windows.Add(new WindowCandidate(new IntPtr(99), desktop, "Desktop", ClassName: "Progman"));
        return windows;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
