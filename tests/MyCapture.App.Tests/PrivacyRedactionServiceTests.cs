using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Ocr;
using MyCapture.Core.Primitives;
using MyCapture.Core.Privacy;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PrivacyRedactionServiceTests
{
    [Fact]
    public async Task FindAsync_MapsOcrWordsToPaddedPlaintextFreeRegions()
    {
        var ocr = new FakeOcr(OcrResult.Success(
            "person@example.com",
            "en-US",
            [new OcrLine(
                "person@example.com",
                new OcrRect(5, 6, 40, 10),
                [new OcrWord("person@example.com", new OcrRect(5, 6, 40, 10))])],
            TimeSpan.Zero));
        var service = new PrivacyRedactionService(ocr, new PrivacyDetector(), () => new OcrSettings());

        PrivacyRedactionResult result = await service.FindAsync(Bitmap(100, 60));

        Assert.Equal(PrivacyRedactionStatus.Success, result.Status);
        Assert.Equal([new RectD(2, 3, 46, 16)], result.Regions);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task FindAsync_ReportsUnavailableWithoutCallingRecognition()
    {
        var ocr = new FakeOcr(OcrResult.Unavailable()) { IsAvailable = false };
        var service = new PrivacyRedactionService(ocr, new PrivacyDetector(), () => new OcrSettings());

        PrivacyRedactionResult result = await service.FindAsync(Bitmap(10, 10));

        Assert.Equal(PrivacyRedactionStatus.Unavailable, result.Status);
        Assert.Equal(0, ocr.CallCount);
    }

    private static BitmapSource Bitmap(int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[width * height * 4],
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakeOcr(OcrResult result) : IOcrService
    {
        public bool IsAvailable { get; set; } = true;

        public IReadOnlyList<string> SupportedLanguages => ["ko-KR", "en-US"];

        public int CallCount { get; private set; }

        public Task<OcrResult> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
