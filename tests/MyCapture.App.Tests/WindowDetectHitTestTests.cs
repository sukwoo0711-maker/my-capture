using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class WindowDetectHitTestTests
{
    private static readonly WindowCandidate Front = new(new IntPtr(1), new RectD(100, 100, 300, 200), "Front");
    private static readonly WindowCandidate Back = new(new IntPtr(2), new RectD(0, 0, 800, 600), "Back");

    [Fact]
    public void FrontmostCandidateContainingThePointerWins()
    {
        IReadOnlyList<WindowCandidate> zOrder = [Front, Back];
        Assert.Same(Front, WindowDetectHitTest.FindTopmost(zOrder, new PointD(150, 150)));
        Assert.Same(Back, WindowDetectHitTest.FindTopmost(zOrder, new PointD(50, 50)));
        Assert.Null(WindowDetectHitTest.FindTopmost(zOrder, new PointD(900, 50)));
        Assert.Null(WindowDetectHitTest.FindTopmost([], new PointD(150, 150)));
    }

    [Fact]
    public void ContainmentIsHalfOpenSoAdjacentWindowsNeverShareAnEdgePixel()
    {
        var left = new WindowCandidate(new IntPtr(3), new RectD(0, 0, 100, 100));
        var right = new WindowCandidate(new IntPtr(4), new RectD(100, 0, 100, 100));
        IReadOnlyList<WindowCandidate> zOrder = [left, right];
        Assert.Same(left, WindowDetectHitTest.FindTopmost(zOrder, new PointD(99, 50)));
        Assert.Same(right, WindowDetectHitTest.FindTopmost(zOrder, new PointD(100, 50)));
        Assert.Null(WindowDetectHitTest.FindTopmost(zOrder, new PointD(200, 50)));
    }

    [Fact]
    public void ExcludedOwnWindowAndDegenerateRectanglesAreSkipped()
    {
        var overlay = new WindowCandidate(new IntPtr(9), new RectD(0, 0, 1920, 1080), "Overlay");
        var sliver = new WindowCandidate(new IntPtr(5), new RectD(140, 140, 1, 400));
        IReadOnlyList<WindowCandidate> zOrder = [overlay, sliver, Front, Back];
        Assert.Same(Front, WindowDetectHitTest.FindTopmost(zOrder, new PointD(140, 150), excludedHandle: new IntPtr(9)));
        Assert.Same(overlay, WindowDetectHitTest.FindTopmost(zOrder, new PointD(140, 150)));
    }

    [Fact]
    public void RegionIsClippedToTheDesktopAndMappedToBitmapPixels()
    {
        // Negative-origin virtual desktop: a second monitor on the left.
        var desktop = new RectD(-1920, 0, 3840, 1080);
        var straddling = new WindowCandidate(new IntPtr(6), new RectD(-2000, -40, 600, 400));
        Assert.Equal(new RectD(0, 0, 520, 360), WindowDetectHitTest.ToBitmapRegion(straddling, desktop));
        var inside = new WindowCandidate(new IntPtr(7), new RectD(100.4, 200.6, 300.2, 100.2));
        Assert.Equal(new RectD(2020, 200, 301, 101), WindowDetectHitTest.ToBitmapRegion(inside, desktop));
        var offscreen = new WindowCandidate(new IntPtr(8), new RectD(5000, 0, 300, 300));
        Assert.Null(WindowDetectHitTest.ToBitmapRegion(offscreen, desktop));
        var edge = new WindowCandidate(new IntPtr(8), new RectD(1919, 0, 300, 300));
        Assert.Null(WindowDetectHitTest.ToBitmapRegion(edge, desktop));
    }

    [Theory]
    [InlineData(10, 10, 10, 10, true)]
    [InlineData(10, 10, 13, 13, true)]
    [InlineData(10, 10, 14, 10, false)]
    [InlineData(10, 10, 10, 6, false)]
    [InlineData(10, 10, 6.5, 13.5, true)]
    public void ClickThresholdMatchesTheSystemDragDistance(double x0, double y0, double x1, double y1, bool click) =>
        Assert.Equal(click, WindowDetectHitTest.IsClick(new PointD(x0, y0), new PointD(x1, y1), 4, 4));
}
