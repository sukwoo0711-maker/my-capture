using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Sibling labels drawn at one size must keep one size after translation, whether or not their
/// words have descenders, and the source-ink cap must still stop inflated OCR rectangles.
/// </summary>
public sealed class PinTranslationOverlaySizeConsistencyTests
{
    private const double Em = 16;
    private const double HeadingEm = 22;
    // Mixed on purpose: with descenders (p, g, y) and without.
    private static readonly string[] Labels =
        ["File name", "Copy to clipboard", "Automatic save", "Keep original", "Display scale", "Mouse pointer", "Ready", "Saved"];

    [Theory]
    [InlineData(0)]
    [InlineData(.3)]
    public void SameSizeLabelsWithAndWithoutDescendersShareOneEstimatedSize(double inflation) => StaTestHost.Run(() =>
    {
        (BitmapSource source, PinOverlayBlock[] blocks) = Scene(inflation);
        using PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(source, blocks);
        double[] labels = preparation.FontSizes.Skip(1).ToArray();
        Assert.All(labels, size => Assert.Equal(labels[0], size, 6));
        // One boundary pixel moves the recovered em by about one pixel at this size.
        Assert.InRange(labels[0], Em - 1.5, Em * 1.1 + 1.5);
        Assert.True(preparation.FontSizes[0] > labels[0] + 3, "The heading must stay larger than the labels.");
    });

    [Fact]
    public void InflatedOcrRectanglesAreStillCappedBySourceInk() => StaTestHost.Run(() =>
    {
        (BitmapSource source, PinOverlayBlock[] blocks) = Scene(.5);
        double[] uncapped = PinTranslationOverlay.EstimateFontSizes(blocks);
        // The OCR rectangles alone would make the translation about twice the source size.
        Assert.All(uncapped.Skip(1), size => Assert.True(size > Em * 1.6, $"Uncapped estimate {size} is not inflated."));
        using PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(source, blocks);
        Assert.All(preparation.FontSizes.Skip(1), size => Assert.InRange(size, Em - 1.5, Em * 1.1 + 1.5));
        Assert.InRange(preparation.FontSizes[0], HeadingEm - 2, HeadingEm * 1.1 + 2);
    });

    [Fact]
    public void TranslatedSiblingLabelsRenderAtOneSize() => StaTestHost.Run(() =>
    {
        (BitmapSource source, PinOverlayBlock[] blocks) = Scene(0);
        string[] translations = ["캡처 설정", "파일 이름", "클립보드로 복사", "자동 저장", "원본 유지", "화면 배율", "마우스 포인터", "준비", "저장됨"];
        var fits = new Dictionary<PinOverlayBlock, PinOverlayFit>(ReferenceEqualityComparer.Instance);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            blocks.Select((block, index) => new PinOverlayTranslation(block, translations[index])).ToArray(),
            observeFit: (block, fit, _) => fits[block] = fit);
        Assert.Equal(blocks.Length, result.RenderedBlocks);
        double[] labels = blocks.Skip(1).Select(block => fits[block].FontSize).ToArray();
        Assert.True(labels.Max() - labels.Min() <= .5, $"Label sizes {string.Join(", ", labels)} differ.");
        Assert.True(fits[blocks[0]].FontSize > labels.Max() + 3);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PaddedOcrRectanglesDoNotEnlargeTheEstimate(double padding) => StaTestHost.Run(() =>
    {
        // Windows OCR rectangles include a pixel or two around the ink. The estimate must follow
        // the source pixels instead: within 3 percent, and not systematically larger.
        (BitmapSource source, PinOverlayBlock[] blocks) = Scene(0, padding);
        using PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(source, blocks);
        Assert.InRange(preparation.FontSizes[0], HeadingEm * .97, HeadingEm * 1.03);
        Assert.All(preparation.FontSizes.Skip(1), size => Assert.InRange(size, Em * .97, Em * 1.03));
    });

    [Fact]
    public void BlurredSmallCopyEstimatesItsScaledSize() => StaTestHost.Run(() =>
    {
        // The same scene resampled to 60 percent: blurred edges and sub-pixel strokes, with
        // OCR rectangles one pixel larger than the scaled ink.
        const double scale = .6;
        (BitmapSource large, PinOverlayBlock[] largeBlocks) = Scene(0);
        var small = new TransformedBitmap(large, new ScaleTransform(scale, scale));
        small.Freeze();
        PinOverlayBlock[] blocks = largeBlocks.Select(block =>
        {
            OcrLine[] lines = block.Lines.Select(line => new OcrLine(line.Text, Scaled(line.Bounds),
                line.Words.Select(word => new OcrWord(word.Text, Scaled(word.Bounds))).ToArray())).ToArray();
            return new PinOverlayBlock(block.Text, Scaled(block.Bounds), lines);
        }).ToArray();
        using PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(small, blocks);
        Assert.InRange(preparation.FontSizes[0], HeadingEm * scale * .95, HeadingEm * scale * 1.05);
        Assert.All(preparation.FontSizes.Skip(1), size => Assert.InRange(size, Em * scale * .95, Em * scale * 1.05));

        static OcrRect Scaled(OcrRect rect) =>
            new(rect.X * scale - 1, rect.Y * scale - 1, rect.Width * scale + 2, rect.Height * scale + 2);
    });

    /// <summary>A heading and one label per row, with word rectangles from the drawn glyphs.</summary>
    private static (BitmapSource Source, PinOverlayBlock[] Blocks) Scene(double inflation, double padding = 0)
    {
        const int width = 420, height = 60 + 34 * 8;
        var lines = new List<OcrLine>();
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            lines.Add(Draw(drawing, "Capture settings", HeadingEm, 20, 14, inflation, padding));
            for (int row = 0; row < Labels.Length; row++)
                lines.Add(Draw(drawing, Labels[row], Em, 20, 60 + row * 34, inflation, padding));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return (bitmap, lines.Select(line => new PinOverlayBlock(line.Text, line.Bounds, [line])).ToArray());
    }

    private static OcrLine Draw(DrawingContext drawing, string text, double em, double x, double y, double inflation, double padding = 0)
    {
        drawing.DrawText(Format(text, em), new Point(x, y));
        var words = new List<OcrWord>();
        int start = 0;
        foreach (string word in text.Split(' '))
        {
            double offset = Format(text[..start], em).WidthIncludingTrailingWhitespace;
            Rect ink = Format(word, em).BuildGeometry(new Point(x + offset, y)).Bounds;
            double grow = ink.Height * inflation + padding;
            words.Add(new(word, new(ink.X - padding, ink.Y - grow, ink.Width + padding * 2, ink.Height + grow * 2)));
            start += word.Length + 1;
        }
        return new(text, words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);
    }

    private static FormattedText Format(string text, double em) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, ClipboardTextRenderer.TextTypeface, em, Brushes.Black, 1);
}
