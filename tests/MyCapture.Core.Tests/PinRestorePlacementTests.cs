using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Where a restored pin lands: exactly where it closed while the layout still shows it,
/// otherwise inside a visible work area, with the size following the target display DPI.
/// </summary>
public sealed class PinRestorePlacementTests
{
    private const string Primary = "\\\\.\\DISPLAY1";
    private const string Secondary = "\\\\.\\DISPLAY2";

    private static PinDisplayArea Display(string name, double left, double top, double width, double height, double scale = 1.0) =>
        new(name, new RectD(left, top, width, height), new RectD(left, top, width, height - 40), scale);

    private static readonly PinDisplayArea PrimaryFullHd = Display(Primary, 0, 0, 1920, 1080);
    private static readonly PinDisplayArea SecondaryRight = Display(Secondary, 1920, 0, 2560, 1440, 1.5);

    [Fact]
    public void UnchangedLayout_RestoresExactPositionAndSize()
    {
        var closed = new PinClosedPlacement(new RectD(300, 200, 400, 300), Primary, PrimaryFullHd.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 400, 300, [PrimaryFullHd, SecondaryRight]);

        Assert.False(result.Adjusted);
        Assert.Equal(0, result.DisplayIndex);
        Assert.Equal(300, result.Left);
        Assert.Equal(200, result.Top);
        Assert.Equal(400, result.Width);
        Assert.Equal(300, result.Height);
    }

    [Fact]
    public void PinSpanningTwoDisplays_IsLeftWhereItWas()
    {
        var closed = new PinClosedPlacement(new RectD(1800, 100, 300, 200), Primary, PrimaryFullHd.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 300, 200, [PrimaryFullHd, Display(Secondary, 1920, 0, 1920, 1080)]);

        Assert.False(result.Adjusted);
        Assert.Equal(1800, result.Left);
        Assert.Equal(100, result.Top);
    }

    [Fact]
    public void HighDpiDisplay_SizesFromDipTimesScale()
    {
        var closed = new PinClosedPlacement(new RectD(2400, 300, 600, 450), Secondary, SecondaryRight.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 400, 300, [PrimaryFullHd, SecondaryRight]);

        Assert.Equal(1, result.DisplayIndex);
        Assert.False(result.Adjusted);
        Assert.Equal(600, result.Width, 6);
        Assert.Equal(450, result.Height, 6);
        Assert.Equal(2400, result.Left);
    }

    [Fact]
    public void DisconnectedDisplay_MovesPinIntoNearestVisibleWorkArea()
    {
        // The pin lived on a right-hand display that is gone; only the primary remains.
        var closed = new PinClosedPlacement(new RectD(2600, 500, 600, 450), Secondary, SecondaryRight.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 400, 300, [PrimaryFullHd]);

        Assert.True(result.Adjusted);
        Assert.Equal(0, result.DisplayIndex);
        // 100% primary: 400x300 DIP is 400x300 px, clamped to the right edge of the work area.
        Assert.Equal(400, result.Width, 6);
        Assert.Equal(1920 - 400, result.Left, 6);
        Assert.Equal(500, result.Top, 6);
        Assert.True(PinRestorePlacement.IsFullyOnDisplays(
            new RectD(result.Left, result.Top, result.Width, result.Height), [PrimaryFullHd]));
    }

    [Fact]
    public void MovedDisplay_KeepsOffsetFromItsOrigin()
    {
        // Same display, rearranged from the right of the primary to its left.
        var closed = new PinClosedPlacement(new RectD(2020, 150, 300, 200), Secondary, new RectD(1920, 0, 1920, 1080));
        PinDisplayArea movedLeft = Display(Secondary, -1920, 0, 1920, 1080);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 300, 200, [PrimaryFullHd, movedLeft]);

        Assert.True(result.Adjusted);
        Assert.Equal(1, result.DisplayIndex);
        Assert.Equal(-1920 + 100, result.Left, 6);
        Assert.Equal(150, result.Top, 6);
    }

    [Fact]
    public void PartiallyOffScreen_IsClampedIntoItsDisplayWorkArea()
    {
        var closed = new PinClosedPlacement(new RectD(-150, 900, 400, 300), Primary, PrimaryFullHd.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 400, 300, [PrimaryFullHd]);

        Assert.True(result.Adjusted);
        Assert.Equal(0, result.Left, 6);
        // Work area ends 40 px above the bottom (taskbar).
        Assert.Equal(1040 - 300, result.Top, 6);
    }

    [Fact]
    public void FarAwayPin_ChoosesNearestDisplay()
    {
        PinDisplayArea left = Display("\\\\.\\DISPLAY3", -1920, 0, 1920, 1080);
        var closed = new PinClosedPlacement(new RectD(-6000, 200, 200, 100), "\\\\.\\DISPLAY9", RectD.Empty);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 200, 100, [PrimaryFullHd, left]);

        Assert.True(result.Adjusted);
        Assert.Equal(1, result.DisplayIndex);
        Assert.Equal(-1920, result.Left, 6);
        Assert.Equal(200, result.Top, 6);
    }

    [Fact]
    public void PinLargerThanWorkArea_AlignsToWorkAreaTopLeft()
    {
        var closed = new PinClosedPlacement(new RectD(2000, 100, 3000, 2000), Secondary, SecondaryRight.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 3000, 2000, [PrimaryFullHd]);

        Assert.True(result.Adjusted);
        Assert.Equal(0, result.Left, 6);
        Assert.Equal(0, result.Top, 6);
        // Zoom is the user's choice and is kept; the visible top-left corner stays grabbable.
        Assert.Equal(3000, result.Width, 6);
    }

    [Fact]
    public void NoDisplays_ReturnsRecordedBounds()
    {
        var closed = new PinClosedPlacement(new RectD(10, 20, 30, 40), Primary, PrimaryFullHd.Bounds);

        PinRestorePlacement.Result result = PinRestorePlacement.Resolve(closed, 30, 40, []);

        Assert.Equal(-1, result.DisplayIndex);
        Assert.False(result.Adjusted);
        Assert.Equal(10, result.Left);
        Assert.Equal(20, result.Top);
    }

    [Fact]
    public void FullyOnDisplays_ToleratesOnePixelRoundingButNotAMissingStrip()
    {
        PinDisplayArea[] displays = [PrimaryFullHd];
        Assert.True(PinRestorePlacement.IsFullyOnDisplays(new RectD(0, 0, 1920, 1080), displays));
        Assert.True(PinRestorePlacement.IsFullyOnDisplays(new RectD(1520.5, 0, 400, 300), displays));
        Assert.False(PinRestorePlacement.IsFullyOnDisplays(new RectD(1600, 0, 400, 300), displays));
        Assert.False(PinRestorePlacement.IsFullyOnDisplays(new RectD(0, 0, 0, 0), displays));
    }
}
