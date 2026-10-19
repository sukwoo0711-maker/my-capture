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

/// <summary>The receipt labels its primary action and gives every icon an accessible name.</summary>
public sealed class QuickAccessCardLabelTests
{
    [Theory]
    [InlineData("ko", new[] { "편집", "핀", "저장", "텍스트 복사", "복사" }, "자동으로 닫지 않음", "자동으로 닫지 않음")]
    [InlineData("en", new[] { "Edit", "Pin", "Save", "Copy text", "Copy" }, "Keep open", "Keep open")]
    public void EditHasAVisibleNameAndSecondaryIconsRetainTheirAccessibleNames(string language, string[] names,
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
                Assert.Equal(names, actions.Select(AutomationProperties.GetName));
                Assert.Equal(names[0], Assert.IsType<TextBlock>(actions[0].Content).Text);
                Assert.All(actions.Skip(1), button => Assert.IsType<SymbolIcon>(button.Content));
                // The key is not written on the face; it is the tooltip's and the accelerator key's.
                Assert.All(actions, button => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetAcceleratorKey(button))));
                ToggleButton keepToggle = card.KeepToggleForTest;
                Assert.IsType<SymbolIcon>(keepToggle.Content);
                Assert.Equal(keepName, keepToggle.ToolTip);
                Assert.Equal(keepName, AutomationProperties.GetName(keepToggle));
                Assert.Equal(keepTip, AutomationProperties.GetHelpText(keepToggle));
                // Keep belongs to the receipt line; close remains over the preview.
                var rows = ((StackPanel)((Border)card.Content).Child).Children.OfType<Grid>().ToArray();
                Assert.Same(rows[1], keepToggle.Parent);
                Assert.Contains(rows[0].Children.OfType<Button>(), button =>
                    AutomationProperties.GetName(button) == UiText.Get("Dialog.Close"));
                // The card's own tooltip is one short line, not a paragraph of keys.
                Assert.True(((string)card.ToolTip).Length <= 40, (string)card.ToolTip);
            }
            finally { card.Close(); }
        });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void PrimaryActionNameAndSecondaryIconsFitWithoutClipping(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var card = new QuickAccessWindow(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"));
        try
        {
            card.IsKept = true;
            Border host = QuickAccessDetachedFixture.Layout(card);
            // Opaque capture-excluded cards omit the two 8-DIP shadow margins.
            Assert.Equal(card.AllowsTransparency ? 344 : 328, host.ActualWidth);
            foreach (Button button in card.ActionButtonsForTest)
            {
                Assert.Equal(32, button.ActualHeight);
                FrameworkElement face = button.Content as FrameworkElement
                    ?? Assert.Single(QuickAccessDetachedFixture.Descendants(button).OfType<TextBlock>(),
                        text => text.Text == UiText.Get("QuickAccess.Edit"));
                Rect bounds = face.TransformToAncestor(button).TransformBounds(new Rect(face.RenderSize));
                Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= button.ActualWidth + 0.5
                    && bounds.Bottom <= button.ActualHeight + 0.5, $"{language}: button content is clipped: {bounds}");
                if (face is not TextBlock name) continue;
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
