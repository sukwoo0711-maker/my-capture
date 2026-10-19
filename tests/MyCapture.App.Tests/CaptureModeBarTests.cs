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
            Assert.True(bar.WindowButton.IsChecked);
            Assert.Equal("영역 선택", AutomationProperties.GetName(bar.CaptureButton));

            // Mouse or UI Automation: selecting the text button is the T key's text mode.
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.TextButton)).Select();
            Assert.Equal(CaptureSelectorMode.Text, view.Mode);
            Assert.True(view.TextCaptureMode);
            Assert.False(bar.CaptureButton.IsChecked);
            Render(view);
            // No status chip repeats the mode under the bar; the label beside the window says it.
            Assert.Empty(view.StatusTextsForTest);
            Assert.Equal("클릭: 창 텍스트 복사  620 × 420", view.TargetLabelForTest);

            // T from the keyboard goes back, and the bar shows it.
            view.RaiseEvent(Overlay.KeyArgs(Key.T));
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.True(bar.WindowButton.IsChecked);

            // Enter on a focused bar button presses it, like Space.
            KeyEventArgs enter = Overlay.KeyArgs(Key.Enter);
            bar.ColorButton.RaiseEvent(enter);
            Assert.True(enter.Handled);
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);
            Assert.True(bar.ColorButton.IsChecked);
            Assert.Null(view.HoverRegionForTest);
            Render(view);
            Assert.Empty(view.StatusTextsForTest);
            Assert.Null(view.TargetTabForTest);

            // From colour, T goes straight to text; the region button returns to drag-only capture.
            view.RaiseEvent(Overlay.KeyArgs(Key.T));
            Assert.Equal(CaptureSelectorMode.Text, view.Mode);
            bar.CaptureButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Capture, view.Mode);
            Assert.Null(view.HoverRegionForTest);

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
        int cancelled = 0;
        ColorCopyFeedback? feedback = null;
        view.ColorCopied = result => feedback = result;
        view.CancelRequested += (_, _) => cancelled++;
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
            Assert.Equal(UiText.Format("Tools_ColorCopied", expected), feedback?.Message);
            Assert.Equal(1, cancelled);
            Assert.Null(view.Selection);
            Assert.Equal(0, confirmed);
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);

            Assert.Null(view.CopyColorAtPointerAsync().Result);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void ColorModeIsUnavailableWithoutACopyHandler() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = BarView(Desktop(), () => new PointD(300, 300));
        try
        {
            Render(view);
            view.ModeBarForTest!.ColorButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Color, view.Mode);
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
    public void OnlyTheLabelBesideTheTargetNamesWindowOrMonitorAndNoChipRepeatsIt() => StaTestHost.Run(() =>
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
            Assert.Equal("클릭: 모니터 캡처  1280 × 720", desktop.TargetLabelForTest);
            Assert.Equal("클릭: 창 캡처  620 × 420", window.TargetLabelForTest);
            Assert.Equal(CaptureCueState.Drag, drag.CueState);
            Assert.Null(drag.TargetLabelForTest);

            // The always-on "drag: … · click: …" chip under the bar is gone in every state: the bar
            // is the only chrome, and nothing lights a status segment.
            Rect bar = desktop.ModeBarBoundsForTest!.Value;
            foreach (CaptureOverlayView view in new[] { desktop, window, drag })
            {
                Assert.Equal([bar], view.ChromeBoundsForTest);
                Assert.Empty(view.StatusTextsForTest);
                Assert.Null(view.ActiveCueSegmentForTest);
            }

            // The detected window's tab keeps clear of the bar.
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
            Assert.Equal("Select region", AutomationProperties.GetName(bar.CaptureButton));
            Assert.Equal("Full screen", AutomationProperties.GetName(bar.FullScreenButton));
            Assert.Equal("Copy text", AutomationProperties.GetName(bar.TextButton));
            Assert.Equal("Copy color", AutomationProperties.GetName(bar.ColorButton));
            CaptureStartGuideCard card = view.StartGuideCardForTest!;
            Assert.Equal(["Click: Capture window", "Drag: Capture region"], card.ActionTexts);
            Assert.Equal("Esc: Cancel", card.EscapeText);
            Assert.Equal("Click a window to capture it, or drag to capture the region you want. Press Esc to cancel.", card.HelpText);
            Assert.Equal("Capture", AutomationProperties.GetName(bar.CaptureKindButton));
            Assert.Equal("Record", AutomationProperties.GetName(bar.RecordKindButton));
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
            // Two pictures named with the overlay's own words: what a click and a drag do. The bar's
            // buttons explain themselves.
            Assert.Equal(["클릭: 창 캡처", "드래그: 영역 캡처"], card.ActionTexts);
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
    public void StartGuideCardNamesBothActionsForUiAutomation() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        PointD pointer = new(640, 400);
        CaptureOverlayView view = BarView(Desktop(), () => pointer);
        view.ShowStartGuide = true;
        try
        {
            view.InitializePointer();
            Render(view);
            CaptureStartGuideCard card = view.StartGuideCardForTest!;
            Assert.False(card.Focusable);
            Assert.False(card.IsHitTestVisible);

            // One group whose help text says what a click and a drag do; the pictures are shapes
            // without peers, so a screen reader reads only the words under them and the Esc line.
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(card);
            Assert.Equal(AutomationControlType.Group, peer.GetAutomationControlType());
            Assert.Equal("캡처 방법", peer.GetName());
            Assert.Equal("창 위에서 클릭하면 그 창을, 드래그하면 원하는 영역을 캡처합니다. Esc로 취소합니다.", peer.GetHelpText());
            Assert.Equal(["클릭: 창 캡처", "드래그: 영역 캡처", "Esc: 취소"], peer.GetChildren().Select(child => child.GetName()));
            Assert.Equal(["클릭: 창 캡처", "드래그: 영역 캡처"], card.ActionTexts);
            double bothWidth = card.DesiredSize.Width;

            // Without window detection a click captures nothing, so only the drag picture remains.
            view.DetectWindows = false;
            Render(view);
            Assert.Equal(["드래그: 영역 캡처"], card.ActionTexts);
            Assert.Equal("드래그하면 원하는 영역을 캡처합니다. Esc로 취소합니다.", peer.GetHelpText());
            Assert.True(card.DesiredSize.Width < bothWidth - CaptureStartGuideCard.PictureWidth);
        }
        finally { view.ReleaseResources(); }
    });

    [Theory]
    [InlineData("ko-KR", 96, 1.0)]
    [InlineData("ko-KR", 144, 1.5)]
    [InlineData("en-US", 144, 1.5)]
    [InlineData("ko-KR", 144, 1.0)]
    public void StartGuideCardFitsA1280By720MonitorAt100And150Percent(string culture, int monitorDpi, double overlayScale) =>
        StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        // A 1280x720 display with a 48-pixel taskbar; the last case is a 150% display under an overlay
        // that renders at 100%, where the card is scaled up to keep the display's physical size.
        IReadOnlyList<MonitorInfo> monitors =
            [new(@"\\.\DISPLAY1", new RectD(0, 0, Width, Height), new RectD(0, 0, Width, 672), (uint)monitorDpi, IsPrimary: true)];
        PointD pointer = new(900, 520);
        var view = new CaptureOverlayView(Desktop(), showMagnifier: false)
        {
            ReadCursor = () => pointer,
            ReadMonitors = () => monitors,
            DetectWindows = true,
            WindowCandidates = [Browser, Notes, Taskbar, Shell],
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            ShowModeBar = true,
            OffersOtherActions = true,
            ShowStartGuide = true,
        };
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        try
        {
            view.InitializePointer();
            RenderTargetBitmap shown = Render(view, overlayScale);
            CaptureStartGuideCard card = view.StartGuideCardForTest!;
            double scale = view.GuideScaleForTest;
            Assert.Equal(monitorDpi / 96.0 / overlayScale, scale, 3);

            // Inside the work area, clear of the bar with the edge margin between them.
            var work = new Rect(0, 0, Width / overlayScale, 672 / overlayScale);
            Rect slot = LayoutInformation.GetLayoutSlot(card);
            Rect bar = view.ModeBarBoundsForTest!.Value;
            Assert.True(slot.Left >= work.Left - 0.5 && slot.Right <= work.Right + 0.5
                && slot.Top >= work.Top - 0.5 && slot.Bottom <= work.Bottom + 0.5, $"{slot} leaves {work}");
            Assert.True(slot.Top >= bar.Bottom + (CaptureModeBarLayout.EdgeMargin * scale) - 0.5, $"{slot} reaches the bar {bar}");

            // Arranged at the size it asked for, so nothing in it is squeezed or clipped.
            Near(card.DesiredSize.Width, slot.Width, 0.5);
            Near(card.DesiredSize.Height, slot.Height, 0.5);
            Near(card.RenderSize.Width * scale, slot.Width, 0.5);
            Near(card.RenderSize.Height * scale, slot.Height, 0.5);

            // Each caption stays on one line under its picture.
            TextBlock[] texts = Descendants(card).OfType<TextBlock>().ToArray();
            Assert.Equal(3, texts.Length);
            foreach (TextBlock text in texts)
            {
                Assert.True(text.ActualHeight < text.FontSize * 2, $"'{text.Text}' wraps: {text.ActualHeight}");
                Assert.True(text.ActualWidth <= CaptureStartGuideCard.PictureWidth + 0.5, $"'{text.Text}' is wider than its picture");
            }

            SaveEvidence(shown, $"start-guide-fit-{culture}-{monitorDpi}dpi-overlay{overlayScale * 100:0}.png");
        }
        finally { view.ReleaseResources(); }

        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (DependencyObject nested in Descendants(child)) yield return nested;
            }
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
