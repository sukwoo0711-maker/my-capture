using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>What the allocation tests may rely on when they measure through <see cref="ThreadAllocation"/>.</summary>
public sealed class ThreadAllocationTests
{
    [Fact]
    public void AnOperationThatAllocatesOnEachCallNeverPasses()
    {
        object? kept = null;
        long[] windows = ThreadAllocation.Measure(() =>
        {
            for (int i = 0; i < 100; i++) kept = new byte[16];
        });

        Assert.NotNull(kept);
        Assert.Equal(ThreadAllocation.Windows, windows.Length);
        Assert.False(ThreadAllocation.AnyWithin(windows, 0), ThreadAllocation.Describe(windows));
        Assert.All(windows, allocated => Assert.True(allocated >= 100 * 16));
    }

    [Fact]
    public void TheLimitIsNotRelaxed()
    {
        // 100 arrays of 16 bytes are over a 1,000-byte limit in every window, however many are measured.
        object? kept = null;
        long[] windows = ThreadAllocation.Measure(() =>
        {
            for (int i = 0; i < 100; i++) kept = new byte[16];
        }, limit: 1000);

        Assert.NotNull(kept);
        Assert.False(ThreadAllocation.AnyWithin(windows, 1000), ThreadAllocation.Describe(windows));
    }

    [Fact]
    public void WorkDoneOnceIsNotCountedAgainstTheOperation()
    {
        // The stand-in for the runtime's one-time work inside the first window. This is also the gap the
        // helper accepts: an operation that allocated only at first and then stopped passes.
        bool first = true;
        object? kept = null;
        int runs = 0;
        long[] windows = ThreadAllocation.Measure(() =>
        {
            runs++;
            if (!first) return;
            first = false;
            kept = new byte[4096];
        });

        Assert.NotNull(kept);
        Assert.True(windows[0] >= 4096, ThreadAllocation.Describe(windows));
        Assert.True(ThreadAllocation.AnyWithin(windows, 0), ThreadAllocation.Describe(windows));
        // Measuring stops at the first window within the limit.
        Assert.Equal(runs, windows.Length);
        Assert.True(windows[^1] <= 0);
    }
}
