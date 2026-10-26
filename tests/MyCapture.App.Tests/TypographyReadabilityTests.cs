using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class TypographyReadabilityTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void BundledGlyphsRenderAtDesktopDpis(int dpi) => StaTestHost.Run(() =>
    {
        var root = new StackPanel { Width = 680, Background = Brushes.White, Margin = new Thickness(20) };
        VisualTreeHelper.SetRootDpi(root, new DpiScale(dpi / 96d, dpi / 96d));
        ThemeFonts.ApplyText(root);
        root.Language = XmlLanguage.GetLanguage("ko-KR");
        AddRows(root, "UI", ThemeFonts.Ui, ThemeFonts.Formatting);
        AddRows(root, new AnnotationDefaults().FontFamily, FontCatalog.Resolve(new AnnotationDefaults().FontFamily), TextFormattingMode.Ideal);
        root.Measure(new Size(720, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        foreach (TextBlock label in root.Children.OfType<TextBlock>())
        {
            Assert.True(label.ActualWidth > 0);
            Assert.True(label.ActualHeight >= label.FontSize);
            Assert.True(label.DesiredSize.Width <= root.Width);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling((root.ActualWidth + 40) * dpi / 96),
            (int)Math.Ceiling((root.ActualHeight + 40) * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Contains(pixels, value => value > 0 && value < 255);
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_TYPOGRAPHY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        directory = DiagnosticOutputPaths.Create(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(DiagnosticOutputPaths.Child(directory, $"typography-{dpi}dpi.png"));
        encoder.Save(output);
    });

    private static void AddRows(Panel panel, string title, FontFamily family, TextFormattingMode mode)
    {
        panel.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Segoe UI"), FontSize = 16,
            Foreground = Brushes.Black, Margin = new Thickness(0, 12, 0, 4) });
        foreach (double size in new[] { 12d, 14d, 18d })
        {
            var label = new TextBlock
            {
                Text = $"{size:0} px  스크린샷 · 편집 · 글꼴 설정  ABC Il1 0123456789  1280x720",
                FontFamily = family, FontSize = size, Foreground = Brushes.Black, Margin = new Thickness(0, 2, 0, 2),
            };
            TextOptions.SetTextFormattingMode(label, mode);
            panel.Children.Add(label);
        }
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
            context.DrawText(new FormattedText("ABC 스크린샷 한글", CultureInfo.GetCultureInfo("ko-KR"),
                FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                14, Brushes.Black, 1), new Point());
        GlyphRunDrawing[] glyphs = Glyphs(drawing).ToArray();
        Assert.NotEmpty(glyphs);
        foreach (GlyphRunDrawing glyph in glyphs)
        {
            Assert.DoesNotContain((ushort)0, glyph.GlyphRun.GlyphIndices);
            Assert.Contains("Pretendard", glyph.GlyphRun.GlyphTypeface.FamilyNames.Values);
        }
    }

    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (GlyphRunDrawing descendant in Glyphs(child)) yield return descendant;
    }
}
