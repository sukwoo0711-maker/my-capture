using System.Windows;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>Empty desktop under window auto-detect selects the pointer's monitor, not the virtual desktop.</summary>
public sealed class WindowDetectDesktopTests
{
    private const uint Explorer = 7972;

    // Negative-origin layout: a 150% 2560x1440 display left of a 100% 1920x1080 primary, so the
    // space below the primary lies inside the virtual desktop but outside every monitor.
    private static readonly RectD VirtualDesktop = new(-2560, 0, 4480, 1440);
    private static readonly MonitorInfo LeftMonitor = new(@"\\.\DISPLAY2",
        new RectD(-2560, 0, 2560, 1440), new RectD(-2560, 0, 2560, 1392), 144, IsPrimary: false);
    private static readonly MonitorInfo PrimaryMonitor = new(@"\\.\DISPLAY1",
        new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1032), 96, IsPrimary: true);
    private static readonly IReadOnlyList<MonitorInfo> Monitors = [LeftMonitor, PrimaryMonitor];

    private static readonly WindowCandidate Progman = new(new IntPtr(0x1017E), VirtualDesktop, "Program Manager",
        ProcessId: Explorer, ClassName: "Progman");
    private static readonly WindowCandidate Taskbar = new(new IntPtr(0x1013A), new RectD(0, 1032, 1920, 48),
        ProcessId: Explorer, ClassName: "Shell_TrayWnd");
    private static readonly WindowCandidate Notepad = new(new IntPtr(0x2001), new RectD(-300, 100, 900, 600),
        "report.txt - Notepad", ProcessId: 4242, ClassName: "Notepad");

    [Fact]
    public void DesktopShellIsProgmanOrAWorkerWOwnedByTheSameProcess()
    {
        var shellWorker = new WindowCandidate(new IntPtr(0x3001), VirtualDesktop, ProcessId: Explorer, ClassName: "WorkerW");
        var foreignWorker = new WindowCandidate(new IntPtr(0x3002), new RectD(0, 0, 300, 200), "Tool",
            ProcessId: 5150, ClassName: "WorkerW");
        var unknownWorker = new WindowCandidate(new IntPtr(0x3003), VirtualDesktop, ClassName: "WorkerW");
        var secondaryTaskbar = new WindowCandidate(new IntPtr(0x10166), new RectD(1920, 1032, 768, 48),
            ProcessId: Explorer, ClassName: "Shell_SecondaryTrayWnd");
        IReadOnlyList<WindowCandidate> snapshot = [Notepad, Taskbar, secondaryTaskbar, foreignWorker, shellWorker, Progman];

        Assert.True(WindowCandidateService.IsDesktopShell(Progman, snapshot));
        Assert.True(WindowCandidateService.IsDesktopShell(Progman, []));
        Assert.True(WindowCandidateService.IsDesktopShell(Progman with { ClassName = "PROGMAN" }, snapshot));
        Assert.True(WindowCandidateService.IsDesktopShell(shellWorker, snapshot));
        Assert.False(WindowCandidateService.IsDesktopShell(shellWorker, [Notepad, shellWorker]));
        Assert.False(WindowCandidateService.IsDesktopShell(foreignWorker, snapshot));
        Assert.False(WindowCandidateService.IsDesktopShell(unknownWorker, snapshot));
        Assert.False(WindowCandidateService.IsDesktopShell(Taskbar, snapshot));
        Assert.False(WindowCandidateService.IsDesktopShell(secondaryTaskbar, snapshot));
        Assert.False(WindowCandidateService.IsDesktopShell(Notepad, snapshot));
        Assert.False(WindowCandidateService.IsDesktopShell(new WindowCandidate(new IntPtr(9), VirtualDesktop), snapshot));
    }

    [Theory]
    [InlineData(-1, 500, -2560, 0, 2560, 1440)] // Last pixel of the negative-coordinate display.
    [InlineData(0, 500, 0, 0, 1920, 1080)] // A shared edge belongs to the display on its right.
    [InlineData(-2560, 0, -2560, 0, 2560, 1440)]
    [InlineData(1919, 1079, 0, 0, 1920, 1080)]
    [InlineData(1920, 500, 0, 0, 1920, 1080)] // Past the far edge: nearest display.
    [InlineData(1500, 1300, 0, 0, 1920, 1080)] // Below the shorter display: nearest is above it.
    [InlineData(10, 1430, -2560, 0, 2560, 1440)] // Nearest is the taller display to the left.
    public void PointerMonitorIsTheContainingOrNearestDisplayAtFullBounds(
        double x, double y, double left, double top, double width, double height) =>
        Assert.Equal(new RectD(left, top, width, height), WindowDetectHitTest.FindMonitorBounds(Monitors, new PointD(x, y)));

    [Fact]
    public void NoDisplaysMeansNoMonitor() =>
        Assert.Null(WindowDetectHitTest.FindMonitorBounds([], new PointD(0, 0)));

    [Fact]
    public void DesktopResolvesToThePointerMonitorAndOtherWindowsKeepTheirRectangle()
    {
        IReadOnlyList<WindowCandidate> zOrder = [Notepad, Taskbar, Progman];
        int reads = 0;
        IReadOnlyList<MonitorInfo> ReadMonitors()
        {
            reads++;
            return Monitors;
        }

        RectD? Resolve(PointD pointer, out bool wholeMonitor)
        {
            WindowCandidate window = WindowDetectHitTest.FindTopmost(zOrder, pointer)!;
            return WindowDetectHitTest.ResolveRegion(window, zOrder, pointer, VirtualDesktop, ReadMonitors, out wholeMonitor);
        }

        // Ordinary windows, the taskbar included, never read the display layout.
        Assert.Equal(new RectD(2260, 100, 900, 600), Resolve(new PointD(100, 300), out bool whole));
        Assert.False(whole);
        Assert.Equal(new RectD(2560, 1032, 1920, 48), Resolve(new PointD(1000, 1050), out whole));
        Assert.False(whole);
        Assert.Equal(0, reads);

        // Empty desktop: the monitor's full bounds (not its work area) in bitmap pixels.
        Assert.Equal(new RectD(0, 0, 2560, 1440), Resolve(new PointD(-2000, 1200), out whole));
        Assert.True(whole);
        Assert.Equal(new RectD(2560, 0, 1920, 1080), Resolve(new PointD(1500, 900), out whole));
        Assert.True(whole);
        Assert.Equal(new RectD(2560, 0, 1920, 1080), Resolve(new PointD(0, 0), out whole));
        Assert.True(whole);
        Assert.Equal(3, reads);

        // Without a display layout the desktop keeps the previous whole-window behaviour.
        Assert.Equal(new RectD(0, 0, 4480, 1440),
            WindowDetectHitTest.ResolveRegion(Progman, zOrder, new PointD(-2000, 1200), VirtualDesktop, () => [], out whole));
        Assert.False(whole);
    }

    [Fact]
    public void MixedDpiMonitorsKeepExactPhysicalPixels()
    {
        // 100% 1080p primary beside a 200% 4K display that starts 540 pixels higher.
        var desktop = new RectD(0, -540, 5760, 2160);
        IReadOnlyList<MonitorInfo> monitors =
        [
            new(@"\\.\DISPLAY1", new RectD(0, 0, 1920, 1080), new RectD(0, 0, 1920, 1032), 96, IsPrimary: true),
            new(@"\\.\DISPLAY2", new RectD(1920, -540, 3840, 2160), new RectD(1920, -540, 3840, 2112), 192, IsPrimary: false),
        ];
        var shell = Progman with { ScreenBounds = desktop };

        Assert.Equal(new RectD(1920, 0, 3840, 2160),
            WindowDetectHitTest.ResolveRegion(shell, [shell], new PointD(2000, -500), desktop, () => monitors, out bool whole));
        Assert.True(whole);
        Assert.Equal(new RectD(0, 540, 1920, 1080),
            WindowDetectHitTest.ResolveRegion(shell, [shell], new PointD(100, 100), desktop, () => monitors, out whole));
        Assert.True(whole);
    }

    [Fact]
    public void ShellCaptionIsNeverASourceTitle()
    {
        IReadOnlyList<WindowCandidate> zOrder = [Notepad, Progman];
        // Centre over empty desktop: no source instead of "Program Manager".
        Assert.Equal(string.Empty, WindowCandidateService.ResolveSourceTitle(zOrder, new RectD(900, 800, 200, 100)));
        // Centre over empty desktop, partly over a window: the documented largest-overlap fallback.
        Assert.Equal(Notepad.Title, WindowCandidateService.ResolveSourceTitle(zOrder, new RectD(550, 650, 200, 200)));
        Assert.Equal(Notepad.Title, WindowCandidateService.ResolveSourceTitle(zOrder, new RectD(0, 200, 100, 100)));
        // Another process's titled WorkerW stays an ordinary source window.
        var foreignWorker = new WindowCandidate(new IntPtr(0x3002), new RectD(800, 700, 400, 300), "Tool",
            ProcessId: 5150, ClassName: "WorkerW");
        Assert.Equal("Tool", WindowCandidateService.ResolveSourceTitle([foreignWorker, Progman], new RectD(900, 800, 200, 100)));
    }

    [Fact]
    public void HoveringEmptyDesktopHighlightsAndClickSelectsThePointerMonitor() => StaTestHost.Run(() =>
    {
        // The 640x360 test frame: a 400x360 display and a shorter 240x200 display at 150%.
        IReadOnlyList<MonitorInfo> monitors =
        [
            new(@"\\.\DISPLAY1", new RectD(0, 0, 400, 360), new RectD(0, 0, 400, 330), 96, IsPrimary: true),
            new(@"\\.\DISPLAY2", new RectD(400, 0, 240, 200), new RectD(400, 0, 240, 180), 144, IsPrimary: false),
        ];
        var editor = new WindowCandidate(new IntPtr(11), new RectD(40, 30, 300, 200), "Editor", ProcessId: 4242, ClassName: "Notepad");
        var desk = new WindowCandidate(new IntPtr(12), new RectD(0, 0, 640, 360), "Program Manager",
            ProcessId: Explorer, ClassName: "Progman");
        PointD pointer = new(500, 100);
        int reads = 0;
        var view = new CaptureOverlayView(Overlay.Frame(), showMagnifier: false)
        {
            ReadCursor = () => pointer,
            DetectWindows = true,
            ReadMonitors = () => { reads++; return monitors; },
            WindowCandidates = [editor, desk],
        };
        RegionSelectionEventArgs? confirmed = null;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        try
        {
            view.InitializePointer();
            Assert.Equal(new RectD(400, 0, 240, 200), view.HoverRegionForTest);
            Assert.True(view.HoverWholeMonitorForTest);

            // The size label stays on the shorter display instead of the unseen space below it.
            Overlay.Render(view);
            Rect drawn = Overlay.Visual(view, "_selectionVisual").ContentBounds;
            Assert.False(drawn.IsEmpty);
            Assert.True(drawn.Left >= 399.5 && drawn.Top >= -0.5 && drawn.Right <= 640.5 && drawn.Bottom <= 200.5,
                drawn.ToString());

            pointer = new PointD(380, 340);
            view.UpdatePointer(false, false);
            Assert.Equal(new RectD(0, 0, 400, 360), view.HoverRegionForTest);
            Assert.True(view.HoverWholeMonitorForTest);

            pointer = new PointD(100, 100);
            view.UpdatePointer(false, false);
            Assert.Equal(new RectD(40, 30, 300, 200), view.HoverRegionForTest);
            Assert.False(view.HoverWholeMonitorForTest);

            pointer = new PointD(500, 100);
            view.UpdatePointer(false, false);
            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(502, 101); // Hand jitter inside the system drag threshold.
            Overlay.Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
            Overlay.PumpUntil(() => confirmed is not null);

            Assert.Equal(new RectD(400, 0, 240, 200), confirmed!.BitmapRegion);
            Assert.Same(desk, confirmed.DetectedWindow);
            Assert.Equal(1, reads); // One display snapshot per selector.
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void DesktopClickHasNoSourceTitleWhileWindowClicksKeepTheirs() => StaTestHost.Run(() =>
    {
        var browser = new WindowCandidate(new IntPtr(21), new RectD(100, 50, 400, 300), "Docs", "https://example.com/doc",
            ProcessId: 4242, ClassName: "Chrome_WidgetWin_1");
        var desk = new WindowCandidate(new IntPtr(12), new RectD(0, 0, 640, 360), "Program Manager",
            ProcessId: Explorer, ClassName: "Progman");
        var selections = new List<CaptureSelectionCompletedEventArgs>();

        // The browser covers the monitor centre, but the user clicked the desktop.
        var desktopClick = new CaptureOverlayWindow(Overlay.Frame(), abortOnFocusLoss: false) { SourceWindows = [browser, desk] };
        desktopClick.SelectionCompleted += (_, e) => selections.Add(e);
        desktopClick.CompleteSelection(new RectD(0, 0, 640, 360), desk, textCapture: false);
        var windowClick = new CaptureOverlayWindow(Overlay.Frame(), abortOnFocusLoss: false) { SourceWindows = [browser, desk] };
        windowClick.SelectionCompleted += (_, e) => selections.Add(e);
        windowClick.CompleteSelection(new RectD(100, 50, 400, 300), browser, textCapture: false);

        Assert.Equal(string.Empty, selections[0].SourceTitle);
        Assert.Equal(string.Empty, selections[0].SourcePageUrl);
        Assert.Equal(new RectD(0, 0, 640, 360), selections[0].BitmapRegion);
        Assert.Equal("Docs", selections[1].SourceTitle);
        Assert.Equal("https://example.com/doc", selections[1].SourcePageUrl);
    });
}
