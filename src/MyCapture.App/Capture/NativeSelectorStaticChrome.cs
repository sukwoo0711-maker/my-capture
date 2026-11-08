using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>Renders only two bounded chips. Reuses their pixels until the text changes.</summary>
internal sealed class NativeSelectorStaticChrome(FrameworkElement size, FrameworkElement guide)
{
    private readonly Border _size = new() { Background = CaptureWindowExclusion.OpaqueBackground(), Child = size };
    private readonly Border _guide = new() { Background = CaptureWindowExclusion.OpaqueBackground(), Child = guide };
    private string? _sizeText, _guideText;
    private double _scale;
    private byte[]? _sizePixels, _guidePixels;

    internal IReadOnlyList<NativeSelectorStamp> Update(string sizeText, string guideText, PointD pointer,
        RectD workArea, RectD screenBounds, double scale, bool guideVisible)
    {
        if (_sizeText != sizeText || _scale != scale || _sizePixels is null)
            _sizePixels = Rasterize(_size, 190, 36, scale);
        if (_guideText != guideText || _scale != scale || _guidePixels is null)
            _guidePixels = Rasterize(_guide, 320, 38, scale);
        _sizeText = sizeText;
        _guideText = guideText;
        _scale = scale;
        NativeSelectorStamp readout = Place(_sizePixels, 190, 36, pointer.X + 20 * scale, pointer.Y + 20 * scale);
        if (!guideVisible) return [readout];
        return [Place(_guidePixels, 320, 38, workArea.Left + (workArea.Width - 320 * scale) / 2,
            workArea.Top + 68 * scale), readout];

        NativeSelectorStamp Place(byte[] pixels, double width, double height, double x, double y)
        {
            int w = (int)Math.Ceiling(width * scale), h = (int)Math.Ceiling(height * scale);
            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - w));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - h));
            return new(new Int32Rect((int)Math.Round(x - screenBounds.Left), (int)Math.Round(y - screenBounds.Top), w, h), pixels);
        }
    }

    private static byte[] Rasterize(Border element, int width, int height, double scale)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
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
        return pixels;
    }
}
