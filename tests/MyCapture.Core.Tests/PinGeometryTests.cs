using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The pure geometry and state that drive a pinned window, exercised with no WPF.
/// </summary>
public sealed class PinGeometryTests
{
    [Fact]
    public void InitialZoom_ImageSmallerThanWork_StaysOneToOne()
    {
        double zoom = PinGeometry.InitialZoom(400, 300, 1920, 1080);
        Assert.Equal(1.0, zoom, 6);
    }

    [Fact]
    public void InitialZoom_ImageLargerThanWork_ShrinksToEightyPercentFit()
    {
        // A 4000px-wide image on a 1920px-wide work area must fit within 80% => 1536px.
        double zoom = PinGeometry.InitialZoom(4000, 1000, 1920, 1080);

        double fittedWidth = 4000 * zoom;
        Assert.True(fittedWidth <= 1920 * PinGeometry.InitialFitFraction + 0.001);
        // Width is the binding constraint here.
        Assert.Equal((1920 * PinGeometry.InitialFitFraction) / 4000, zoom, 6);
    }

    [Fact]
    public void InitialZoom_ConstrainedByHeight_UsesHeightRatio()
    {
        double zoom = PinGeometry.InitialZoom(1000, 4000, 1920, 1080);
        Assert.Equal((1080 * PinGeometry.InitialFitFraction) / 4000, zoom, 6);
    }

    [Fact]
    public void InitialZoom_DegenerateImage_ReturnsOne()
    {
        Assert.Equal(1.0, PinGeometry.InitialZoom(0, 0, 1920, 1080), 6);
    }

    [Fact]
    public void InitialPlacement_CentersNearCursorAndStaysInsideWork()
    {
        PinGeometry.Placement p = PinGeometry.InitialPlacement(
            imageWidthDip: 400,
            imageHeightDip: 300,
            workLeftDip: 0,
            workTopDip: 0,
            workWidthDip: 1920,
            workHeightDip: 1080,
            cursorXDip: 960,
            cursorYDip: 540);

        // Centered on the cursor.
        Assert.Equal(960 - 200, p.Left, 3);
        Assert.Equal(540 - 150, p.Top, 3);
        Assert.Equal(400, p.Width, 3);
        Assert.Equal(300, p.Height, 3);
    }

    [Fact]
    public void InitialPlacement_CursorNearEdge_NudgesFullyInsideWork()
    {
        PinGeometry.Placement p = PinGeometry.InitialPlacement(
            imageWidthDip: 400,
            imageHeightDip: 300,
            workLeftDip: 0,
            workTopDip: 0,
            workWidthDip: 1920,
            workHeightDip: 1080,
            cursorXDip: 1910,
            cursorYDip: 1070);

        Assert.True(p.Left + p.Width <= 1920 + 0.001);
        Assert.True(p.Top + p.Height <= 1080 + 0.001);
        Assert.True(p.Left >= 0);
        Assert.True(p.Top >= 0);
    }

    [Fact]
    public void InitialPlacement_WorkAreaWithOffset_ClampsToThatMonitor()
    {
        // Secondary monitor to the right: work origin at x=1920.
        PinGeometry.Placement p = PinGeometry.InitialPlacement(
            imageWidthDip: 400,
            imageHeightDip: 300,
            workLeftDip: 1920,
            workTopDip: 0,
            workWidthDip: 1280,
            workHeightDip: 1024,
            cursorXDip: 1925,
            cursorYDip: 5);

        Assert.True(p.Left >= 1920);
        Assert.True(p.Top >= 0);
        Assert.True(p.Left + p.Width <= 1920 + 1280 + 0.001);
    }

    [Fact]
    public void ClampZoom_BoundsBothEnds()
    {
        Assert.Equal(PinGeometry.MinZoom, PinGeometry.ClampZoom(0.001), 6);
        Assert.Equal(PinGeometry.MaxZoom, PinGeometry.ClampZoom(100), 6);
        Assert.Equal(1.5, PinGeometry.ClampZoom(1.5), 6);
    }

    [Fact]
    public void ClampOpacity_NeverFullyInvisible()
    {
        Assert.Equal(PinGeometry.MinOpacity, PinGeometry.ClampOpacity(0.0), 6);
        Assert.Equal(PinGeometry.MaxOpacity, PinGeometry.ClampOpacity(5.0), 6);
        Assert.Equal(0.6, PinGeometry.ClampOpacity(0.6), 6);
    }

    [Fact]
    public void StepZoom_TenPercentPerNotch_Compounds()
    {
        double up = PinGeometry.StepZoom(1.0, 1, 0.1);
        Assert.Equal(1.1, up, 6);

        double down = PinGeometry.StepZoom(1.0, -1, 0.1);
        Assert.Equal(1.0 / 1.1, down, 6);

        double twoUp = PinGeometry.StepZoom(1.0, 2, 0.1);
        Assert.Equal(1.1 * 1.1, twoUp, 6);
    }

    [Fact]
    public void StepZoom_ClampsAtBounds()
    {
        Assert.Equal(PinGeometry.MaxZoom, PinGeometry.StepZoom(PinGeometry.MaxZoom, 5, 0.1), 6);
        Assert.Equal(PinGeometry.MinZoom, PinGeometry.StepZoom(PinGeometry.MinZoom, -5, 0.1), 6);
    }

    [Fact]
    public void AnchorTopLeftForZoom_KeepsPointerPixelFixed()
    {
        // Window at (100,100), zoom 1.0. Pointer at (200,150) is 100px right, 50px down of
        // the top-left, i.e. image pixel (100,50). After zooming to 2.0 that pixel must still
        // be under the pointer, so the top-left moves to (200-200, 150-100) = (0,50).
        (double left, double top) = PinGeometry.AnchorTopLeftForZoom(
            left: 100, top: 100, oldZoom: 1.0, newZoom: 2.0, pointerXDip: 200, pointerYDip: 150);

        Assert.Equal(0, left, 3);
        Assert.Equal(50, top, 3);
    }

    [Fact]
    public void AnchorTopLeftForZoom_PointerAtTopLeft_DoesNotMove()
    {
        (double left, double top) = PinGeometry.AnchorTopLeftForZoom(
            left: 300, top: 200, oldZoom: 1.0, newZoom: 3.0, pointerXDip: 300, pointerYDip: 200);

        Assert.Equal(300, left, 3);
        Assert.Equal(200, top, 3);
    }

    [Fact]
    public void KeepGrabbable_OffScreenRight_LeavesMarginOnScreen()
    {
        (double left, double top) = PinGeometry.KeepGrabbable(
            left: 5000, top: 100, width: 400, height: 300,
            desktopLeftDip: 0, desktopTopDip: 0, desktopWidthDip: 1920, desktopHeightDip: 1080,
            grabMarginDip: 24);

        // At least 24px must remain to the left of the right desktop edge.
        Assert.True(left <= 1920 - 24 + 0.001);
        Assert.True(left + 400 >= 24);
    }

    [Fact]
    public void KeepGrabbable_NeverPushesTitleAboveTop()
    {
        (double left, double top) = PinGeometry.KeepGrabbable(
            left: 100, top: -500, width: 400, height: 300,
            desktopLeftDip: 0, desktopTopDip: 0, desktopWidthDip: 1920, desktopHeightDip: 1080,
            grabMarginDip: 24);

        Assert.True(top >= 0);
        Assert.Equal(100, left, 3);
    }
}
