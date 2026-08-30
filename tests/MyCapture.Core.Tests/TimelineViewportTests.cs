using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class TimelineViewportTests
{
    [Fact]
    public void New_StartsFittingWholeClip()
    {
        var vp = new TimelineViewport(12_000, 15);
        Assert.Equal(0, vp.ViewStartMs);
        Assert.Equal(12_000, vp.ViewEndMs);
        Assert.True(vp.IsFitAll);
    }

    [Fact]
    public void MsToPx_And_PxToMs_RoundTripWithinView()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(2_000, 4_000); // 2s window

        double px = vp.MsToPx(3_000, 800); // midpoint -> 400px
        Assert.Equal(400, px, 1);

        double ms = vp.PxToMs(400, 800);
        Assert.Equal(3_000, ms, 1);
    }

    [Fact]
    public void MsToPx_ClampsOutsideView()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(2_000, 4_000);

        Assert.Equal(0, vp.MsToPx(0, 800), 1);      // before view -> left edge
        Assert.Equal(800, vp.MsToPx(9_000, 800), 1); // after view -> right edge
    }

    [Fact]
    public void Zoom_In_KeepsCentreAnchored()
    {
        var vp = new TimelineViewport(10_000, 15); // view 0..10000
        vp.Zoom(centerMs: 5_000, factor: 0.5);      // zoom to 5s span around 5000

        Assert.Equal(5_000, vp.VisibleSpanMs, 1);
        Assert.Equal(2_500, vp.ViewStartMs, 1);
        Assert.Equal(7_500, vp.ViewEndMs, 1);
    }

    [Fact]
    public void Zoom_RespectsMinimumSpan()
    {
        var vp = new TimelineViewport(10_000, 15); // min span = 3 frames @15fps = 200ms
        for (int i = 0; i < 40; i++)
        {
            vp.Zoom(5_000, 0.5); // repeatedly zoom in hard
        }

        Assert.True(vp.VisibleSpanMs >= vp.MinSpanMs - 0.001);
        Assert.True(vp.VisibleSpanMs <= vp.MinSpanMs + 0.001);
    }

    [Fact]
    public void Zoom_Out_NeverExceedsClip()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(4_000, 6_000);

        for (int i = 0; i < 20; i++)
        {
            vp.Zoom(5_000, 2.0); // zoom out hard
        }

        Assert.Equal(0, vp.ViewStartMs, 1);
        Assert.Equal(10_000, vp.ViewEndMs, 1);
        Assert.True(vp.IsFitAll);
    }

    [Fact]
    public void Pan_ClampsToClipBounds_PreservingSpan()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(2_000, 4_000); // span 2000

        vp.Pan(-5_000); // try to pan before start
        Assert.Equal(0, vp.ViewStartMs, 1);
        Assert.Equal(2_000, vp.ViewEndMs, 1);

        vp.Pan(100_000); // try to pan past end
        Assert.Equal(8_000, vp.ViewStartMs, 1);
        Assert.Equal(10_000, vp.ViewEndMs, 1);
    }

    [Fact]
    public void SetView_EnforcesMinimumSpan()
    {
        var vp = new TimelineViewport(10_000, 15); // min 200ms
        vp.SetView(5_000, 5_010); // 10ms — below min

        Assert.True(vp.VisibleSpanMs >= vp.MinSpanMs - 0.001);
        // Centred on the requested midpoint (~5005).
        Assert.True(vp.ViewStartMs < 5_005 && vp.ViewEndMs > 5_005);
    }

    [Fact]
    public void SetView_ReversedArgs_AreNormalised()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(6_000, 3_000); // reversed

        Assert.Equal(3_000, vp.ViewStartMs, 1);
        Assert.Equal(6_000, vp.ViewEndMs, 1);
    }

    [Fact]
    public void FitAll_RestoresWholeClip()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(4_000, 5_000);
        vp.FitAll();

        Assert.True(vp.IsFitAll);
        Assert.Equal(0, vp.ViewStartMs);
        Assert.Equal(10_000, vp.ViewEndMs);
    }

    [Fact]
    public void EnsureVisible_PansMinimallyToRevealTime()
    {
        var vp = new TimelineViewport(10_000, 15);
        vp.SetView(2_000, 4_000);

        vp.EnsureVisible(5_000); // past the right edge
        Assert.True(5_000 <= vp.ViewEndMs + 0.001 && 5_000 >= vp.ViewStartMs - 0.001);
        Assert.Equal(2_000, vp.VisibleSpanMs, 1); // span preserved

        vp.EnsureVisible(500); // before the left edge
        Assert.True(500 >= vp.ViewStartMs - 0.001);
        Assert.Equal(2_000, vp.VisibleSpanMs, 1);
    }
}
