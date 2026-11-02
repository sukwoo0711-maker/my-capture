using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationOverlayPreparationTests
{
    [Fact]
    public void SamplingEveryBlockBeforeTheTranslationComposesTheSamePicture() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(420, 220, drawing =>
        {
            Text(drawing, "Open the settings", 20, 20);
            drawing.DrawRectangle(Brushes.Black, null, new Rect(20, 41, 140, 1));
            Text(drawing, "Save the document before closing", 20, 60);
            Text(drawing, "Unchanged label", 20, 100);
            Text(drawing, "Rejected sentence", 20, 140);
            Text(drawing, "Kept picture text", 20, 180);
        });
        PinOverlayBlock[] blocks =
        [
            Block("Open the settings", 20, 20, 140, 18),
            Block("Save the document before closing", 20, 60, 270, 18),
            Block("Unchanged label", 20, 100, 130, 18),
            Block("Rejected sentence", 20, 140, 150, 18),
            Block("Kept picture text", 20, 180, 140, 18) with { PreservePixels = true },
        ];
        PinOverlayTranslation[] translations =
        [
            new(blocks[0], "설정을 열고 다음 단계에서 계정을 확인합니다"),
            new(blocks[1], "닫기 전에 문서를 저장합니다"),
            new(blocks[2], "Unchanged label"),
            new(blocks[3], null),
            new(blocks[4], "보존된 글자"),
        ];
        var direct = new List<string>();
        PinOverlayComposition expected = PinTranslationOverlay.Compose(source, translations,
            observeDecision: (block, decision) => direct.Add($"{Array.IndexOf(blocks, block)}:{decision}"));
        var sampled = new List<string>();
        PinOverlayComposition actual;
        using (PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(source, blocks))
        {
            Assert.All(new[] { 0, 1, 2, 3 }, index => Assert.NotNull(preparation.Layouts[index]));
            Assert.Null(preparation.Layouts[4]);
            actual = PinTranslationOverlay.Compose(preparation, translations,
                observeDecision: (block, decision) => sampled.Add($"{Array.IndexOf(blocks, block)}:{decision}"));
        }
        Assert.Contains("1:rendered", direct);
        Assert.Equal(["2:identity", "3:all-rejected", "4:protected"], direct.Skip(2));
        Assert.Equal(direct, sampled);
        Assert.Equal(expected.RenderedBlocks, actual.RenderedBlocks);
        Assert.Equal(expected.NeedsReview, actual.NeedsReview);
        Assert.Equal(Pixels(expected.Image), Pixels(actual.Image));
    });

    [Fact]
    public void APreparedSurfaceIsKeptOnlyWhenAnEarlierPlacementCannotChangeIt() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(260, 80, drawing => drawing.DrawRectangle(Brushes.Black, null, new Rect(40, 39, 120, 1)));
        PinOverlayBlock block = Block("Original link", 40, 20, 120, 16);
        using PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(source, [block]);
        PinTranslationOverlay.BackgroundSurface surface = preparation.Layouts[0]!.Surface;
        // The underline below the line is a decoration of this block, outside its OCR bounds.
        Assert.True(surface.Underline);
        OcrRect[] prepared = preparation.Layouts[0]!.Obstacles;
        Assert.True(surface.Admits(prepared, prepared));
        Assert.True(surface.Admits(prepared, [new(60, 42, 10, 4)]));
        Assert.True(surface.Admits(prepared, [new(170, 20, 30, 16)]));
        Assert.False(surface.Admits(prepared, [new(60, 38, 10, 4)]));
        Assert.False(surface.Admits(prepared, [new(100, 30, 10, 4)]));
        Assert.False(surface.Admits([new(200, 60, 20, 10)], []));
    });

    [Fact]
    public void TheLayoutStepTakesOverTheBlockScanPixels() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(320, 80, drawing => Text(drawing, "Save the document", 20, 20));
        OcrResult recognized = PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line("Save the document", 20, 20, 150, 18));
        PinOverlayBlock[] blocks = [.. PinTranslationOverlay.BuildBlocks(recognized, source, out byte[]? scanned)];
        Assert.NotNull(scanned);
        using PinOverlayPreparation converted = PinTranslationOverlay.Prepare(source, blocks);
        using PinOverlayPreparation shared = PinTranslationOverlay.Prepare(source, blocks, sourcePixels: scanned);
        Assert.Same(scanned, shared.Pixels);
        Assert.Equal(converted.Pixels, shared.Pixels);
        Assert.Equal(converted.FontSizes, shared.FontSizes);
        Assert.Equal(converted.Layouts.Select(layout => layout?.Foreground), shared.Layouts.Select(layout => layout?.Foreground));
        Assert.Throws<ArgumentException>(() => PinTranslationOverlay.Prepare(source, blocks, sourcePixels: new byte[4]));
    });

    [Fact]
    public void CompositionRejectsTranslationsOfOtherBlocks() => StaTestHost.Run(() =>
    {
        BitmapSource source = Draw(200, 60, _ => { });
        PinOverlayBlock block = Block("Source text", 20, 20, 100, 16);
        using PinOverlayPreparation preparation = PinTranslationOverlay.Prepare(source, [block]);
        Assert.Throws<ArgumentException>(() => PinTranslationOverlay.Compose(preparation, [new(block with { }, "번역")]));
        Assert.Throws<ArgumentException>(() => PinTranslationOverlay.Compose(preparation, []));
    });

    [Fact]
    public async Task AFailedTranslationStopsTheSamplingWithoutComposing()
    {
        BitmapSource source = PinTranslationOverlayTests.Bitmap(320, 120);
        OcrResult recognized = PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line("Save the document before closing", 20, 20, 260, 20));
        var service = new PinTranslationService(new FailingTranslator(), PinTranslationDirection.EnglishToKorean);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateRecognizedAsync(recognized, PinContent.FromImage(source), CancellationToken.None));
    }

    private sealed class FailingTranslator : ITextTranslator
    {
        public long ModelDownloadBytes => 0;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<TranslatedText>>(new InvalidOperationException("The model failed."));
    }

    private static PinOverlayBlock Block(string text, double x, double y, double width, double height)
    {
        OcrLine line = PinTranslationOverlayTests.Line(text, x, y, width, height);
        return new(text, line.Bounds, [line]);
    }

    private static void Text(DrawingContext drawing, string text, double x, double y) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, 16, Brushes.Black, 1), new Point(x, y - 2));

    private static BitmapSource Draw(int width, int height, Action<DrawingContext> paint)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            paint(drawing);
        }
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
}
