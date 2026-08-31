using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Storage;

namespace MyCapture.Platform.Imaging;

/// <summary>
/// Encodes and rescales bitmaps.
/// </summary>
/// <remarks>
/// Wraps WPF's imaging stack rather than <c>System.Drawing</c>. The capture engine
/// already produces <see cref="BitmapSource"/>, the annotation renderer produces one
/// too, and staying inside one imaging stack avoids a conversion that would cost a
/// full-frame copy on every save.
/// </remarks>
public static class ImageCodec
{
    /// <summary>
    /// JPEG quality used for gallery thumbnails.
    /// </summary>
    /// <remarks>
    /// Thumbnails are decorative and there can be hundreds of them, so JPEG at 82 is
    /// preferred over PNG: roughly a tenth of the size with no visible difference at
    /// 320px. The captures themselves are always PNG.
    /// </remarks>
    public const int ThumbnailJpegQuality = 82;

    public static byte[] EncodePng(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var encoder = new PngBitmapEncoder
        {
            // Interlace costs decode time for a benefit that only applies to
            // progressive network loading, which never happens here.
            Interlace = PngInterlaceOption.Off,
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] EncodeJpeg(BitmapSource bitmap, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // A JPEG cannot carry alpha. Flattening onto white first avoids the black
        // fringes that appear when an encoder discards the alpha channel outright.
        BitmapSource opaque = HasAlpha(bitmap) ? FlattenOnto(bitmap, Colors.White) : bitmap;

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(opaque));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Writes a PNG atomically and returns the byte count.
    /// </summary>
    public static long SavePng(BitmapSource bitmap, string path)
    {
        byte[] bytes = EncodePng(bitmap);
        AtomicFile.WriteAllBytes(path, bytes);
        return bytes.Length;
    }

    /// <summary>
    /// Writes a user-facing PNG atomically without retaining an internal recovery backup.
    /// </summary>
    public static long SavePngExport(BitmapSource bitmap, string path)
    {
        byte[] bytes = EncodePng(bitmap);
        AtomicFile.WriteExportBytes(path, bytes);
        return bytes.Length;
    }

    public static long SaveJpeg(BitmapSource bitmap, string path, int quality)
    {
        byte[] bytes = EncodeJpeg(bitmap, quality);
        AtomicFile.WriteAllBytes(path, bytes);
        return bytes.Length;
    }

    /// <summary>
    /// Scales <paramref name="bitmap"/> so its long edge is <paramref name="longEdge"/>.
    /// </summary>
    /// <remarks>
    /// Never upscales. A capture smaller than the thumbnail size is returned as-is,
    /// because blowing a 60px capture up to 320px produces a blurry tile that looks
    /// like a rendering defect.
    /// </remarks>
    public static BitmapSource CreateThumbnail(BitmapSource bitmap, int longEdge)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (longEdge <= 0)
        {
            return bitmap;
        }

        int sourceLongEdge = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (sourceLongEdge <= longEdge)
        {
            return bitmap;
        }

        double scale = (double)longEdge / sourceLongEdge;
        return Resize(bitmap, scale);
    }

    public static BitmapSource Resize(BitmapSource bitmap, double scale)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (Math.Abs(scale - 1.0) < 0.0001)
        {
            return bitmap;
        }

        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>
    /// Scales up with nearest-neighbour sampling.
    /// </summary>
    /// <remarks>
    /// Used before OCR. Smooth interpolation invents intermediate greys along glyph
    /// edges and measurably lowers recognition accuracy on small UI text, whereas
    /// nearest-neighbour keeps the strokes hard.
    /// </remarks>
    public static BitmapSource UpscaleForRecognition(BitmapSource bitmap, double factor)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (factor <= 1.0)
        {
            return bitmap;
        }

        int width = (int)Math.Round(bitmap.PixelWidth * factor);
        int height = (int)Math.Round(bitmap.PixelHeight * factor);

        var visual = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static bool HasAlpha(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        PixelFormat format = bitmap.Format;
        return format == PixelFormats.Bgra32
            || format == PixelFormats.Pbgra32
            || format == PixelFormats.Rgba64
            || format == PixelFormats.Prgba64
            || format == PixelFormats.Rgba128Float
            || format == PixelFormats.Prgba128Float;
    }

    /// <summary>
    /// Composites <paramref name="bitmap"/> over a solid colour.
    /// </summary>
    public static BitmapSource FlattenOnto(BitmapSource bitmap, Color background)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new System.Windows.Rect(0, 0, width, height));
            dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// Decodes an image file into a frozen bitmap.
    /// </summary>
    /// <remarks>
    /// <c>OnLoad</c> caching is essential: the default delays reading and keeps the
    /// file locked, which would prevent the queue from ever evicting that capture.
    /// </remarks>
    public static BitmapSource? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            // Normalise to an absolute path: a relative path passes File.Exists (resolved
            // against the CWD) but throws UriFormatException on an Absolute Uri, which would
            // escape this method's null-on-failure contract.
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (NotSupportedException)
        {
            // Corrupt or truncated file.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an image file at reduced resolution.
    /// </summary>
    /// <remarks>
    /// Used by the gallery. Decoding 300 full-resolution 4K PNGs to draw 320px tiles
    /// would consume gigabytes; <c>DecodePixelWidth</c> lets the decoder do the
    /// downscale and never materialise the full frame.
    /// </remarks>
    public static BitmapSource? TryLoadScaled(string path, int decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Max(1, decodePixelWidth);
            bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            return null;
        }
    }
}
