using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PracticeReviewFixProducerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpenOpacityMenuDistinguishesAChoiceFromCancellation(bool choose) => StaTestHost.Run(() =>
    {
        using var fixture = new PracticeReviewPinFixture();
        fixture.Guide.Session.Skip(); // B2 -> B3; pin identity comes from the real manager.
        PinWindow pin = fixture.Pin;
        pin.ShowHoverBar();
        pin.HoverBarForTest!.OpacityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Drain();
        ContextMenu menu = OpenMenu(pin);
        Assert.True(menu.IsOpen);
        Assert.Equal(PracticeStep.B4, fixture.Guide.Session.Step);
        Popup popup = Assert.IsType<Popup>(LogicalTreeHelper.GetParent(menu));
        popup.PopupAnimation = PopupAnimation.None;
        if (choose) InvokeMenuItem((MenuItem)menu.Items[2]);
        else menu.IsOpen = false;
        Drain();
        output.WriteLine(string.Join(" -> ", fixture.Signals.Select(signal => signal.Kind)));
        Assert.False(menu.IsOpen);
        Assert.Equal(choose ? .6 : 1, pin.State.Opacity, 6);
        Assert.Equal(choose ? PracticeStep.B5 : PracticeStep.B3, fixture.Guide.Session.Step);
        Assert.Equal(choose ? PracticeStepResult.Completed : PracticeStepResult.Pending, fixture.Guide.Session.Results[PracticeStep.B4]);
        if (choose)
            Assert.True(fixture.Signals.FindIndex(e => e.Kind == PracticeEventKind.OpacityChanged)
                < fixture.Signals.FindIndex(e => e.Kind == PracticeEventKind.OpacityMenuClosed));
    });

    [Fact]
    public Task KeyboardOpensTheSameOpacityMenuAndCanChooseSixtyPercent() => SettingsThemePreviewTests.RunIsolated(
        nameof(KeyboardOpensTheSameOpacityMenuAndCanChooseSixtyPercent), VerifyKeyboardMenu, typeof(PracticeReviewFixProducerTests));

    private static void VerifyKeyboardMenu()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml") });
        try
        {
            using var fixture = new PracticeReviewPinFixture();
            fixture.Guide.Session.Skip();
            fixture.Pin.Activate(); fixture.Pin.Focus();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(fixture.Pin)!, 0, Key.O)
            { RoutedEvent = Keyboard.KeyDownEvent, Source = fixture.Pin };
            InputManager.Current.ProcessInput(key);
            Drain();
            Assert.True(key.Handled);
            ContextMenu menu = OpenMenu(fixture.Pin);
            Assert.True(menu.IsOpen);
            Assert.Equal(PracticeStep.B4, fixture.Guide.Session.Step);
            // The offscreen host cannot grant native popup keyboard focus (the existing pin menu
            // has the same limitation). Verify WPF focus ownership and inject its key events.
            Assert.Same(menu.Items[0], FocusManager.GetFocusedElement(menu));
            Assert.Equal(new[] { "100%", "80%", "60%", "40%" }, menu.Items.Cast<MenuItem>().Select(i => (string)i.Header));
            var choice = (MenuItem)menu.Items[2];
            choice.Focus();
            // WPF's menu key handler, using injected input rather than the desktop keyboard.
            var enter = WindowDetectOverlayTests.KeyArgs(Key.Enter);
            enter.RoutedEvent = Keyboard.KeyDownEvent;
            choice.RaiseEvent(enter);
            Drain();
            Assert.True(enter.Handled);
            Assert.Equal(.6, fixture.Pin.State.Opacity, 6);
            Assert.False(menu.IsOpen);
            Assert.Equal(PracticeStep.B5, fixture.Guide.Session.Step);
        }
        finally { application.Shutdown(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyEditorDoneReleasesItsIdentityAndReopensTheSameRecord(bool arrowSelected) => StaTestHost.Run(() =>
    {
        var app = TestApp();
        var guide = Guide(app);
        AnnotationEditorWindow? first = null, second = null;
        var events = new List<PracticeEvent>();
        PracticeSignals.Result += events.Add;
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.A3);
            first = Editor(PracticeJourneyDriver.Record);
            first.Show(); Drain();
            Assert.Equal(PracticeStep.A4, guide.Session.Step);
            Guid? previousEditor = guide.Session.EditorId;
            if (arrowSelected) Assert.True(first.Editor.HandleShortcut(Key.A, ModifierKeys.None));
            PracticeStep step = arrowSelected ? PracticeStep.A5 : PracticeStep.A4;
            Assert.Equal(step, guide.Session.Step);
            Assert.True(first.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
            WaitUntil(() => !first.IsVisible);
            Assert.Contains(events, e => e.Kind == PracticeEventKind.EditorCopiedAndClosed);
            Assert.Null(guide.Session.EditorId);
            Assert.Equal(step, guide.Session.Step);
            Assert.Equal(PracticeStepResult.Pending, guide.Session.Results[PracticeStep.A6]);
            Assert.True(guide.TryNowButton.IsEnabled && guide.TryNowButton.IsDefault);
            Assert.Equal(Visibility.Visible, guide.TryNowButton.Visibility);
            second = Editor(PracticeJourneyDriver.Record);
            second.Show(); Drain();
            Assert.NotNull(guide.Session.EditorId);
            Assert.NotEqual(previousEditor, guide.Session.EditorId);
            Assert.Equal(PracticeJourneyDriver.Record, second.Editor.PracticeTarget!.RecordId);
            Assert.True(second.Editor.HandleShortcut(Key.A, ModifierKeys.None));
            Assert.Equal(PracticeStep.A5, guide.Session.Step);
        }
        finally
        {
            PracticeSignals.Result -= events.Add;
            guide.Close(); first?.Close(); second?.Close();
        }
    });

    [Theory]
    [InlineData("preparing")]
    [InlineData("selecting")]
    [InlineData("saving")]
    public void RepeatedCaptureKeepsTheFirstRequestUntilItsSavedResult(string phase) => StaTestHost.Run(() =>
    {
        using var releaseFrame = new ManualResetEventSlim(false);
        FrozenFrame frame = Frame();
        using var coordinator = Coordinator(_ =>
        {
            if (!releaseFrame.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Synthetic frame was not released.");
            return frame;
        });
        var app = TestApp(); Set(app, "_overlay", coordinator);
        var guide = Guide(app);
        CaptureOverlayWindow? selector = null;
        coordinator.ApplyCaptureExclusion = window => { selector = (CaptureOverlayWindow)window; Offscreen(window); return true; };
        var saved = new TaskCompletionSource();
        CaptureSelectionCompletedEventArgs? selection = null;
        coordinator.SelectionPersistRequested = async value =>
        {
            selection = value;
            await saved.Task;
            typeof(App).GetMethod("PublishPracticeCapture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value, PracticeJourneyDriver.Record]);
        };
        try
        {
            guide.DismissForCapture();
            Guid request = guide.Session.CaptureRequestId;
            Capture(app);
            if (phase == "preparing")
            {
                Capture(app);
                Assert.True(guide.CapturePending);
            }
            releaseFrame.Set();
            PinHistoryTestSupport.Await(coordinator.LastPreparationForTest); Drain();
            Assert.NotNull(selector);
            Assert.True(selector.IsVisible && selector.Left < -8000 && selector.Top < -8000);
            if (phase == "selecting") Capture(app);
            Assert.True(guide.CapturePending);
            Assert.Equal(request, guide.Session.CaptureRequestId);
            Assert.True(((CaptureOverlayView)selector.Content).CaptureFullScreen());
            Assert.NotNull(selection);
            Assert.False(selector.IsVisible);
            if (phase == "saving") Capture(app);
            Assert.True(guide.CapturePending);
            Assert.Equal(request, selection.PracticeTarget!.RequestId);
            saved.SetResult();
            PinHistoryTestSupport.Await(coordinator.LastTransitionForTest);
            Assert.Equal(PracticeStep.A3, guide.Session.Step);
            Assert.Equal(PracticeStepResult.Completed, guide.Session.Results[PracticeStep.A2]);
            Assert.Equal(PracticeJourneyDriver.Record, guide.Session.CaptureId);
        }
        finally { releaseFrame.Set(); saved.TrySetResult(); guide.Close(); }
    });

    [Fact]
    public void TextModeSelectionEndsImagePracticeAndOffersAnotherCapture() => StaTestHost.Run(() =>
    {
        FrozenFrame frame = Frame();
        using var coordinator = Coordinator(_ => frame);
        var app = TestApp(); Set(app, "_overlay", coordinator);
        var guide = Guide(app);
        CaptureOverlayWindow? selector = null;
        coordinator.ApplyCaptureExclusion = window => { selector = (CaptureOverlayWindow)window; Offscreen(window); return true; };
        int texts = 0, saves = 0;
        coordinator.TextCaptureRequested = value => { Assert.True(value.TextCapture); texts++; };
        coordinator.SelectionPersistRequested = _ => { saves++; return Task.CompletedTask; };
        try
        {
            guide.DismissForCapture(); Capture(app);
            PinHistoryTestSupport.Await(coordinator.LastPreparationForTest); Drain();
            var view = Assert.IsType<CaptureOverlayView>(selector!.Content);
            var textKey = WindowDetectOverlayTests.KeyArgs(Key.T);
            view.HandleKeyDown(textKey, ModifierKeys.None);
            Assert.True(textKey.Handled && selector.TextCaptureMode);
            Assert.True(view.CaptureFullScreen()); Drain();
            Assert.False(selector.IsVisible);
            Assert.Equal(1, texts); Assert.Equal(0, saves);
            Assert.False(guide.CapturePending);
            Assert.Null(guide.Session.CaptureId);
            Assert.Equal(PracticeStepResult.Pending, guide.Session.Results[PracticeStep.A2]);
            Assert.True(guide.TryNowButton.IsEnabled);
            Guid previous = guide.Session.CaptureRequestId;
            PracticeJourneyDriver.Click(guide.TryNowButton); Drain();
            PinHistoryTestSupport.Await(coordinator.LastPreparationForTest); Drain();
            Assert.True(guide.CapturePending);
            Assert.NotEqual(previous, guide.Session.CaptureRequestId);
            Assert.True(selector.IsVisible);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void BackAfterRestoringOffersClosingPracticeInsteadOfRestoringConsumedHistory() => StaTestHost.Run(() =>
    {
        using var fixture = new PracticeReviewPinFixture();
        while (fixture.Guide.Session.Step != PracticeStep.B5) fixture.Guide.Session.Skip();
        fixture.Pin.Close();
        PinHistoryTestSupport.Await(fixture.Manager.LastClosedEncodingForTest);
        Assert.Equal(PracticeStep.B6, fixture.Guide.Session.Step);
        PinWindow restored = Assert.IsType<PinWindow>(PinHistoryTestSupport.Await(fixture.Manager.RestoreLastClosedAsync(fixture.Guide.Session.LogicalPinId)));
        Assert.Equal(PracticeStep.C1, fixture.Guide.Session.Step);
        Assert.Equal(0, fixture.Manager.ClosedCount);
        PracticeJourneyDriver.Click(fixture.Guide.BackButton);
        Assert.Equal(PracticeStep.B6, fixture.Guide.Session.Step);
        int restoreRequests = 0;
        fixture.Guide.RestorePinRequested += (_, _) => restoreRequests++;
        PracticeJourneyDriver.Click(fixture.Guide.TryNowButton);
        Assert.Equal(PracticeStep.B5, fixture.Guide.Session.Step);
        Assert.True(restored.IsVisible);
        Assert.Equal(0, restoreRequests);
        restored.Close(); PinHistoryTestSupport.Await(fixture.Manager.LastClosedEncodingForTest);
        Assert.Equal(PracticeStep.B6, fixture.Guide.Session.Step);
        Assert.True(fixture.Guide.Session.PinRestorable);
        Assert.NotNull(PinHistoryTestSupport.Await(fixture.Manager.RestoreLastClosedAsync(fixture.Guide.Session.LogicalPinId)));
        Assert.Equal(PracticeStep.C1, fixture.Guide.Session.Step);
    });

    private static void InvokeMenuItem(MenuItem item) =>
        ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static App TestApp()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        Set(app, "_settings", new AppSettings());
        return app;
    }
    private static void Set(App app, string name, object value) => typeof(App).GetField(name, Private)!.SetValue(app, value);
    private static WelcomeWindow Guide(App app) => (WelcomeWindow)typeof(App).GetMethod("CreatePracticeWindow", Private)!.Invoke(app, [true, false, false])!;
    private static void Capture(App app) => typeof(App).GetMethod("HandleCaptureRequested", Private)!.Invoke(app, null);
    private static FrozenFrame Frame() => new(EditorKeysTests.Solid(320, 200, 80), new RectD(-10000, -10000, 320, 200), null, 0);
    private static CaptureOverlayCoordinator Coordinator(Func<bool, FrozenFrame> acquire) => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
        new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance,
        acquire, () => new RectD(-10000, -10000, 320, 200), _ => [])
    {
        RequiresCaptureExclusion = () => true, ReadWindowSources = null, OpenEditorAfterCapture = () => false,
        ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
    };
    private static AnnotationEditorWindow Editor(Guid id)
    {
        FrozenFrame frame = Frame();
        var monitor = new MonitorInfo("synthetic", new RectD(-10000, -10000, 1600, 1000), new RectD(-10000, -10000, 1600, 1000), 96, true);
        var window = new AnnotationEditorWindow(frame, new RectD(0, 0, 320, 200), frame.Bitmap, pointerMonitor: monitor)
        { DraftRecordSource = () => (id, 0), CommitRequested = _ => Task.FromResult(true), ConfirmClose = () => EditorCloseChoice.DontSave };
        Offscreen(window);
        return window;
    }
    private static void Offscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -10000;
        window.ShowActivated = window.ShowInTaskbar = false;
    }
    private static void WaitUntil(Func<bool> predicate)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Synthetic window did not complete.");
            Drain();
        }
    }

    internal static ContextMenu OpenMenu(PinWindow pin) => Assert.IsType<ContextMenu>(
        typeof(PinHoverBar).GetField("_opacityMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin.HoverBarForTest));
    internal static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}

internal sealed class PracticeReviewPinFixture : IDisposable
{
    private readonly bool? _motion = FluidMotion.AnimationsEnabledOverrideForTest;
    internal WelcomeWindow Guide { get; }
    internal PinManager Manager { get; }
    internal PinWindow Pin { get; }
    internal List<PracticeEvent> Signals { get; } = [];
    internal PracticeReviewPinFixture()
    {
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var monitor = new MonitorInfo("synthetic", new RectD(-10000, -10000, 1600, 1000), new RectD(-10000, -10000, 1600, 1000), 96, true);
        Guide = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor,
            options: new WelcomeOptions { FeaturePractice = true, ReducedMotion = true, CopyPracticePicture = () => 42 });
        Manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => monitor, ReadRestoreMonitorsForTest = () => [monitor],
            BeforeShowForTest = pin =>
            {
                pin.ShowActivated = false; pin.ShowInTaskbar = false;
                pin.ReadWorkArea = _ => monitor.WorkArea;
            }, ActivatePinForTest = _ => { },
        };
        PracticeSignals.Result += Result;
        PracticeJourneyDriver.Click(Guide.TryNowButton);
        Assert.Equal(PasteResult.Pinned, PinHistoryTestSupport.Await(Manager.PasteFromClipboardAsync(
            () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(PinContent.FromImage(PracticePicture.Load())) with { ClipboardSequence = 42 }))));
        Pin = Assert.Single(Manager.SnapshotOpenPinWindows());
        Assert.Equal(PracticeStep.B2, Guide.Session.Step);
    }
    private void Result(PracticeEvent signal)
    {
        Signals.Add(signal);
        Guide.Notify(signal.SessionId == Guid.Empty ? signal with { SessionId = Guide.Session.Id } : signal);
    }
    public void Dispose()
    {
        PracticeSignals.Result -= Result;
        Guide.Close(); Manager.CloseAllForExit();
        FluidMotion.AnimationsEnabledOverrideForTest = _motion;
    }
}
