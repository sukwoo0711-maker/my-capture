using Xunit;

namespace MyCapture.App.Tests;

public sealed class IdleMemoryReclamationTests
{
    [Fact]
    public void FlowEnd_WaitsThreeSeconds_ThenCollectsOnlyOnce()
    {
        var idle = new IdleMemoryReclamation();
        int calls = 0;
        idle.FlowEnded(100);
        Assert.False(idle.TryCollect(3099, true, () => calls++));
        Assert.True(idle.TryCollect(3100, true, () => calls++));
        Assert.False(idle.TryCollect(6100, true, () => calls++));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NewCapture_CancelsOldEnd_EvenWhenItFinishesBeforeOldDeadline()
    {
        var idle = new IdleMemoryReclamation();
        idle.FlowEnded(100);
        idle.FlowStarted();
        Assert.False(idle.TryCollect(3100, true, () => Assert.Fail("Cancelled collection")));
        idle.FlowEnded(4000);
        Assert.False(idle.TryCollect(6999, true, () => Assert.Fail("Early collection")));
        Assert.True(idle.TryCollect(7000, true, () => { }));
    }

    [Fact]
    public void BusyAtDeadline_DiscardsStaleCollection()
    {
        var idle = new IdleMemoryReclamation();
        idle.FlowEnded(0);
        Assert.False(idle.TryCollect(3000, false, () => Assert.Fail("Busy collection")));
        Assert.False(idle.TryCollect(4000, true, () => Assert.Fail("Stale collection")));
    }

    [Fact]
    public void MultipleClosingSurfaces_DebounceToLastEnd()
    {
        var idle = new IdleMemoryReclamation();
        idle.FlowEnded(0);
        idle.FlowEnded(2000);
        Assert.False(idle.TryCollect(3000, true, () => Assert.Fail("Early collection")));
        Assert.True(idle.TryCollect(5000, true, () => { }));
    }
}
