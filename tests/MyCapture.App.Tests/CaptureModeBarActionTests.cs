using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;
using Overlay = MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The mode bar's capture/record switch and ⋯ menu hand the app's existing commands back instead of
/// capturing, and the recording selector says "record" in the recording colour.
/// </summary>
public sealed class CaptureModeBarActionTests
{
    [Fact]
    public void RecordSwitchEndsTheSelectorAndAsksForTheRecordingSelector() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        CaptureOverlayView view = BarView(offers: false);
        var requests = new List<CaptureSelectorRequest>();
        int confirmed = 0, cancelled = 0;
        view.ActionRequested += (_, request) => requests.Add(request);
        view.SelectionConfirmed += (_, _) => confirmed++;
        view.CancelRequested += (_, _) => cancelled++;
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            // Without an owner for the commands, neither the switch nor the menu is offered.
            Assert.Equal(Visibility.Collapsed, bar.RecordKindButton.Parent is FrameworkElement kinds ? kinds.Visibility : Visibility.Visible);
            Assert.Equal(Visibility.Collapsed, bar.MoreButton.Visibility);
            bar.RecordKindButton.IsChecked = true;
            Assert.Empty(requests);

            view.OffersOtherActions = true;
            Assert.Equal(Visibility.Visible, ((FrameworkElement)bar.RecordKindButton.Parent).Visibility);
            Assert.Equal(Visibility.Visible, bar.MoreButton.Visibility);
            Assert.True(bar.CaptureKindButton.IsChecked);
            Assert.Equal("녹화", AutomationProperties.GetName(bar.RecordKindButton));

            // Through UI Automation, like a click: the selector ends with the record request, nothing else.
            ((ISelectionItemProvider)new System.Windows.Automation.Peers.RadioButtonAutomationPeer(bar.RecordKindButton)).Select();
            Assert.Equal([new CaptureSelectorRequest(CaptureSelectorAction.Record)], requests);
            Assert.True(bar.CaptureKindButton.IsChecked);
            Assert.False(bar.RecordKindButton.IsChecked);
            Assert.Equal(0, confirmed);
            Assert.Equal(0, cancelled);

