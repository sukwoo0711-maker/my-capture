using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The quick access card names its actions and says plainly what keeping it does.</summary>
public sealed class QuickAccessCardLabelTests
{
    [Theory]
    [InlineData("ko", new[] { "복사", "편집", "핀", "저장", "텍스트 복사" }, "유지", "카드 유지", "자동으로 닫지 않기")]
    [InlineData("en", new[] { "Copy", "Edit", "Pin", "Save", "Copy text" }, "Keep", "Keep card open", "Keep open")]
    public void ActionsShowAShortNameUnderTheIconAndKeepIsAWord(string language, string[] names, string keep,
        string keepName, string keepTip) =>
        StaTestHost.Run(() =>
        {
            using var culture = UiText.UseLanguage(language);
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
            try
            {
                IReadOnlyList<Button> actions = card.ActionButtonsForTest;
                Assert.Equal(names, actions.Select(button => LogicalText(button)));
                // The key is not written on the face; it is the tooltip's and the accelerator key's.
                Assert.All(actions, button => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button))));
                // A word, not a padlock glyph; the visible word is part of its accessible name.
                ToggleButton keepToggle = card.KeepToggleForTest;
                Assert.Equal(keep, keepToggle.Content);
                Assert.Equal(keepTip, keepToggle.ToolTip);
                Assert.Equal(keepName, AutomationProperties.GetName(keepToggle));
                Assert.Contains(keep, keepName, StringComparison.Ordinal);
                Assert.Equal(keepTip, AutomationProperties.GetHelpText(keepToggle));
                // It sits beside close, in the row that decides how long the card stays.
                var header = ((StackPanel)((Border)card.Content).Child).Children.OfType<Grid>().First();
                Assert.Same(header, keepToggle.Parent);
                // The card's own tooltip is one short line, not a paragraph of keys.
                Assert.True(((string)card.ToolTip).Length <= 40, (string)card.ToolTip);
            }
            finally { card.Close(); }
        });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void ActionNamesFitUnderTheirIconsWithoutTrimming(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        try
        {
            card.IsKept = true;
            Ocr410Fixture.LoadTheme(card);
            card.Show();
            card.UpdateLayout();
            // The card keeps its width with five actions.
            Assert.Equal(344, card.ActualWidth);
            foreach (Button button in card.ActionButtonsForTest)
            {
                TextBlock name = FaceName(button)!;
                var probe = new TextBlock
                {
                    Text = name.Text, FontFamily = name.FontFamily, FontSize = name.FontSize,
                    FontWeight = name.FontWeight, FontStyle = name.FontStyle, FontStretch = name.FontStretch,
                    UseLayoutRounding = name.UseLayoutRounding,
                };
                TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(name));
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(name.ActualWidth + 0.5 >= probe.DesiredSize.Width,
                    $"{language}: '{name.Text}' needs {probe.DesiredSize.Width:0.#} DIP but has {name.ActualWidth:0.#} DIP");
            }
        }
        finally { card.Close(); }
    });

    /// <summary>The visible name on a button face (the icon is a shape; a key hint, when shown, is not a name).</summary>
    private static string LogicalText(DependencyObject root) => FaceName(root)?.Text ?? string.Empty;

    private static TextBlock? FaceName(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is StackPanel { Children.Count: 2 } face && face.Children[0] is MyCapture.App.Themes.SymbolIcon
                && face.Children[1] is TextBlock name) return name;
            if (child is DependencyObject node && FaceName(node) is { } found) return found;
        }

        return null;
    }
}

