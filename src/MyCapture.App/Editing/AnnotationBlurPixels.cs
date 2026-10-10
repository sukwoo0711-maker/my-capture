using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

/// <summary>
/// Bridges WPF bitmaps and <see cref="GaussianBlurKernel"/>.
/// </summary>
/// <remarks>
/// Both entry points convert to premultiplied BGRA, run the same integer kernel and return a
/// frozen 96-DPI bitmap. Annotation coordinates are image pixels and every caller draws the
/// result into an explicit pixel rectangle, so neither the source bitmap's DPI nor the
/// monitor scale changes the outcome, and a region cut from <see cref="BlurWhole"/> holds
/// exactly the bytes <see cref="BlurRegion"/> returns for it.
/// </remarks>
internal static class AnnotationBlurPixels
{
    /// <summary>Blurs the whole of <paramref name="source"/>. Safe to call off the UI thread for a frozen source.</summary>
    internal static BitmapSource BlurWhole(BitmapSource source, int radius, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource pixels = AsPremultiplied(source);
        int width = pixels.PixelWidth;
        int height = pixels.PixelHeight;
        int stride = checked(width * 4);
        byte[] buffer = new byte[checked(stride * height)];
        pixels.CopyPixels(buffer, stride, 0);
        cancellationToken.ThrowIfCancellationRequested();
        GaussianBlurKernel.BlurInPlace(buffer, width, height, radius, cancellationToken);
        return Freeze(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, buffer, stride));
    }

    /// <summary>
    /// Blurs only <paramref name="pixelRegion"/> (whole pixels inside the source), reading just
    /// the margin the kernel needs around it.
    /// </summary>
    internal static BitmapSource BlurRegion(BitmapSource source, RectD pixelRegion, int radius)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource pixels = AsPremultiplied(source);
        int x = (int)pixelRegion.X;
        int y = (int)pixelRegion.Y;
        int width = (int)pixelRegion.Width;
        int height = (int)pixelRegion.Height;
        (int wx, int wy, int ww, int wh) = GaussianBlurKernel.SourceWindow(
            pixels.PixelWidth, pixels.PixelHeight, x, y, width, height, radius);

        int windowStride = checked(ww * 4);
        byte[] window = new byte[checked(windowStride * wh)];
        pixels.CopyPixels(new Int32Rect(wx, wy, ww, wh), window, windowStride, 0);
        byte[] patch = GaussianBlurKernel.BlurRegion(window, ww, wh, windowStride, x - wx, y - wy, width, height, radius);
        return Freeze(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, patch, width * 4));
    }

    private static BitmapSource AsPremultiplied(BitmapSource source) =>
        source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);

    private static BitmapSource Freeze(BitmapSource bitmap)
    {
        bitmap.Freeze();
        return bitmap;
    }
}
