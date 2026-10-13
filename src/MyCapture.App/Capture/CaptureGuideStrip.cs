using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Capture;

/// <summary>
/// The selector's dashed starting guides, rasterised once into device-pixel bitmaps.
/// </summary>
/// <remarks>
/// Each guide is a shadow stroke two physical pixels wide under a one-pixel accent stroke, both
/// dashed and antialiased. Drawn as geometry, a guide spanning the virtual desktop is hundreds of
/// dashes: WPF re-rasterised all of them whenever a dirty region touched the strip, which is every
/// pointer frame, and that alone kept the render thread near saturation. A cursor sits on a whole
/// physical pixel, so the strokes always fall on the same device-pixel pattern; the selector draws
/// these bitmaps there pixel for pixel instead, and keeps the geometry for any other alignment.
/// </remarks>
internal static class CaptureGuideStrip
{
    /// <summary>Shadow and accent widths in physical pixels.</summary>
    internal const double ShadowWidth = 2, LineWidth = 1;

    /// <summary>Strips across: the shadow covers one pixel on each side of the cursor's pixel edge.</summary>
    internal const int Thickness = 2;

    private static readonly object Gate = new();
    private static readonly List<(int Length, bool Horizontal, Color Shadow, Color Line, BitmapSource Strip)> Cache = new(8);

    /// <summary>The shadow stroke; <paramref name="pixel"/> is one physical pixel in drawing units.</summary>
    internal static Pen ShadowPen(Brush brush, double pixel) => Create(brush, ShadowWidth * pixel, [1.5, 1.5]);

    /// <summary>The accent stroke; <paramref name="pixel"/> is one physical pixel in drawing units.</summary>
    internal static Pen LinePen(Brush brush, double pixel) => Create(brush, LineWidth * pixel, [3, 3]);

    private static Pen Create(Brush brush, double width, double[] dashes)
    {
        var pen = new Pen(brush, width) { DashStyle = new DashStyle(dashes, 0) };
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// One guide <paramref name="length"/> physical pixels long and <see cref="Thickness"/> across, starting
    /// at the selector's edge: exactly what the two strokes paint when the line lies on a pixel edge.
    /// Frozen; solid-colour palettes are cached, so later selectors reuse it.
    /// </summary>
    internal static BitmapSource Get(Brush shadow, Brush line, int length, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        bool cacheable = shadow is SolidColorBrush { Opacity: 1 } && line is SolidColorBrush { Opacity: 1 };
        Color shadowColor = (shadow as SolidColorBrush)?.Color ?? default;
        Color lineColor = (line as SolidColorBrush)?.Color ?? default;
        if (cacheable)
        {
            lock (Gate)
            {
                foreach (var entry in Cache)
                {
                    if (entry.Length == length && entry.Horizontal == horizontal && entry.Shadow == shadowColor && entry.Line == lineColor)
                        return entry.Strip;
                }
            }
        }

        BitmapSource strip = Render(shadow, line, length, horizontal);
        if (cacheable)
        {
            lock (Gate)
            {
                if (Cache.Count >= 8) Cache.RemoveAt(0);
                Cache.Add((length, horizontal, shadowColor, lineColor, strip));
            }
        }

        return strip;
    }

    private static BitmapSource Render(Brush shadow, Brush line, int length, bool horizontal)
    {
        // Always rasterised as a vertical line. WPF scan-converts row by row, so a long horizontal
        // dashed line keeps every dash on the same two rows and takes about ten times longer; the
        // transposed vertical result is the same coverage, pixel for pixel.
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var from = new Point(1, 0);
            var to = new Point(1, length);
            dc.DrawLine(ShadowPen(shadow, 1), from, to);
            dc.DrawLine(LinePen(line, 1), from, to);
        }

        var target = new RenderTargetBitmap(Thickness, length, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        if (!horizontal)
        {
            target.Freeze();
            return target;
        }

        int rowBytes = Thickness * 4;
        byte[] column = new byte[checked(length * rowBytes)];
        target.CopyPixels(column, rowBytes, 0);
        byte[] row = new byte[column.Length];
        for (int y = 0; y < length; y++)
        {
            for (int x = 0; x < Thickness; x++)
                Buffer.BlockCopy(column, ((y * Thickness) + x) * 4, row, ((x * length) + y) * 4, 4);
        }

        BitmapSource transposed = BitmapSource.Create(length, Thickness, 96, 96, PixelFormats.Pbgra32, null, row, length * 4);
        transposed.Freeze();
        return transposed;
    }
}