/// <summary>The help names the frame keys the recording editor really uses.</summary>
public sealed class ShortcutHelpFrameKeysTests
{
    [Fact]
    public void FrameStepKeysMatchTheEditorsStepButtons() => StaTestHost.Run(() =>
    {
        Assert.Equal(", / .", ShortcutHelpWindow.FrameStepKeys);
        Assert.Equal(",", ShortcutCatalog.Get(ShortcutScope.Video, "PreviousFrame").Hint);
        Assert.Equal("PreviousFrame", ShortcutCatalog.Match(ShortcutScope.Video, System.Windows.Input.Key.OemComma, System.Windows.Input.ModifierKeys.None));
        Assert.Equal("NextFrame", ShortcutCatalog.Match(ShortcutScope.Video, System.Windows.Input.Key.OemPeriod, System.Windows.Input.ModifierKeys.None));

        using var language = UiText.UseLanguage("ko-KR");
        var help = new ShortcutHelpWindow();
        try
        {
            var tabs = ((DockPanel)help.Content).Children.OfType<TabControl>().Single();
            TabItem video = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("Help.Video")));
            var rows = ((StackPanel)((ScrollViewer)video.Content).Content).Children.OfType<UniformGrid>();
            UniformGrid frame = rows.Single(row => ShortcutHelpRowAssertions.Title(row) == UiText.Get("Help.Frame"));
            Assert.Equal(", / .", ShortcutHelpRowAssertions.Keys(frame));
            // The window title is in the title bar only.
            Assert.DoesNotContain(((DockPanel)help.Content).Children.OfType<TextBlock>(), text => text.Text == help.Title);
        }
        finally { help.Close(); }
    });

    /// <summary>The pin tab is named like the settings group, and F3 says what it pins.</summary>
    [Theory]
    [InlineData("ko-KR", "핀", "클립보드를 핀으로 띄우기")]
    [InlineData("en-US", "Pins", "Pin clipboard to screen")]
    public void PinTabNamesThePinsAndF3SaysItPinsTheClipboard(string language, string tab, string pasteToScreen) =>
        StaTestHost.Run(() =>
        {
            using var scope = UiText.UseLanguage(language);
            var help = new ShortcutHelpWindow();
            try
            {
                var tabs = ((DockPanel)help.Content).Children.OfType<TabControl>().Single();
                TabItem pins = tabs.Items.OfType<TabItem>().Single(item => Equals(item.Header, tab));
                var rows = ((StackPanel)((ScrollViewer)pins.Content).Content).Children.OfType<UniformGrid>();
                Assert.Equal(pasteToScreen, ShortcutHelpRowAssertions.Title(rows.First()));
            }
            finally { help.Close(); }
        });
}

/// <summary>Failure dialogs say what to do next, and a failed start does not say "return to your work".</summary>
public sealed class AppFailureGuidanceTests
{
    [Fact]
    public void UnhandledAndStartupFailuresNameTheNextStep()
    {
        using var language = UiText.UseLanguage("ko-KR");
        string unhandled = ThemedDialog.UnhandledMessage(new InvalidOperationException("The tray icon has not been initialized."));
        Assert.StartsWith(UiText.Get("Dialog.Unexpected"), unhandled, StringComparison.Ordinal);
        Assert.Contains("다시 실행하십시오", unhandled, StringComparison.Ordinal);
        Assert.Contains("자세히", unhandled, StringComparison.Ordinal);
        Assert.DoesNotContain("tray icon", unhandled, StringComparison.Ordinal);
        Assert.Equal(UiText.Get("Dialog.Storage") + "\n\n" + UiText.Get("Onboard_ErrorNextStep"),
            ThemedDialog.UnhandledMessage(new IOException("private-path")));

        Assert.Equal("MyCapture를 시작할 수 없습니다", UiText.Get("Onboard_StartFailureTitle"));
        Assert.DoesNotContain("작업 화면", UiText.Get("Onboard_StartFailureBody"), StringComparison.Ordinal);
        Assert.Contains("다시 실행하십시오", UiText.Get("Onboard_StartFailureBody"), StringComparison.Ordinal);

        // The fallback (when even the themed dialog fails) keeps its plain message.
        string? fallback = null;
        var changed = new InvalidOperationException("x");
        ThemedDialog.ShowUnhandled(changed, _ => throw new InvalidOperationException("theme failure"), (text, _) => fallback = text);
        Assert.Equal(ThemedDialog.RecoveryMessage(changed), fallback);
    }
}
