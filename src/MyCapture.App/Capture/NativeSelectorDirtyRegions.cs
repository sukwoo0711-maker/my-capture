using System.Windows;

namespace MyCapture.App.Capture;

/// <summary>Physical-pixel damage: the reveal's symmetric difference and its old/new border.</summary>
internal static class NativeSelectorDirtyRegions
{
    internal static IReadOnlyList<Int32Rect> Changed(Int32Rect previous, Int32Rect current,
        int width, int height)
    {
        var result = new List<Int32Rect>(16);
        Changed(previous, current, width, height, result);
        return result.ToArray();
    }

    /// <summary>
    /// Fills <paramref name="result"/> (cleared first) with the clipped, distinct damage. Called for every
    /// pointer sample of a drag, so it reuses the caller's list and allocates nothing.
    /// </summary>
    internal static void Changed(Int32Rect previous, Int32Rect current, int width, int height, List<Int32Rect> result)
    {
        result.Clear();
        if (previous == current) return;
        Difference(previous, current, result);
        Difference(current, previous, result);
        Border(previous, result);
        Border(current, result);
        int kept = 0;
        for (int index = 0; index < result.Count; index++)
        {
            Int32Rect clipped = Clip(result[index], width, height);
            if (clipped.Width == 0 || clipped.Height == 0 || Contains(result, kept, clipped)) continue;
            result[kept++] = clipped;
        }
        result.RemoveRange(kept, result.Count - kept);
    }

    private static bool Contains(List<Int32Rect> list, int count, Int32Rect rectangle)
    {
        for (int index = 0; index < count; index++) if (list[index] == rectangle) return true;
        return false;
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
        Add(result, a.X, a.Y, a.Width, top - a.Y);
        Add(result, a.X, bottom, a.Width, a.Y + a.Height - bottom);
        Add(result, a.X, top, left - a.X, bottom - top);
        Add(result, right, top, a.X + a.Width - right, bottom - top);
    }

    private static void Add(List<Int32Rect> result, int x, int y, int w, int h)
    {
        if (w > 0 && h > 0) result.Add(new Int32Rect(x, y, w, h));
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
