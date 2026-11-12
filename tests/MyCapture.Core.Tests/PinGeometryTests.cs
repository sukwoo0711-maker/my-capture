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
    public void InitialZoom_ImageLargerThanWork_ShrinksJustEnoughToFit()
    {
        // A 4000px-wide image on a 1920px-wide work area fits the whole work area width => 1920px.
        // (Until 11-12 the pin shrank to 80% of the work area, 1536px, even when it would fit.)
        Assert.Equal(1.0, PinGeometry.InitialFitFraction);
        double zoom = PinGeometry.InitialZoom(4000, 1000, 1920, 1080);

        double fittedWidth = 4000 * zoom;
        Assert.True(fittedWidth <= 1920 + 0.001);
        // Width is the binding constraint here.
        Assert.Equal(1920.0 / 4000, zoom, 6);
    }

    [Fact]
    public void InitialZoom_ConstrainedByHeight_UsesHeightRatio()
    {
        double zoom = PinGeometry.InitialZoom(1000, 4000, 1920, 1080);
        Assert.Equal(1080.0 / 4000, zoom, 6);
    }

    [Theory]
    [InlineData(1540, 870)]   // 80.2% x 80.6% of the work area
    [InlineData(1800, 1000)]  // 93.8% x 92.6%
    [InlineData(1920, 1080)]  // exactly the work area
    public void InitialZoom_ImageThatFitsTheWorkArea_StaysOneToOne(double width, double height)
    {
        // Pins show what the user was looking at at its own size; these shrank to 80% before.
        Assert.Equal(1.0, PinGeometry.InitialZoom(width, height, 1920, 1080), 6);
        PinGeometry.Placement placement = PinGeometry.CascadedPlacement(width, height, 0, 0, 1920, 1080, 960, 540, 3);
        Assert.Equal((width, height, 1.0), (placement.Width, placement.Height, placement.Zoom));
        Assert.InRange(placement.Left, 0, 1920 - width);
        Assert.InRange(placement.Top, 0, 1080 - height);
    }

    [Fact]
    public void InitialZoom_PreferredZoom_IsKeptUnlessItWouldNotFit()
    {
        Assert.Equal(1.5, PinGeometry.InitialZoom(400, 300, 1920, 1080, preferredZoom: 1.5), 6);
        Assert.Equal(0.8, PinGeometry.InitialZoom(400, 300, 1920, 1080, preferredZoom: 0.8), 6);
        // 1000 x 1.5 = 1500 DIP tall does not fit 1080: shrink to the work area, not to the preferred size.
        Assert.Equal(1080.0 / 1000, PinGeometry.InitialZoom(800, 1000, 1920, 1080, preferredZoom: 1.5), 6);
        Assert.Equal(1.0, PinGeometry.InitialZoom(400, 300, 1920, 1080, preferredZoom: double.NaN), 6);
        Assert.Equal(1.0, PinGeometry.InitialZoom(400, 300, 1920, 1080, preferredZoom: 0), 6);

        PinGeometry.Placement placement = PinGeometry.CascadedPlacement(400, 300, 0, 0, 1920, 1080, 960, 540, 0, 1.5);
        Assert.Equal((600.0, 450.0, 1.5), (placement.Width, placement.Height, placement.Zoom));
    }

    // A 10 x 20 cell range at default widths: 480 pt x 300 pt (640 x 400 DIP at 100% sheet zoom).
    private const double RangeWidthPt = 480, RangeHeightPt = 300;

    [Theory]
    // The picture holds the cells at 100% sheet zoom in the monitor's pixels (what pins assume today).
    [InlineData(80, 1.0, 640, 400, 0.8)]
    [InlineData(100, 1.0, 640, 400, 1.0)]
    [InlineData(150, 1.0, 640, 400, 1.5)]
    [InlineData(80, 1.5, 960, 600, 0.8)]
    [InlineData(100, 1.5, 960, 600, 1.0)]
    [InlineData(150, 1.5, 960, 600, 1.5)]
    // The picture holds the cells at 96 DPI whatever the monitor's scale: the ratio also corrects that.
    [InlineData(100, 1.5, 640, 400, 1.5)]
    [InlineData(80, 1.5, 640, 400, 1.2)]
    // The picture already holds the cells at the window's zoom: it stays at 100%.
    [InlineData(150, 1.0, 960, 600, 1.0)]
    public void SpreadsheetViewZoom_ShowsTheCellsAtTheirOnScreenSize(
        double sheetZoom, double monitorScale, int pictureWidth, int pictureHeight, double expected)
    {
        double? zoom = PinGeometry.SpreadsheetViewZoom(pictureWidth, pictureHeight, monitorScale,
            new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, sheetZoom));
        Assert.NotNull(zoom);
        Assert.Equal(expected, zoom.Value, 6);
        // On screen the pin is as large as the cells were: 640 x 400 DIP times the sheet zoom.
        Assert.Equal(640 * sheetZoom / 100, pictureWidth / monitorScale * zoom.Value, 6);
    }

    [Fact]
    public void SpreadsheetViewZoom_ToleratesBorderPixelsAndSnapsNearOneToExactlyOne()
    {
        // One extra border pixel on each axis of a one-cell picture (64 x 20 DIP).
        Assert.Equal(1.0, PinGeometry.SpreadsheetViewZoom(65, 21, 1.0, new SpreadsheetSelectionView(48, 15, 100)));
        // 2% off on both axes is resampling noise, not a size difference.
        Assert.Equal(1.0, PinGeometry.SpreadsheetViewZoom(653, 408, 1.0, new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 100)));
    }

    [Theory]
    [InlineData(640, 200)]   // the selection grew taller after the copy
    [InlineData(320, 400)]   // the selection lost half of its columns
    [InlineData(400, 640)]   // a different range of the transposed shape
    public void SpreadsheetViewZoom_SelectionOfAnotherShape_IsIgnored(int pictureWidth, int pictureHeight) =>
        Assert.Null(PinGeometry.SpreadsheetViewZoom(pictureWidth, pictureHeight, 1.0,
            new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 100)));

    [Theory]
    [InlineData(double.NaN, 300, 100)]
    [InlineData(480, 0, 100)]
    [InlineData(480, 300, 5)]      // below the sheet's 10% minimum
    [InlineData(480, 300, 500)]    // above the sheet's 400% maximum
    [InlineData(480, 300, double.PositiveInfinity)]
    [InlineData(200_000, 300, 100)]
    public void SpreadsheetViewZoom_ImplausibleView_IsIgnored(double width, double height, double zoom) =>
        Assert.Null(PinGeometry.SpreadsheetViewZoom(640, 400, 1.0, new SpreadsheetSelectionView(width, height, zoom)));

    [Fact]
    public void SpreadsheetViewZoom_RatioOutsideTheSupportedRange_IsIgnored()
    {
        // 400% of a picture already rendered at a quarter of the cells' size would be 16x.
        Assert.Null(PinGeometry.SpreadsheetViewZoom(160, 100, 1.0, new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 400)));
        Assert.Null(PinGeometry.SpreadsheetViewZoom(0, 400, 1.0, new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 100)));
        Assert.Null(PinGeometry.SpreadsheetViewZoom(640, 400, double.NaN, new SpreadsheetSelectionView(RangeWidthPt, RangeHeightPt, 100)));
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

    [Theory]
    [InlineData(.95, 1, .1)]
    [InlineData(1.05, -1, .1)]
    [InlineData(.5, 12, .1)]
    [InlineData(2, -12, .1)]
    [InlineData(.9, 1, .25)]
    [InlineData(1.1, -1, .25)]
    public void StepZoom_StopsExactlyAtOneWhenCrossing(double start, int notches, double step)
    {
        Assert.Equal(1, PinGeometry.StepZoom(start, notches, step));
        Assert.Equal(Math.Pow(1 + step, Math.Sign(notches)), PinGeometry.StepZoom(1, Math.Sign(notches), step));
    }

    [Theory]
    [InlineData(1, 3, .1)]
    [InlineData(1, -3, .1)]
    [InlineData(.5, 2, .25)]
    [InlineData(2, -1, .25)]
    [InlineData(.9, 0, .1)]
    public void StepZoom_LeavesNonCrossingStepsUnchanged(double start, int notches, double step) =>
        Assert.Equal(PinGeometry.ClampZoom(start * Math.Pow(1 + step, notches)), PinGeometry.StepZoom(start, notches, step));

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

    [Theory]
    [InlineData(-2500, -1100)]
    [InlineData(3700, 500)]
    public void PhysicalDesktop_AllowsBothEndsOfMixedDpiMonitorLayout(double x, double y)
    {
        // A 2560px display left/above a 3840px display: use one physical plane,
        // regardless of their different 150%/200% UI scaling factors.
        (double left, double top) = PinGeometry.KeepGrabbable(x, y, 600, 400,
            -2560, -1440, 6400, 3600, 48);
        Assert.Equal(x, left);
        Assert.Equal(y, top);
    }
}
