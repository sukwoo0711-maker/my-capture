using System.Windows;

namespace MyCapture.App.Capture;

/// <summary>Physical-pixel damage: the reveal's symmetric difference and its old/new border.</summary>
internal static class NativeSelectorDirtyRegions
{
    internal static IReadOnlyList<Int32Rect> Changed(Int32Rect previous, Int32Rect current,
        int width, int height)
    {
        if (previous == current) return [];
        var result = new List<Int32Rect>(16);
        Difference(previous, current, result);
        Difference(current, previous, result);
        Border(previous, result);
        Border(current, result);
        return result.Select(r => Clip(r, width, height)).Where(r => r.Width > 0 && r.Height > 0).Distinct().ToArray();
    }

    internal static Int32Rect Clip(Int32Rect rectangle, int width, int height)
    {
        int left = Math.Clamp(rectangle.X, 0, width), top = Math.Clamp(rectangle.Y, 0, height);
        int right = (int)Math.Clamp((long)rectangle.X + rectangle.Width, 0, width);
        int bottom = (int)Math.Clamp((long)rectangle.Y + rectangle.Height, 0, height);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static void Difference(Int32Rect a, Int32Rect b, List<Int32Rect> result)
    {
        if (a.Width == 0 || a.Height == 0) return;
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (b.Width == 0 || b.Height == 0 || right <= left || bottom <= top) { result.Add(a); return; }
        Add(a.X, a.Y, a.Width, top - a.Y);
        Add(a.X, bottom, a.Width, a.Y + a.Height - bottom);
        Add(a.X, top, left - a.X, bottom - top);
        Add(right, top, a.X + a.Width - right, bottom - top);
        void Add(int x, int y, int w, int h) { if (w > 0 && h > 0) result.Add(new Int32Rect(x, y, w, h)); }
    }

    private static void Border(Int32Rect r, List<Int32Rect> result)
    {
        if (r.Width == 0 || r.Height == 0) return;
        // Two physical pixels on either side cover the 2px GDI pen, including corner joins.
        result.Add(new Int32Rect(r.X - 2, r.Y - 2, r.Width + 4, 4));
        result.Add(new Int32Rect(r.X - 2, r.Y + r.Height - 2, r.Width + 4, 4));
        result.Add(new Int32Rect(r.X - 2, r.Y - 2, 4, r.Height + 4));
        result.Add(new Int32Rect(r.X + r.Width - 2, r.Y - 2, 4, r.Height + 4));
    }
}
