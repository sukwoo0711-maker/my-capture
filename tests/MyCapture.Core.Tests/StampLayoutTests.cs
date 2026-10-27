using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The reusable stamp's placement math: nine-grid anchors, margins, the width fraction, the
/// no-upscale rule, small canvases, and the way back from a rectangle to a remembered placement.
/// </summary>
public sealed class StampLayoutTests
{
    private const double Margin = StampLayout.DefaultMargin;
    private const double Fraction = StampLayout.DefaultWidthFraction;

    [Fact]
    public void DefaultsPlaceTheLogoBottomRightAtFifteenPercentOfTheWidthWithA16PixelMargin()
    {
        RectD rect = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 1000, 600, 400, 200);

        Assert.Equal(150, rect.Width, 6);
        Assert.Equal(75, rect.Height, 6);
        Assert.Equal(1000 - 16 - 150, rect.X, 6);
        Assert.Equal(600 - 16 - 75, rect.Y, 6);
    }

    [Fact]
    public void ASmallLogoIsNeverUpscaled()
    {
        RectD rect = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 1000, 600, 100, 50);

        Assert.Equal(100, rect.Width, 6);
        Assert.Equal(50, rect.Height, 6);
        Assert.Equal(1000 - 16 - 100, rect.X, 6);
        Assert.Equal(600 - 16 - 50, rect.Y, 6);
    }

    [Fact]
    public void AWiderCanvasGivesAProportionallyWiderLogoButTheSameMargin()
    {
        RectD small = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 1000, 600, 1024, 512);
        RectD large = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 2000, 1200, 1024, 512);

        Assert.Equal(150, small.Width, 6);
        Assert.Equal(300, large.Width, 6);
        Assert.Equal(16, 1000 - small.Right, 6);
        Assert.Equal(16, 2000 - large.Right, 6);
        Assert.Equal(16, 600 - small.Bottom, 6);
        Assert.Equal(16, 1200 - large.Bottom, 6);
    }

    public static TheoryData<StampAnchor, double, double> NineGrid() => new()
    {
        // Canvas 1000x800, logo 100x50 (never upscaled), margins 20 and 30.
        { StampAnchor.TopLeft, 20, 30 },
        { StampAnchor.TopCenter, 450, 30 },
        { StampAnchor.TopRight, 1000 - 20 - 100, 30 },
        { StampAnchor.MiddleLeft, 20, 375 },
        { StampAnchor.Center, 450, 375 },
        { StampAnchor.MiddleRight, 1000 - 20 - 100, 375 },
        { StampAnchor.BottomLeft, 20, 800 - 30 - 50 },
        { StampAnchor.BottomCenter, 450, 800 - 30 - 50 },
        { StampAnchor.BottomRight, 1000 - 20 - 100, 800 - 30 - 50 },
    };

    [Theory]
    [MemberData(nameof(NineGrid))]
    public void EveryAnchorTouchesItsOwnEdgesAndCentresTheOthers(StampAnchor anchor, double x, double y)
    {
        RectD rect = StampLayout.Place(anchor, 20, 30, Fraction, 1000, 800, 100, 50);

        Assert.Equal(100, rect.Width, 6);
        Assert.Equal(50, rect.Height, 6);
        Assert.Equal(x, rect.X, 6);
        Assert.Equal(y, rect.Y, 6);
    }

    [Fact]
    public void ARotatedCanvasIsLaidOutAgainstItsRotatedSize()
    {
        RectD landscape = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 1200, 800, 600, 300);
        RectD portrait = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 800, 1200, 600, 300);

        Assert.Equal(180, landscape.Width, 6);
        Assert.Equal(120, portrait.Width, 6);
        Assert.Equal(1200 - 16, landscape.Right, 6);
        Assert.Equal(800 - 16, landscape.Bottom, 6);
        Assert.Equal(800 - 16, portrait.Right, 6);
        Assert.Equal(1200 - 16, portrait.Bottom, 6);
    }

    [Fact]
    public void ACroppedCanvasKeepsTheStampInsideTheCroppedSize()
    {
        RectD rect = StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 300, 120, 600, 300);

        Assert.Equal(45, rect.Width, 6);
        Assert.Equal(22.5, rect.Height, 6);
        Assert.Equal(300 - 16, rect.Right, 6);
        Assert.Equal(120 - 16, rect.Bottom, 6);
    }

    [Theory]
    [InlineData(40, 30, 1024, 1024)]
    [InlineData(4, 4, 50, 50)]
    [InlineData(2000, 40, 100, 600)]
    [InlineData(30, 500, 10, 10)]
    public void ATinyCanvasStillGetsAStampThatFitsInsideIt(double canvasWidth, double canvasHeight, double sourceWidth, double sourceHeight)
    {
        foreach (StampAnchor anchor in Enum.GetValues<StampAnchor>())
        {
            RectD rect = StampLayout.Place(anchor, Margin, Margin, 1.0, canvasWidth, canvasHeight, sourceWidth, sourceHeight);

            Assert.True(rect.Width >= 1 && rect.Height >= 1, $"{anchor} collapsed");
            Assert.True(rect.Left >= -1e-9 && rect.Top >= -1e-9, $"{anchor} starts outside");
            Assert.True(rect.Right <= canvasWidth + 1e-9 && rect.Bottom <= canvasHeight + 1e-9, $"{anchor} ends outside");
            Assert.Equal(sourceWidth / sourceHeight, rect.Width / rect.Height, 3);
        }
    }

    [Fact]
    public void HugeMarginsAreReducedSoTheStampKeepsRoom()
    {
        RectD rect = StampLayout.Place(StampAnchor.TopLeft, 5000, 5000, Fraction, 400, 200, 100, 50);

        Assert.Equal(100, rect.X, 6);
        Assert.Equal(50, rect.Y, 6);
    }

    [Fact]
    public void NoCanvasOrNoPictureGivesNoRectangle()
    {
        Assert.Equal(RectD.Empty, StampLayout.Place(StampAnchor.Center, Margin, Margin, Fraction, 0, 100, 10, 10));
        Assert.Equal(RectD.Empty, StampLayout.Place(StampAnchor.Center, Margin, Margin, Fraction, 100, 100, 0, 10));
        Assert.Equal(RectD.Empty, StampLayout.Place(StampAnchor.Center, Margin, Margin, Fraction, double.NaN, 100, 10, 10));
    }

    [Fact]
    public void NonFiniteSettingsFallBackToTheDefaults()
    {
        RectD rect = StampLayout.Place(StampAnchor.BottomRight, double.NaN, double.PositiveInfinity, double.NaN, 1000, 600, 400, 200);

        Assert.Equal(StampLayout.Place(StampAnchor.BottomRight, Margin, Margin, Fraction, 1000, 600, 400, 200), rect);
    }

    [Theory]
    [InlineData(StampAnchor.TopLeft)]
    [InlineData(StampAnchor.TopCenter)]
    [InlineData(StampAnchor.TopRight)]
    [InlineData(StampAnchor.MiddleLeft)]
    [InlineData(StampAnchor.Center)]
    [InlineData(StampAnchor.MiddleRight)]
    [InlineData(StampAnchor.BottomLeft)]
    [InlineData(StampAnchor.BottomCenter)]
    [InlineData(StampAnchor.BottomRight)]
    public void DescribingAPlacedStampGivesBackTheSamePlacement(StampAnchor anchor)
    {
        RectD rect = StampLayout.Place(anchor, 24, 40, 0.2, 1600, 900, 2000, 1000);

        (StampAnchor described, double marginX, double marginY, double fraction) = StampLayout.Describe(rect, 1600, 900, 24, 40);

        Assert.Equal(anchor, described);
        Assert.Equal(24, marginX, 6);
        Assert.Equal(40, marginY, 6);
        Assert.Equal(0.2, fraction, 6);
        Assert.True(StampLayout.AreSame(rect, StampLayout.Place(described, marginX, marginY, fraction, 1600, 900, 2000, 1000)));
    }

    [Fact]
    public void AMovedStampIsRememberedByTheNearestCellAndItsDistancesToThatCellsEdges()
    {
        // Dragged to the top-left corner of a 1000x600 canvas, 40 from the left and 10 from the top.
        (StampAnchor anchor, double marginX, double marginY, double fraction) =
            StampLayout.Describe(new RectD(40, 10, 200, 100), 1000, 600);

        Assert.Equal(StampAnchor.TopLeft, anchor);
        Assert.Equal(40, marginX, 6);
        Assert.Equal(10, marginY, 6);
        Assert.Equal(0.2, fraction, 6);
    }

    [Fact]
    public void AStampDraggedPartlyOffTheCanvasIsRememberedAsTouchingTheEdge()
    {
        (StampAnchor anchor, double marginX, double marginY, _) =
            StampLayout.Describe(new RectD(-30, 700, 200, 100), 1000, 800);

        Assert.Equal(StampAnchor.BottomLeft, anchor);
        Assert.Equal(0, marginX, 6);
        Assert.Equal(0, marginY, 6);
    }

    [Fact]
    public void ACentredAxisKeepsThePreviousMarginBecauseThePositionCarriesNone()
    {
        (StampAnchor anchor, double marginX, double marginY, _) =
            StampLayout.Describe(new RectD(400, 680, 200, 100), 1000, 800, previousMarginX: 33, previousMarginY: 44);

        Assert.Equal(StampAnchor.BottomCenter, anchor);
        Assert.Equal(33, marginX, 6);
        Assert.Equal(20, marginY, 6);
    }

    [Fact]
    public void TheWidthFractionIsClampedToItsRange()
    {
        Assert.Equal(StampLayout.MaxWidthFraction, StampLayout.Describe(new RectD(0, 0, 5000, 100), 1000, 800).WidthFraction, 6);
        Assert.Equal(StampLayout.MinWidthFraction, StampLayout.Describe(new RectD(0, 0, 2, 2), 1000, 800).WidthFraction, 6);
    }

    [Fact]
    public void ADegenerateRectangleOrCanvasFallsBackToTheDefaultPlacement()
    {
        (StampAnchor anchor, double marginX, double marginY, double fraction) = StampLayout.Describe(RectD.Empty, 1000, 800, 7, 9);

        Assert.Equal(StampAnchor.BottomRight, anchor);
        Assert.Equal(7, marginX);
        Assert.Equal(9, marginY);
        Assert.Equal(StampLayout.DefaultWidthFraction, fraction);
    }

    [Fact]
    public void RectanglesCountAsTheSameOnlyWithinHalfAPixel()
    {
        var rect = new RectD(10, 20, 100, 50);

        Assert.True(StampLayout.AreSame(rect, new RectD(10.4, 19.6, 100.4, 50.4)));
        Assert.False(StampLayout.AreSame(rect, new RectD(10.6, 20, 100, 50)));
        Assert.False(StampLayout.AreSame(rect, new RectD(10, 20, 100, 51)));
    }
}
