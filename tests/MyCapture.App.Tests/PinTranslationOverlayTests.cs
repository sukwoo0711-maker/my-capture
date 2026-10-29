using System.Diagnostics;
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
        Assert.True(fit.FontSize < 30 / .85);
        Assert.False(fit.Grew);
        Assert.True(Measure(text, fit.FontSize, fit.Bounds.Width).Height <= fit.Bounds.Height);
    }

    [Fact]
    public void MinimumFontGrowsDownThenRightAndNeverTruncates()
    {
        string text = new('A', 120);
        PinOverlayFit down = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 50, 10), 10, 300, 400, Measure));
        Assert.Equal(9, down.FontSize);
        Assert.Equal(50, down.Bounds.Width);
        Assert.True(down.Bounds.Height > 10);
        PinOverlayFit right = Assert.IsType<PinOverlayFit>(PinTranslationOverlay.Fit(text, new(10, 10, 50, 10), 10, 300, 50, Measure));
        Assert.True(right.Bounds.Width > 50);
        Assert.Equal(text, right.Text);
        Assert.True(right.Bounds.Right <= 300 && right.Bounds.Bottom <= 50);
    }

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
    public async Task ServiceBatchesSentencesAndRejectsTheWholeUnsafeBlock()
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
        Assert.Contains("\n\nKeep 42 documents. Open the folder.", result.Content.OriginalText);
        Assert.NotNull(result.Document);
        Assert.Equal(result.Content.OriginalText, result.Document.OriginalText);
        for (int y = 100; y < 124; y++)
            for (int x = 20; x < 260; x++) Assert.Equal(ReadPixel(image, x, y), ReadPixel(result.Content.Image, x, y));
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
    private static Size Measure(string text, double fontSize, double width) => new(Math.Min(width, text.Length * fontSize * .5),
        Math.Ceiling(text.Length * fontSize * .5 / width) * fontSize * 1.2);
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
