using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
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

    [Theory]
    [InlineData("ko-KR", "덮어쓰기", "그대로 두기")]
    [InlineData("en-US", "Overwrite", "Keep current")]
    public void QuestionUsesLocalizedLabelsAndOverwriteIsDefault(string language, string overwrite, string keep) => StaTestHost.Run(() =>
    {
        using var locale = UiText.UseLanguage(language);
        ThemedDialog dialog = App.CreateCoreShortcutsQuestion();
        try
        {
            var scroll = Assert.IsType<ScrollViewer>(dialog.Content);
            var panel = Assert.IsType<StackPanel>(scroll.Content);
            Button[] buttons = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
            Assert.Equal(new[] { overwrite, keep }, buttons.Select(button => (string)button.Content));
            Assert.True(buttons[0].IsDefault);
            Assert.False(buttons[0].IsCancel);
            Assert.True(buttons[1].IsCancel);
            Assert.False(buttons[1].IsDefault);
            Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
            Assert.Contains("Ctrl+Shift+C", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains("Ctrl+Shift+X", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains("Ctrl+Shift+Z", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains("F3", UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            Assert.Contains(UiText.Get("SettingsUx_TabShortcuts"), UiText.Get("CoreShortcuts.Body"), StringComparison.Ordinal);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MessageBoxResult.OK, dialog.Result);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void OwnerlessQuestionOpensCentredOnTheScreen() => StaTestHost.Run(() =>
    {
        ThemedDialog dialog = App.CreateCoreShortcutsQuestion();
        try
        {
            Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
            ThemedDialog.PlaceOwnerless(dialog, MessageBoxImage.None);
            Assert.Equal(WindowStartupLocation.CenterScreen, dialog.WindowStartupLocation);
            Assert.Null(dialog.Owner);
            Assert.True(dialog.ShowInTaskbar);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void ClosingQuestionWithoutChoosingKeepsTheCancelResult() => StaTestHost.Run(() =>
    {
        ThemedDialog dialog = App.CreateCoreShortcutsQuestion();
        dialog.Close();
        Assert.Equal(MessageBoxResult.Cancel, dialog.Result);
    });

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
