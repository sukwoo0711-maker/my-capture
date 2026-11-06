using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
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
        Assert.False(pending.CoreShortcutsNotice);

        LaunchPresentation forcedPending = FirstRunPlan.ForLaunch(firstRun, quiet, settings,
            coreShortcutsAsked: false, coreShortcutsForced: true);
        Assert.Equal(pending, forcedPending);

        LaunchPresentation answered = FirstRunPlan.ForLaunch(firstRun, quiet, settings, coreShortcutsAsked: true);
        Assert.False(answered.ShowCoreShortcuts);
        Assert.Equal(firstRun, answered.ShowWelcome);
        Assert.Equal(!firstRun && !quiet && !settings, answered.OpenLibrary);
        Assert.False(answered.CoreShortcutsNotice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AssignedCoreShortcutsShowANoticeOnlyOnFirstRun(bool quiet, bool settings)
    {
        LaunchPresentation notice = FirstRunPlan.ForLaunch(isFirstRun: true, quiet, settings,
            coreShortcutsAsked: true, coreShortcutsForced: true);
        Assert.Equal(new LaunchPresentation(ShowWelcome: false, OpenLibrary: false,
            ShowCoreShortcuts: true, CoreShortcutsNotice: true), notice);

        LaunchPresentation welcome = FirstRunPlan.ForLaunch(isFirstRun: true, quiet, settings,
            coreShortcutsAsked: true, coreShortcutsForced: false);
        Assert.Equal(new LaunchPresentation(ShowWelcome: true, OpenLibrary: false), welcome);

        LaunchPresentation later = FirstRunPlan.ForLaunch(isFirstRun: false, quiet, settings,
            coreShortcutsAsked: true, coreShortcutsForced: true);
        Assert.Equal(new LaunchPresentation(ShowWelcome: false, OpenLibrary: !quiet && !settings), later);
    }

    [Fact]
    public void ResidentProbeOpensNoLaunchWindowFromAFreshProfile()
    {
        // A fresh profile loads the new-installation settings, which handle the core keys first.
        AppSettings settings = NewInstallDefaults.Create();
        settings.General.IsFirstRun = true;
        Assert.True(settings.Hotkeys.ForceCoreShortcuts);
        App.ApplyResidentProbeSettings(settings);

        Assert.True(settings.Hotkeys.CoreShortcutsAsked);
        // The measured copy leaves those keys to the copy the user runs, so it installs no keyboard hook.
        Assert.False(settings.Hotkeys.ForceCoreShortcuts);
        LaunchPresentation launch = FirstRunPlan.ForLaunch(settings.General.IsFirstRun, quietLaunch: true,
            openSettings: false, settings.Hotkeys.CoreShortcutsAsked, settings.Hotkeys.ForceCoreShortcuts);
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
            AssertCoreShortcutPage(dialog, notice: false);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(answer);
            Assert.Equal(WelcomeStep.Capture, dialog.CurrentStep);
            AssertCoreShortcutPageHidden(dialog);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "확인", "단축키 설정")]
    [InlineData("en-US", "OK", "Shortcut settings")]
    public void NoticeUsesLocalizedLabelsAndAcknowledgementIsDefault(string language, string ok, string settings) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        WelcomeWindow dialog = CreateNotice();
        try
        {
            Assert.Equal(WelcomeStep.Consent, dialog.CurrentStep);
            Assert.Equal(UiText.Get("CoreShortcuts.NoticeTitle"), dialog.TitleText);
            Assert.Equal(ok, dialog.TryNowButton.Content);
            Assert.Equal(settings, dialog.SkipButton.Content);
            Assert.True(dialog.TryNowButton.IsDefault);
            Assert.False(dialog.TryNowButton.IsCancel);
            Assert.False(dialog.SkipButton.IsDefault);
            Assert.False(dialog.SkipButton.IsCancel);
            Assert.Equal(settings, dialog.SkipButton.ToolTip);
            AssertCoreShortcutPage(dialog, notice: true);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoticeAcknowledgementContinuesTheGuideAndRaisesOnlyOneNoticeEvent(bool openSettings) => StaTestHost.Run(() =>
    {
        WelcomeWindow dialog = CreateNotice();
        var acknowledgements = new List<bool>();
        var answers = new List<bool>();
        dialog.CoreShortcutsNoticeAcknowledged += acknowledgements.Add;
        dialog.CoreShortcutsAnswered += answers.Add;
        try
        {
            (openSettings ? dialog.SkipButton : dialog.TryNowButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(openSettings, Assert.Single(acknowledgements));
            Assert.Empty(answers);
            Assert.Equal(WelcomeStep.Capture, dialog.CurrentStep);
            AssertCoreShortcutPageHidden(dialog);

            dialog.TryNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(openSettings, Assert.Single(acknowledgements));
            Assert.Empty(answers);
        }
        finally { dialog.Close(); }
        Assert.Equal(openSettings, Assert.Single(acknowledgements));
        Assert.Empty(answers);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingNoticeRaisesNeitherAcknowledgementNorAnswer(bool escape) => StaTestHost.Run(() =>
    {
        WelcomeWindow dialog = CreateNotice();
        var acknowledgements = new List<bool>();
        var answers = new List<bool>();
        dialog.CoreShortcutsNoticeAcknowledged += acknowledgements.Add;
        dialog.CoreShortcutsAnswered += answers.Add;
        try
        {
            dialog.Show();
            if (escape)
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                dialog.RaiseEvent(key);
                Assert.True(key.Handled);
            }
            else dialog.Close();
            Assert.False(dialog.IsVisible);
            Assert.Equal(WelcomeOutcome.Closed, dialog.Outcome);
            Assert.Empty(acknowledgements);
            Assert.Empty(answers);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", false)]
    [InlineData("ko-KR", true)]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    public void CoreShortcutColumnsAndParagraphsUseTheInstructionTextScale(string language, bool notice) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        const double scale = 1.5;
        WelcomeWindow dialog = OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(),
            new WelcomeOptions { ShowCoreShortcutsConsent = !notice, CoreShortcutsNotice = notice, TextScale = scale });
        try
        {
            AssertCoreShortcutPage(dialog, notice);
            TextBlock instruction = dialog.MotionParts.OfType<TextBlock>()
                .Single(block => KoreanWordWrap.Strip(block.Text) == dialog.InstructionText);
            Assert.Equal(TypeScale.BodyLarge * scale, instruction.FontSize);
            Assert.All(dialog.CoreShortcutKeyList.Children.OfType<TextBlock>(), block => Assert.Equal(instruction.FontSize, block.FontSize));
            Assert.Equal(instruction.FontSize, dialog.CoreShortcutParagraphs.FontSize);
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

    private static WelcomeWindow CreateNotice() => OnboardingGuideTests.Create(new OnboardingGuideTests.FakeClipboard(),
        new WelcomeOptions { CoreShortcutsNotice = true });

    private static void AssertCoreShortcutPage(WelcomeWindow dialog, bool notice)
    {
        Assert.Equal(UiText.Get(notice ? "CoreShortcuts.NoticeBody" : "CoreShortcuts.Body"), dialog.InstructionText);
        Grid list = dialog.CoreShortcutKeyList;
        Assert.Equal(Visibility.Visible, list.Visibility);
        Assert.Equal(4, list.RowDefinitions.Count);
        Assert.Equal(2, list.ColumnDefinitions.Count);
        Assert.True(list.ColumnDefinitions[0].Width.IsAuto);
        Assert.True(list.ColumnDefinitions[1].Width.IsStar);
        Assert.Equal(new Thickness(0, 0, 0, 14), list.Margin);
        Assert.Equal(8, list.Children.Count);
        string[] chords = [CoreShortcuts.Capture.ToString(), CoreShortcuts.RecordRegion.ToString(),
            CoreShortcuts.OpenLibrary.ToString(), CoreShortcuts.PasteToScreen.ToString()];
        string[] names = ["CoreShortcuts.Capture", "CoreShortcuts.RecordRegion", "CoreShortcuts.OpenLibrary", "CoreShortcuts.PasteToScreen"];

        list.Measure(new Size(650, double.PositiveInfinity));
        list.Arrange(new Rect(new Point(), new Size(650, list.DesiredSize.Height)));
        list.UpdateLayout();
        Assert.True(list.ColumnDefinitions[0].ActualWidth > 0);
        double? nameX = null;
        double? keyWidth = null;
        for (int row = 0; row < chords.Length; row++)
        {
            TextBlock key = Assert.Single(list.Children.OfType<TextBlock>(), block => Grid.GetRow(block) == row && Grid.GetColumn(block) == 0);
            TextBlock name = Assert.Single(list.Children.OfType<TextBlock>(), block => Grid.GetRow(block) == row && Grid.GetColumn(block) == 1);
            Assert.Equal(chords[row], key.Text);
            Assert.Equal(UiText.Get(names[row]), KoreanWordWrap.Strip(name.Text));
            Assert.Equal(FontWeights.SemiBold, key.FontWeight);
            Assert.Equal(FontWeights.Normal, name.FontWeight);
            Assert.Equal(TextWrapping.Wrap, name.TextWrapping);
            double currentNameX = name.TranslatePoint(new Point(), list).X;
            nameX ??= currentNameX;
            keyWidth ??= key.ActualWidth;
            Assert.InRange(Math.Abs(currentNameX - nameX.Value), 0, 0.5);
            Assert.InRange(Math.Abs(key.ActualWidth - keyWidth.Value), 0, 0.5);
            Assert.Equal(16, key.Margin.Right + name.Margin.Left);
            Assert.Equal(row < 3 ? 4 : 0, key.Margin.Bottom);
            Assert.Equal(row < 3 ? 4 : 0, name.Margin.Bottom);
        }

        string paragraphs = KoreanWordWrap.Strip(dialog.CoreShortcutParagraphs.Text);
        Assert.Equal(Visibility.Visible, dialog.CoreShortcutParagraphs.Visibility);
        Assert.Equal(UiText.Get("CoreShortcuts.Takeover") + "\n\n" + UiText.Get(notice ? "CoreShortcuts.NoticeChange" : "CoreShortcuts.Question"), paragraphs);
        Assert.Contains(UiText.Get("SettingsUx_TabShortcuts"), paragraphs, StringComparison.Ordinal);
        string helpText = AutomationProperties.GetHelpText(dialog.TryNowButton);
        Assert.Contains(dialog.InstructionText, helpText, StringComparison.Ordinal);
        for (int row = 0; row < chords.Length; row++)
            Assert.Contains(chords[row] + " " + UiText.Get(names[row]), helpText, StringComparison.Ordinal);
        Assert.Contains(paragraphs, helpText, StringComparison.Ordinal);
        int instructionIndex = dialog.MotionParts.ToList().FindIndex(part => part is TextBlock block && KoreanWordWrap.Strip(block.Text) == dialog.InstructionText);
        Assert.True(instructionIndex >= 0);
        Assert.Same(list, dialog.MotionParts[instructionIndex + 1]);
        Assert.Same(dialog.CoreShortcutParagraphs, dialog.MotionParts[instructionIndex + 2]);
    }

    private static void AssertCoreShortcutPageHidden(WelcomeWindow dialog)
    {
        Assert.Equal(Visibility.Collapsed, dialog.CoreShortcutKeyList.Visibility);
        Assert.Equal(Visibility.Collapsed, dialog.CoreShortcutParagraphs.Visibility);
    }

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
