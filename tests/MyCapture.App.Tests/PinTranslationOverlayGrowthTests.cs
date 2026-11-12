using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Growth beyond the source rectangle takes only the width the translation needs, and the
/// grown area keeps the picture's own pixels instead of being painted with the block colour.
/// </summary>
public sealed class PinTranslationOverlayGrowthTests
{
    private const string LongText = "Some keyboard shortcuts behave differently in other apps and in older versions";

    [Fact]
    public void TwoLineBlockGrowsOnlyToItsBalancedWidth() => StaTestHost.Run(() =>
    {
        // Too narrow for the source size at the source width; the page offers 1,990 pixels.
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(LongText, new(10, 10, 160, 40), 14,
            2000, 200, PinTranslationOverlay.Measure, sourceLineCount: 2, sourceLineHeight: 20));
        double single = PinTranslationOverlay.Measure(LongText, 14, double.PositiveInfinity).Width;
        string[] lines = fit.RenderText.Split('\n');
        Assert.Equal(14, fit.FontSize);
        Assert.Equal(2, lines.Length);
        Assert.True(fit.Grew);
        // Two balanced lines: at most half the single-line width plus the longest word.
        double longestWord = LongText.Split(' ').Max(word => PinTranslationOverlay.Measure(word, 14, double.PositiveInfinity).Width);
        Assert.True(fit.Bounds.Width <= single / 2 + longestWord,
            $"Width {fit.Bounds.Width:F1} for a single-line width of {single:F1}.");
    });

    [Fact]
    public void GrownAreaOverADifferentSurfaceIsNotPainted() => StaTestHost.Run(() =>
    {
        // A gray note box (243) ends at x = 200 on a white page. The label inside it is translated
        // into a longer text that grows across the box edge onto the white page.
        const int width = 600, height = 80;
        var text = new FormattedText("Save note", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 16, new SolidColorBrush(Color.FromRgb(30, 34, 40)), 1);
        Rect ink = text.BuildGeometry(new Point(110, 30)).Bounds;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(243, 243, 243)), null, new Rect(0, 0, 200, height));
            drawing.DrawText(text, new Point(110, 30));
        }
        var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        source.Render(visual);
        source.Freeze();
        OcrLine line = PinTranslationOverlayTests.Line("Save note", ink.X, ink.Y, ink.Width, ink.Height);
        var block = new PinOverlayBlock(line.Text, line.Bounds, [line]);
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(block, "Save the note to the shared folder")],
            drawText: false, observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.True(chosen.Bounds.Right > 210, $"The fixture must grow across the box edge: {chosen.Bounds}.");

        byte[] before = Pixels(source), after = Pixels(result.Image);
        int eraseLeft = (int)Math.Floor(line.Bounds.X - 2), eraseTop = (int)Math.Floor(line.Bounds.Y - 2);
        int eraseRight = (int)Math.Ceiling(line.Bounds.Right + 2), eraseBottom = (int)Math.Ceiling(line.Bounds.Bottom + 2);
        int changed = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= eraseLeft && x < eraseRight && y >= eraseTop && y < eraseBottom) continue;
                int index = (y * width + x) * 4;
                int difference = Math.Max(Math.Abs(before[index] - after[index]),
                    Math.Max(Math.Abs(before[index + 1] - after[index + 1]), Math.Abs(before[index + 2] - after[index + 2])));
                if (difference > 6) changed++;
            }
        Assert.Equal(0, changed);
    });

    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }
}
