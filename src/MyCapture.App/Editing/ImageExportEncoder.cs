using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

internal sealed record ImageExportResult(byte[] Bytes, string Extension, long OriginalBytes,
    int TargetReductionPercent, int? JpegQuality, bool PreservedTransparency)
{
    internal int PixelWidth { get; init; }
    internal int PixelHeight { get; init; }
    internal double ActualReductionPercent => 100.0 * (1.0 - (double)Bytes.LongLength / OriginalBytes);
    internal bool TargetReached => Bytes.LongLength <= OriginalBytes * (100 - TargetReductionPercent) / 100;
}

/// <summary>Encodes a styled copy; automatic reduction preserves alpha and explicit JPEG uses white.</summary>
internal static class ImageExportEncoder
{
    internal static byte[] EncodePngCopy(BitmapSource image, ImageExportOptions options, string? sourceUrl)
    {
        BitmapSource copy = ImageExportRenderer.Render(image, new() { LongEdge = options.Normalize().LongEdge }, default);
        byte[] bytes = PngSourceUrlMetadata.StripSourceUrl(ImageCodec.EncodePng(copy));
        return options.IncludeSourceUrl ? PngSourceUrlMetadata.WithSourceUrl(bytes, sourceUrl) : bytes;
    }

    internal static ImageExportResult Encode(BitmapSource image, byte[] originalPng, ImageExportOptions options,
        Color accent, string? sourceUrl, CancellationToken cancellationToken = default)
    {
        options = options.Normalize();
        sourceUrl ??= PngSourceUrlMetadata.TryReadSourceUrl(originalPng);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource rendered = ImageExportRenderer.Render(image, options, accent);
        byte[] png = ReferenceEquals(rendered, image) ? PngSourceUrlMetadata.StripSourceUrl(originalPng)
            : ImageCodec.EncodePng(rendered);
        ImageExportResult result;
        if (options.Format == ImageExportFormat.Png)
            result = new(png, ".png", originalPng.LongLength, options.ReductionPercent, null, false);
        else if (options.Format == ImageExportFormat.Jpeg)
        {
            long target = originalPng.LongLength * (100 - options.ReductionPercent) / 100;
            byte[] best = EncodeJpeg(rendered, 100, options.IncludeSourceUrl ? sourceUrl : null);
            int bestQuality = 100;
            if (options.ReductionPercent > 0 && best.LongLength > target)
            {
                int lower = 1, upper = 99;
                while (lower <= upper)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int quality = lower + (upper - lower) / 2;
                    byte[] candidate = EncodeJpeg(rendered, quality, options.IncludeSourceUrl ? sourceUrl : null);
                    if (candidate.LongLength <= target || (best.LongLength > target && candidate.Length < best.Length))
                    { best = candidate; bestQuality = quality; }
                    if (candidate.LongLength <= target) lower = quality + 1;
                    else upper = quality - 1;
                }
            }
            result = new(best, ".jpg", originalPng.LongLength, options.ReductionPercent, bestQuality, false);
        }
        else
        {
            // Keep the existing automatic reduction/alpha-preservation policy.
            result = EncodeAutomatic(rendered, png, options.ReductionPercent, originalPng.LongLength, cancellationToken);
            if (result.Extension == ".jpg" && options.IncludeSourceUrl)
                result = result with { Bytes = EncodeJpeg(rendered, result.JpegQuality!.Value, sourceUrl) };
        }
        if (result.Extension == ".png" && options.IncludeSourceUrl)
            result = result with { Bytes = PngSourceUrlMetadata.WithSourceUrl(result.Bytes, sourceUrl) };
        cancellationToken.ThrowIfCancellationRequested();
        return result with { PixelWidth = rendered.PixelWidth, PixelHeight = rendered.PixelHeight };
    }

    private static byte[] EncodeJpeg(BitmapSource image, int quality, string? sourceUrl)
    {
        byte[] jpeg = ImageCodec.EncodeJpeg(image, quality);
        if (string.IsNullOrEmpty(sourceUrl)) return jpeg;
        // JPEG has no PNG tEXt chunks. Store the opted-in URL in a UTF-8 COM segment.
        byte[] comment = System.Text.Encoding.UTF8.GetBytes("SourceURL=" + sourceUrl);
        if (comment.Length > 65533) throw new ArgumentException("Source URL is too long.", nameof(sourceUrl));
        byte[] result = new byte[jpeg.Length + comment.Length + 4];
        jpeg.AsSpan(0, 2).CopyTo(result);
        result[2] = 0xFF; result[3] = 0xFE;
        result[4] = (byte)((comment.Length + 2) >> 8); result[5] = (byte)(comment.Length + 2);
        comment.CopyTo(result, 6);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(6 + comment.Length));
        return result;
    }

    internal static ImageExportResult Encode(BitmapSource image, byte[] originalPng, int reduction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalPng);
        return EncodeAutomatic(image, originalPng, reduction, originalPng.LongLength, cancellationToken);
    }

    private static ImageExportResult EncodeAutomatic(BitmapSource image, byte[] originalPng, int reduction,
        long baselineBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(originalPng);
        ArgumentOutOfRangeException.ThrowIfNegative(reduction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reduction, 90);
        if (originalPng.Length == 0) throw new ArgumentException("The original PNG is empty.", nameof(originalPng));
        cancellationToken.ThrowIfCancellationRequested();
        if (reduction == 0) return new(originalPng, ".png", baselineBytes, reduction, null, false);

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
                    return new(originalPng, ".png", baselineBytes, reduction, null, true);
        }

        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        opaque.Freeze();
        long target = baselineBytes * (100 - reduction) / 100;
        if (originalPng.LongLength <= target) return new(originalPng, ".png", baselineBytes, reduction, null, false);
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
        return new(best, bestQuality.HasValue ? ".jpg" : ".png", baselineBytes,
            reduction, bestQuality, false);
    }
}
