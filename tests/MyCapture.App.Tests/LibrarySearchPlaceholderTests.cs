using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibrarySearchPlaceholderTests
{
    [Theory]
    [InlineData("en-US", 1.0)]
    [InlineData("en-US", 1.25)]
    [InlineData("en-US", 1.5)]
    [InlineData("ko-KR", 1.0)]
    [InlineData("ko-KR", 1.25)]
    [InlineData("ko-KR", 1.5)]
    public void FitsAtTheMeasuredBoundaryAndReevaluatesMetricsAndText(string language, double scale) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var hint = new LibrarySearchPlaceholder
        {
            FullText = UiText.Get("LibraryUx_SearchPlaceholder"),
            CompactText = UiText.Get("LibraryUx_SearchPlaceholderCompact"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            UseLayoutRounding = true, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        hint.SetBinding(TextBlock.TextProperty, new Binding(nameof(LibrarySearchPlaceholder.FittedText)) { Source = hint });
        TextOptions.SetTextFormattingMode(hint, TextFormattingMode.Display);
        VisualTreeHelper.SetRootDpi(hint, new DpiScale(scale, scale));
        var host = new Decorator { Child = hint, UseLayoutRounding = true };
        VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));

        double fullWidth = Measure(hint, hint.FullText);
        double compactWidth = Measure(hint, hint.CompactText);
        Assert.True(compactWidth < fullWidth);
        foreach (double width in new[] { fullWidth + 8, fullWidth, fullWidth - 2 / scale, fullWidth + 8 })
        {
            Arrange(width);
            AssertFit();
            Assert.Equal(width < fullWidth ? hint.CompactText : hint.FullText, hint.Text);
        }

        // Change metrics without resizing the host. Both formatting modes and inherited
        // theme font properties must invalidate the decision, not just SizeChanged.
        Arrange((fullWidth + compactWidth) / 2);
        hint.FontSize = 10;
        Arrange(host.ActualWidth);
        AssertFit();
        hint.FontWeight = FontWeights.Bold;
        TextOptions.SetTextFormattingMode(hint, TextFormattingMode.Ideal);
        Arrange(host.ActualWidth);
        AssertFit();
        VisualTreeHelper.SetRootDpi(host, new DpiScale(1.5, 1.5));
        Arrange(host.ActualWidth);
        AssertFit();

        using (UiText.UseLanguage(language == "en-US" ? "ko-KR" : "en-US"))
        {
            hint.FullText = UiText.Get("LibraryUx_SearchPlaceholder");
            hint.CompactText = UiText.Get("LibraryUx_SearchPlaceholderCompact");
        }
        Arrange(host.ActualWidth);
        AssertFit();

        // Folder text stays intact even when it is too long; WPF handles its ellipsis.
        hint.CompactText = string.Empty;
        hint.FullText = "Search in a folder with a very long name that cannot fit in this field";
        Arrange(180);
        Assert.Equal(hint.FullText, hint.Text);
        Assert.True(Measure(hint, hint.Text) > hint.ActualWidth);
        Assert.Equal(TextTrimming.CharacterEllipsis, hint.TextTrimming);

        void Arrange(double width)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                host.Measure(new Size(width, 32));
                host.Arrange(new Rect(0, 0, width, 32));
                host.UpdateLayout();
            }
        }

        void AssertFit()
        {
            Assert.Equal(Measure(hint, hint.FullText) > hint.ActualWidth ? hint.CompactText : hint.FullText, hint.Text);
            Assert.True(Measure(hint, hint.Text) <= hint.ActualWidth,
                $"{language}/{scale}: '{hint.Text}' must fit {hint.ActualWidth} DIP");
        }
    });

    private static double Measure(TextBlock source, string text)
    {
        var probe = new TextBlock
        {
            Text = text, FontFamily = source.FontFamily, FontSize = source.FontSize,
            FontWeight = source.FontWeight, FontStyle = source.FontStyle, FontStretch = source.FontStretch,
            Language = source.Language, FlowDirection = source.FlowDirection, UseLayoutRounding = source.UseLayoutRounding,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(source));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(source));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }
}
