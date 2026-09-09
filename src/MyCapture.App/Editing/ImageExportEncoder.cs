using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

internal sealed record ImageExportResult(byte[] Bytes, string Extension, long OriginalBytes,
    int TargetReductionPercent, int? JpegQuality, bool PreservedTransparency)
{
    internal double ActualReductionPercent => 100.0 * (1.0 - (double)Bytes.LongLength / OriginalBytes);
    internal bool TargetReached => Bytes.LongLength <= OriginalBytes * (100 - TargetReductionPercent) / 100;
}

/// <summary>Targets encoded byte size without changing pixel dimensions or discarding alpha.</summary>
internal static class ImageExportEncoder
{
    internal static ImageExportResult Encode(BitmapSource image, byte[] originalPng, int reduction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(originalPng);
        ArgumentOutOfRangeException.ThrowIfNegative(reduction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reduction, 90);
        if (originalPng.Length == 0) throw new ArgumentException("The original PNG is empty.", nameof(originalPng));
        cancellationToken.ThrowIfCancellationRequested();
        if (reduction == 0) return new(originalPng, ".png", originalPng.LongLength, reduction, null, false);

        // Alpha-capable formats are also used for opaque screen captures. Inspect actual
        // pixels instead of rejecting every BGRA screenshot. One scan line bounds scratch RAM.
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        byte[] row = new byte[checked(image.PixelWidth * 4)];
        for (int y = 0; y < image.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            converted.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, row.Length, 0);
            for (int x = 3; x < row.Length; x += 4)
                if (row[x] != 255)
                    return new(originalPng, ".png", originalPng.LongLength, reduction, null, true);
        }

        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        long target = originalPng.LongLength * (100 - reduction) / 100;
        byte[] best = originalPng;
        int? bestQuality = null;
        int lower = 1, upper = 100;
        // At most seven attempts. Retain only the best candidate; no quality/image cache.
        while (lower <= upper)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int quality = lower + (upper - lower) / 2;
            byte[] candidate = ImageCodec.EncodeJpeg(opaque, quality);
            if (candidate.LongLength <= target)
            {
                best = candidate;
                bestQuality = quality;
                lower = quality + 1;
            }
            else
            {
                if (best.LongLength > target && candidate.Length < best.Length)
                {
                    best = candidate;
                    bestQuality = quality;
                }
                upper = quality - 1;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(best, bestQuality.HasValue ? ".jpg" : ".png", originalPng.LongLength,
            reduction, bestQuality, false);
    }
}
