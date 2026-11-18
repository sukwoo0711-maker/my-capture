using System.Windows;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorDirtyRegionsTests
{
    [Fact]
    public void UnchangedSelectionHasNoDamage()
    {
        var region = new Int32Rect(5, 6, 10, 8);
        Assert.Empty(NativeSelectorDirtyRegions.Changed(region, region, 40, 30));
    }

    [Fact]
    public void SmallMovePreservesUnchangedInteriorAndDistantBackground()
    {
        var before = new Int32Rect(10, 10, 60, 60);
        var after = new Int32Rect(11, 10, 60, 60);
        IReadOnlyList<Int32Rect> damage = NativeSelectorDirtyRegions.Changed(before, after, 100, 100);
        Assert.DoesNotContain(damage, r => Contains(r, 40, 40));
        Assert.DoesNotContain(damage, r => Contains(r, 95, 95));
        Assert.Contains(damage, r => Contains(r, 10, 40));
        Assert.Contains(damage, r => Contains(r, 70, 40));
        Assert.True(damage.Sum(r => r.Width * r.Height) < 100 * 100);
    }

    [Fact]
    public void DamageIsExactlyTheChangedRevealAndTheOldAndNewFrameWithinTheDesktop()
    {
        const int width = 32, height = 24;
        Int32Rect[] regions = [Int32Rect.Empty, new(0, 0, width, height), new(0, 0, 1, 1),
            new(31, 23, 1, 1), new(2, 3, 13, 11), new(3, 3, 13, 11), new(20, 15, 10, 8),
            new(5, 5, 4, 4), new(0, 8, 32, 1), new(3, 3, 26, 18), new(2, 2, 28, 20), new(4, 0, 9, 24)];
        foreach (Int32Rect before in regions)
        foreach (Int32Rect after in regions)
        {
            IReadOnlyList<Int32Rect> damage = NativeSelectorDirtyRegions.Changed(before, after, width, height);
            Assert.All(damage, r =>
            {
                Assert.InRange(r.X, 0, width - 1);
                Assert.InRange(r.Y, 0, height - 1);
                Assert.InRange(r.Width, 1, width - r.X);
                Assert.InRange(r.Height, 1, height - r.Y);
            });
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool needed = before != after && (Contains(before, x, y) != Contains(after, x, y)
                    || InFrame(before, x, y, width, height) || InFrame(after, x, y, width, height));
                // Covered where the picture changes, and nowhere else: the DIB path repaints only these.
                Assert.True(needed == damage.Any(r => Contains(r, x, y)),
                    $"{(needed ? "Missing" : "Extra")} ({x}, {y}): {before} -> {after}");
            }
        }
    }

    [Fact]
    public void TheFrameLiesOutsideTheTargetAndMovesInsideOnlyAtTheDesktopEdge()
    {
        // A 1-pixel move of a window in the middle: three pixels outside each old and new edge.
        IReadOnlyList<Int32Rect> damage = NativeSelectorDirtyRegions.Changed(
            new Int32Rect(100, 80, 200, 120), new Int32Rect(101, 80, 200, 120), 1000, 800);
        Assert.Contains(damage, r => Contains(r, 97, 140));
        Assert.Contains(damage, r => Contains(r, 99, 77));
        Assert.Contains(damage, r => Contains(r, 303, 140));
        Assert.DoesNotContain(damage, r => Contains(r, 96, 140));
        Assert.DoesNotContain(damage, r => Contains(r, 101, 140));
        Assert.DoesNotContain(damage, r => Contains(r, 299, 140));
        Assert.DoesNotContain(damage, r => Contains(r, 304, 140));
        Assert.DoesNotContain(damage, r => Contains(r, 200, 81));

        // A maximized window: every side is on the desktop edge, so the frame is its outer three
        // pixels, the stroke on the edge and the keyline next to the content.
        Assert.True(NativeSelectorFrame.TryLayout(new Int32Rect(0, 0, 1000, 800), 1000, 800, out NativeSelectorFrame.Rings full));
        Assert.Equal(new Int32Rect(0, 0, 1000, 800), full.Outer);
        Assert.Equal(new Int32Rect(2, 2, 996, 796), full.Keyline);
        Assert.Equal(new Int32Rect(3, 3, 994, 794), full.Inner);

        // Touching only the left edge: inside there, outside everywhere else.
        Assert.True(NativeSelectorFrame.TryLayout(new Int32Rect(0, 50, 100, 60), 1000, 800, out NativeSelectorFrame.Rings rings));
        Assert.Equal(new Int32Rect(0, 47, 103, 66), rings.Outer);
        Assert.Equal(new Int32Rect(2, 49, 99, 62), rings.Keyline);
        Assert.Equal(new Int32Rect(3, 50, 97, 60), rings.Inner);
        Assert.False(NativeSelectorFrame.TryLayout(Int32Rect.Empty, 1000, 800, out _));
    }

    [Fact]
    public void DragSamplesReuseTheCallersListWithoutAllocating()
    {
        // Every pointer sample of a drag computes this damage; it must not feed the GC.
        var damage = new List<Int32Rect>(16) { new(1, 1, 1, 1) };
        var before = new Int32Rect(100, 80, 300, 200);
        NativeSelectorDirtyRegions.Changed(before, new Int32Rect(100, 80, 314, 206), 4208, 1440, damage);
        Assert.Equal(NativeSelectorDirtyRegions.Changed(before, new Int32Rect(100, 80, 314, 206), 4208, 1440), damage);
        NativeSelectorDirtyRegions.Changed(before, before, 4208, 1440, damage);
        Assert.Empty(damage);
        for (int warm = 0; warm < 3; warm++)
            NativeSelectorDirtyRegions.Changed(before, new Int32Rect(90 + warm, 70, 330, 220), 4208, 1440, damage);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 1; step <= 16; step++)
            NativeSelectorDirtyRegions.Changed(new Int32Rect(100, 80, 300 + step * 14, 200 + step * 6),
                new Int32Rect(100, 80, 314 + step * 14, 206 + step * 6), 4208, 1440, damage);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
        Assert.Equal(NativeSelectorDirtyRegions.Changed(new Int32Rect(100, 80, 524, 296), new Int32Rect(100, 80, 538, 302), 4208, 1440), damage);
    }

    [Fact]
    public void ClipHandlesNegativeOriginsAndLargeRightEdges()
    {
        Assert.Equal(new Int32Rect(0, 0, 5, 6), NativeSelectorDirtyRegions.Clip(new(-5, -4, 10, 10), 20, 20));
        Assert.Equal(new Int32Rect(20, 20, 0, 0), NativeSelectorDirtyRegions.Clip(new(int.MaxValue - 2, 30, 10, 10), 20, 20));
    }

    private static bool Contains(Int32Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    // The frame's three pixels on each side: just outside the target, or just inside it on a side
    // whose outside would leave the desktop.
    private static bool InFrame(Int32Rect r, int x, int y, int width, int height)
    {
        if (r.Width == 0 || r.Height == 0) return false;
        int left = r.X >= 3 ? r.X - 3 : r.X, top = r.Y >= 3 ? r.Y - 3 : r.Y;
        int right = r.X + r.Width + 3 <= width ? r.X + r.Width + 3 : r.X + r.Width;
        int bottom = r.Y + r.Height + 3 <= height ? r.Y + r.Height + 3 : r.Y + r.Height;
        bool outer = x >= left && x < right && y >= top && y < bottom;
        bool inner = x >= left + 3 && x < right - 3 && y >= top + 3 && y < bottom - 3;
        return outer && !inner;
    }
}
