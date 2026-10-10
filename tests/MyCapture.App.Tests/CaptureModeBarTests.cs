using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The screenshot selector's mode bar and start guide: what each button does, that presses on the bar
/// never start a capture, where the bar sits on one of several monitors, and that none of it reaches
/// the captured pixels.
/// </summary>
public sealed class CaptureModeBarTests
{
    [Fact]
    public void BarButtonsAndKeysSwitchTheSameModeAndTheBarFollows() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        CaptureOverlayView view = EvidenceView(Desktop(), AppTheme.Glass, "window-hover");
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.True(bar.CaptureButton.IsChecked);
            Assert.Equal("영역·창", AutomationProperties.GetName(bar.CaptureButton));

            // Mouse or UI Automation: selecting the text button is the T key's text mode.
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.TextButton)).Select();
            Assert.Equal(CaptureSelectorMode.Text, view.Mode);
            Assert.True(view.TextCaptureMode);
            Assert.False(bar.CaptureButton.IsChecked);
            Render(view);
            Assert.Equal(["드래그: 영역 텍스트 추출", "클릭: 창 텍스트 추출"], view.StatusTextsForTest);
            Assert.Equal("클릭: 창 텍스트 추출  620 × 420", view.TargetLabelForTest);

            // T from the keyboard goes back, and the bar shows it.
            view.RaiseEvent(Overlay.KeyArgs(Key.T));
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.True(bar.CaptureButton.IsChecked);

            // Enter on a focused bar button presses it, like Space.
            KeyEventArgs enter = Overlay.KeyArgs(Key.Enter);
            bar.ColorButton.RaiseEvent(enter);
            Assert.True(enter.Handled);
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);
            Assert.True(bar.ColorButton.IsChecked);
            Assert.Null(view.HoverRegionForTest);
            Render(view);
            Assert.Equal(["클릭: 색상 복사"], view.StatusTextsForTest);
            Assert.Null(view.TargetTabForTest);

            // From colour, T goes straight to text; the image button returns to capture.
            view.RaiseEvent(Overlay.KeyArgs(Key.T));
            Assert.Equal(CaptureSelectorMode.Text, view.Mode);
            bar.CaptureButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.NotNull(view.HoverRegionForTest);

            // Arrows on a focused bar button are left to keyboard navigation along the bar.
            KeyEventArgs arrow = Overlay.KeyArgs(Key.Right);
            bar.CaptureButton.RaiseEvent(arrow);
            Assert.False(arrow.Handled);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ColorModeCopiesThroughTheCKeyPathAndNeverCaptures() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var copied = new List<string>();
        PointD pointer = new(300, 300);
        CaptureOverlayView view = BarView(Desktop(), () => pointer);
        view.CopyColorText = text => { copied.Add(text); return Task.FromResult(true); };
        int confirmed = 0;
        view.SelectionConfirmed += (_, _) => confirmed++;
        try
        {
            view.InitializePointer();
            Render(view);
            view.ModeBarForTest!.ColorButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);

            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
            Overlay.Pump();

            string expected = ColorSampleFormatter.Format(0xF7, 0xF8, 0xFA, ColorFormat.Hex);
            Assert.Equal([expected], copied);
            Assert.Equal(UiText.Format("Tools_ColorCopied", expected), view.ToastMessageForTest);
            Assert.Null(view.Selection);
            Assert.Equal(0, confirmed);
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);

            // Without a colour handler the button is not offered and the mode cannot be chosen.
            view.CopyColorText = null;
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            view.SelectMode(CaptureSelectorMode.Color);
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.Equal(Visibility.Collapsed, view.ModeBarForTest.ColorButton.Visibility);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void FullScreenCancelAndHelpButtonsUseTheSelectorsOwnPaths() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        PointD pointer = new(300, 300);
        CaptureOverlayView view = BarView(Desktop(), () => pointer);
        RegionSelectionEventArgs? confirmed = null;
        int cancelled = 0;
        view.SelectionConfirmed += (_, e) => confirmed = e;
        view.CancelRequested += (_, _) => cancelled++;
        try
        {
            view.InitializePointer();
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;

            // Help (here through UI Automation) shows the start guide, and again hides it.
            Assert.False(view.ShowStartGuide);
            ((IInvokeProvider)new ButtonAutomationPeer(bar.HelpButton)).Invoke();
            Overlay.PumpUntil(() => view.ShowStartGuide);
            Assert.Equal(Visibility.Visible, view.StartGuideCardForTest!.Visibility);
            bar.HelpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(view.ShowStartGuide);

            // Full screen confirms the bar's whole monitor in the current mode, like a desktop click.
            view.SelectMode(CaptureSelectorMode.Text);
            bar.FullScreenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.NotNull(confirmed);
            Assert.Equal(new RectD(0, 0, Width, Height), confirmed!.BitmapRegion);
            Assert.True(confirmed.WholeMonitor);
            Assert.True(confirmed.TextCapture);
            Assert.Null(confirmed.DetectedWindow);
        }
        finally { view.ReleaseResources(); }

        CaptureOverlayView second = BarView(Desktop(), () => pointer);
        second.CancelRequested += (_, _) => cancelled++;
        try
        {
            second.InitializePointer();
            Render(second);
            second.ModeBarForTest!.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, cancelled);
            Assert.Equal("Esc", AutomationProperties.GetAcceleratorKey(second.ModeBarForTest.CancelButton));
            Assert.Equal(UiText.Get("CaptureGuide2_Cancel"), AutomationProperties.GetName(second.ModeBarForTest.CancelButton));
        }
        finally { second.ReleaseResources(); }
    });

    [Fact]
    public void PressesOnTheBarNeverDetectAWindowOrStartACapture() => StaTestHost.Run(() =>
    {
        PointD pointer = new(300, 300);
        CaptureOverlayView view = BarView(Desktop(), () => pointer);
        int confirmed = 0;
        view.SelectionConfirmed += (_, _) => confirmed++;
        try
        {
            view.InitializePointer();
            Render(view);
            Rect bar = view.ModeBarBoundsForTest!.Value;
            // Over the bar, empty desktop would otherwise highlight the whole monitor.
            pointer = new PointD(bar.Left + 4, bar.Top + (bar.Height / 2));
            view.UpdatePointer(false, false);
            Assert.True(view.PointerOverModeBarForTest);
            Assert.Null(view.HoverRegionForTest);
            Assert.Equal(CaptureCueState.None, view.CueState);
            Render(view);
            Assert.True(Overlay.Visual(view, "_pointerVisual").ContentBounds.IsEmpty);

            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(bar.Left + 200, bar.Bottom + 150);
            Overlay.Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
            Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
            Overlay.Pump();
            Assert.Null(view.Selection);
            Assert.Equal(0, confirmed);

            // A press that lands on the bar's own padding, wherever the pointer reading says it is. WPF
            // bubbles MouseDown and raises the left-button event on each element of its route.
            pointer = new PointD(300, 300);
            view.UpdatePointer(false, false);
            Assert.False(view.PointerOverModeBarForTest);
            MouseButtonEventArgs padding = Overlay.Button(Mouse.MouseDownEvent);
            ((Decorator)view.ModeBarForTest!).Child.RaiseEvent(padding);
            Assert.True(padding.Handled);
            Assert.Null(view.Selection);

            // A press on a bar button belongs to the button.
            MouseButtonEventArgs onButton = Overlay.Button(Mouse.MouseDownEvent);
            view.ModeBarForTest.FullScreenButton.RaiseEvent(onButton);
            Assert.True(onButton.Handled);
            Assert.Null(view.Selection);
            Assert.Equal(0, confirmed);

            // Off the bar the same press still starts the usual window click.
            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
            Overlay.PumpUntil(() => confirmed == 1);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void StatusNamesTheMonitorOverTheDesktopAndLightsTheDragWhileDragging() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        CaptureOverlayView desktop = EvidenceView(frame, AppTheme.Glass, "desktop-hover");
        CaptureOverlayView window = EvidenceView(frame, AppTheme.Glass, "window-hover");
        CaptureOverlayView drag = EvidenceView(frame, AppTheme.Glass, "drag");
        try
        {
            Render(desktop);
            Render(window);
            Render(drag);
            Assert.Equal(["드래그: 영역 캡처", "클릭: 모니터 캡처"], desktop.StatusTextsForTest);
            Assert.Equal("클릭: 모니터 캡처  1280 × 720", desktop.TargetLabelForTest);
            Assert.Equal(["드래그: 영역 캡처", "클릭: 창 캡처"], window.StatusTextsForTest);
            Assert.Equal(CaptureCueState.Drag, drag.CueState);

            // The status sits under the bar, the same size whichever wording it shows.
            Rect bar = desktop.ModeBarBoundsForTest!.Value;
            Rect status = desktop.ChromeBoundsForTest[1];
            Assert.Equal(bar, desktop.ChromeBoundsForTest[0]);
            Assert.True(status.Top > bar.Bottom);
            Near(bar.Left + (bar.Width / 2), status.Left + (status.Width / 2));
            Assert.Equal(status, window.ChromeBoundsForTest[1]);
            Assert.Equal(status, drag.ChromeBoundsForTest[1]);
            Rect lit = drag.ActiveCueSegmentForTest!.Value;
            Assert.True(status.Contains(lit));
            Near(status.Left + 5, lit.Left);
            // The detected window's tab keeps clear of the bar and the status.
            Rect tab = window.TargetTabForTest!.Value;
            Assert.DoesNotContain(window.ChromeBoundsForTest, chrome => CaptureCueLayout.Overlaps(chrome, tab));
        }
        finally
        {
            desktop.ReleaseResources();
            window.ReleaseResources();
            drag.ReleaseResources();
        }
    });

    [Fact]
    public void EnglishBarAndGuideUsePlainWords() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        CaptureOverlayView view = EvidenceView(Desktop(), AppTheme.Glass, "first-open");
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.Equal("Region or window", AutomationProperties.GetName(bar.CaptureButton));
            Assert.Equal("Full screen", AutomationProperties.GetName(bar.FullScreenButton));
            Assert.Equal("Extract text", AutomationProperties.GetName(bar.TextButton));
            Assert.Equal("Copy color", AutomationProperties.GetName(bar.ColorButton));
            Assert.Equal("How to capture", view.StartGuideCardForTest!.TitleText);
            Assert.StartsWith("Click a window to capture it, or drag", view.StartGuideCardForTest.BodyText, StringComparison.Ordinal);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void StartGuideLeavesOnTravelPressKeyOrTimeAndHonoursReducedMotion() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PointD pointer = new(640, 400);
        CaptureOverlayView view = BarView(Desktop(), () => pointer);
        view.ShowStartGuide = true;
        try
        {
            view.InitializePointer();
            Render(view);
            CaptureStartGuideCard card = view.StartGuideCardForTest!;
            Assert.Equal("캡처 방법", card.TitleText);
            Assert.Equal("창 위에서 클릭하면 그 창을, 드래그하면 원하는 영역을 캡처합니다. Esc로 취소합니다. "
                + "위쪽 막대에서 전체 화면, 텍스트 추출, 색상 복사를 고를 수 있습니다. "
                + "도움말(?) 버튼을 누르면 이 안내를 다시 볼 수 있습니다.", card.BodyText);
            Assert.False(card.IsHitTestVisible);
            Assert.True(view.StartGuideTimerRunningForTest);
            Assert.Equal(TimeSpan.FromSeconds(8), CaptureOverlayView.StartGuideDuration);
            // In the middle of the monitor's work area.
            Rect placed = LayoutInformation.GetLayoutSlot(card);
            Near(Width / 2.0, placed.Left + (placed.Width / 2));
            Near(672 / 2.0, placed.Top + (placed.Height / 2));

            // Small hand movement keeps it; heading somewhere removes it at once (animations off).
            double travel = CaptureOverlayView.StartGuideMoveDismissDip / view.GuidePixelSizeX;
            pointer = new PointD(640 + (travel * 0.6), 400);
            view.UpdatePointer(false, false);
            Assert.True(view.ShowStartGuide);
            pointer = new PointD(640 + travel + 2, 400);
            view.UpdatePointer(false, false);
            Assert.False(view.ShowStartGuide);
            Assert.Equal(Visibility.Collapsed, card.Visibility);
            Assert.False(view.StartGuideTimerRunningForTest);

            // A key (not a modifier) or a press removes it too.
            view.ShowStartGuide = true;
            view.RaiseEvent(Overlay.KeyArgs(Key.LeftShift));
            Assert.True(view.ShowStartGuide);
            view.RaiseEvent(Overlay.KeyArgs(Key.A));
            Assert.False(view.ShowStartGuide);
            view.ShowStartGuide = true;
            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            Assert.False(view.ShowStartGuide);
            Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));

            // The timer is the fourth way out.
            view.ShowStartGuide = true;
            Assert.True(view.StartGuideTimerRunningForTest);
            DispatcherTimer timer = (DispatcherTimer)typeof(CaptureOverlayView)
                .GetField("_startGuideTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            Assert.Equal(CaptureOverlayView.StartGuideDuration, timer.Interval);
            typeof(DispatcherTimer).GetMethod("FireTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(timer, null);
            Assert.False(view.ShowStartGuide);
        }
        finally
        {
            view.ReleaseResources();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    [Fact]
    public void CoordinatorAsksOncePerSelectorAndTheCountIsSavedOnlyAfterTheWelcome() => StaTestHost.Run(() =>
    {
        var settings = new AppSettings();
        var saved = new List<int>();
        var pending = new List<Action>();
        var bookkeeping = new CaptureStartGuideBookkeeping(() => settings, s => saved.Add(s.Capture.StartGuideShownCount),
            pending.Add, NullLogger.Instance);
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            AutoDetectWindows = () => true,
            ClaimStartGuide = bookkeeping.Claim,
            ApplyCaptureExclusion = _ => true,
        };
        MethodInfo show = typeof(CaptureOverlayCoordinator).GetMethod("ShowOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo active = typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var shown = new List<bool>();
        for (int open = 0; open < 4; open++)
        {
            show.Invoke(coordinator, [new RectD(0, 0, 640, 360), false, false, Stopwatch.StartNew(), null, true]);
            var overlay = (CaptureOverlayWindow)active.GetValue(coordinator)!;
            Assert.True(overlay.ShowModeBar);
            shown.Add(overlay.ShowStartGuide);
            coordinator.Cancel();
        }

        Assert.Equal([true, true, true, false], shown);
        Assert.Equal(3, settings.Capture.StartGuideShownCount);
        Assert.Equal(3, pending.Count);
        foreach (Action write in pending) write();
        Assert.Equal([3, 3, 3], saved);

        // A first run keeps the count in memory; the welcome card's save writes it.
        var firstRun = new AppSettings { General = { IsFirstRun = true } };
        pending.Clear();
        saved.Clear();
        var welcome = new CaptureStartGuideBookkeeping(() => firstRun, s => saved.Add(s.Capture.StartGuideShownCount),
            pending.Add, NullLogger.Instance);
        Assert.True(welcome.Claim());
        Assert.Single(pending)();
        Assert.Empty(saved);
        Assert.Equal(1, firstRun.Capture.StartGuideShownCount);

        // A failed write is logged, not thrown into the selector.
        var failing = new CaptureStartGuideBookkeeping(() => new AppSettings(), _ => throw new IOException("locked"),
            write => write(), NullLogger.Instance);
        Assert.True(failing.Claim());
        Assert.False(new CaptureStartGuideBookkeeping(() => null, _ => { }, write => write(), NullLogger.Instance).Claim());
    });

    [Fact]
    public void TheBarSitsOnThePointersMonitorAndKeepsThatMonitorsScale() => StaTestHost.Run(() =>
    {
        const int wide = 2560;
        IReadOnlyList<MonitorInfo> monitors =
        [
            new(@"\\.\DISPLAY1", new RectD(0, 0, 1280, 720), new RectD(0, 0, 1280, 672), 96, IsPrimary: true),
            new(@"\\.\DISPLAY2", new RectD(1280, 0, 1280, 720), new RectD(1280, 0, 1280, 720), 144, IsPrimary: false),
        ];
        FrozenFrame frame = SolidFrame(wide, 720);
        RegionSelectionEventArgs? confirmed = null;
        foreach ((PointD pointer, double center, double scale, RectD monitor) in new[]
        {
            (new PointD(2000, 300), 1920.0, 1.5, new RectD(1280, 0, 1280, 720)),
            (new PointD(200, 300), 640.0, 1.0, new RectD(0, 0, 1280, 720)),
        })
        {
            var view = new CaptureOverlayView(frame, showMagnifier: false)
            {
                ReadCursor = () => pointer,
                ReadMonitors = () => monitors,
                DetectWindows = true,
                TextCaptureAvailable = true,
                CopyColorText = _ => Task.FromResult(true),
                ShowModeBar = true,
            };
            view.SelectionConfirmed += (_, e) => confirmed = e;
            try
            {
                view.InitializePointer();
                RenderAt(view, wide, 720);
                Rect bar = view.ModeBarBoundsForTest!.Value;
                Near(center, bar.Left + (bar.Width / 2));
                Assert.True(bar.Left >= monitor.Left && bar.Right <= monitor.Right, $"{bar} leaves {monitor}");
                Assert.Equal(scale, view.GuideScaleForTest, 3);
                Near(CaptureModeBarLayout.TopMargin * scale, bar.Top);

                // Full screen takes that monitor, not the whole desktop.
                view.ModeBarForTest!.FullScreenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(monitor, confirmed!.BitmapRegion);
            }
            finally { view.ReleaseResources(); }
        }
    });

    [Fact]
    public void CaptureResultsContainNoBarStatusOrGuidePixels() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        var results = new List<CaptureSelectionCompletedEventArgs>();
        PointD pointer = new(640, 400);
        CaptureOverlayWindow Open()
        {
            var window = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false)
            {
                DetectWindows = true,
                SourceWindows = [Browser, Notes, Taskbar, Shell],
                TextCaptureAvailable = true,
                CopyColorText = _ => Task.FromResult(true),
                ShowModeBar = true,
                ShowStartGuide = true,
            };
            var view = Assert.IsType<CaptureOverlayView>(window.Content);
            view.ReadCursor = () => pointer;
            view.ReadMonitors = () => Monitors;
            UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
            window.SelectionCompleted += (_, e) => results.Add(e);
            view.InitializePointer();
            return window;
        }

        CaptureOverlayWindow first = Open();
        var firstView = (CaptureOverlayView)first.Content;
        RenderTargetBitmap shown = Render(firstView);
        // The bar, the status and the guide are really drawn over the frozen desktop.
        Rect bar = firstView.ModeBarBoundsForTest!.Value;
        Rect card = LayoutInformation.GetLayoutSlot(firstView.StartGuideCardForTest!);
        foreach (Point probe in new[] { new Point(bar.Left + 12, bar.Top + 4), new Point(card.Left + 6, card.Top + 6) })
            Assert.NotEqual(Pixel(frame.Bitmap, (int)probe.X, (int)probe.Y), Pixel(shown, (int)probe.X, (int)probe.Y));

        // Full screen: the whole frozen monitor, byte for byte, and no window title as its source.
        firstView.ModeBarForTest!.FullScreenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        CaptureSelectionCompletedEventArgs full = Assert.Single(results);
        Assert.Equal(new RectD(0, 0, Width, Height), full.BitmapRegion);
        Assert.Equal(Bgra(frame.Bitmap), Bgra(full.SelectedBitmap));
        Assert.Equal(string.Empty, full.SourceTitle);

        // A drag straight through the bar, the status and the guide.
        CaptureOverlayWindow second = Open();
        var secondView = (CaptureOverlayView)second.Content;
        Render(secondView);
        pointer = new PointD(200, 4);
        secondView.UpdatePointer(false, false);
        Assert.False(secondView.PointerOverModeBarForTest);
        Overlay.Invoke(secondView, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
        pointer = new PointD(1100, 500);
        Overlay.Invoke(secondView, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        Render(secondView);
        Overlay.Invoke(secondView, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
        Overlay.PumpUntil(() => results.Count == 2);
        Assert.Equal(new RectD(200, 4, 900, 496), results[1].BitmapRegion);
        Assert.Equal(Bgra(new CroppedBitmap(frame.Bitmap, new Int32Rect(200, 4, 900, 496))), Bgra(results[1].SelectedBitmap));
    });

    [Fact]
    public void RecordingSelectorKeepsThePlainInstructionWithoutABar() => StaTestHost.Run(() =>
    {
        var view = new CaptureOverlayView(Desktop(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(300, 300),
            ReadMonitors = () => Monitors,
        };
        try
        {
            view.InitializePointer();
            Render(view);
            Assert.Null(view.ModeBarForTest);
            Assert.Null(view.ModeBarBoundsForTest);
            Assert.Equal([CaptureOverlayView.InstructionText], view.StatusTextsForTest);
            Assert.Single(view.ChromeBoundsForTest);
        }
        finally { view.ReleaseResources(); }
    });

    private static void Near(double expected, double actual, double tolerance = 1) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{actual} is not within {tolerance} of {expected}");

    private static CaptureOverlayView BarView(FrozenFrame frame, Func<PointD> pointer)
    {
        var view = new CaptureOverlayView(frame, showMagnifier: false)
        {
            ReadCursor = pointer,
            DetectWindows = true,
            ReadMonitors = () => Monitors,
            WindowCandidates = [Browser, Notes, Taskbar, Shell],
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            ShowModeBar = true,
        };
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        return view;
    }

    private static void RenderAt(CaptureOverlayView view, int width, int height)
    {
        view.InvalidateVisual();
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32).Render(view);
    }

    private static FrozenFrame SolidFrame(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x50;
            pixels[i + 1] = 0x60;
            pixels[i + 2] = 0x70;
            pixels[i + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, width, height), null, 0);
    }

    private static byte[] Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        new FormatConvertedBitmap(new CroppedBitmap(bitmap, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixel, 4, 0);
        return pixel;
    }

    private static byte[] Bgra(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }
}
