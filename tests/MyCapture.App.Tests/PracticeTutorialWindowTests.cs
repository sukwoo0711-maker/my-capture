using System.Windows;
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
    public void UnavailablePinRetryOffersTheEditorActionWithoutAFailedShortcut(bool assigned) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var keys = new HotkeySettings
        {
            PasteToScreen = assigned ? new Hotkey(HotkeyModifiers.None, Hotkey.VkF3) : Hotkey.None,
        };
        WelcomeWindow guide = OnboardingGuideTests.Create(new(), keys: keys);
        try
        {
            guide.UpdateShortcutAvailability(capture: true, pin: false, library: true);
            OnboardingGuideTests.AdvanceTo(guide, WelcomeStep.Pin);
            guide.ShowRetryHint();
            Assert.False(guide.KeyShown);
            Assert.Equal(assigned, guide.ConflictShown);
            Assert.Equal(UiText.Get("Practice.PinInstructionNoKey"), guide.RetryHintText);
            Assert.DoesNotContain("F3", guide.RetryHintText, StringComparison.Ordinal);
            Assert.DoesNotContain("단축키를 누르십시오", guide.RetryHintText, StringComparison.Ordinal);
        }
        finally { guide.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeycapsAcknowledgeOnlyTheirShortcutAndWaitForTheRealEvent(bool reducedMotion) => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = OnboardingGuideTests.Create(new(), new WelcomeOptions { ReducedMotion = reducedMotion });
        try
        {
            Assert.False(guide.KeyPressed);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.OpenLibrary);
            Assert.False(guide.KeyPressed);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.CaptureRegion);
            Assert.True(guide.KeyPressed);
            Assert.Equal(WelcomeStep.Capture, guide.CurrentStep);
            guide.NotifyCaptureCompleted(true);
            Assert.False(guide.KeyPressed);
            guide.NotifyArrowShortcutFired();
            Assert.True(guide.KeyPressed);
            Assert.Equal(WelcomeStep.Draw, guide.CurrentStep);
            guide.NotifyArrowAdded();
            Assert.False(guide.KeyPressed);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
            Assert.True(guide.KeyPressed);
            guide.NotifyPinCreated();
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
            Guid pin = Guid.NewGuid();
            guide.NotifyEditorPinCreated(pin);
            Assert.False(guide.KeyPressed);
            Assert.Equal(WelcomeStep.Resize, guide.CurrentStep);
            guide.NotifyPinZoomChanged(Guid.NewGuid());
            Assert.Equal(WelcomeStep.Resize, guide.CurrentStep);
            guide.NotifyPinZoomChanged(pin);
            Assert.Equal(WelcomeStep.Library, guide.CurrentStep);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.OpenLibrary);
            Assert.True(guide.KeyPressed);
            Assert.Equal(WelcomeStep.Library, guide.CurrentStep);
            guide.NotifyLibraryOpened();
            Assert.Equal(WelcomeStep.Finish, guide.CurrentStep);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void FeaturePracticeContainsOnlyEditorPinAndItsUserResize() => StaTestHost.Run(() =>
    {
        WelcomeWindow guide = OnboardingGuideTests.Create(new(), new WelcomeOptions { FeaturePractice = true });
        try
        {
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
            Assert.StartsWith("1 / 2", guide.ProgressText, StringComparison.Ordinal);
            guide.NotifyCaptureCompleted(true);
            guide.NotifyArrowAdded();
            guide.NotifyPinCreated();
            Assert.Equal(WelcomeStep.Pin, guide.CurrentStep);
            Guid pin = Guid.NewGuid();
            guide.NotifyEditorPinCreated(pin);
            Assert.Equal(WelcomeStep.Resize, guide.CurrentStep);
            Assert.StartsWith("2 / 2", guide.ProgressText, StringComparison.Ordinal);
            guide.NotifyPinZoomChanged(pin);
            Assert.Equal(WelcomeStep.Finish, guide.CurrentStep);
            Assert.False(guide.PreferencesShown);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void ConflictActionsUseTheNormalShortcutTargetAndSkipDependentSteps() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = OnboardingGuideTests.Create(new());
        var commands = new List<GlobalHotkeyCommand>();
        guide.ShortcutChangeRequested += commands.Add;
        try
        {
            guide.ChangeShortcutLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal([GlobalHotkeyCommand.CaptureRegion], commands);
            guide.UpdateShortcutAvailability(false, false, false);
            Assert.True(guide.ConflictShown);
            Assert.False(guide.KeyShown);
            guide.NotifyHotkeyFired(GlobalHotkeyCommand.CaptureRegion);
            Assert.False(guide.KeyPressed);
            guide.ChangeShortcutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([GlobalHotkeyCommand.CaptureRegion, GlobalHotkeyCommand.CaptureRegion], commands);
            guide.SkipStepButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(WelcomeStep.Library, guide.CurrentStep);
            guide.UpdateShortcutAvailability(true, true, true);
            Assert.False(guide.ConflictShown);
            Assert.True(guide.KeyShown);
            guide.ChangeShortcutLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.Equal(GlobalHotkeyCommand.OpenLibrary, commands[^1]);
        }
        finally { guide.Close(); }
    });

    [Fact]
    public void FinishUsesActualReassignedKeysAndTheSelectionHelpLine() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        WelcomeWindow guide = OnboardingGuideTests.Create(new());
        try
        {
            var keys = new HotkeySettings { Capture = new Hotkey(HotkeyModifiers.Control, 0x47),
                PasteToScreen = new Hotkey(HotkeyModifiers.None, 0x74), OpenLibrary = Hotkey.None };
            guide.UpdateHotkeys(keys);
            OnboardingGuideTests.AdvanceTo(guide, WelcomeStep.Finish);
            Assert.Equal("선택 화면에서 F1로 단축키 안내판을 볼 수 있습니다.", guide.InstructionText);
            string[] texts = FirstRunWelcomeTests.Descendants(guide).OfType<TextBlock>()
                .Select(text => KoreanWordWrap.Strip(text.Text)).ToArray();
            Assert.Contains("Ctrl+G", texts);
            Assert.Contains("F5", texts);
            Assert.Contains("A", texts);
            Assert.Contains("알림 영역 아이콘", texts);
        }
        finally { guide.Close(); }
    });
}
