using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CoreShortcutPromptTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void UnansweredQuestionPrecedesEveryOtherLaunchWindow(bool firstRun, bool quiet, bool settings)
    {
        LaunchPresentation pending = FirstRunPlan.ForLaunch(firstRun, quiet, settings, coreShortcutsAsked: false);
        Assert.True(pending.ShowCoreShortcuts);
        Assert.False(pending.ShowWelcome);
        Assert.False(pending.OpenLibrary);

        LaunchPresentation answered = FirstRunPlan.ForLaunch(firstRun, quiet, settings, coreShortcutsAsked: true);
        Assert.False(answered.ShowCoreShortcuts);
        Assert.Equal(firstRun, answered.ShowWelcome);
        Assert.Equal(!firstRun && !quiet && !settings, answered.OpenLibrary);
    }

    [Fact]
    public void ResidentProbeOpensNoLaunchWindowFromAFreshProfile()
    {
        var settings = new AppSettings { General = { IsFirstRun = true } };
        App.ApplyResidentProbeSettings(settings);

        Assert.True(settings.Hotkeys.CoreShortcutsAsked);
        LaunchPresentation launch = FirstRunPlan.ForLaunch(settings.General.IsFirstRun, quietLaunch: true,
            openSettings: false, settings.Hotkeys.CoreShortcutsAsked);
        Assert.Equal(new LaunchPresentation(ShowWelcome: false, OpenLibrary: false, ShowCoreShortcuts: false), launch);
    }

    [Theory]
    [InlineData("ko-KR", "연습 시작", "지금 단축키로 연습")]
    [InlineData("en-US", "Start practice", "Practise with current keys")]
    public void QuestionUsesLocalizedLabelsAndOverwriteIsDefault(string language, string overwrite, string keep) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        WelcomeWindow dialog = CreateConsent();
        bool? answer = null;
        dialog.CoreShortcutsAnswered += accepted => answer = accepted;
        try
        {
            Button[] buttons = [dialog.TryNowButton, dialog.SkipButton];
            Assert.Equal(new[] { overwrite, keep }, buttons.Select(button => (string)button.Content));
            Assert.True(buttons[0].IsDefault);
            Assert.False(buttons[0].IsCancel);
            Assert.False(buttons[1].IsCancel);
            Assert.False(buttons[1].IsDefault);
            Assert.Null(answer);
            Assert.Equal(WelcomeStep.Consent, dialog.CurrentStep);
            Assert.Contains("Ctrl+Shift+C", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains("Ctrl+Shift+X", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains("Ctrl+Shift+Z", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains("F3", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains(UiText.Get("SettingsUx_TabShortcuts"), UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(answer);
            Assert.Equal(WelcomeStep.Capture, dialog.CurrentStep);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void OwnerlessQuestionOpensCentredOnTheScreen() => StaTestHost.Run(() =>
    {
        // Do not use the offscreen helper here: it deliberately overrides startup placement.
        var dialog = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor,
            options: new WelcomeOptions { ShowCoreShortcutsConsent = true, ReducedMotion = true,
                MediaFactory = () => new OnboardingGuideTests.FakeMedia() });
        try
        {
            Assert.Equal(WindowStartupLocation.CenterScreen, dialog.WindowStartupLocation);
            Assert.Null(dialog.Owner);
            Assert.True(dialog.ShowInTaskbar);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ClosingQuestionWithoutChoosingKeepsTheCancelResult() => StaTestHost.Run(() =>
    {
        WelcomeWindow dialog = CreateConsent();
        var answers = new List<bool>();
        dialog.CoreShortcutsAnswered += answers.Add;
        dialog.Close();
        Assert.False(Assert.Single(answers));
        Assert.Equal(WelcomeOutcome.Closed, dialog.Outcome);
    });

    [Fact]
    public void KeepCurrentContinuesPracticeAndClosingDoesNotAnswerTwice() => StaTestHost.Run(() =>
    {
        WelcomeWindow dialog = CreateConsent();
        var answers = new List<bool>();
        dialog.CoreShortcutsAnswered += answers.Add;
        dialog.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Assert.Single(answers));
        Assert.Equal(WelcomeStep.Capture, dialog.CurrentStep);
        dialog.Close();
        Assert.Single(answers);
    });

    private static WelcomeWindow CreateConsent() => OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(),
        new WelcomeOptions { ShowCoreShortcutsConsent = true });

    [Fact]
    public void OrdinaryDialogKeepsItsExistingCancelLabel() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var dialog = new ThemedDialog("Body", "Title", MessageBoxButton.OKCancel, MessageBoxResult.OK);
        try
        {
            var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(dialog.Content).Content);
            Assert.Equal(UiText.Get("Dialog.Cancel"), panel.Children.OfType<WrapPanel>().Single()
                .Children.OfType<Button>().Single(button => button.IsCancel).Content);
        }
        finally { dialog.Close(); }
    });
}
