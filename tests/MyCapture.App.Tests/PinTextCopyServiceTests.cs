using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTextCopyServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recognition_CopiesOnlySuccessfulText(bool recognized)
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        image.Freeze();
        string? copied = null;
        var ocr = new FakeOcr(recognized ? OcrResult.Success("exact\r\ntext", "en", [], TimeSpan.Zero) : OcrResult.Unavailable());
        bool success = await PinTextCopyService.CopyAsync(image, ocr,
            text => { copied = text; return Task.FromResult(true); }, 2, ["en"]);
        Assert.Equal(recognized, success);
        Assert.Equal(recognized ? "exact\r\ntext" : null, copied);
        Assert.Same(image, ocr.Request?.Bitmap);
    }

    private sealed class FakeOcr(OcrResult result) : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en"];
        internal OcrRequest? Request { get; private set; }
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
