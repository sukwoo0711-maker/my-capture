using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>
/// Renders only two bounded chips, the size read-out and the hint, at the sizes their content
/// takes. Reuses their pixels until the text changes. The chips are stamped into the selector's
/// own bitmap, where no DWM border reaches, so they keep their drawn one-pixel edges.
/// </summary>
internal sealed class NativeSelectorStaticChrome(FrameworkElement size, FrameworkElement guide)
{
    private readonly Border _size = new() { Background = CaptureWindowExclusion.OpaqueBackground(), Child = size };
    private readonly Border _guide = new() { Background = CaptureWindowExclusion.OpaqueBackground(), Child = guide };
    private string? _sizeText, _guideText;
    private double _scale;
    private Raster _sizePixels, _guidePixels;

    /// <summary>
    /// The read-out stamp follows the pointer; the hint is centred in the work area with its top at
    /// <paramref name="guideTop"/> (physical pixels), just below the mode bar.
    /// </summary>
    internal IReadOnlyList<NativeSelectorStamp> Update(string sizeText, string guideText, PointD pointer,
        RectD workArea, RectD screenBounds, double scale, bool guideVisible, double guideTop)
    {
        if (_sizeText != sizeText || _scale != scale || _sizePixels.Pixels is null)
            _sizePixels = Rasterize(_size, scale);
        if (_guideText != guideText || _scale != scale || _guidePixels.Pixels is null)
            _guidePixels = Rasterize(_guide, scale);
        _sizeText = sizeText;
        _guideText = guideText;
        _scale = scale;
        NativeSelectorStamp readout = Place(_sizePixels, pointer.X + 20 * scale, pointer.Y + 20 * scale);
        if (!guideVisible) return [readout];
        return [Place(_guidePixels, workArea.Left + (workArea.Width - _guidePixels.Width) / 2, guideTop), readout];

        NativeSelectorStamp Place(Raster raster, double x, double y)
        {
            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - raster.Width));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - raster.Height));
            return new(new Int32Rect((int)Math.Round(x - screenBounds.Left), (int)Math.Round(y - screenBounds.Top),
                raster.Width, raster.Height), raster.Pixels!);
        }
    }

    private static Raster Rasterize(Border element, double scale)
    {
        // A changed text marks only its own element dirty until a layout pass, which this detached
        // wrapper never gets: invalidate the chain so Measure returns the new size.
        for (UIElement? item = element; item is not null; item = (item as Decorator)?.Child) item.InvalidateMeasure();
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Whole physical pixels, so the chip's one-pixel edge lands on the stamp's last row and column.
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Width * scale - 0.0001));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Height * scale - 0.0001));
        double width = pixelWidth / scale, height = pixelHeight / scale;
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        // RenderTargetBitmap retains fractional-edge coverage and alpha rounding. Flatten onto
        // the same opaque window floor so those pixels cannot pick up the desktop underneath.
        Color backing = ((SolidColorBrush)element.Background).Color;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int inverse = 255 - pixels[i + 3];
            pixels[i] = (byte)Math.Min(255, pixels[i] + (backing.B * inverse + 127) / 255);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + (backing.G * inverse + 127) / 255);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + (backing.R * inverse + 127) / 255);
            pixels[i + 3] = 255;
        }
        return new Raster(pixels, pixelWidth, pixelHeight);
    }

    private readonly record struct Raster(byte[]? Pixels, int Width, int Height);
}
