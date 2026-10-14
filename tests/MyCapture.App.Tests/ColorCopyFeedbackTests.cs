using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ColorCopyFeedbackTests
{
    [Theory]
    [InlineData(0, 0, 0, "#000000")]
    [InlineData(255, 255, 255, "#FFFFFF")]
    [InlineData(96, 64, 32, "#604020")]
    public void FeedbackKeepsTheSampleColorAndHexWhenTheCopiedFormatIsRgb(byte red, byte green, byte blue, string hex)
    {
        string value = $"rgb({red}, {green}, {blue})";
        var feedback = new ColorCopyFeedback(value, red, green, blue);
        Assert.Equal(value, feedback.Value);
        Assert.Equal(Color.FromRgb(red, green, blue), feedback.ChipColor);
        Assert.Equal(hex, feedback.HexValue);
        Assert.Equal(UiText.Format("Tools_ColorCopied", value), feedback.Message);
    }

    [Theory]
    [InlineData(AppTheme.Daylight, false)]
    [InlineData(AppTheme.Midnight, false)]
    [InlineData(AppTheme.Glass, false)]
    [InlineData(AppTheme.GlassLight, false)]
    [InlineData(AppTheme.Workspace, false)]
    [InlineData(AppTheme.HighContrast, false)]
    [InlineData(AppTheme.Glass, true)]
    public void NotificationShowsFrozenColorChipBesideReadableTextInEveryTheme(AppTheme theme, bool systemHighContrast) => StaTestHost.Run(() =>
    {
        var feedback = new ColorCopyFeedback("#FFFFFF", 255, 255, 255);
        var content = Assert.IsType<DockPanel>(ThemedShellPresenter.CreateColorCopyContent(feedback));
        content.Resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(theme, content.Resources, systemHighContrast: systemHighContrast);
        content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text.Primary");
        Assert.Same(feedback, content.DataContext);
        var outline = Assert.IsType<Border>(content.Children[0]);
        var chip = Assert.IsType<Border>(outline.Child);
        var brush = Assert.IsType<SolidColorBrush>(chip.Background);
        Assert.Equal(feedback.ChipColor, brush.Color);
        Assert.True(brush.IsFrozen);
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(outline.BorderBrush).Color);
        Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(chip.BorderBrush).Color);
        Assert.Equal(Dock.Left, DockPanel.GetDock(outline));
        var labels = Assert.IsType<StackPanel>(content.Children[1]);
        var text = Assert.IsType<TextBlock>(Assert.Single(labels.Children.Cast<UIElement>()));
        Assert.Equal(feedback.Message, text.Text.Replace("\u2060", string.Empty, StringComparison.Ordinal));
        Assert.Same(content.Resources["Text.Primary"], text.Foreground);
        content.Measure(new Size(320, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        Assert.True(content.DesiredSize.Width <= 320);
        Assert.True(chip.ActualWidth > 0);
    });

    [Fact]
    public void ConfirmationRemainsAfterTheSelectorAndUsesTheShellCountdown() => StaTestHost.Run(() =>
    {
        using var presenter = new ThemedShellPresenter(() => false);
        presenter.ShowColorCopy(new ColorCopyFeedback("#604020", 96, 64, 32));
        Assert.Equal((true, FeedbackKind.Confirmation, (TimeSpan?)FeedbackTiming.Confirmation), presenter.NotificationForTest);
        Assert.False(presenter.NotificationWindowForTest!.ShowActivated);
        presenter.AdvanceNotificationForTest(FeedbackTiming.Confirmation);
        Assert.False(presenter.NotificationForTest.Open);
    });
}
