using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RectDTests
{
    [Fact]
    public void Normalized_TurnsUpwardLeftwardDragIntoPositiveRect()
    {
        // Dragging from bottom-right to top-left produces negative extents.
        var dragged = new RectD(100, 100, -40, -30);

        RectD normalized = dragged.Normalized();

        Assert.Equal(60, normalized.X);
        Assert.Equal(70, normalized.Y);
        Assert.Equal(40, normalized.Width);
        Assert.Equal(30, normalized.Height);
    }

    [Fact]
    public void FromCorners_IsOrderIndependent()
    {
        var a = new PointD(10, 20);
        var b = new PointD(50, 5);

        Assert.Equal(RectD.FromCorners(a, b), RectD.FromCorners(b, a));
    }

    [Fact]
    public void ToPixelBounds_RoundsOutwardSoNoFramedPixelIsLost()
    {
        var subPixel = new RectD(10.4, 20.7, 30.2, 15.1);

        RectD pixels = subPixel.ToPixelBounds();

        Assert.Equal(10, pixels.Left);
        Assert.Equal(20, pixels.Top);
        Assert.Equal(41, pixels.Right);   // ceil(40.6)
        Assert.Equal(36, pixels.Bottom);  // ceil(35.8)
    }

    [Fact]
    public void ClampTo_KeepsSelectionInsideMonitorBounds()
    {
        var monitor = new RectD(0, 0, 1920, 1080);
        var pushedOffEdge = new RectD(1900, 1060, 200, 200);

        RectD clamped = pushedOffEdge.ClampTo(monitor);

        Assert.True(clamped.Right <= monitor.Right);
        Assert.True(clamped.Bottom <= monitor.Bottom);
        Assert.True(clamped.Left >= monitor.Left);
        Assert.True(clamped.Top >= monitor.Top);
    }

    [Fact]
    public void ClampTo_ShrinksSelectionLargerThanBounds()
    {
        var monitor = new RectD(0, 0, 800, 600);
        var oversized = new RectD(-100, -100, 2000, 2000);

        RectD clamped = oversized.ClampTo(monitor);

        Assert.Equal(0, clamped.Left);
        Assert.Equal(0, clamped.Top);
        Assert.Equal(800, clamped.Width);
        Assert.Equal(600, clamped.Height);
    }

    [Fact]
    public void ClampTo_HandlesNegativeOriginMonitorArrangement()
    {
        // A secondary monitor placed left of the primary has negative coordinates.
        var monitor = new RectD(-1920, 0, 1920, 1080);
        var selection = new RectD(-2000, -50, 300, 300);

        RectD clamped = selection.ClampTo(monitor);

        Assert.Equal(-1920, clamped.Left);
        Assert.Equal(0, clamped.Top);
    }
}

public sealed class GeometryMathTests
{
    [Fact]
    public void DistanceToSegment_ProjectsOntoSegmentInterior()
    {
        double d = GeometryMath.DistanceToSegment(
            new PointD(5, 3), new PointD(0, 0), new PointD(10, 0));

        Assert.Equal(3, d, precision: 10);
    }

    [Fact]
    public void DistanceToSegment_ClampsBeyondEndpoints()
    {
        // Perpendicular projection falls outside the segment, so the nearest point is
        // the endpoint itself.
        double d = GeometryMath.DistanceToSegment(
            new PointD(-4, 3), new PointD(0, 0), new PointD(10, 0));

        Assert.Equal(5, d, precision: 10);
    }

    [Fact]
    public void DistanceToSegment_HandlesDegenerateSegment()
    {
        double d = GeometryMath.DistanceToSegment(
            new PointD(3, 4), new PointD(0, 0), new PointD(0, 0));

        Assert.Equal(5, d, precision: 10);
    }

    [Fact]
    public void Simplify_CollapsesCollinearRun()
    {
        List<PointD> straight = [.. Enumerable.Range(0, 50).Select(i => new PointD(i, 0))];

        List<PointD> simplified = GeometryMath.Simplify(straight, tolerance: 0.5);

        Assert.Equal(2, simplified.Count);
        Assert.Equal(straight[0], simplified[0]);
        Assert.Equal(straight[^1], simplified[^1]);
    }

    [Fact]
    public void Simplify_KeepsCornersThatCarryShape()
    {
        List<PointD> lShape =
        [
            new(0, 0), new(10, 0), new(20, 0),
            new(20, 10), new(20, 20),
        ];

        List<PointD> simplified = GeometryMath.Simplify(lShape, tolerance: 0.5);

        Assert.Contains(new PointD(20, 0), simplified);
        Assert.Equal(3, simplified.Count);
    }

    [Fact]
    public void Simplify_NeverDropsEndpoints()
    {
        List<PointD> noisy =
        [
            new(0, 0), new(1, 0.1), new(2, -0.1), new(3, 0.05), new(4, 0),
        ];

        List<PointD> simplified = GeometryMath.Simplify(noisy, tolerance: 5.0);

        Assert.Equal(2, simplified.Count);
        Assert.Equal(new PointD(0, 0), simplified[0]);
        Assert.Equal(new PointD(4, 0), simplified[1]);
    }

    [Fact]
    public void Rotate_ByFullTurnReturnsOriginalPoint()
    {
        var p = new PointD(13, -7);
        PointD rotated = GeometryMath.Rotate(p, new PointD(2, 2), 360);

        Assert.Equal(p.X, rotated.X, precision: 9);
        Assert.Equal(p.Y, rotated.Y, precision: 9);
    }

    [Fact]
    public void BoundsOf_EnclosesAllPoints()
    {
        List<PointD> points = [new(5, 5), new(-3, 12), new(20, -1)];

        RectD bounds = GeometryMath.BoundsOf(points);

        Assert.Equal(-3, bounds.Left);
        Assert.Equal(-1, bounds.Top);
        Assert.Equal(20, bounds.Right);
        Assert.Equal(12, bounds.Bottom);
    }
}

public sealed class ColorRgbaTests
{
    [Theory]
    [InlineData("#FF3366", 255, 0xFF, 0x33, 0x66)]
    [InlineData("#80FF3366", 0x80, 0xFF, 0x33, 0x66)]
    [InlineData("#f00", 255, 0xFF, 0x00, 0x00)]
    [InlineData("#8f00", 0x88, 0xFF, 0x00, 0x00)]
    [InlineData("FF3366", 255, 0xFF, 0x33, 0x66)]
    public void TryParse_AcceptsEveryDocumentedForm(string text, int a, int r, int g, int b)
    {
        Assert.True(ColorRgba.TryParse(text, out ColorRgba color));

        Assert.Equal((byte)a, color.A);
        Assert.Equal((byte)r, color.R);
        Assert.Equal((byte)g, color.G);
        Assert.Equal((byte)b, color.B);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("#12345")]
    [InlineData("not-a-colour")]
    public void TryParse_RejectsMalformedInput(string? text)
    {
        Assert.False(ColorRgba.TryParse(text, out _));
    }

    [Fact]
    public void ToHex_RoundTripsThroughTryParse()
    {
        var original = new ColorRgba(0x7F, 0x12, 0xAB, 0xCD);

        Assert.True(ColorRgba.TryParse(original.ToHex(), out ColorRgba parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Fade_ScalesAlphaAndClamps()
    {
        var opaque = new ColorRgba(200, 1, 2, 3);

        Assert.Equal(100, opaque.Fade(0.5).A);
        Assert.Equal(0, opaque.Fade(0).A);
        Assert.Equal(255, opaque.Fade(10).A);
    }
}
