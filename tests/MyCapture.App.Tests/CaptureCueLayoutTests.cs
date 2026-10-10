using System.Windows;
using MyCapture.App.Capture;
using Xunit;
using Placement = MyCapture.App.Capture.CaptureCueTabPlacement;

namespace MyCapture.App.Tests;

/// <summary>Pure placement of the tab that names the window or monitor a click captures.</summary>
public sealed class CaptureCueLayoutTests
{
    private static readonly Size Screen = new(1920, 1080);
    private static readonly Size Tab = new(200, 24);
    // The top instruction and the key-hint chip below it, centred like the overlay draws them.
    private static readonly Rect Instruction = new(700, 18, 520, 37);
    private static readonly Rect Hints = new(800, 61, 320, 28);
    private static readonly IReadOnlyList<Rect> Chrome = [Instruction, Hints];

    [Fact]
    public void RoomAboveAttachesTheTabOnTheWindowsTopEdge()
    {
        var (bounds, placement) = Place(new Rect(300, 300, 800, 500));
        Assert.Equal(Placement.Above, placement);
        Assert.Equal(new Rect(300, 276, 200, 24), bounds);
    }

    [Fact]
    public void WindowOnTheTopEdgeTakesTheTabInsideItsTopEdge()
    {
        var (bounds, placement) = Place(new Rect(300, 0, 800, 500));
        Assert.Equal(Placement.InsideTop, placement);
        Assert.Equal(new Rect(300, 0, 200, 24), bounds);

        // A maximized window fills the screen; its tab stays on screen inside it.
        (bounds, placement) = Place(new Rect(0, 0, 1920, 1080));
        Assert.Equal(Placement.InsideTop, placement);
        Assert.Equal(new Rect(0, 0, 200, 24), bounds);
    }

    [Fact]
    public void SmallWindowOnTheTopEdgeIsNotBuriedUnderItsTab()
    {
        var (bounds, placement) = Place(new Rect(300, 0, 120, 16));
        Assert.Equal(Placement.Below, placement);
        Assert.Equal(new Rect(300, 16, 200, 24), bounds);
    }

    [Fact]
    public void WindowOnTheBottomEdgeKeepsItsTabAbove()
    {
        var (bounds, placement) = Place(new Rect(300, 1000, 800, 80));
        Assert.Equal(Placement.Above, placement);
        Assert.Equal(new Rect(300, 976, 200, 24), bounds);
    }

    [Fact]
    public void LeftAndRightEdgesClampTheTabOntoTheScreen()
    {
        // Partly off the left edge: the tab starts at the visible left edge.
        var (bounds, placement) = Place(new Rect(-50, 300, 600, 400));
        Assert.Equal(Placement.Above, placement);
        Assert.Equal(new Rect(0, 276, 200, 24), bounds);

        // A narrow window on the right edge: the wider tab ends at the screen edge.
        (bounds, placement) = Place(new Rect(1850, 300, 70, 60));
        Assert.Equal(Placement.Above, placement);
        Assert.Equal(new Rect(1720, 276, 200, 24), bounds);

        // Partly off the right edge.
        (bounds, _) = Place(new Rect(1800, 300, 400, 300));
        Assert.Equal(1720, bounds.Left);
        Assert.True(bounds.Right <= Screen.Width);
    }

    [Fact]
    public void WindowWiderThanTheTabNearTheRightEdgeKeepsTheTabLeftAligned()
    {
        var (bounds, _) = Place(new Rect(1700, 300, 220, 300));
        Assert.Equal(new Rect(1700, 276, 200, 24), bounds);
    }

    [Fact]
    public void NothingFitsCleanly_TheTabStillStaysOnScreen()
    {
        var viewport = new Size(300, 40);
        var (bounds, placement) = CaptureCueLayout.PlaceTab(new Rect(10, 0, 50, 30), Tab, viewport, [], insideOnly: false);
        Assert.Equal(Placement.Floating, placement);
        Assert.Equal(new Rect(10, 0, 200, 24), bounds);
        Assert.True(new Rect(viewport).Contains(bounds));
    }

    [Fact]
    public void TopInstructionChipsAreNeverCovered()
    {
        // Above would cover the instruction; inside the window it moves under both chips.
        var target = new Rect(720, 40, 600, 400);
        var (bounds, placement) = Place(target);
        Assert.Equal(Placement.Floating, placement);
        Assert.Equal(new Rect(720, Hints.Bottom + CaptureCueLayout.ChromeGap, 200, 24), bounds);
        Assert.True(target.Contains(bounds));
        Assert.DoesNotContain(Chrome, chrome => CaptureCueLayout.Overlaps(chrome, bounds));

        // A small window right under the chips: above is covered, inside is too small, so below.
        target = new Rect(720, 60, 120, 40);
        (bounds, placement) = Place(target);
        Assert.Equal(Placement.Below, placement);
        Assert.Equal(new Rect(720, 100, 200, 24), bounds);
        Assert.DoesNotContain(Chrome, chrome => CaptureCueLayout.Overlaps(chrome, bounds));
    }