            // The selector has ended: a press no longer starts a capture.
            Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
            Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
            Overlay.Pump();
            Assert.Equal(0, confirmed);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void DirectDelayAndScrollingControlsAndMoreMenuRunExistingCommands() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        bool canRepeat = false;
        CaptureOverlayView view = BarView(offers: true);
        view.CanRepeatLastRegion = () => canRepeat;
        view.OffersOtherActions = true;
        var requests = new List<CaptureSelectorRequest>();
        view.ActionRequested += (_, request) => requests.Add(request);
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.Equal("다른 캡처", AutomationProperties.GetName(bar.MoreButton));
            ContextMenu menu = bar.CreateDelayMenu();
            Assert.Same(bar.DelayButton, menu.PlacementTarget);
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(["3초 후 캡처", "5초 후 캡처", "10초 후 캡처"], items.Select(item => (string)item.Header));
            ContextMenu more = bar.CreateMoreMenu();
            Assert.Same(bar.MoreButton, more.PlacementTarget);
            MenuItem repeat = Assert.IsType<MenuItem>(Assert.Single(more.Items));
            Assert.Equal("이전 영역 반복", repeat.Header);
            // Without a previous region the entry stays visible but cannot run.
            Assert.False(repeat.IsEnabled);
            canRepeat = true;
            view.OffersOtherActions = true;
            Assert.True(bar.CreateMoreMenu().Items.OfType<MenuItem>().Last().IsEnabled);

            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal([new CaptureSelectorRequest(CaptureSelectorAction.DelayedCapture, 5)], requests);
            // One request per selector: the rest are ignored once it has ended.
            bar.ScrollButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Single(requests);
        }
        finally { view.ReleaseResources(); }

        CaptureOverlayView scroll = BarView(offers: true);
        CaptureSelectorRequest? chosen = null;
        scroll.ActionRequested += (_, request) => chosen = request;
        try
        {
            Render(scroll);
            scroll.ModeBarForTest!.ScrollButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new CaptureSelectorRequest(CaptureSelectorAction.ScrollingCapture), chosen);
        }
        finally { scroll.ReleaseResources(); }
    });

    [Fact]
    public void CoordinatorRunsTheChosenCommandOnlyAfterTheSelectorSessionEnded() => StaTestHost.Run(() =>
    {
        var run = new List<(CaptureSelectorRequest Request, bool StillActive)>();
        int closedSessions = 0;
        CaptureOverlayCoordinator? coordinator = null;
        coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            AutoDetectWindows = () => true,
            ApplyCaptureExclusion = _ => true,
            CanRepeatLastRegion = () => false,
        };
        coordinator.OverlayClosed += (_, _) => closedSessions++;
        MethodInfo show = typeof(CaptureOverlayCoordinator).GetMethod("ShowOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo active = typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            // Without a handler the bar offers neither the switch nor the menu.
            show.Invoke(coordinator, [new RectD(0, 0, 640, 360), false, false, Stopwatch.StartNew(), null, true]);
            var plain = (CaptureOverlayWindow)active.GetValue(coordinator)!;
            Assert.False(plain.OffersOtherActions);
            coordinator.Cancel();

            coordinator.SelectorActionRequested = request => run.Add((request, coordinator.IsActive));
            show.Invoke(coordinator, [new RectD(0, 0, 640, 360), false, false, Stopwatch.StartNew(), null, true]);
            var overlay = (CaptureOverlayWindow)active.GetValue(coordinator)!;
            Assert.True(overlay.OffersOtherActions);
            var view = (CaptureOverlayView)overlay.Content;
            Assert.False(view.CanRepeatLastRegion());
            view.ModeBarForTest!.RecordKindButton.IsChecked = true;

            // The selector is closed and the session over before the app's command starts.
            Assert.Null(active.GetValue(coordinator));
            Assert.Equal(2, closedSessions);
            Overlay.PumpUntil(() => coordinator.LastSelectorActionForTest.IsCompleted);
            Assert.Equal([(new CaptureSelectorRequest(CaptureSelectorAction.Record), false)], run);
        }
        finally { coordinator.Dispose(); }
    });

    [Fact]
    public void RecordingSelectorSaysRecordInTheRecordingColourAndKeepsTheGeometry() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        Brush recording = RecordingSelectionCue.Create(AppTheme.Glass, systemHighContrast: false);
        Color red = ((SolidColorBrush)recording).Color;
        ThemeColor danger = ThemeCatalog.ColorsFor(AppTheme.Glass)["State.Danger"];
        Assert.Equal(Color.FromRgb(danger.R, danger.G, danger.B), red);
        Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)RecordingSelectionCue.Create(AppTheme.Glass, true)).Color);

        PointD pointer = new(320, 210);
        var window = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: false) { RecordingSelection = true };
        var view = (CaptureOverlayView)window.Content;
        view.ReadCursor = () => pointer;
        view.ReadMonitors = () => Monitors;
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        view.RecordingBrush = recording;
        RectD? geometry = null;
        window.GeometrySelectionCompleted = region => geometry = region;
        Assert.Equal("MyCapture — 녹화 영역 선택", window.Title);

        view.InitializePointer();
        RenderTargetBitmap ready = Render(view);
        Assert.Equal(["녹화할 영역을 드래그하십시오", "Esc: 취소"], view.StatusTextsForTest);
        Assert.Null(view.ModeBarForTest);
        // The monitor edge is in the recording colour; a screenshot selector never draws it.
        AssertNear(red, Pixel(ready, 1, 360));
        AssertNear(red, Pixel(ready, 640, 1));
        Rect chip = view.ChromeBoundsForTest[0];
        AssertNear(red, Pixel(ready, (int)(chip.Left + 14 + 5), (int)(chip.Top + (chip.Height / 2))));

        Overlay.Invoke(view, "OnMouseLeftButtonDown", Overlay.Button(UIElement.MouseLeftButtonDownEvent));
        pointer = new PointD(900, 560);
        Overlay.Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        RenderTargetBitmap dragged = Render(view);
        // The selection border takes the recording colour, inside the selected pixels.
        AssertNear(red, Pixel(dragged, 320, 400));
        AssertNear(red, Pixel(dragged, 600, 210));

        // The recorded geometry is the dragged rectangle, as before.
        Overlay.Invoke(view, "OnMouseLeftButtonUp", Overlay.Button(UIElement.MouseLeftButtonUpEvent));
        Overlay.PumpUntil(() => geometry is not null);
        Assert.Equal(new RectD(320, 210, 580, 350), geometry);

        // The screenshot selector never shows the record cue.
        CaptureOverlayView capture = EvidenceView(frame, AppTheme.Glass, "window-hover");
        try
        {
            RenderTargetBitmap shot = Render(capture);
            Assert.False(capture.RecordingSelection);
            Assert.NotEqual(red, Pixel(shot, 1, 360));
        }
        finally { capture.ReleaseResources(); }
    });

    [Fact]
    public void RegionRecordingOpensTheRecordingSelector() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-cue-");
        using var release = new ManualResetEventSlim();
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        var coordinator = new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            paths, () => new RecordingSettings(), NullLoggerFactory.Instance, library)
        {
            SelectionDesktopBounds = () => new RectD(0, 0, 32, 20),
        };
        CaptureOverlayWindow? overlay = null;
        coordinator.ExcludeSelectionWindow = window => { overlay = (CaptureOverlayWindow)window; return true; };
        coordinator.AcquireSelectionFrame = () =>
        {
            release.Wait(TimeSpan.FromSeconds(5));
            BitmapSource bitmap = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 20 * 4], 32 * 4);
            bitmap.Freeze();
            return new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);
        };
        try
        {
            coordinator.Toggle();
            Assert.NotNull(overlay);
            Assert.True(overlay.RecordingSelection);
            Assert.True(overlay.ShowModeBar);
            coordinator.CancelRegionSelection();
        }
        finally
        {
            release.Set();
            coordinator.CancelRegionSelection();
            Overlay.PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void RecordingToCaptureSwitchWaitsForClosedSelectorAndFinishedFramePreparation() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-record-capture-switch-");
        using var acquiring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        var coordinator = new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            paths, () => new RecordingSettings(), NullLoggerFactory.Instance, library)
        {
            SelectionDesktopBounds = () => new RectD(0, 0, 32, 20),
        };
        CaptureOverlayWindow? overlay = null;
        bool closed = false;
        var callbacks = new List<(bool Closed, bool Prepared, bool Active, object? Overlay, object? Preparation)>();
        coordinator.ExcludeSelectionWindow = window =>
        {
            overlay = (CaptureOverlayWindow)window;
            overlay.Closed += (_, _) => closed = true;
            return true;
        };
        coordinator.CaptureSelectionRequested = () =>
        {
            callbacks.Add((closed, coordinator.LastSelectionPreparation.IsCompleted, coordinator.IsActive,
                typeof(RegionRecordingCoordinator).GetField("_selectionOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator),
                typeof(RegionRecordingCoordinator).GetField("_selectionPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)));
        };
        coordinator.AcquireSelectionFrame = () =>
        {
            acquiring.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            BitmapSource bitmap = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 20 * 4], 32 * 4);
            bitmap.Freeze();
            return new FrozenFrame(bitmap, new RectD(0, 0, 32, 20), null, 0);
        };
        try
        {
            coordinator.Toggle();
            Overlay.PumpUntil(() => acquiring.IsSet);
            Assert.NotNull(overlay);
            Assert.False(overlay.IsVisible);
            var view = Assert.IsType<CaptureOverlayView>(overlay.Content);
            Assert.True(view.ModeBarForTest!.RecordKindButton.IsChecked);
            ((ISelectionItemProvider)new System.Windows.Automation.Peers.RadioButtonAutomationPeer(view.ModeBarForTest.CaptureKindButton)).Select();
            Assert.True(closed);
            Overlay.Pump();
            Assert.Empty(callbacks);
            Assert.False(coordinator.LastSelectionPreparation.IsCompleted);
            Assert.False(coordinator.LastSelectorActionForTest.IsCompleted);
            release.Set();
            Overlay.PumpUntil(() => coordinator.LastSelectorActionForTest.IsCompleted);
            coordinator.LastSelectorActionForTest.GetAwaiter().GetResult();
            var callback = Assert.Single(callbacks);
            Assert.True(callback.Closed);
            Assert.True(callback.Prepared);
            Assert.False(callback.Active);
            Assert.Null(callback.Overlay);
            Assert.Null(callback.Preparation);
            Assert.False(coordinator.IsActive);
        }
        finally
        {
            release.Set();
            coordinator.CancelRegionSelection();
            Overlay.PumpUntil(() => coordinator.LastSelectionPreparation.IsCompleted);
            OwnedTestDirectory.Delete(root);
        }
    });

    private static CaptureOverlayView BarView(bool offers)
    {
        var view = new CaptureOverlayView(Desktop(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(300, 300),
            DetectWindows = true,
            ReadMonitors = () => Monitors,
            WindowCandidates = [Browser, Notes, Taskbar, Shell],
            TextCaptureAvailable = true,
            CopyColorText = _ => Task.FromResult(true),
            ShowModeBar = true,
            OffersOtherActions = offers,
        };
        UsePalette(view, CaptureGuidePalette.Create(AppTheme.Glass, customColor: null, systemHighContrast: false));
        view.InitializePointer();
        return view;
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        new FormatConvertedBitmap(new CroppedBitmap(bitmap, new Int32Rect(x, y, 1, 1)), PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static void AssertNear(Color expected, Color actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= 2 && Math.Abs(expected.G - actual.G) <= 2 && Math.Abs(expected.B - actual.B) <= 2,
            $"{actual} is not {expected}");
    }
}
