using System.Text.Json;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinSnapTests
{
    private static readonly PinDisplayArea Screen = new("main", new RectD(0, 0, 1000, 800), new RectD(0, 0, 1000, 760), 1);

    [Fact]
    public void NoTargetsAndShiftLeaveBoundsUnchanged()
    {
        var bounds = new RectD(6, 7, 100, 80);
        Assert.Equal(bounds, PinSnap.Calculate(bounds, [], []));
        Assert.Equal(bounds, PinSnap.Calculate(bounds, [Screen], [new(new RectD(102, 7, 100, 80))], shiftHeld: true));
    }

    [Fact]
    public void ScreenAxesSnapIndependentlyAndOversizedPinKeepsItsSize()
    {
        Assert.Equal(new RectD(0, 100, 100, 80), PinSnap.Calculate(new RectD(7, 100, 100, 80), [Screen], []));
        Assert.Equal(new RectD(900, 680, 100, 80), PinSnap.Calculate(new RectD(894, 687, 100, 80), [Screen], []));
        Assert.Equal(new RectD(0, 0, 1500, 1000), PinSnap.Calculate(new RectD(7, 6, 1500, 1000), [Screen], []));
    }

    [Fact]
    public void NearestOfCompetingScreenAndPinTargetsWins()
    {
        RectD result = PinSnap.Calculate(new RectD(6, 100, 100, 80), [Screen],
            [new(new RectD(108, 100, 100, 80)), new(new RectD(9, 100, 100, 80))]);
        Assert.Equal(8, result.Left); // Right edge 106 is two pixels from the first pin's left edge.
    }

    [Theory]
    [InlineData(195, 100, 200, 100)] // left to right
    [InlineData(5, 100, 0, 100)] // right to left
    [InlineData(104, 100, 100, 100)] // left alignment
    [InlineData(100, 175, 100, 180)] // top to bottom
    [InlineData(100, 25, 100, 20)] // bottom to top
    [InlineData(100, 104, 100, 100)] // top alignment
    public void AdjacencyAndAlignmentWorkOnBothAxes(double left, double top, double expectedLeft, double expectedTop)
    {
        var result = PinSnap.Calculate(new RectD(left, top, 100, 80), [], [new(new RectD(100, 100, 100, 80))]);
        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(expectedTop, result.Top);
    }

    [Fact]
    public void OtherAxisDistanceAndHiddenStateRejectTargets()
    {
        var bounds = new RectD(195, 100, 100, 80);
        Assert.Equal(bounds, PinSnap.Calculate(bounds, [], [new(new RectD(100, 300, 100, 80))]));
        Assert.Equal(bounds, PinSnap.Calculate(bounds, [], [new(new RectD(100, 100, 100, 80), IsHidden: true)]));
        Assert.Equal(200, PinSnap.Calculate(bounds, [], [new(new RectD(100, 188, 100, 80))]).Left);
    }

    [Fact]
    public void MixedDpiUsesPhysicalPlaneAndTheMovingPinsMonitorScale()
    {
        var highDpi = new PinDisplayArea("secondary", new RectD(1000, -200, 1600, 1200), new RectD(1000, -200, 1600, 1160), 1.5);
        Assert.Equal(1000, PinSnap.Calculate(new RectD(1011, 200, 150, 120), [Screen, highDpi], []).Left);
        Assert.Equal(1013, PinSnap.Calculate(new RectD(1013, 200, 150, 120), [Screen, highDpi], []).Left);
        Assert.Equal(11, PinSnap.Calculate(new RectD(11, 200, 100, 80), [Screen, highDpi], []).Left);
        Assert.Equal(1200, PinSnap.Calculate(new RectD(1189, 200, 150, 120), [Screen, highDpi],
            [new(new RectD(1100, 200, 100, 80))]).Left);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SettingsRoundTripCloneAndDraftKeepSnap(bool enabled)
    {
        Assert.True(JsonSerializer.Deserialize<PinSettings>("{}")!.SnapWhileDragging);
        var pin = JsonSerializer.Deserialize<PinSettings>(JsonSerializer.Serialize(new PinSettings { SnapWhileDragging = enabled }))!;
        Assert.Equal(enabled, pin.SnapWhileDragging);
        var settings = new AppSettings { Pin = pin }.DeepClone();
        var draft = new SettingsDraft(settings);
        Assert.Equal(enabled, draft.SnapWhileDragging);
        Assert.Equal(enabled, draft.ToAppSettings().Pin.SnapWhileDragging);
        draft.SnapWhileDragging = !enabled;
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal(!enabled, draft.ToAppSettings().Pin.SnapWhileDragging);
    }
}
