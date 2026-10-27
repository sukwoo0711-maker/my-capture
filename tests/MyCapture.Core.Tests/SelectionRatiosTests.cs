using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The region selector's aspect-ratio lock and fixed sizes: the cycle, drag fitting, refitting,
/// arrow-key resizing, centring on a monitor, the ratio rounding that keeps a size labelled "16:9",
/// and even sides for recording.
/// </summary>
public sealed class SelectionRatiosTests
{
    private static readonly RectD Frame = new(0, 0, 1920, 1080);

    [Fact]
    public void CycleVisitsFreeThenTheFourRatiosAndComesBackToFree()
    {
        var seen = new List<SelectionRatio> { SelectionRatio.Free };
        for (SelectionRatio next = SelectionRatios.Next(SelectionRatio.Free); next != SelectionRatio.Free; next = SelectionRatios.Next(next))
            seen.Add(next);

        Assert.Equal(
            [SelectionRatio.Free, SelectionRatio.Ratio16x9, SelectionRatio.Ratio4x3, SelectionRatio.Ratio1x1, SelectionRatio.Ratio9x16],
            seen);
        Assert.Equal(seen, SelectionRatios.All);
        Assert.Equal(new[] { "", "16:9", "4:3", "1:1", "9:16" }, SelectionRatios.All.Select(SelectionRatios.Label));
        Assert.Null(SelectionRatios.Terms(SelectionRatio.Free));
        Assert.Equal((9, 16), SelectionRatios.Terms(SelectionRatio.Ratio9x16)!.Value);
        Assert.Equal(["1280 × 720", "1920 × 1080", "1080 × 1080"], SelectionRatios.FixedSizes.Select(size => size.ToString()));
    }

