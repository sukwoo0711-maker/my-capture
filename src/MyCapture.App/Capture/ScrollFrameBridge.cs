using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;

namespace MyCapture.App.Capture;

/// <summary>
/// Converts between WPF <see cref="BitmapSource"/> and the pure-Core <see cref="ScrollFrame"/>
/// so the stitching algorithm never sees a WPF type.
/// </summary>
/// <remarks>
/// Scroll stitching lives in <c>MyCapture.Core</c> and works on tightly packed top-down
/// BGRA32 byte buffers. This bridge is the only place WPF imaging touches that path: a
/// captured region becomes a <see cref="ScrollFrame"/> on the way in and the stitched result
/// becomes a frozen <see cref="BitmapSource"/> on the way out.
/// </remarks>
internal static class ScrollFrameBridge
{
    internal static ScrollFrame ToScrollFrame(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : Convert(source);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = checked(width * ScrollFrame.BytesPerPixel);
        long byteCount = checked((long)stride * height);
        if (byteCount > Array.MaxLength)
        {
            throw new InvalidOperationException("The scrolling frame exceeds managed-array limits.");
        }

        byte[] pixels = new byte[(int)byteCount];
        bgra.CopyPixels(pixels, stride, 0);

        return new ScrollFrame(width, height, pixels);
    }

    internal static BitmapSource ToBitmap(ScrollFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bitmap = new WriteableBitmap(
            frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Stride,
            0);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Convert(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }
}
