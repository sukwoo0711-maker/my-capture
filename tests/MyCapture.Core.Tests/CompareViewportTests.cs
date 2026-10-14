using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CompareViewportTests
{
    [Fact]
    public void DifferentAspectRatios_ShareTheLargerCanvasAndScale()
    {
        var canvas = CompareViewport.CanvasSize(1600, 600, 800, 1200);
        Assert.Equal((1600.0, 1200.0), canvas);
        double scale = CompareViewport.FitScale(canvas.Width, canvas.Height, 1600, 900, overlay: false);
        Assert.Equal(0.5, scale);
        Assert.Equal(800, 1600 * scale);
        Assert.Equal(400, 800 * scale);
        Assert.Equal(0.75, CompareViewport.FitScale(canvas.Width, canvas.Height, 1600, 900, overlay: true));
    }

    [Fact]
    public void Fit_DoesNotUpscaleSmallImages()
    {
        Assert.Equal(1, CompareViewport.FitScale(100, 50, 1000, 600, overlay: false));
    }

    [Fact]
    public void Fit_ZeroAndInvalidDimensionsStillProduceFinitePositiveScale()
    {
        double scale = CompareViewport.FitScale(0, double.NaN, 0, double.PositiveInfinity, overlay: false);
        Assert.True(double.IsFinite(scale));
        Assert.True(scale > 0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0.5)]
    [InlineData(1000)]
    [InlineData(0.00001)]
    public void Zoom_KeepsTheImagePixelUnderThePointerEvenAtLimits(double factor)
    {
        var before = new CompareViewport(0.5, -45, 12);
        CompareViewport after = before.ZoomAt(factor, 230, 170, 0.1, 8);
        Assert.InRange(after.Scale, 0.1, 8);
        Assert.Equal((230 - before.X) / before.Scale, (230 - after.X) / after.Scale, 8);
        Assert.Equal((170 - before.Y) / before.Scale, (170 - after.Y) / after.Scale, 8);
    }

    [Fact]
    public void ZoomAndPan_AreReversibleAndIgnoreInvalidInput()
    {
        var initial = new CompareViewport(1, 0, 0);
        Assert.Equal(initial, initial.Pan(40, -70).Pan(-40, 70));
        CompareViewport zoomed = initial.ZoomAt(2, 50, 90, 0.1, 8).ZoomAt(0.5, 50, 90, 0.1, 8);
        Assert.Equal(initial, zoomed);
        Assert.Equal(initial, initial.ZoomAt(double.NaN, 0, 0, 0.1, 8));
        Assert.Equal(initial, initial.Pan(double.PositiveInfinity, 0));
    }

    [Theory]
    [InlineData(-10, 800, 0)]
    [InlineData(200, 800, 0.25)]
    [InlineData(800, 800, 1)]
    [InlineData(900, 800, 1)]
    [InlineData(10, 0, 0.5)]
    [InlineData(double.NaN, 800, 0.5)]
    public void Divider_ClampsAtCanvasEdges(double pointer, double width, double expected)
    {
        Assert.Equal(expected, CompareViewport.DividerFraction(pointer, width));
    }
}
