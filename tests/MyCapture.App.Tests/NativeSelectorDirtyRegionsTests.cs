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
    public void DamageCoversEveryChangedPixelAndBorderWithinTheDesktop()
    {
        const int width = 32, height = 24;
        Int32Rect[] regions = [Int32Rect.Empty, new(0, 0, width, height), new(0, 0, 1, 1),
            new(31, 23, 1, 1), new(2, 3, 13, 11), new(3, 3, 13, 11), new(20, 15, 10, 8),
            new(5, 5, 4, 4), new(0, 8, 32, 1)];
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
                if (before != after && (Contains(before, x, y) != Contains(after, x, y)
                    || NearBorder(before, x, y) || NearBorder(after, x, y)))
                    Assert.True(damage.Any(r => Contains(r, x, y)), $"Missing ({x}, {y}): {before} -> {after}");
        }
    }

    [Fact]
    public void ClipHandlesNegativeOriginsAndLargeRightEdges()
    {
        Assert.Equal(new Int32Rect(0, 0, 5, 6), NativeSelectorDirtyRegions.Clip(new(-5, -4, 10, 10), 20, 20));
        Assert.Equal(new Int32Rect(20, 20, 0, 0), NativeSelectorDirtyRegions.Clip(new(int.MaxValue - 2, 30, 10, 10), 20, 20));
    }

    private static bool Contains(Int32Rect r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.Width && y < r.Y + r.Height;

    private static bool NearBorder(Int32Rect r, int x, int y) => r.Width > 0 && r.Height > 0
        && x >= r.X - 2 && x < r.X + r.Width + 2 && y >= r.Y - 2 && y < r.Y + r.Height + 2
        && (x < r.X + 2 || x >= r.X + r.Width - 2 || y < r.Y + 2 || y >= r.Y + r.Height - 2);
}