    [Fact]
    public void WholeMonitorKeepsTheTabWithinThatMonitor()
    {
        // The right display of two: never above or beside it, even with space there.
        var twoDisplays = new Size(3840, 1080);
        var monitor = new Rect(1920, 0, 1920, 1080);
        var (bounds, placement) = CaptureCueLayout.PlaceTab(monitor, Tab, twoDisplays, [], insideOnly: true);
        Assert.Equal(Placement.InsideTop, placement);
        Assert.Equal(new Rect(1920, 0, 200, 24), bounds);

        // A lower display in a stacked layout has room above, but that is another display.
        var stacked = new Size(1920, 2160);
        monitor = new Rect(0, 1080, 1920, 1080);
        (bounds, placement) = CaptureCueLayout.PlaceTab(monitor, Tab, stacked, [], insideOnly: true);
        Assert.Equal(Placement.InsideTop, placement);
        Assert.Equal(new Rect(0, 1080, 200, 24), bounds);

        // Under the centred chips on a narrow display, it moves down but stays on that display.
        monitor = new Rect(350, 0, 500, 800);
        (bounds, placement) = CaptureCueLayout.PlaceTab(monitor, Tab, new Size(1200, 800),
            [new Rect(300, 18, 600, 37)], insideOnly: true);
        Assert.Equal(Placement.Floating, placement);
        Assert.Equal(new Rect(350, 61, 200, 24), bounds);
        Assert.True(monitor.Contains(bounds));

        // A display narrower than the tab shortens the tab instead of spilling onto its neighbour.
        monitor = new Rect(400, 0, 150, 200);
        (bounds, placement) = CaptureCueLayout.PlaceTab(monitor, Tab, new Size(550, 360), [], insideOnly: true);
        Assert.Equal(Placement.InsideTop, placement);
        Assert.Equal(new Rect(400, 0, 150, 24), bounds);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void HighDpiTabsLandOnWholePhysicalPixelsAndMeetTheBorderExactly(double scale)
    {
        double pixel = 1 / scale;
        // 300x300 physical pixels from (300, 300): on the grid at both scales.
        var target = new Rect(300 * pixel, 300 * pixel, 600 * pixel, 450 * pixel);
        var tab = new Size(151.3, 23.7);
        var screen = new Size(1920 * pixel, 1080 * pixel);
        var (bounds, placement) = CaptureCueLayout.PlaceTab(target, tab, screen, [], insideOnly: false, pixel, pixel);
        Assert.Equal(Placement.Above, placement);
        AssertOnGrid(bounds, scale);
        Assert.Equal(target.Top, bounds.Bottom, 9);
        Assert.Equal(target.Left, bounds.Left, 9);
        Assert.True(bounds.Width >= tab.Width && bounds.Width - tab.Width < pixel);
        Assert.True(bounds.Height >= tab.Height && bounds.Height - tab.Height < pixel);

        // Moved under a chip whose bottom is between pixels: still on the grid.
        var chip = new Rect(0, 18, screen.Width, 37.37);
        var top = new Rect(300 * pixel, 0, 600 * pixel, 450 * pixel);
        (bounds, placement) = CaptureCueLayout.PlaceTab(top, tab, screen, [chip], insideOnly: false, pixel, pixel);
        Assert.Equal(Placement.Floating, placement);
        AssertOnGrid(bounds, scale);
        Assert.True(bounds.Top >= chip.Bottom + CaptureCueLayout.ChromeGap);
        Assert.False(CaptureCueLayout.Overlaps(chip, bounds));
    }

    [Fact]
    public void EdgeContactIsNotAnOverlap()
    {
        Assert.False(CaptureCueLayout.Overlaps(new Rect(0, 0, 10, 10), new Rect(10, 0, 10, 10)));
        Assert.False(CaptureCueLayout.Overlaps(new Rect(0, 0, 10, 10), new Rect(0, 10, 10, 10)));
        Assert.True(CaptureCueLayout.Overlaps(new Rect(0, 0, 10, 10), new Rect(9, 9, 10, 10)));
    }

    private static (Rect Bounds, Placement Placement) Place(Rect target) =>
        CaptureCueLayout.PlaceTab(target, Tab, Screen, Chrome, insideOnly: false);

    private static void AssertOnGrid(Rect bounds, double scale)
    {
        foreach (double edge in new[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom })
            Assert.True(Math.Abs((edge * scale) - Math.Round(edge * scale)) < 1e-6, $"{edge} at {scale}x");
    }
}
