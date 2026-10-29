using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinResizeTests
{
    [Theory]
    [InlineData(0, 0, PinResizeEdge.TopLeft)]
    [InlineData(200, 0, PinResizeEdge.TopRight)]
    [InlineData(0, 100, PinResizeEdge.BottomLeft)]
    [InlineData(200, 100, PinResizeEdge.BottomRight)]
    [InlineData(8, 50, PinResizeEdge.Left)]
    [InlineData(192, 50, PinResizeEdge.Right)]
    [InlineData(100, 8, PinResizeEdge.Top)]
    [InlineData(100, 92, PinResizeEdge.Bottom)]
    [InlineData(8.01, 50, PinResizeEdge.None)]
    [InlineData(100, 50, PinResizeEdge.None)]
    [InlineData(-1, 50, PinResizeEdge.None)]
    [InlineData(100, 101, PinResizeEdge.None)]
    public void HitZonesFollowImageEdges(double x, double y, PinResizeEdge expected) =>
        Assert.Equal(expected, PinResize.HitTest(x, y, 200, 100));

    [Theory]
    [InlineData(20, 10)]
    [InlineData(1, 1)]
    [InlineData(20, 100)]
    [InlineData(100, 10)]
    public void TwentyPercentCapLeavesSmallPinsDraggable(double width, double height)
    {
        double x = Math.Min(8, width * 0.2), y = Math.Min(8, height * 0.2);
        Assert.Equal(PinResizeEdge.Left, PinResize.HitTest(x, height / 2, width, height));
        Assert.Equal(PinResizeEdge.Top, PinResize.HitTest(width / 2, y, width, height));
        Assert.Equal(PinResizeEdge.None, PinResize.HitTest(x + 0.01, y + 0.01, width, height));
        Assert.Equal(PinResizeEdge.None, PinResize.HitTest(width / 2, height / 2, width, height));
    }

    [Theory]
    [InlineData(PinResizeEdge.TopLeft, -200, -100, -100, 100)]
    [InlineData(PinResizeEdge.TopRight, 200, -100, 100, 100)]
    [InlineData(PinResizeEdge.BottomLeft, -200, 100, -100, 200)]
    [InlineData(PinResizeEdge.BottomRight, 200, 100, 100, 200)]
    [InlineData(PinResizeEdge.Left, -200, 17, -100, 150)]
    [InlineData(PinResizeEdge.Right, 200, 17, 100, 150)]
    [InlineData(PinResizeEdge.Top, 17, -100, 0, 100)]
    [InlineData(PinResizeEdge.Bottom, 17, 100, 0, 200)]
    public void AllHandlesScaleProportionallyAboutOppositeAnchor(PinResizeEdge edge,
        double dx, double dy, double left, double top)
    {
        PinGeometry.Placement result = PinResize.Calculate(new(100, 200, 200, 100), 1, edge, dx, dy);
        Assert.Equal(new PinGeometry.Placement(left, top, 400, 200, 2), result);
    }

    [Theory]
    [InlineData(PinResizeEdge.TopLeft, -1, -1)]
    [InlineData(PinResizeEdge.TopRight, 1, -1)]
    [InlineData(PinResizeEdge.BottomLeft, -1, 1)]
    [InlineData(PinResizeEdge.BottomRight, 1, 1)]
    [InlineData(PinResizeEdge.Left, -1, 0)]
    [InlineData(PinResizeEdge.Right, 1, 0)]
    [InlineData(PinResizeEdge.Top, 0, -1)]
    [InlineData(PinResizeEdge.Bottom, 0, 1)]
    public void EveryHandleClampsWithoutLosingItsAnchor(PinResizeEdge edge, int x, int y)
    {
        var start = new RectD(-400, -200, 200, 100);
        foreach (double delta in new[] { -10000d, 10000d })
        {
            PinGeometry.Placement result = PinResize.Calculate(start, 1, edge, x * delta, y * delta);
            Assert.Equal(delta < 0 ? PinGeometry.MinZoom : PinGeometry.MaxZoom, result.Zoom);
            Assert.Equal(2, result.Width / result.Height, 8);
            double ax = x < 0 ? 1 : x > 0 ? 0 : 0.5;
            double ay = y < 0 ? 1 : y > 0 ? 0 : 0.5;
            Assert.Equal(start.Left + start.Width * ax, result.Left + result.Width * ax, 8);
            Assert.Equal(start.Top + start.Height * ay, result.Top + result.Height * ay, 8);
        }
    }

    [Fact]
    public void CornerProjectionRespondsToEitherAxisWithoutChangingAspectRatio()
    {
        var start = new RectD(0, 0, 200, 100);
        Assert.Equal(1.8, PinResize.Calculate(start, 1, PinResizeEdge.BottomRight, 200, 0).Zoom, 8);
        Assert.Equal(1.2, PinResize.Calculate(start, 1, PinResizeEdge.BottomRight, 0, 100).Zoom, 8);
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MonitorDpiChangeKeepsPhysicalAnchorAndPointerExtent(double dpiRatio)
    {
        var start = new RectD(-400, -200, 200, 100);
        PinGeometry.Placement same = PinResize.Calculate(start, 1, PinResizeEdge.TopLeft, 0, 0, dpiRatio, dpiRatio);
        Assert.Equal(start.Left, same.Left, 8);
        Assert.Equal(start.Top, same.Top, 8);
        Assert.Equal(1 / dpiRatio, same.Zoom, 8);
        PinGeometry.Placement grown = PinResize.Calculate(start, 1, PinResizeEdge.TopLeft, -200, -100, dpiRatio, dpiRatio);
        Assert.Equal(2 / dpiRatio, grown.Zoom, 8);
        Assert.Equal(start.Right, grown.Left + grown.Width, 8);
        Assert.Equal(start.Bottom, grown.Top + grown.Height, 8);
        Assert.Equal(400, grown.Width, 8);
        Assert.Equal(200, grown.Height, 8);
    }

    [Theory]
    [InlineData(PinResizeEdge.Left, 0, 0)]
    [InlineData(PinResizeEdge.TopLeft, 0, 0)]
    [InlineData(PinResizeEdge.Top, 0, -10)]
    public void TinyAxisAtMinimumZoomUsesActualImageSizeAndWindowFloor(PinResizeEdge edge, double dx, double dy)
    {
        var start = new RectD(100, 200, 1, 10);
        PinGeometry.Placement result = PinResize.Calculate(start, 0.1, edge, dx, dy, imageWidth: 1, imageHeight: 100);
        Assert.Equal(dy == 0 ? 0.1 : 0.2, result.Zoom, 8);
        Assert.Equal(1, result.Width);
        Assert.Equal(100, result.Left);
        Assert.Equal(210, result.Top + result.Height, 8);
    }
}
