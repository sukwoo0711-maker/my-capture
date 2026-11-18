using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FirstPinExperienceTests
{
    [Fact]
    public void AFirstPinRequiresAnArmedMatchingResultWithBothRealPinIdentities()
    {
        Guid sessionId = Guid.NewGuid();
        var first = new FirstPinSession(sessionId);
        var result = Opened(sessionId);
        Assert.False(first.Accept(result));
        Assert.Equal(FirstPinState.Ready, first.State);
        first.BeginAttempt(42);
        foreach (PracticeEvent unrelated in new[]
        {
            result with { SessionId = Guid.NewGuid() },
            result with { ClipboardSequence = 41 },
            result with { ClipboardSequence = 0 },
            result with { LogicalPinId = Guid.Empty },
            result with { WindowPinId = Guid.Empty },
            result with { Kind = PracticeEventKind.PictureCopied },
            result with { Kind = PracticeEventKind.PinRestored },
        })
        {
            Assert.False(first.Accept(unrelated));
            Assert.Equal(FirstPinState.Ready, first.State);
            Assert.Null(first.LogicalPinId);
            Assert.Null(first.WindowPinId);
        }
        Assert.True(first.Accept(result));
        Assert.Equal(FirstPinState.Pinned, first.State);
        Assert.Equal(result.LogicalPinId, first.LogicalPinId);
        Assert.Equal(result.WindowPinId, first.WindowPinId);
        Assert.False(first.Accept(result with { WindowPinId = Guid.NewGuid() }));
        Assert.Equal(result.WindowPinId, first.WindowPinId);
    }

    [Fact]
    public void OnlyClosingTheMatchingPinResetsTheFirstPinMomentForAnotherAttempt()
    {
        Guid sessionId = Guid.NewGuid();
        var first = new FirstPinSession(sessionId);
        PracticeEvent opened = Opened(sessionId);
        first.BeginAttempt(42);
        Assert.True(first.Accept(opened));
        PracticeEvent closing = opened with { Kind = PracticeEventKind.PinClosing };
        Assert.False(first.Accept(closing with { SessionId = Guid.NewGuid() }));
        Assert.False(first.Accept(closing with { LogicalPinId = Guid.NewGuid() }));
        Assert.False(first.Accept(closing with { WindowPinId = Guid.NewGuid() }));
        Assert.Equal(FirstPinState.Pinned, first.State);
        Assert.True(first.Accept(closing));
        Assert.Equal(FirstPinState.Ready, first.State);
        Assert.Null(first.LogicalPinId);
        Assert.Null(first.WindowPinId);
        Assert.Equal(0u, first.ExpectedClipboardSequence);
        Assert.False(first.Accept(opened));
        first.BeginAttempt(43);
        Assert.True(first.Accept(opened with { ClipboardSequence = 43, WindowPinId = Guid.NewGuid() }));
    }

    [Fact]
    public void AFailedOrNewerAttemptRejectsThePreviousClipboardResult()
    {
        Guid sessionId = Guid.NewGuid();
        var first = new FirstPinSession(sessionId);
        first.BeginAttempt(42);
        first.BeginAttempt(0);
        Assert.Equal(0u, first.ExpectedClipboardSequence);
        Assert.False(first.Accept(Opened(sessionId)));
        first.BeginAttempt(43);
        Assert.False(first.Accept(Opened(sessionId)));
        Assert.True(first.Accept(Opened(sessionId) with { ClipboardSequence = 43 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippingOrClosingCannotBeTurnedIntoSuccessByALatePin(bool close)
    {
        Guid sessionId = Guid.NewGuid();
        var first = new FirstPinSession(sessionId);
        first.BeginAttempt(42);
        if (close) first.End();
        else first.Skip();
        Assert.False(first.Accept(Opened(sessionId)));
        Assert.NotEqual(FirstPinState.Pinned, first.State);
        Assert.Null(first.WindowPinId);
        if (close) Assert.True(first.Ended);
        else Assert.Equal(FirstPinState.Skipped, first.State);
    }

    [Fact]
    public void OpeningAndReadingTheFirstPinMomentNeverTouchesTheClipboard() => StaTestHost.Run(() =>
    {
        var clipboard = new OnboardingGuideTests.FakeClipboard();
        int sequenceReads = 0;
        WelcomeWindow guide = Create(clipboard, () => { sequenceReads++; return 42; });
        try
        {
            guide.Show();
            Drain(guide);
            guide.UpdateHotkeys(NewInstallDefaults.Create().Hotkeys);
            guide.UpdateShortcutAvailability(true, true, true);
            guide.ShowRetryHint();
            Assert.Empty(clipboard.Writes);
            Assert.Equal(0, sequenceReads);
            Assert.True(guide.IsFirstPinExperience);
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            Assert.False(guide.SuccessShown);
            Assert.False(guide.PreferencesShown);
            Assert.False(guide.PreviewShown);
            Assert.True(guide.SampleTextButton.IsVisible);
            Assert.False(guide.FullPracticeButton.IsVisible);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void CopyingTheSampleAndPressingTheKeyStillWaitForThePinResult() => StaTestHost.Run(() =>
    {
        var clipboard = new OnboardingGuideTests.FakeClipboard();
        WelcomeWindow guide = Create(clipboard, () => 42);
        try
        {
            PracticeJourneyDriver.Click(guide.SampleTextButton);
            Assert.Single(clipboard.Writes);
            Assert.Equal(0u, guide.FirstPin.ExpectedClipboardSequence);
            Assert.False(guide.Notify(Opened(guide.Session.Id)));
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            Assert.False(guide.SuccessShown);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            Assert.False(guide.SuccessShown);
            guide.NotifyPinCreated();
            guide.NotifyEditorPinCreated(Guid.NewGuid());
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            Assert.False(guide.Notify(Opened(guide.Session.Id) with { ClipboardSequence = 41 }));
            Assert.True(guide.Notify(Opened(guide.Session.Id)));
            Assert.Equal(FirstPinState.Pinned, guide.FirstPin.State);
            Assert.All(guide.Session.Results.Values, value => Assert.Equal(PracticeStepResult.Pending, value));
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void SampleCopyFailureLeavesTheMomentUncompletedAndAllowsRetry() => StaTestHost.Run(() =>
    {
        var clipboard = new OnboardingGuideTests.FakeClipboard { Succeeds = false };
        WelcomeWindow guide = Create(clipboard, () => 42);
        try
        {
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            PracticeJourneyDriver.Click(guide.SampleTextButton);
            Assert.Empty(clipboard.Writes);
            Assert.Equal(0u, guide.FirstPin.ExpectedClipboardSequence);
            Assert.False(guide.Notify(Opened(guide.Session.Id)));
            Assert.False(guide.SuccessShown);
            Assert.True(guide.SampleTextButton.IsEnabled);
            clipboard.Succeeds = true;
            PracticeJourneyDriver.Click(guide.SampleTextButton);
            Assert.Single(clipboard.Writes);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            Assert.True(guide.Notify(Opened(guide.Session.Id)));
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullPracticeIsReachableAfterSkippingOrPinningWithoutCompletingItsFifteenSteps(bool pinned) => StaTestHost.Run(() =>
    {
        var clipboard = new OnboardingGuideTests.FakeClipboard();
        WelcomeWindow guide = Create(clipboard, () => 42);
        try
        {
            if (pinned)
            {
                guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
                Assert.True(guide.Notify(Opened(guide.Session.Id)));
            }
            else
            {
                PracticeJourneyDriver.Click(guide.SkipButton);
                Assert.Equal(FirstPinState.Skipped, guide.FirstPin.State);
                Assert.False(guide.Notify(Opened(guide.Session.Id)));
            }
            Assert.Equal(Visibility.Visible, guide.FullPracticeButton.Visibility);
            PracticeJourneyDriver.Click(guide.FullPracticeButton);
            Assert.False(guide.IsFirstPinExperience);
            Assert.Equal(PracticeStep.Groups, guide.Session.Step);
            Assert.Equal(15, guide.Session.Results.Count);
            Assert.All(guide.Session.Results.Values, value => Assert.Equal(PracticeStepResult.Pending, value));
            Assert.Empty(clipboard.Writes);
            Assert.False(guide.Notify(Opened(guide.Session.Id)));
            guide.Session.StartGroup(PracticeStep.A1);
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.Results);
            Assert.All(guide.Session.Results.Values, value => Assert.Equal(PracticeStepResult.Completed, value));
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("skip")]
    [InlineData("escape")]
    [InlineData("close")]
    public void EveryExitFromTheFirstPinMomentLeavesTheAttemptUncompleted(string route) => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = Create(new(), () => 42);
        try
        {
            guide.Show();
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            if (route == "skip")
            {
                PracticeJourneyDriver.Click(guide.SkipButton);
                Assert.True(guide.IsVisible);
                Assert.Equal(FirstPinState.Skipped, guide.FirstPin.State);
                Assert.False(guide.Notify(Opened(guide.Session.Id)));
                PracticeJourneyDriver.Click(guide.TryNowButton);
            }
            else if (route == "escape")
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(guide)!, 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                guide.RaiseEvent(key);
                Assert.True(key.Handled);
            }
            else guide.Close();
            Assert.False(guide.IsVisible);
            Assert.NotEqual(WelcomeOutcome.Completed, guide.Outcome);
            Assert.NotEqual(WelcomeOutcome.FirstPinCompleted, guide.Outcome);
            Assert.NotEqual(FirstPinState.Pinned, guide.FirstPin.State);
            Assert.True(guide.FirstPin.Ended);
            Assert.False(guide.Notify(Opened(guide.Session.Id)));
            Assert.All(guide.Session.Results.Values, value => Assert.Equal(PracticeStepResult.Pending, value));
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void CompletingTheFirstPinKeepsTheActualPinOpenWhenTheGuideCloses() => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = Create(new(), () => 42);
        uint dpi = MonitorEnumerator.GetFromPoint(new PointD(-10000, -10000)).Dpi;
        double scale = dpi / 96.0;
        var bounds = new RectD(-10000 * scale, -10000 * scale, 1600 * scale, 1000 * scale);
        var monitor = new MonitorInfo("first-pin-producer", bounds, bounds, dpi, true);
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => monitor,
            ReadRestoreMonitorsForTest = () => [monitor],
            BeforeShowForTest = pin => { pin.ShowActivated = false; pin.ShowInTaskbar = false; },
            ActivatePinForTest = _ => { },
        };
        void OnResult(PracticeEvent result) => guide.Notify(result with { SessionId = guide.Session.Id });
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PracticeSignals.Result += OnResult;
        try
        {
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            Assert.Equal(FirstPinState.Ready, guide.FirstPin.State);
            var content = PinContent.FromImage(PracticePicture.Load());
            Assert.Equal(PasteResult.Pinned, PinHistoryTestSupport.Await(manager.PasteFromClipboardAsync(
                () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content) with { ClipboardSequence = 42 }))));
            PinWindow pin = Assert.Single(manager.SnapshotOpenPinWindows());
            Assert.True(pin.IsVisible);
            Assert.True(pin.Left < -8000 && pin.Top < -8000);
            Assert.Equal(FirstPinState.Pinned, guide.FirstPin.State);
            Assert.Equal(pin.PracticeId, guide.FirstPin.WindowPinId);
            Assert.Equal(pin.LogicalPracticeId, guide.FirstPin.LogicalPinId);
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(WelcomeOutcome.FirstPinCompleted, guide.Outcome);
            Assert.Same(pin, Assert.Single(manager.SnapshotOpenPinWindows()));
            Assert.True(pin.IsVisible);
        }
        finally
        {
            PracticeSignals.Result -= OnResult;
            guide.Close();
            manager.CloseAllForExit();
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
        }
    });

    [Fact]
    public void AnInjectedFirstPinResultDoesNotTieTheVisiblePinLifetimeToTheGuide() => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = Create(new(), () => 42);
        var image = PracticePicture.Load();
        var bounds = new RectD(-10000, -10000, 1600, 1000);
        Func<HotkeySettings>? previousKeys = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => NewInstallDefaults.Create().Hotkeys;
        var pin = new PinWindow(PinContent.FromImage(image),
            new PinViewState(image.PixelWidth, image.PixelHeight, 1, 1, .1), -10000, -10000,
            () => new PinSettings(), new SyntheticPinDragInput())
        {
            ShowActivated = false, ShowInTaskbar = false, ReadWorkArea = _ => bounds,
        };
        pin.PlaceAtPhysicalOnShow(new RectD(-9904, -9904, image.PixelWidth, image.PixelHeight));
        try
        {
            _ = new WindowInteropHelper(pin).EnsureHandle();
            pin.Show();
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            Assert.True(guide.Notify(Opened(guide.Session.Id) with
                { LogicalPinId = pin.LogicalPracticeId, WindowPinId = pin.PracticeId }));
            PracticeJourneyDriver.Click(guide.TryNowButton);
            Assert.Equal(WelcomeOutcome.FirstPinCompleted, guide.Outcome);
            Assert.False(guide.IsVisible);
            Assert.True(pin.IsVisible);
            Assert.Null(pin.Owner);
        }
        finally { guide.Close(); pin.Close(); ShortcutHelpWindow.ReadHotkeys = previousKeys; }
    });

    private static PracticeEvent Opened(Guid sessionId) => new(PracticeEventKind.PinOpened, sessionId,
        LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Pin, ClipboardSequence: 42);

    private static WelcomeWindow Create(OnboardingGuideTests.FakeClipboard clipboard, Func<uint> sequence) =>
        new(NewInstallDefaults.Create().Hotkeys, AfterCaptureAction.QuickAccess, options: new WelcomeOptions
        {
            FirstPinExperience = true,
            Clipboard = clipboard,
            ClipboardSequenceProvider = sequence,
            ReducedMotion = true,
            MediaFactory = () => new OnboardingGuideTests.FakeMedia(),
            MonitorProvider = () => new MonitorInfo("first-pin-test", new RectD(-10000, -10000, 1600, 1040),
                new RectD(-10000, -10000, 1600, 1000), 96, true),
        })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };

    private static void Drain(WelcomeWindow guide) => guide.Dispatcher.Invoke(guide.UpdateLayout, DispatcherPriority.ApplicationIdle);
}
