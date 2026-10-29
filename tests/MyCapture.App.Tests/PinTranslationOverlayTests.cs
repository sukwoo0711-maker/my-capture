using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayTests(ITestOutputHelper output)
{
    [Fact]
    public void TwoColumnsStaySeparateAndKeepParagraphLineGeometry()
    {
        OcrResult ocr = Recognition(
            Line("Left first sentence", 20, 20, 220), Line("Left second sentence", 20, 48, 220),
            Line("Right first sentence", 400, 20, 220), Line("Right second sentence", 400, 48, 220));
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(ocr);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(2, blocks[0].Lines.Count);
        Assert.Equal(2, blocks[1].Lines.Count);
        Assert.True(blocks[0].Bounds.Right < blocks[1].Bounds.X);
        Assert.Equal("Left first sentence Left second sentence", blocks[0].Text);
        Assert.All(OcrExtractionAnalyzer.Analyze(ocr).Paragraphs, paragraph => Assert.NotEmpty(paragraph.SourceLines));
    }

    [Fact]
    public void ThreeByThreeTableKeepsEveryCellSeparate()
    {
        string[,] cells = { { "Name", "Status", "Count" }, { "Document", "Ready", "12" }, { "Picture", "Saved", "24" } };
        OcrLine[] lines = Enumerable.Range(0, 3).SelectMany(row => Enumerable.Range(0, 3)
            .Select(column => Line(cells[row, column], 20 + column * 240, 20 + row * 40, 100))).ToArray();
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(lines));
        Assert.Equal(9, blocks.Count);
        Assert.All(blocks, block => Assert.Single(block.Lines));
        Assert.Equal(cells.Cast<string>().Order(), blocks.Select(block => block.Text).Order());
    }

    [Fact]
    public void SingleOcrLineNeverBridgesAWideHorizontalGap()
    {
        OcrWord[] words = [new("Save", new(20, 20, 45, 20)), new("Cancel", new(250, 20, 60, 20))];
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(Recognition(new OcrLine("Save Cancel", new(20, 20, 290, 20), words)));
        Assert.Equal(2, blocks.Count);
        Assert.Equal(["Save", "Cancel"], blocks.Select(block => block.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RingMedianDistinguishesFlatBackgroundAndTexture(bool noisy)
    {
        byte[] pixels = Pixels(100, 80, noisy);
        PinOverlayBackground background = PinTranslationOverlay.EstimateBackground(pixels, 100, 80, 400, new(20, 20, 50, 25));
        Assert.Equal(noisy, background.NeedsPlate);
        if (!noisy) Assert.Equal(Color.FromRgb(240, 240, 240), background.Color);
    }

    [Fact]
    public void ForegroundComesFromFarLuminanceCluster()
    {
        byte[] pixels = Pixels(100, 60);
        for (int y = 20; y < 40; y++)
            for (int x = 20; x < 30; x++) Paint(pixels, 100, x, y, 20);
        Color color = PinTranslationOverlay.EstimateTextColor(pixels, 100, 60, 400,
            [Line("Text", 20, 20, 50)], Color.FromRgb(240, 240, 240));
        Assert.Equal(Color.FromRgb(20, 20, 20), color);
        Assert.True(PinTranslationOverlay.Contrast(color, Color.FromRgb(240, 240, 240)) >= 4.5);
    }

    [Theory]
    [InlineData(245, 220, true)]
    [InlineData(20, 45, false)]
    public void LowContrastFallsBackToDarkOrLight(byte background, byte foreground, bool dark)
    {
        Color surface = Color.FromRgb(background, background, background);
        Color result = PinTranslationOverlay.EnsureContrast(Color.FromRgb(foreground, foreground, foreground), surface);
        Assert.Equal(dark ? (byte)20 : (byte)250, result.R);
        Assert.True(PinTranslationOverlay.Contrast(result, surface) >= 4.5);
    }

    [Fact]
    public void FittingWrapsAndShrinksWithoutChangingText()
    {
        const string text = "This complete translation wraps over several lines.";
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 180, 60), 30, 500, 300, Measure));
        Assert.Equal(text, fit.Text);
        Assert.True(fit.FontSize < 30);
        Assert.True(fit.Grew);
        Assert.Equal(text, fit.RenderText.Replace('\n', ' '));
        Assert.True(Measure(fit.RenderText, fit.FontSize, fit.Bounds.Width).Height <= fit.Bounds.Height);
    }

    [Fact]
    public void FittingGrowsRightBeforeShrinkingAndFinallyGrowsDownWithoutTruncating()
    {
        string text = new('가', 120);
        PinOverlayFit down = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 50, 10), 10, 300, 400, Measure));
        Assert.Equal(9, down.FontSize);
        Assert.True(down.Bounds.Width > 50);
        Assert.True(down.Bounds.Height > 10);
        PinOverlayFit right = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 50, 10), 10, 300, 50, Measure));
        Assert.True(right.Bounds.Width > 50);
        Assert.Equal(text, right.Text);
        Assert.Equal(text, right.RenderText.Replace("\n", ""));
        Assert.True(right.Bounds.Right <= 300 && right.Bounds.Bottom <= 50);
    }

    [Fact]
    public void InkExtentsRecoverTheSameEmWithAndWithoutDescenders() => StaTestHost.Run(() =>
    {
        const double em = 24;
        OcrLine Source(string text, double y)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                ClipboardTextRenderer.TextTypeface, em, Brushes.Black, 1);
            return Line(text, 20, y, formatted.WidthIncludingTrailingWhitespace, formatted.BuildGeometry(new Point()).Bounds.Height);
        }
        PinOverlayBlock[] blocks = [Block(Source("Save document", 20)), Block(Source("Copy picture", 70))];
        Assert.NotEqual(blocks[0].Bounds.Height, blocks[1].Bounds.Height);
        double[] sizes = PinTranslationOverlay.EstimateFontSizes(blocks);
        Assert.InRange(sizes[0], em - .75, em + .75);
        Assert.Equal(sizes[0], sizes[1], 6);
    });

    [Fact]
    public void RasterizedInkRecoversConsistentEmWithinPixelQuantization() => StaTestHost.Run(() =>
    {
        const int width = 320, height = 80;
        const double em = 24;
        PinOverlayBlock RasterBlock(string text)
        {
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                drawing.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    ClipboardTextRenderer.TextTypeface, em, Brushes.Black, 1), new Point(20, 20));
            }
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            byte[] pixels = ReadPixels(image);
            int left = width, top = height, right = 0, bottom = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (pixels[(y * width + x) * 4] < 224)
                    {
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                    }
            Assert.True(right > left && bottom > top);
            return Block(Line(text, left, top, right - left, bottom - top));
        }
        PinOverlayBlock[] blocks = [RasterBlock("Save document"), RasterBlock("Copy picture")];
        Assert.NotEqual(blocks[0].Bounds.Height, blocks[1].Bounds.Height);
        double[] sizes = PinTranslationOverlay.EstimateFontSizes(blocks);
        // At this size one boundary pixel changes the recovered em by about 1.3 px.
        Assert.InRange(sizes[0], em - 1.5, em + 1.5);
        Assert.Equal(sizes[0], sizes[1], 6);
    });

    [Fact]
    public void NearbyEstimatedSizesShareMedianWithoutFlatteningHeadings() => StaTestHost.Run(() =>
    {
        double ink = PinTranslationOverlay.Measure("Sample", 1, double.PositiveInfinity).Height;
        PinOverlayBlock[] blocks = [Block(Line("Sample", 10, 10, 100, 20 * ink)),
            Block(Line("Sample", 10, 50, 100, 22 * ink)), Block(Line("Sample", 10, 100, 200, 40 * ink))];
        double[] sizes = PinTranslationOverlay.EstimateFontSizes(blocks);
        Assert.Equal(21, sizes[0], 6);
        Assert.Equal(sizes[0], sizes[1]);
        Assert.Equal(40, sizes[2], 6);
    });

    [Theory]
    [InlineData("Automatic")]
    [InlineData("ÉÉÉÉÉÉÉ")]
    [InlineData("Document")]
    [InlineData("Enabled")]
    [InlineData("Show notifications")]
    public void ShortValueUsesFreeSpaceBeforeShrinkingOrWrapping(string text)
    {
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 30, 20), 20, 300, 100, Measure));
        Assert.Equal(20, fit.FontSize);
        Assert.Equal(text, fit.RenderText);
        Assert.True(fit.Bounds.Width > 30);
    }

    [Fact]
    public void RightwardGrowthStopsBeforeTheNextBlockOnTheSameRows()
    {
        OcrRect neighbor = new(120, 10, 90, 24);
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit("Automatic", new(10, 10, 30, 24), 20,
            400, 200, Measure, [neighbor]));
        Assert.Equal(20, fit.FontSize);
        Assert.True(fit.Bounds.Right <= neighbor.X - 4);
        Assert.Equal("Automatic", fit.RenderText);
    }

    [Theory]
    [InlineData("Show notifications", 130, "Show\nnotifications")]
    [InlineData("문서를 저장합니다", 70, "문서를\n저장합니다")]
    [InlineData("well-known setting", 70, "well-\nknown\nsetting")]
    [InlineData("가나다라마바", 30, "가나다\n라마바")]
    public void WrappingOnlyUsesWordHyphenOrUnspacedHangulBreaks(string text, double width, string expected)
    {
        Assert.Equal(expected, PinTranslationOverlay.Wrap(text, 20, width, Measure));
    }

    [Theory]
    [InlineData("Automatic")]
    [InlineData("ÉÉÉÉÉÉÉ")]
    [InlineData("a notifications value")]
    public void OversizedLatinWordsNeverUseCharacterFallback(string text)
    {
        Assert.Null(PinTranslationOverlay.Wrap(text, 20, 50, Measure));
    }

    [Fact]
    public void FractionalErasePaddingCannotTouchAFailedNeighbor() => StaTestHost.Run(() =>
    {
        BitmapSource source = Bitmap(250, 140);
        OcrLine first = Line("Save", 20, 20.2, 80, 20.2), failed = Line("Keep", 20, 42.6, 80, 20);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(Block(first), "저장"), new(Block(failed), null)]);
        for (int y = 42; y < 63; y++)
            for (int x = 20; x < 100; x++) Assert.Equal(ReadPixel(source, x, y), ReadPixel(result.Image, x, y));
        Assert.True(result.NeedsReview);
    });

    [Fact]
    public void DownwardGrowthCanUseANarrowPassageBesideAStaggeredBlock()
    {
        string text = new('가', 120);
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 50, 10), 10,
            300, 300, Measure, [new(100, 24, 190, 240)]));
        Assert.True(fit.Bounds.Right < 100);
        Assert.True(fit.Bounds.Height > 10);
        Assert.Equal(text, fit.RenderText.Replace("\n", ""));
    }

    [Fact]
    public void WpfMeasurementPreservesLatinWordsAndUsesFreeWidth() => StaTestHost.Run(() =>
    {
        double wordWidth = PinTranslationOverlay.Measure("notifications", 20, double.PositiveInfinity).Width;
        Assert.Equal("Show\nnotifications", PinTranslationOverlay.Wrap("Show notifications", 20, wordWidth + .1,
            PinTranslationOverlay.Measure));
        Assert.Null(PinTranslationOverlay.Wrap("notifications", 20, wordWidth - 1, PinTranslationOverlay.Measure));
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit("Show notifications", new(10, 10, 30, 20),
            20, 300, 100, PinTranslationOverlay.Measure));
        Assert.Equal(20, fit.FontSize);
        Assert.Equal("Show notifications", fit.RenderText);
        Assert.True(fit.Bounds.Width > 30);
    });

    [Fact]
    public void ExistingSourceLineCountIsKeptAtTheEstimatedEm()
    {
        PinOverlayFit fit = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit("Save file", new(10, 10, 40, 38), 20,
            250, 200, Measure, sourceLineCount: 2));
        Assert.Equal(20, fit.FontSize);
        Assert.Equal("Save\nfile", fit.RenderText);
        Assert.Equal(40, fit.Bounds.Width);
    }

    [Fact]
    public void IdentityOutputKeepsOriginalPixelsWithoutReview() => StaTestHost.Run(() =>
    {
        BitmapSource source = Bitmap(250, 140);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(Block(Line("숨김", 20, 20, 40)), " \t숨김\n"),
             new(Block(Line("Keep   original", 20, 70, 120)), "Keep original")]);
        Assert.Same(source, result.Image);
        Assert.Equal(0, result.RenderedBlocks);
        Assert.False(result.NeedsReview);
    });

    [Fact]
    public void NoisyBackgroundIsErasedOpaquelyBeforeTranslationIsDrawn() => StaTestHost.Run(() =>
    {
        const int width = 320, height = 140;
        byte[] noisy = Pixels(width, height, true);
        BitmapSource texture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, noisy, width * 4);
        var visual = new DrawingVisual();
        var sourceText = new FormattedText("Original source", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 24, Brushes.White, 1);
        double top = sourceText.Height + sourceText.OverhangAfter - sourceText.Extent;
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawImage(texture, new Rect(0, 0, width, height));
            drawing.DrawText(sourceText, new Point(20, 20 - top));
        }
        var source = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        source.Render(visual);
        source.Freeze();
        OcrLine line = Line("Original source", 20, 20, sourceText.WidthIncludingTrailingWhitespace, sourceText.Extent);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(Block(line), "번역한 문장")], drawText: false);
        Assert.Equal(1, result.RenderedBlocks);
        byte[] pixels = ReadPixels(source);
        PinOverlayBackground background = PinTranslationOverlay.EstimateBackground(pixels, width, height, width * 4, line.Bounds);
        Assert.True(background.NeedsPlate);
        for (int y = (int)Math.Floor(line.Bounds.Y - 2); y < Math.Ceiling(line.Bounds.Bottom + 2); y++)
            for (int x = (int)Math.Floor(line.Bounds.X - 2); x < Math.Ceiling(line.Bounds.Right + 2); x++)
            {
                byte[] pixel = ReadPixel(result.Image, x, y);
                double toSource = Math.Pow(pixel[0] - 255, 2) + Math.Pow(pixel[1] - 255, 2) + Math.Pow(pixel[2] - 255, 2);
                double toFill = Math.Pow(pixel[0] - background.Color.B, 2) + Math.Pow(pixel[1] - background.Color.G, 2)
                    + Math.Pow(pixel[2] - background.Color.R, 2);
                Assert.True(toFill <= toSource, $"Un-erased source ink at ({x}, {y}).");
            }
    });

    [Fact]
    public void UnfittableTextDoesNotBecomeClippedOrEllipsized()
    {
        Assert.Null(PinTranslationOverlay.Fit(new string('A', 10_000), new(5, 5, 20, 10), 10, 50, 30, Measure));
    }

    [Theory]
    [InlineData(96, 96)]
    [InlineData(144, 192)]
    public void ComposerPreservesNativePixelSizeDpiAndUntouchedPixels(double dpiX, double dpiY) => StaTestHost.Run(() =>
    {
        BitmapSource source = Bitmap(320, 160, dpiX, dpiY);
        OcrLine line = Line("Save the document", 20, 20, 170, 22);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source, [new(Block(line), "문서를 저장합니다")]);
        Assert.Equal(1, result.RenderedBlocks);
        Assert.Equal((320, 160, dpiX, dpiY), (result.Image.PixelWidth, result.Image.PixelHeight, result.Image.DpiX, result.Image.DpiY));
        Assert.Equal(ReadPixel(source, 300, 140), ReadPixel(result.Image, 300, 140));
        Assert.NotEqual(ReadPixels(source), ReadPixels(result.Image));
    });

    [Fact]
    public void FailedBlockPixelsSurviveIncludingAdjacentErasePadding() => StaTestHost.Run(() =>
    {
        BitmapSource source = Bitmap(250, 140);
        OcrLine first = Line("Save the document", 20, 20, 150, 20), failed = Line("Keep 42", 20, 41, 150, 20);
        PinOverlayComposition result = PinTranslationOverlay.Compose(source,
            [new(Block(first), "문서를 저장합니다"), new(Block(failed), null)]);
        for (int y = 41; y < 61; y++)
            for (int x = 20; x < 170; x++) Assert.Equal(ReadPixel(source, x, y), ReadPixel(result.Image, x, y));
        Assert.True(result.NeedsReview);
    });

    [Fact]
    public async Task ServiceBatchesSentencesAndKeepsOnlyTheUnsafeSentenceInTheSourceLanguage()
    {
        var translator = new FakeTranslator(text => text.Contains("42", StringComparison.Ordinal) ? "문서 41개" : "문서를 저장합니다.");
        OcrLine first = Line("Save the document. Close the window.", 20, 20, 280, 24);
        OcrLine failed = Line("Keep 42 documents. Open the folder.", 20, 100, 240, 24);
        BitmapSource image = Bitmap(500, 240);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(translator)
            .TranslateRecognizedAsync(Recognition(first, failed), PinContent.FromImage(image), CancellationToken.None));
        Assert.Equal(1, translator.Calls);
        Assert.Equal(4, translator.Sources.Count);
        Assert.True(result.NeedsReview);
        Assert.Equal(PinContentKind.Image, result.Content.Kind);
        // The sentence whose number changed stays in English; its neighbour in the same block translates.
        Assert.EndsWith("\n\nKeep 42 documents. 문서를 저장합니다.", result.Content.OriginalText);
        Assert.NotNull(result.Document);
        Assert.Equal(result.Content.OriginalText, result.Document.OriginalText);
        bool redrawn = false;
        for (int y = 100; y < 124 && !redrawn; y++)
            for (int x = 20; x < 260 && !redrawn; x++)
                redrawn = !ReadPixel(image, x, y).SequenceEqual(ReadPixel(result.Content.Image, x, y));
        Assert.True(redrawn);
    }

    [Fact]
    public async Task AnUnfittableBlockKeepsPixelsAndFullTranslationInDocument()
    {
        string translation = string.Join(" ", Enumerable.Repeat("문서", 500));
        BitmapSource image = Bitmap(100, 40);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(new FakeTranslator(_ => translation))
            .TranslateRecognizedAsync(Recognition(Line("Save document", 10, 10, 80, 15)), PinContent.FromImage(image), CancellationToken.None));
        Assert.True(result.NeedsReview);
        Assert.Same(image, result.Content.Image);
        Assert.Equal(translation, result.Document!.OriginalText);
    }

    [Theory]
    [InlineData(2.1)]
    [InlineData(-12)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task TiltedImageFallsBackToDocument(double angle)
    {
        OcrResult ocr = OcrResult.Success("Save the document.", "en-US", [Line("Save the document.", 10, 10, 200)], TimeSpan.Zero, angle);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(new FakeTranslator(_ => "문서를 저장합니다."))
            .TranslateRecognizedAsync(ocr, PinContent.FromImage(Bitmap(320, 160)), CancellationToken.None));
        Assert.Equal(PinContentKind.Text, result.Content.Kind);
        Assert.Null(result.Document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrOffImageRectanglesFallBackToDocument(bool outside)
    {
        OcrResult ocr = OcrResult.Success("Save the document.", "en-US",
            outside ? [Line("Save the document.", 999, 999, 200)] : [], TimeSpan.Zero);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await new PinTranslationService(new FakeTranslator(_ => "문서를 저장합니다."))
            .TranslateRecognizedAsync(ocr, PinContent.FromImage(Bitmap(320, 160)), CancellationToken.None));
        Assert.Equal(PinContentKind.Text, result.Content.Kind);
    }

    [Fact]
    public void NeuralAndMergedResultsRetainTiltForFallback()
    {
        double angle = Assert.IsType<double>(NeuralOcrEngine.TextAngle([new SKPointI(0, 0), new(100, 20), new(96, 40), new(-4, 20)]));
        Assert.True(angle > 2);
        OcrLine line = Line("Save the document", 20, 20, 200);
        OcrResult merged = OcrResultMerger.Merge(OcrResult.Success(line.Text, "en-US", [line], TimeSpan.Zero, 0),
            OcrResult.Success(line.Text, "en-US", [line], TimeSpan.Zero, angle), TimeSpan.Zero);
        Assert.Equal(angle, merged.TextAngle!.Value, 10);
        Assert.False(PinTranslationOverlay.CanOverlay(merged));
        Assert.True(PinTranslationOverlay.CanOverlay(OcrResult.Success(line.Text, "en-US", [line], TimeSpan.Zero, 360)));
        double upsideDown = Assert.IsType<double>(NeuralOcrEngine.TextAngle([new SKPointI(0, 0), new(100, 0), new(100, 20), new(0, 20)], 1));
        Assert.Equal(180, upsideDown);
        Assert.False(PinTranslationOverlay.CanOverlay(OcrResult.Success(line.Text, "en-US", [line], TimeSpan.Zero, upsideDown)));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(128)]
    public void PlateContrastIncludesUnderlyingBlackAndWhitePixels(byte gray)
    {
        Color background = Color.FromRgb(gray, gray, gray);
        Assert.True(PinTranslationOverlay.Contrast(PinTranslationOverlay.EnsureContrast(background, background), background) >= 4.5);
        (Color foreground, double opacity) = PinTranslationOverlay.PlateStyle(background, Color.FromRgb(20, 20, 20));
        Assert.InRange(opacity, .88, 1);
        byte low = (byte)Math.Round(gray * opacity), high = (byte)Math.Round(gray * opacity + 255 * (1 - opacity));
        Assert.True(PinTranslationOverlay.Contrast(foreground, Color.FromRgb(low, low, low)) >= 4.5);
        Assert.True(PinTranslationOverlay.Contrast(foreground, Color.FromRgb(high, high, high)) >= 4.5);
    }

    [Fact]
    public void CompositionOfSixtyLinesReportsTimeAndSampledManagedPeak() => StaTestHost.Run(() =>
    {
        BitmapSource source = Bitmap(1920, 1080);
        PinOverlayTranslation[] blocks = Enumerable.Range(0, 60).Select(index =>
            new PinOverlayTranslation(Block(Line("Save the document before closing", 20 + index / 20 * 630,
                20 + index % 20 * 50, 550, 28)), "창을 닫기 전에 문서를 저장합니다.")).ToArray();
        _ = PinTranslationOverlay.Compose(source, blocks);
        long baseline = GC.GetTotalMemory(true), peak = baseline;
        void Sample()
        {
            long observed = GC.GetTotalMemory(false), previous;
            do { previous = Interlocked.Read(ref peak); }
            while (observed > previous && Interlocked.CompareExchange(ref peak, observed, previous) != previous);
        }
        using var stop = new CancellationTokenSource();
        Task sampler = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Sample();
                Thread.Sleep(1);
            }
        });
        var elapsed = new List<double>();
        try
        {
            for (int round = 0; round < 5; round++)
            {
                var watch = Stopwatch.StartNew();
                PinOverlayComposition result = PinTranslationOverlay.Compose(source, blocks);
                watch.Stop();
                Assert.Equal(60, result.RenderedBlocks);
                elapsed.Add(watch.Elapsed.TotalMilliseconds);
                Sample();
            }
        }
        finally { stop.Cancel(); sampler.GetAwaiter().GetResult(); }
        output.WriteLine($"1920x1080, 60 lines, composition only, 5 warm samples: median {elapsed.Order().ElementAt(2):F2} ms; "
            + $"1 ms sampled managed peak {peak:N0} bytes; baseline {baseline:N0} bytes; delta {peak - baseline:N0} bytes. "
            + "GC.GetTotalMemory includes the test process; unmanaged WPF memory is excluded.");
    });

    internal static OcrLine Line(string text, double x, double y, double width, double height = 20) =>
        new(text, new(x, y, width, height), [new(text, new(x, y, width, height))]);
    internal static OcrResult Recognition(params OcrLine[] lines) => OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)), "en-US", lines, TimeSpan.Zero);
    private static PinOverlayBlock Block(OcrLine line) => new(line.Text, line.Bounds, [line]);
    private static Size Measure(string text, double fontSize, double width) => new(text.Split('\n').Max(line => line.Length) * fontSize * .5,
        text.Split('\n').Length * fontSize * 1.2);
    internal static BitmapSource Bitmap(int width, int height, double dpiX = 96, double dpiY = 96)
    {
        var image = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, Pixels(width, height), width * 4);
        image.Freeze();
        return image;
    }
    private static byte[] Pixels(int width, int height, bool noisy = false)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) Paint(pixels, width, x, y, noisy ? (byte)((x + y) % 2 == 0 ? 40 : 240) : (byte)240);
        return pixels;
    }
    private static void Paint(byte[] pixels, int width, int x, int y, byte value)
    {
        int index = (y * width + x) * 4;
        pixels[index] = pixels[index + 1] = pixels[index + 2] = value;
        pixels[index + 3] = 255;
    }
    private static byte[] ReadPixel(BitmapSource source, int x, int y)
    {
        byte[] pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel;
    }
    private static byte[] ReadPixels(BitmapSource source)
    {
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(pixels, source.PixelWidth * 4, 0);
        return pixels;
    }
    internal sealed class FakeTranslator(Func<string, string> translate) : ITextTranslator
    {
        internal int Calls;
        internal IReadOnlyList<string> Sources = [];
        public long ModelDownloadBytes => 0;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            Sources = sources.ToArray();
            return Task.FromResult<IReadOnlyList<TranslatedText>>(sources.Select(source => new TranslatedText(source, translate(source), [], [])).ToArray());
        }
    }
}
