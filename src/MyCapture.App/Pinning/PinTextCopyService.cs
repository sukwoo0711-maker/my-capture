using System.Windows.Media.Imaging;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static class PinTextCopyService
{
    internal static Task<bool> CopyAsync(
        BitmapSource image,
        IOcrService ocr,
        Func<string, Task<bool>> copyText,
        double upscale,
        IReadOnlyList<string> languages) =>
        CopyAsync(image, ocr, copyText, OcrRequest.FromBitmap(image, upscale, languages));

    internal static Task<bool> CopyAsync(
        BitmapSource image,
        IOcrService ocr,
        Func<string, Task<bool>> copyText,
        OcrSettings settings) =>
        CopyAsync(image, ocr, copyText, OcrRequestFactory.FromBitmap(image, settings));

    private static async Task<bool> CopyAsync(
        BitmapSource image,
        IOcrService ocr,
        Func<string, Task<bool>> copyText,
        OcrRequest request)
    {
        _ = image;
        OcrResult result = await ocr.RecognizeAsync(request);
        return result.HasText && await copyText(result.Text);
    }
}
