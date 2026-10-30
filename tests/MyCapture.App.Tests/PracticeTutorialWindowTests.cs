using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PracticeTutorialWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingPracticePinReturnsToPinAndAcceptsOnlyTheNextReplacement(bool featurePractice) => StaTestHost.Run(() =>
    {
        var guide = OnboardingGuideTests.Create(new(), new WelcomeOptions { FeaturePractice = featurePractice });
        try
        {
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B2);
            var signal = new PracticeEvent(PracticeEventKind.PinClosed, guide.Session.Id, LogicalPinId: PracticeJourneyDriver.Logical, WindowPinId: PracticeJourneyDriver.Pin, Restorable: true);
            Assert.False(guide.Notify(signal with { WindowPinId = Guid.NewGuid() }));
            Assert.True(guide.Notify(signal));
            Assert.Equal(PracticeStep.B2, guide.Session.Step);
            Assert.True(guide.TryNowButton.IsEnabled);
            Assert.Equal(UiText.Get("Journey.Reopen"), guide.TryNowButton.Content);
            Assert.False(guide.Notify(signal with { Kind = PracticeEventKind.PinOpened, WindowPinId = Guid.NewGuid(), ClipboardSequence = 42 }));
            Assert.True(guide.Notify(signal with { Kind = PracticeEventKind.PinRestored, WindowPinId = PracticeJourneyDriver.Restored }));
            Assert.False(guide.Notify(PracticeJourneyDriver.Signal(guide.Session)));
            Assert.True(guide.Notify(PracticeJourneyDriver.Signal(guide.Session) with { WindowPinId = PracticeJourneyDriver.Restored }));
            Assert.Equal(PracticeStep.B3, guide.Session.Step);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "연습 종료", "이 단계 건너뛰기")]
    [InlineData("en-US", "End practice", "Skip this step")]
    public void PracticeExitAndSingleStepSkipHaveDistinctLabels(string language, string end, string skip) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        WelcomeWindow guide = OnboardingGuideTests.Create(new());
        try
        {
            guide.UpdateShortcutAvailability(false, false, false);
            Assert.True(guide.ConflictShown);
            Assert.Equal(end, guide.SkipButton.Content);
            Assert.Equal(end, AutomationProperties.GetName(guide.SkipButton));
            Assert.Equal(skip, guide.SkipStepButton.Content);
            Assert.Equal(skip, AutomationProperties.GetName(guide.SkipStepButton));
            Assert.Contains(end, Assert.IsType<string>(guide.SkipButton.ToolTip), StringComparison.Ordinal);
            guide.SkipStepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(PracticeStep.A2, guide.Session.Step);
            Assert.Equal(end, guide.SkipButton.Content);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailablePinRetryOffersTheEditorActionWithoutAFailedShortcut(bool assigned) => StaTestHost.Run(() =>
    {
        var keys = new HotkeySettings { PasteToScreen = assigned ? new Hotkey(HotkeyModifiers.None, Hotkey.VkF3) : Hotkey.None };
        var guide = OnboardingGuideTests.Create(new(), keys: keys);
        try
        {
            guide.UpdateShortcutAvailability(true, false, true);
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1);
            guide.ShowRetryHint();
            Assert.False(guide.KeyShown);
            Assert.Equal(assigned, guide.ConflictShown);
            Assert.Equal(UiText.Get("Journey.B1.NoKey"), guide.RetryHintText);
            Assert.DoesNotContain("F3", guide.RetryHintText, StringComparison.Ordinal);
            Assert.Equal(UiText.Get("Journey.ShowPicture"), guide.TryNowButton.Content);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeycapsAcknowledgeOnlyTheirShortcutAndWaitForTheRealEvent(bool reducedMotion) => StaTestHost.Run(() =>
    {
        var guide = OnboardingGuideTests.Create(new(), new WelcomeOptions { ReducedMotion = reducedMotion });
        try
        {
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.OpenLibrary); Assert.False(guide.KeyPressed);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.CaptureRegion); Assert.True(guide.KeyPressed);
            Assert.Equal(PracticeStep.A1, guide.Session.Step);
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1);
            Assert.False(guide.KeyPressed);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen); Assert.True(guide.KeyPressed);
            guide.NotifyPinCreated(); guide.NotifyEditorPinCreated(Guid.NewGuid());
            Assert.Equal(PracticeStep.B1, guide.Session.Step);
            PracticeJourneyDriver.Complete(guide.Session);
            Assert.False(guide.KeyPressed);
            Assert.Equal(PracticeStep.B2, guide.Session.Step);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void FeaturePracticeContainsOnlyEditorPinAndItsUserResize() => StaTestHost.Run(() =>
    {
        var guide = OnboardingGuideTests.Create(new(), new WelcomeOptions { FeaturePractice = true });
        try
        {
            Assert.Equal(PracticeStep.B0, guide.Session.Step);
            guide.NotifyCaptureCompleted(true); guide.NotifyArrowAdded(); guide.NotifyPinCreated();
            Assert.Equal(PracticeStep.B0, guide.Session.Step);
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.C2);
            guide.Session.Skip();
            foreach (PracticeStep step in PracticeSession.Steps.Where(step => step <= PracticeStep.A6))
                Assert.Equal(PracticeStepResult.Pending, guide.Session.Results[step]);
            Assert.False(guide.PreferencesShown);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ConflictActionsUseTheNormalShortcutTargetAndSkipDependentSteps() => StaTestHost.Run(() =>
    {
        var guide = OnboardingGuideTests.Create(new());
        var commands = new List<GlobalHotkeyCommand>(); guide.ShortcutChangeRequested += commands.Add;
        try
        {
            guide.ChangeShortcutLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            guide.UpdateShortcutAvailability(false, false, false);
            Assert.True(guide.ConflictShown); Assert.False(guide.KeyShown);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.CaptureRegion); Assert.False(guide.KeyPressed);
            PracticeJourneyDriver.Click(guide.ChangeShortcutButton);
            Assert.Equal([GlobalHotkeyCommand.CaptureRegion, GlobalHotkeyCommand.CaptureRegion], commands);
            PracticeJourneyDriver.Click(guide.SkipStepButton);
            Assert.Equal(PracticeStep.A2, guide.Session.Step);
            Assert.Equal(PracticeStepResult.Skipped, guide.Session.Results[PracticeStep.A1]);
            guide.Session.StartGroup(PracticeStep.B0); PracticeJourneyDriver.Complete(guide.Session);
            guide.UpdateShortcutAvailability(true, true, true);
            Assert.True(guide.KeyShown); Assert.False(guide.ConflictShown);
            guide.ChangeShortcutLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal(GlobalHotkeyCommand.PasteToScreen, commands[^1]);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void FinishUsesActualReassignedKeysAndTheSelectionHelpLine() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var guide = OnboardingGuideTests.Create(new());
        try
        {
            guide.UpdateHotkeys(new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control, 0x47), PasteToScreen = new Hotkey(HotkeyModifiers.None, 0x74), OpenLibrary = Hotkey.None });
            PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1);
            Assert.Contains("F5", guide.InstructionText);
            guide.Session.Skip();
            while (guide.Session.Step != PracticeStep.Results) guide.Session.Skip();
            Assert.Equal(UiText.Get("Journey.Results.Body"), guide.InstructionText);
            string[] text = FirstRunWelcomeTests.Descendants(guide).OfType<TextBlock>().Select(t => KoreanWordWrap.Strip(t.Text)).ToArray();
            Assert.Contains(text, value => value.Contains("연습 그림 복사 · 수행", StringComparison.Ordinal));
            Assert.Contains(text, value => value.Contains("화면 위에 놓기 · 생략", StringComparison.Ordinal));
        }
        finally { guide.Close(); }
    });
}
