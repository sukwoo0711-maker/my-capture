using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Ocr;

/// <summary>The preview and Windows OCR consume the same frozen, in-memory image.</summary>
internal static class OcrSourceSnapshot
{
    internal static Task<BitmapSource?> LoadAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Bitmap is not null)
            return Task.FromResult<BitmapSource?>(Freeze(request.Bitmap));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource? source = null;
            if (request.EncodedImage is not null)
            {
                using var stream = new MemoryStream(request.EncodedImage, writable: false);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                source = decoder.Frames[0];
            }
            else if (!string.IsNullOrWhiteSpace(request.FilePath))
                source = ImageCodec.TryLoad(request.FilePath);
            cancellationToken.ThrowIfCancellationRequested();
            return source is null ? null : Freeze(source);
        }, cancellationToken);
    }

    private static BitmapSource Freeze(BitmapSource image)
    {
        if (image.IsFrozen) return image;
        BitmapSource snapshot = image.CloneCurrentValue();
        snapshot.Freeze();
        return snapshot;
    }

    internal static OcrRequest WindowsRequest(OcrRequest original, BitmapSource image) =>
        OcrRequest.FromBitmap(image, original.UpscaleFactor, original.PreferredLanguages,
            original.SearchRotatedOrientations, original.EnhanceContrast,
            useNeuralModel: original.UseNeuralModel, localCorrection: original.LocalCorrection, useSuperResolution: false);
}
