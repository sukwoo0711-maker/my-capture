using MyCapture.App.Accessibility;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The screen reader flag and its use by the host policy, and the snap rectangle of a native pin, without a window.</summary>
public sealed class ScreenReaderStateTests
{
    [Theory]
    [InlineData(0x001Au, 0x0047L, true)]
    [InlineData(0x001Au, 0x0046L, false)]
    [InlineData(0x001Au, 0L, false)]
    [InlineData(0x0200u, 0x0047L, false)]
    public void OnlyTheSettingChangeNoticeForTheScreenReaderFlagIsRecognized(uint message, long wParam, bool expected) =>
        Assert.Equal(expected, ScreenReaderState.IsScreenReaderNotice(message, new IntPtr(wParam)));

    [Fact]
    public void TheFlagIsReadAtStartAndChangedIsRaisedOnlyWhenItDiffers()
    {
        bool flag = true;
        using var state = new ScreenReaderState(window: null, reader: () => flag);
        Assert.True(state.Active);
        var raised = new List<bool>();
        state.Changed += raised.Add;

        state.Refresh();
        Assert.Empty(raised);
        flag = false;
        state.Refresh();
        state.Refresh();
        flag = true;
        state.Refresh();
        Assert.Equal([false, true], raised);
        Assert.True(state.Active);
    }

    [Fact]
    public void AReaderThatThrowsCountsAsNoScreenReader()
    {
        bool fail = false;
        using var state = new ScreenReaderState(window: null, reader: () => fail ? throw new InvalidOperationException() : true);
        Assert.True(state.Active);
        fail = true;
        state.Refresh();
        Assert.False(state.Active);
    }

    [Fact]
    public void TheSystemFlagReaderReturnsWithoutThrowing() => _ = ScreenReaderState.ReadSystemFlag();

    [Fact]
    public void ThePolicyKeepsAPlainPictureOnWpfWhileAScreenReaderRuns()
    {
        var settings = new PinSettings { LightweightImagePins = true };
        PinContent picture = PinContent.FromImage(PinNativeTestImages.Picture(40, 30));
        Assert.Equal(PinHostKind.Native, PinHostPolicy.Choose(picture, settings, screenReader: false, collapsed: false));
        Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(picture, settings, screenReader: true, collapsed: false));
        Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(picture, new PinSettings(), screenReader: false, collapsed: false));
    }

    [Fact]
    public void ASurfaceLargerThanTheScreenSnapsByItsWholeEdgesNotByTheVisiblePart()
    {
        var display = new PinDisplayArea("test", new RectD(0, 0, 1000, 800), new RectD(0, 0, 1000, 760), 1);
        // A pin zoomed past the screen: its surface is 3000x2000 and its left part lies off the screen.
        var moving = new RectD(-1500, 300, 3000, 2000);
        var other = new PinSnap.Target(new RectD(1504, 300, 200, 100));
        RectD snapped = PinSnap.Calculate(moving, [display], [other]);
        Assert.Equal(-1496, snapped.Left);
        Assert.Equal(3000, snapped.Width);
        Assert.Equal(2000, snapped.Height);
        // The right edge of the whole surface met the other pin's left edge (1500 to 1504 is 4 px).
        Assert.Equal(1504, snapped.Right);
        Assert.Equal(moving.Top, snapped.Top);
        Assert.Equal(moving, PinSnap.Calculate(moving, [display], [other], shiftHeld: true));
    }
}
