using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;

namespace MyCapture.Platform.Capture;

/// <summary>Materialises a freeform selection from the frozen frame, without reading the screen.</summary>
public static class FreeformCapture
{
    public static BitmapSource Crop(FrozenFrame frame, FreeformMask mask)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(mask);
        if (mask.Bounds.Left < 0 || mask.Bounds.Top < 0
            || mask.Bounds.Right > frame.PixelWidth || mask.Bounds.Bottom > frame.PixelHeight)
            throw new ArgumentOutOfRangeException(nameof(mask), "The mask must be inside the frozen bitmap.");

        // Crop before converting, so only the selected pixels are decoded and retained. Native
        // screen frames are Bgr32 (undefined desktop alpha); conversion makes them opaque BGRA.
        BitmapSource source = new CroppedBitmap(frame.Bitmap,
            new Int32Rect((int)mask.Bounds.Left, (int)mask.Bounds.Top, mask.Width, mask.Height));
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(mask.Width * 4);
        var pixels = new byte[checked(stride * mask.Height)];
        source.CopyPixels(pixels, stride, 0);
        mask.ApplyToBgra32(pixels, stride);
        BitmapSource result = BitmapSource.Create(mask.Width, mask.Height, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
