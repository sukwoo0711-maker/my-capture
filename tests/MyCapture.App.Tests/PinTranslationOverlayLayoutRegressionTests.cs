using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayLayoutRegressionTests
{
    [Fact]
    public void DenseTwelveLineParagraphUsesSourcePitchAndFullWidth() => StaTestHost.Run(() =>
    {
        const double em = 9, pitch = 10.5;
        double inkHeight = PinTranslationOverlay.Measure("Save document", 1, double.PositiveInfinity).Height * em;
        double lineWidth = PinTranslationOverlay.Measure(new string('가', 10), em, double.PositiveInfinity).Width + .1;
        OcrLine[] lines = Enumerable.Range(0, 12).Select(index =>
            PinTranslationOverlayTests.Line("Save document", 10, 10 + index * pitch, lineWidth, inkHeight)).ToArray();
        PinOverlayBlock block = Block(lines);
        int imageWidth = (int)Math.Ceiling(block.Bounds.Right);
        int imageHeight = (int)Math.Ceiling(block.Bounds.Bottom + 4);
        string translation = new('가', 120);
        double estimated = PinTranslationOverlay.EstimateFontSizes([block])[0];
        // The old path had the same font-size grid but used the typeface's natural pitch.
        var natural = new FormattedText("Ag", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, estimated, Brushes.Black, 1);
        Assert.Null(PinTranslationOverlay.Fit(translation, block.Bounds, estimated, imageWidth, imageHeight,
            PinTranslationOverlay.Measure, sourceLineCount: 12, sourceLineHeight: natural.Height));
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(Solid(imageWidth, imageHeight), [new(block, translation)],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.False(result.NeedsReview);
        Assert.NotNull(chosen);
        Assert.Equal(12, chosen.RenderText.Split('\n').Length);
        Assert.Equal(pitch, PinTranslationOverlay.SourceLinePitch(lines), 8);
        Assert.Equal(block.Bounds.Width, chosen.Bounds.Width, 8);
        Assert.Equal(em, chosen.FontSize, 8);
    });

    [Fact]
    public void BubbleEdgesStopRightwardAndDownwardGrowth() => StaTestHost.Run(() =>
    {
        Color outside = Color.FromRgb(150, 180, 210);
        BitmapSource image = Draw(360, 160, drawing =>
        {
            drawing.DrawRectangle(new SolidColorBrush(outside), null, new Rect(0, 0, 360, 160));
            drawing.DrawRoundedRectangle(Brushes.White, null, new Rect(20, 20, 160, 90), 12, 12);
        });
        var block = Block(PinTranslationOverlayTests.Line("Please send the notes", 30, 35, 140, 18));
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(image,
            [new(block, "Please send the meeting notes before lunch today")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.True(chosen.Bounds.Right <= 180);
        Assert.True(chosen.Bounds.Bottom <= 110);
        AssertAreaUnchanged(image, result.Image, new Int32Rect(180, 0, 180, 160));
        AssertAreaUnchanged(image, result.Image, new Int32Rect(0, 110, 360, 50));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageAndBorderPixelsStopExpansionWithoutOcrObstacles(bool border) => StaTestHost.Run(() =>
    {
        BitmapSource image = Draw(360, 160, drawing =>
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 360, 160));
            drawing.DrawRectangle(Brushes.DarkSlateBlue, null, new Rect(190, 0, border ? 1 : 170, 160));
            drawing.DrawRectangle(Brushes.DarkSeaGreen, null, new Rect(0, 105, 190, border ? 1 : 55));
        });
        var block = Block(PinTranslationOverlayTests.Line("Keep the source image", 20, 20, 135, 18));
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(image,
            [new(block, "The translated paragraph stays inside the original background")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.True(chosen.Bounds.Right <= 190);
        Assert.True(chosen.Bounds.Bottom <= 105);
        AssertAreaUnchanged(image, result.Image, new Int32Rect(190, 0, 170, 160));
        AssertAreaUnchanged(image, result.Image, new Int32Rect(0, 105, 190, 55));
    });

    [Fact]
    public void CenteredTwoLineBlockKeepsItsCenterAndRenderedLineCenters() => StaTestHost.Run(() =>
    {
        OcrLine[] lines = [PinTranslationOverlayTests.Line("The original first subtitle", 60, 30, 200, 20),
            PinTranslationOverlayTests.Line("Second subtitle", 90, 57, 140, 20)];
        var block = Block(lines);
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(Solid(320, 140),
            [new(block, "가운데 정렬을 유지합니다 다음 문장도 같습니다")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(TextAlignment.Center, PinTranslationOverlay.InferAlignment(lines));
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.Equal(160, chosen.Bounds.X + chosen.Bounds.Width / 2, 6);
        Assert.True(chosen.Bounds.Width > block.Bounds.Width);
        byte[] pixels = Pixels(result.Image);
        var centers = new List<double>();
        int left = 320, right = -1;
        bool ink = false;
        for (int y = 0; y <= 140; y++)
        {
            bool row = false;
            if (y < 140)
                for (int x = 0; x < 320; x++)
                    if (pixels[(y * 320 + x) * 4] < 100)
                    { row = true; left = Math.Min(left, x); right = Math.Max(right, x); }
            if (!row && ink)
            { centers.Add((left + right + 1) / 2d); left = 320; right = -1; }
            ink = row;
        }
        Assert.Equal(chosen.RenderText.Split('\n').Length, centers.Count);
        Assert.All(centers, center => Assert.InRange(center, 158, 162));
    });

    [Fact]
    public void RightAlignmentUsesTheCommonRightEdge()
    {
        OcrLine[] lines = [PinTranslationOverlayTests.Line("First", 60, 30, 200, 20),
            PinTranslationOverlayTests.Line("Second", 120, 57, 140, 20)];
        Assert.Equal(TextAlignment.Right, PinTranslationOverlay.InferAlignment(lines));
        static Size Measure(string text, double size, double _) => new(text.Length * size / 2, size);
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit("This translation grows left", new(100, 10, 60, 20),
            20, 320, 140, Measure, alignment: TextAlignment.Right));
        Assert.Equal(160, fit.Bounds.Right, 6);
        Assert.True(fit.Bounds.X < 100);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EraseAndPlatePaddingPreserveABorderOnePixelFromTheSource(bool horizontal) => StaTestHost.Run(() =>
    {
        BitmapSource image = Draw(180, 90, drawing =>
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 180, 90));
            drawing.DrawRectangle(Brushes.DarkSlateBlue, null, horizontal
                ? new Rect(0, 41, 180, 1) : new Rect(101, 0, 1, 90));
        });
        PinOverlayComposition result = PinTranslationOverlay.Compose(image,
            [new(Block(PinTranslationOverlayTests.Line("Original label", 20, 20, 80, 20)), "Text")]);
        Assert.Equal(1, result.RenderedBlocks);
        AssertAreaUnchanged(image, result.Image, horizontal ? new Int32Rect(0, 41, 180, 1) : new Int32Rect(101, 0, 1, 90));
    });

    [Fact]
    public void CenteredTwoLineSubtitleCanUseItsContinuousGradient() => StaTestHost.Run(() =>
    {
        const int width = 320, height = 140;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = (byte)(70 + 25 * Math.Sin(x / 90d) + x / 8d + y / 10d);
                pixels[index + 3] = 255;
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        OcrLine[] lines = [PinTranslationOverlayTests.Line("The original first subtitle", 60, 30, 200, 20),
            PinTranslationOverlayTests.Line("Second subtitle", 90, 57, 140, 20)];
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(image,
            [new(Block(lines), "가운데 정렬을 유지합니다 다음 문장도 같습니다")],
            drawText: false, observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.Equal(160, chosen.Bounds.X + chosen.Bounds.Width / 2, 6);
        Assert.False(result.NeedsReview);
        byte[] actual = Pixels(result.Image);
        for (int y = 30; y < 77; y++)
            for (int x = 60; x < 260; x++)
            {
                int index = (y * width + x) * 4;
                Assert.InRange(Math.Abs(actual[index] - pixels[index]), 0, 3);
            }
    });

    [Fact]
    public void MissingLeadingWordAndRaisedReferenceAreRecoveredAsGlyphRuns() => StaTestHost.Run(() =>
    {
        BitmapSource image = Draw(180, 90, drawing =>
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 180, 90));
            foreach (int x in new[] { 20, 28, 36 })
                drawing.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(x + .5, 20.5, 4, 11));
            foreach (int x in new[] { 112, 117, 122 })
                drawing.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(x + .5, 39.5, 2, 4));
        });
        OcrLine[] lines = [PinTranslationOverlayTests.Line("remaining first line", 48, 20, 92, 16),
            PinTranslationOverlayTests.Line("second line", 20, 44, 90, 16)];
        PinOverlayComposition result = PinTranslationOverlay.Compose(image, [new(Block(lines), "번역된 문장입니다")], drawText: false);
        Assert.Equal(1, result.RenderedBlocks);
        byte[] pixels = Pixels(result.Image);
        foreach (var area in new[] { new Int32Rect(20, 20, 21, 12), new Int32Rect(112, 39, 13, 5) })
            for (int y = area.Y; y < area.Y + area.Height; y++)
                for (int x = area.X; x < area.X + area.Width; x++)
                    Assert.Equal(255, pixels[(y * 180 + x) * 4]);
    });

    [Theory]
    [InlineData("icon")]
    [InlineData("image")]
    [InlineData("rule")]
    public void UnrecognizedGraphicsInsideParagraphWhitespaceRemainUnchanged(string graphic) => StaTestHost.Run(() =>
    {
        BitmapSource image = Draw(180, 90, drawing =>
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 180, 90));
            if (graphic == "icon")
                drawing.DrawRectangle(null, new Pen(Brushes.Black, 1), new Rect(120.5, 44.5, 11, 11));
            else drawing.DrawRectangle(Brushes.DarkSlateBlue, null,
                graphic == "rule" ? new Rect(115, 20, 1, 40) : new Rect(115, 44, 20, 16));
        });
        OcrLine[] lines = [PinTranslationOverlayTests.Line("first line", 20, 20, 120, 16),
            PinTranslationOverlayTests.Line("second line", 20, 44, 90, 16)];
        PinOverlayComposition result = PinTranslationOverlay.Compose(image, [new(Block(lines), "번역된 문장입니다")]);
        Assert.Equal(0, result.RenderedBlocks);
        AssertAreaUnchanged(image, result.Image, new Int32Rect(0, 0, 180, 90));
    });

    [Fact]
    public void FaintRasterFringeConnectedToSourceInkDoesNotBecomeABorder() => StaTestHost.Run(() =>
    {
        var text = new FormattedText("원본 보기", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 26, new SolidColorBrush(Color.FromRgb(44, 50, 57)), 1);
        Rect ink = text.BuildGeometry(new Point(64, 20)).Bounds;
        BitmapSource image = Draw(320, 90, drawing =>
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 90));
            drawing.DrawText(text, new Point(64, 20));
        });
        var block = Block(PinTranslationOverlayTests.Line("원본 보기", ink.X, ink.Y, ink.Width, ink.Height));
        PinOverlayFit? chosen = null;
        PinOverlayComposition result = PinTranslationOverlay.Compose(image, [new(block, "Show original")],
            observeFit: (_, fit, _) => chosen = fit);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.NotNull(chosen);
        Assert.Equal(26, chosen.FontSize, 5);
        Assert.True(chosen.Bounds.Width > block.Bounds.Width);
    });

    [Fact]
    public void DecisionsDistinguishIdentityRejectedProtectedAndFitFallback() => StaTestHost.Run(() =>
    {
        PinOverlayBlock[] blocks = Enumerable.Range(0, 4).Select(index =>
            Block(PinTranslationOverlayTests.Line("Source", 10, 10 + index * 30, 70, 15))).ToArray();
        var reasons = new List<string>();
        PinTranslationOverlay.Compose(Solid(90, 130),
            [new(blocks[0], "Source"), new(blocks[1], null), new(blocks[2] with { PreservePixels = true }, "변경"),
             new(blocks[3], new string('A', 1000))], observeDecision: (_, reason) => reasons.Add(reason));
        Assert.Equal(["identity", "all-rejected", "protected", "fit"], reasons);
    });

    private static PinOverlayBlock Block(params OcrLine[] lines) => new(string.Join(" ", lines.Select(line => line.Text)),
        lines.Select(line => line.Bounds).Aggregate(default(OcrRect), OcrRect.Union), lines);
    private static BitmapSource Solid(int width, int height) => Draw(width, height,
        drawing => drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height)));
    private static BitmapSource Draw(int width, int height, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen()) paint(drawing);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }
    private static void AssertAreaUnchanged(BitmapSource source, BitmapSource result, Int32Rect area)
    {
        byte[] before = Pixels(source), after = Pixels(result);
        for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                int index = (y * source.PixelWidth + x) * 4;
                Assert.True(before.AsSpan(index, 4).SequenceEqual(after.AsSpan(index, 4)), $"Pixel ({x},{y}) changed.");
            }
    }
}
