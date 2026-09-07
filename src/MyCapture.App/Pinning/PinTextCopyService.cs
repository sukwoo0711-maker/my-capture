using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static class PinTextCopyService
{
    internal static async Task<bool> CopyAsync(BitmapSource image, IOcrService ocr,
        Func<string, Task<bool>> copyText, double upscale, IReadOnlyList<string> languages)
    {
        OcrResult result = await ocr.RecognizeAsync(OcrRequest.FromBitmap(image, upscale, languages));
        return result.HasText && await copyText(result.Text);
    }
}