    [Fact]
    public void FittingAllocatesNothingBecauseItRunsOnEveryPointerMove()
    {
        var anchor = new PointD(100, 100);
        RectD last = default;
        for (int warm = 0; warm < 500; warm++) last = Round(anchor, warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++) last = Round(anchor, i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(last.Width >= 1);

        static RectD Round(PointD anchor, int i)
        {
            RectD fitted = SelectionRatios.FitDrag(anchor, new PointD(300 + (i % 700), 200 + (i % 300)), Frame, SelectionRatio.Ratio16x9);
            fitted = SelectionRatios.Refit(fitted, Frame, SelectionRatio.Ratio4x3, evenSize: true);
            fitted = SelectionRatios.Resize(fitted, horizontal: true, 1, 1, Frame, SelectionRatio.Ratio9x16);
            _ = SelectionRatios.PlaceFixed(new FixedSelectionSize(1280, 720), Frame, Frame);
            return fitted;
        }
    }

    [Fact]
    public void FreeDragIsThePlainClampedRectangle()
    {
        var anchor = new PointD(100, 100);
        var pointer = new PointD(40, 300);
        Assert.Equal(RectD.FromCorners(anchor, pointer).ClampTo(Frame), SelectionRatios.FitDrag(anchor, pointer, Frame, SelectionRatio.Free));
        Assert.Equal(new RectD(40, 100, 60, 200), SelectionRatios.FitDrag(anchor, pointer, Frame, SelectionRatio.Free));
    }

    [Theory]
    // The longer side of the drag leads and the other follows the ratio.
    [InlineData(SelectionRatio.Ratio16x9, 100, 100, 500, 250, 100, 100, 400, 225)]
    [InlineData(SelectionRatio.Ratio16x9, 100, 100, 200, 400, 100, 100, 533, 300)]
    [InlineData(SelectionRatio.Ratio4x3, 0, 0, 1000, 500, 0, 0, 1000, 750)]
    [InlineData(SelectionRatio.Ratio1x1, 100, 100, 180, 400, 100, 100, 300, 300)]
    [InlineData(SelectionRatio.Ratio9x16, 100, 100, 400, 200, 100, 100, 300, 533)]
    // Dragging up and to the left keeps the anchor on the bottom-right corner.
    [InlineData(SelectionRatio.Ratio16x9, 500, 500, 100, 400, 100, 275, 400, 225)]
    // The anchor is whole pixels; the rest follows from there.
    [InlineData(SelectionRatio.Ratio16x9, 100.4, 100.6, 500, 250, 100, 101, 400, 225)]
    // Half a pixel rounds up: 1000 x 9/16 is 562.5.
    [InlineData(SelectionRatio.Ratio16x9, 100, 100, 1100, 300, 100, 100, 1000, 563)]
    [InlineData(SelectionRatio.Ratio16x9, 100, 100, 1101, 300, 100, 100, 1001, 563)]
    public void LockedDragAnchorsAtTheStartAndSnapsTheFreeCorner(SelectionRatio ratio, double ax, double ay, double px, double py,
        double left, double top, double width, double height)
    {
        RectD fitted = SelectionRatios.FitDrag(new PointD(ax, ay), new PointD(px, py), Frame, ratio);
        Assert.Equal(new RectD(left, top, width, height), fitted);
        Assert.True(SelectionRatios.Matches((int)fitted.Width, (int)fitted.Height, ratio));
    }

    [Fact]
    public void DragThatHasNotLeftTheAnchorIsEmpty()
    {
        Assert.Equal(new RectD(300, 300, 0, 0),
            SelectionRatios.FitDrag(new PointD(300, 300), new PointD(300, 300), Frame, SelectionRatio.Ratio16x9));
    }

    [Fact]
    public void DragAlongOneAxisStillDrawsARegionTowardsTheSideWithRoom()
    {
        // No sideways movement at all: the width comes from the height, towards the side with more room.
        RectD fitted = SelectionRatios.FitDrag(new PointD(1900, 500), new PointD(1900, 700), Frame, SelectionRatio.Ratio16x9);
        Assert.Equal(new RectD(1544, 500, 356, 200), fitted);
    }

    [Fact]
    public void LockedDragIsClampedToTheFrameWithoutLeavingTheRatio()
    {
        // 420 x 180 is left to the corner: the height limits the width, 16:9 is kept.
        Assert.Equal(new RectD(1500, 900, 320, 180),
            SelectionRatios.FitDrag(new PointD(1500, 900), new PointD(1920, 1080), Frame, SelectionRatio.Ratio16x9));
        // A pointer far outside the frame (precision pointer, a stray event) never leaves it.
        Assert.Equal(new RectD(1900, 500, 20, 11),
            SelectionRatios.FitDrag(new PointD(1900, 500), new PointD(2500, 520), Frame, SelectionRatio.Ratio16x9));
        foreach (SelectionRatio ratio in SelectionRatios.All.Skip(1))
        {
            foreach (PointD pointer in new[] { new PointD(-300, -300), new PointD(5000, 5000), new PointD(5000, -10), new PointD(-10, 5000) })
            {
                RectD fitted = SelectionRatios.FitDrag(new PointD(960, 540), pointer, Frame, ratio);
                Assert.True(fitted.Left >= 0 && fitted.Top >= 0 && fitted.Right <= 1920 && fitted.Bottom <= 1080, $"{ratio} {pointer}: {fitted}");
                Assert.True(SelectionRatios.Matches((int)fitted.Width, (int)fitted.Height, ratio), $"{ratio} {pointer}: {fitted}");
            }
        }
    }

    [Fact]
    public void LockedDragHonoursAFrameThatDoesNotStartAtTheOrigin()
    {
        var frame = new RectD(100, 50, 400, 300);
        RectD fitted = SelectionRatios.FitDrag(new PointD(450, 300), new PointD(0, 0), frame, SelectionRatio.Ratio4x3);
        Assert.True(fitted.Left >= 100 && fitted.Top >= 50 && fitted.Right <= 500 && fitted.Bottom <= 350, fitted.ToString());
        Assert.Equal(450, fitted.Right);
        Assert.Equal(300, fitted.Bottom);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void ADragInDipsFitsTheSameAtOneAndOnePointFiveScale(double scale)
    {
        // The same gesture on a 100% and a 150% display: 600 x 300 DIPs of pointer travel.
        PointD anchor = new(Math.Round(200 * scale), Math.Round(100 * scale));
        PointD pointer = new(Math.Round(800 * scale), Math.Round(400 * scale));
        var frame = new RectD(0, 0, Math.Round(1920 * scale), Math.Round(1080 * scale));
        RectD fitted = SelectionRatios.FitDrag(anchor, pointer, frame, SelectionRatio.Ratio16x9);

        Assert.Equal(anchor.X, fitted.Left);
        Assert.Equal(anchor.Y, fitted.Top);
        Assert.Equal(Math.Round(600 * scale), fitted.Width);
        Assert.True(SelectionRatios.Matches((int)fitted.Width, (int)fitted.Height, SelectionRatio.Ratio16x9));
        Assert.Equal(scale == 1.0 ? new RectD(200, 100, 600, 338) : new RectD(300, 150, 900, 506), fitted);
    }

    [Theory]
    [InlineData(SelectionRatio.Ratio16x9)]
    [InlineData(SelectionRatio.Ratio4x3)]
    [InlineData(SelectionRatio.Ratio1x1)]
    [InlineData(SelectionRatio.Ratio9x16)]
    public void EvenDragKeepsEverySideEvenAndTheRatioAsCloseAsThatAllows(SelectionRatio ratio)
    {
        foreach (int pointerX in new[] { 999, 1000, 1001, 1002, 1003 })
        {
            RectD fitted = SelectionRatios.FitDrag(new PointD(0, 0), new PointD(pointerX, 400), Frame, ratio, evenSize: true);
            Assert.Equal(0, fitted.Width % 2);
            Assert.Equal(0, fitted.Height % 2);
            Assert.True(SelectionRatios.Matches((int)fitted.Width, (int)fitted.Height, ratio), fitted.ToString());
        }
    }

    [Fact]
    public void EvenDragRoundsTheSideOnPairsOfPixels()
    {
        // 1001 is 500.5 pairs: it rounds up to 501 pairs, and the height is 501 x 9/16 = 281.8 pairs.
        Assert.Equal(new RectD(0, 0, 1002, 564),
            SelectionRatios.FitDrag(new PointD(0, 0), new PointD(1001, 400), Frame, SelectionRatio.Ratio16x9, evenSize: true));
        // Without the even rule the same drag is 1001 x 563.
        Assert.Equal(new RectD(0, 0, 1001, 563),
            SelectionRatios.FitDrag(new PointD(0, 0), new PointD(1001, 400), Frame, SelectionRatio.Ratio16x9));
    }

    [Fact]
    public void EvenDragNearAnOddFrameEdgeStaysInsideAndEven()
    {
        var frame = new RectD(0, 0, 1921, 1081);
        RectD fitted = SelectionRatios.FitDrag(new PointD(1000, 500), new PointD(1921, 1081), frame, SelectionRatio.Ratio16x9, evenSize: true);
        Assert.Equal(0, fitted.Width % 2);
        Assert.Equal(0, fitted.Height % 2);
        Assert.True(fitted.Right <= 1921 && fitted.Bottom <= 1081);
    }

    [Theory]
    [InlineData(1920, 1080, SelectionRatio.Ratio16x9, true)]
    [InlineData(1000, 563, SelectionRatio.Ratio16x9, true)]
    [InlineData(1000, 562, SelectionRatio.Ratio16x9, true)]
    [InlineData(1000, 560, SelectionRatio.Ratio16x9, false)]
    [InlineData(1080, 1080, SelectionRatio.Ratio1x1, true)]
    [InlineData(1080, 1079, SelectionRatio.Ratio1x1, true)]
    [InlineData(1080, 1078, SelectionRatio.Ratio1x1, false)]
    [InlineData(1440, 1080, SelectionRatio.Ratio4x3, true)]
    [InlineData(1080, 1920, SelectionRatio.Ratio9x16, true)]
    [InlineData(1920, 1080, SelectionRatio.Ratio9x16, false)]
    [InlineData(1920, 1080, SelectionRatio.Free, false)]
    [InlineData(0, 0, SelectionRatio.Ratio16x9, false)]
    public void MatchesTellsASelectionThatCarriesTheLockFromOneThatDoesNot(int width, int height, SelectionRatio ratio, bool expected) =>
        Assert.Equal(expected, SelectionRatios.Matches(width, height, ratio));

    [Fact]
    public void RefitShrinksAroundTheCentreAndNeverGrows()
    {
        var selection = new RectD(100, 50, 800, 600);
        Assert.Equal(new RectD(100, 125, 800, 450), SelectionRatios.Refit(selection, Frame, SelectionRatio.Ratio16x9));
        Assert.Equal(new RectD(100, 50, 800, 600), SelectionRatios.Refit(selection, Frame, SelectionRatio.Ratio4x3));
        Assert.Equal(new RectD(200, 50, 600, 600), SelectionRatios.Refit(selection, Frame, SelectionRatio.Ratio1x1));
        Assert.Equal(new RectD(331, 50, 337, 599), SelectionRatios.Refit(selection, Frame, SelectionRatio.Ratio9x16));
        Assert.Equal(selection, SelectionRatios.Refit(selection, Frame, SelectionRatio.Free));

        foreach (SelectionRatio ratio in SelectionRatios.All.Skip(1))
        {
            RectD refit = SelectionRatios.Refit(selection, Frame, ratio);
            Assert.True(refit.Left >= selection.Left && refit.Top >= selection.Top
                && refit.Right <= selection.Right && refit.Bottom <= selection.Bottom, $"{ratio}: {refit}");
            Assert.True(SelectionRatios.Matches((int)refit.Width, (int)refit.Height, ratio));
        }
    }

    [Fact]
    public void RefitOfAnEvenSelectionStaysInsideTheFrameAndEven()
    {
        RectD refit = SelectionRatios.Refit(new RectD(101, 51, 801, 601), Frame, SelectionRatio.Ratio16x9, evenSize: true);
        Assert.Equal(new RectD(101, 126, 800, 450), refit);

        // Part of the selection is outside the frame: the result is not.
        RectD clipped = SelectionRatios.Refit(new RectD(-50, -50, 200, 200), Frame, SelectionRatio.Ratio1x1);
        Assert.Equal(new RectD(0, 0, 200, 200), clipped);
        RectD wide = SelectionRatios.Refit(new RectD(1800, 900, 400, 400), Frame, SelectionRatio.Ratio16x9);
        Assert.True(wide.Right <= 1920 && wide.Bottom <= 1080);
    }

    [Fact]
    public void RefitLeavesAnEmptySelectionAlone()
    {
        var empty = new RectD(10, 10, 0, 0);
        Assert.Equal(empty, SelectionRatios.Refit(empty, Frame, SelectionRatio.Ratio16x9));
        var tiny = new RectD(10, 10, 0.4, 0.4);
        RectD refit = SelectionRatios.Refit(tiny, Frame, SelectionRatio.Ratio16x9);
        Assert.True(refit.Width >= 1 && refit.Height >= 1);
    }

    [Fact]
    public void ArrowResizeKeepsTheRatioAndTheTopLeftCorner()
    {
        var selection = new RectD(100, 100, 160, 90);
        const SelectionRatio Wide = SelectionRatio.Ratio16x9;
        Assert.Equal(new RectD(100, 100, 161, 91), SelectionRatios.Resize(selection, horizontal: true, 1, 1, Frame, Wide));
        Assert.Equal(new RectD(100, 100, 170, 96), SelectionRatios.Resize(selection, horizontal: true, 1, 10, Frame, Wide));
        Assert.Equal(new RectD(100, 100, 159, 89), SelectionRatios.Resize(selection, horizontal: true, -1, 1, Frame, Wide));
        Assert.Equal(new RectD(100, 100, 162, 91), SelectionRatios.Resize(selection, horizontal: false, 1, 1, Frame, Wide));
        Assert.Equal(new RectD(100, 100, 142, 80), SelectionRatios.Resize(selection, horizontal: false, -1, 10, Frame, Wide));
        Assert.Equal(selection, SelectionRatios.Resize(selection, horizontal: true, 1, 1, Frame, SelectionRatio.Free));
    }

    [Fact]
    public void ArrowResizeBringsASelectionThatLacksTheLockOntoItFirst()
    {
        var selection = new RectD(0, 0, 800, 600);
        RectD resized = SelectionRatios.Resize(selection, horizontal: true, 1, 1, Frame, SelectionRatio.Ratio16x9);
        Assert.Equal(new RectD(0, 0, 801, 451), resized);
    }

    [Fact]
    public void ArrowResizeStopsAtTheFrameKeepingTheRatio()
    {
        var selection = new RectD(1800, 1000, 100, 56);
        RectD grown = SelectionRatios.Resize(selection, horizontal: true, 1, 100, Frame, SelectionRatio.Ratio16x9);
        Assert.Equal(new RectD(1800, 1000, 120, 68), grown);
        // Pressing again at the limit changes nothing.
        Assert.Equal(grown, SelectionRatios.Resize(grown, horizontal: true, 1, 100, Frame, SelectionRatio.Ratio16x9));
        Assert.Equal(grown, SelectionRatios.Resize(grown, horizontal: false, 1, 100, Frame, SelectionRatio.Ratio16x9));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrowResizeNeverGetsStuckOnTheSideTheKeyChanges(bool horizontal)
    {
        // 9:16 is the ratio whose derived side rounds worst; the pressed side still moves by exactly the step.
        RectD selection = new(0, 0, 56, 100);
        for (int press = 0; press < 40; press++)
        {
            RectD next = SelectionRatios.Resize(selection, horizontal, 1, 1, Frame, SelectionRatio.Ratio9x16);
            Assert.Equal((horizontal ? selection.Width : selection.Height) + 1, horizontal ? next.Width : next.Height);
            Assert.True(SelectionRatios.Matches((int)next.Width, (int)next.Height, SelectionRatio.Ratio9x16));
            selection = next;
        }

        for (int press = 0; press < 400; press++)
        {
            selection = SelectionRatios.Resize(selection, horizontal, -1, 1, Frame, SelectionRatio.Ratio9x16);
            Assert.True(selection.Width >= 1 && selection.Height >= 1);
        }

        // Shrinking stops at one pixel on the side the key changes.
        Assert.Equal(1, horizontal ? selection.Width : selection.Height);
        Assert.True(SelectionRatios.Matches((int)selection.Width, (int)selection.Height, SelectionRatio.Ratio9x16));
    }

    [Fact]
    public void EvenArrowResizeMovesByWholePixelPairs()
    {
        RectD resized = SelectionRatios.Resize(new RectD(0, 0, 160, 90), horizontal: true, 1, 1, Frame, SelectionRatio.Ratio16x9, evenSize: true);
        Assert.Equal(new RectD(0, 0, 162, 92), resized);
        Assert.Equal(0, resized.Width % 2);
        Assert.Equal(0, resized.Height % 2);
    }

    [Theory]
    // Monitors that hold the size: exactly that size, centred.
    [InlineData(2560, 1440, 1920, 1080, 320, 180, 1920, 1080)]
    [InlineData(1920, 1080, 1280, 720, 320, 180, 1280, 720)]
    [InlineData(1366, 768, 1280, 720, 43, 24, 1280, 720)]
    // Monitors that do not: the largest rectangle of the same ratio, exactly that ratio.
    [InlineData(1366, 768, 1920, 1080, 3, 1, 1360, 765)]
    [InlineData(1366, 768, 1080, 1080, 299, 0, 768, 768)]
    [InlineData(1024, 768, 1280, 720, 0, 96, 1024, 576)]
    public void FixedSizeIsCentredOnTheMonitorOrScaledDownToItsRatio(int monitorWidth, int monitorHeight, int width, int height,
        double left, double top, double placedWidth, double placedHeight)
    {
        var monitor = new RectD(0, 0, monitorWidth, monitorHeight);
        RectD? placed = SelectionRatios.PlaceFixed(new FixedSelectionSize(width, height), monitor, monitor);
        Assert.Equal(new RectD(left, top, placedWidth, placedHeight), placed);
        if (placedWidth != width) Assert.Equal(width * placedHeight, height * placedWidth);
    }

    [Fact]
    public void FixedSizeOnASecondMonitorIsCentredThereAndNotOnTheDesktop()
    {
        var frame = new RectD(0, 0, 4480, 1440);
        var second = new RectD(2560, 0, 1920, 1080);
        Assert.Equal(new RectD(2880, 180, 1280, 720), SelectionRatios.PlaceFixed(new FixedSelectionSize(1280, 720), second, frame));
        // A monitor too small for the size stays inside that monitor.
        var small = new RectD(2560, 0, 1280, 720);
        RectD placed = SelectionRatios.PlaceFixed(new FixedSelectionSize(1920, 1080), small, frame)!.Value;
        Assert.Equal(new RectD(2560, 0, 1280, 720), placed);
    }

    [Fact]
    public void EvenFixedSizeScaledDownKeepsEverySideEvenAndTheRatioExact()
    {
        var monitor = new RectD(0, 0, 1366, 768);
        RectD placed = SelectionRatios.PlaceFixed(new FixedSelectionSize(1920, 1080), monitor, monitor, evenSize: true)!.Value;
        Assert.Equal(new RectD(11, 6, 1344, 756), placed);
        Assert.Equal(1920 * placed.Height, 1080 * placed.Width);
        // A size that already fits is placed as asked, even or not.
        Assert.Equal(new RectD(53, 0, 1260, 768), SelectionRatios.PlaceFixed(new FixedSelectionSize(1260, 768), monitor, monitor, evenSize: true));
    }

    [Theory]
    // 100% and 150% displays, in physical pixels: 1920 x 1080 and 1280 x 720 DIPs scaled.
    [InlineData(2880, 1620, 1920, 1080, 480, 270, 1920, 1080)]
    [InlineData(2256, 1504, 1280, 720, 488, 392, 1280, 720)]
    [InlineData(1536, 864, 1920, 1080, 0, 0, 1536, 864)]
    [InlineData(1280, 720, 1920, 1080, 0, 0, 1280, 720)]
    public void FixedSizeIsInPhysicalPixelsWhateverTheDisplayScale(int monitorWidth, int monitorHeight, int width, int height,
        double left, double top, double placedWidth, double placedHeight)
    {
        var monitor = new RectD(0, 0, monitorWidth, monitorHeight);
        Assert.Equal(new RectD(left, top, placedWidth, placedHeight),
            SelectionRatios.PlaceFixed(new FixedSelectionSize(width, height), monitor, monitor));
    }

    [Fact]
    public void FixedSizeFallsBackToTheFrameWhenTheMonitorIsOutsideItAndRefusesNothingToPlace()
    {
        var frame = new RectD(0, 0, 1920, 1080);
        Assert.Equal(new RectD(320, 180, 1280, 720),
            SelectionRatios.PlaceFixed(new FixedSelectionSize(1280, 720), new RectD(5000, 0, 1920, 1080), frame));
        Assert.Null(SelectionRatios.PlaceFixed(new FixedSelectionSize(0, 720), frame, frame));
        Assert.Null(SelectionRatios.PlaceFixed(new FixedSelectionSize(1280, -1), frame, frame));
        Assert.Null(SelectionRatios.PlaceFixed(new FixedSelectionSize(1280, 720), frame, RectD.Empty));
    }
}
