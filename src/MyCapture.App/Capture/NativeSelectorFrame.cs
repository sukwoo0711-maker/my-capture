using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MyCapture.App.Capture;

/// <summary>
/// The native selector's target frame (<see cref="CaptureFrameStyle"/>): a one-pixel contrast keyline
/// touching the target and a two-pixel guide-colour stroke around it, both outside the target and
/// filled as solid GDI rectangles, so no captured pixel is covered and every edge is crisp.
/// </summary>
/// <remarks>
/// A side whose rings would leave the surface (a maximized window, a whole monitor, a drag from the
/// screen edge) moves inside the target instead, with the stroke on the surface edge and the keyline
/// next to the content, so the frame stays visible. The painter and the damage both come from
/// <see cref="TryLayout"/>, so a repaint covers exactly where the frame was and where it is now.
/// </remarks>
internal static class NativeSelectorFrame
{
    private const int DcBrush = 18;
    private const uint PatCopy = 0x00F00021, InvalidColor = 0xFFFFFFFF;

    /// <summary>
    /// Boxes of the frame: the stroke fills <see cref="Outer"/> minus <see cref="Keyline"/>, the keyline
    /// fills <see cref="Keyline"/> minus <see cref="Inner"/>, and <see cref="Inner"/> is left alone.
    /// </summary>
    internal readonly record struct Rings(Int32Rect Outer, Int32Rect Keyline, Int32Rect Inner);

    /// <summary>The frame of <paramref name="target"/> on a <paramref name="width"/> × <paramref name="height"/> surface.</summary>
    internal static bool TryLayout(Int32Rect target, int width, int height, out Rings rings)
    {
        Int32Rect clipped = NativeSelectorDirtyRegions.Clip(target, width, height);
        if (clipped.Width == 0 || clipped.Height == 0)
        {
            rings = default;
            return false;
        }
        const int outset = CaptureFrameStyle.Outset;
        int right = clipped.X + clipped.Width, bottom = clipped.Y + clipped.Height;
        int outerLeft = clipped.X >= outset ? clipped.X - outset : clipped.X;
        int outerTop = clipped.Y >= outset ? clipped.Y - outset : clipped.Y;
        int outerRight = right <= width - outset ? right + outset : right;
        int outerBottom = bottom <= height - outset ? bottom + outset : bottom;
        var outer = new Int32Rect(outerLeft, outerTop, outerRight - outerLeft, outerBottom - outerTop);
        rings = new Rings(outer, Deflate(outer, CaptureFrameStyle.StrokeWidth), Deflate(outer, outset));
        return true;
    }

    /// <summary>
    /// Writes the strips (at most four) that cover <paramref name="outer"/> outside
    /// <paramref name="inner"/> and returns how many there are.
    /// </summary>
    internal static int Ring(Int32Rect outer, Int32Rect inner, Span<Int32Rect> strips)
    {
        if (outer.Width <= 0 || outer.Height <= 0) return 0;
        if (inner.Width <= 0 || inner.Height <= 0)
        {
            strips[0] = outer;
            return 1;
        }
        int count = 0;
        int outerRight = outer.X + outer.Width, outerBottom = outer.Y + outer.Height;
        int innerRight = inner.X + inner.Width, innerBottom = inner.Y + inner.Height;
        Add(strips, ref count, outer.X, outer.Y, outer.Width, inner.Y - outer.Y);
        Add(strips, ref count, outer.X, innerBottom, outer.Width, outerBottom - innerBottom);
        Add(strips, ref count, outer.X, inner.Y, inner.X - outer.X, inner.Height);
        Add(strips, ref count, innerRight, inner.Y, outerRight - innerRight, inner.Height);
        return count;
    }

    /// <summary>Fills the frame of <paramref name="target"/> into <paramref name="dc"/> with GDI COLORREFs.</summary>
    internal static void Paint(IntPtr dc, Int32Rect target, int width, int height, uint stroke, uint keyline)
    {
        if (!TryLayout(target, width, height, out Rings rings)) return;
        Span<Int32Rect> strips = stackalloc Int32Rect[4];
        IntPtr previousBrush = NativeSelectorInterop.SelectObject(dc, NativeSelectorInterop.GetStockObject(DcBrush));
        uint previousColor = SetDCBrushColor(dc, stroke);
        try
        {
            Fill(dc, strips[..Ring(rings.Outer, rings.Keyline, strips)]);
            _ = SetDCBrushColor(dc, keyline);
            Fill(dc, strips[..Ring(rings.Keyline, rings.Inner, strips)]);
        }
        finally
        {
            if (previousColor != InvalidColor) _ = SetDCBrushColor(dc, previousColor);
            _ = NativeSelectorInterop.SelectObject(dc, previousBrush);
        }
    }

    /// <summary>The GDI COLORREF of <paramref name="brush"/>'s colour, or of <paramref name="fallback"/>.</summary>
    internal static uint ColorRef(Brush? brush, Color fallback)
    {
        Color color = (brush as SolidColorBrush)?.Color ?? fallback;
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
    }

    private static void Fill(IntPtr dc, ReadOnlySpan<Int32Rect> strips)
    {
        foreach (Int32Rect strip in strips) _ = PatBlt(dc, strip.X, strip.Y, strip.Width, strip.Height, PatCopy);
    }

    private static Int32Rect Deflate(Int32Rect box, int by) => box.Width > 2 * by && box.Height > 2 * by
        ? new Int32Rect(box.X + by, box.Y + by, box.Width - (2 * by), box.Height - (2 * by))
        : new Int32Rect(box.X + by, box.Y + by, 0, 0);

    private static void Add(Span<Int32Rect> strips, ref int count, int x, int y, int width, int height)
    {
        if (width > 0 && height > 0) strips[count++] = new Int32Rect(x, y, width, height);
    }

    [DllImport("gdi32.dll")] private static extern uint SetDCBrushColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint operation);
}
