using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// The frame schedule must not accumulate the capture loop's wake-up latency: frames are due
/// on a fixed grid, and only a stall of a whole interval re-anchors it.
/// </summary>
public sealed class RecordingClockPacingTests
{
    [Fact]
    public void ALateFrame_DoesNotDelayTheNextSlot()
    {
        var clock = new RecordingClock(30);
        clock.TryClaimFrame(0, out _);

        Assert.True(clock.TryClaimFrame(34, out double late));

        Assert.Equal(34, late);
        // Due at 66.67 ms (second grid slot), not 34 + 33.33 = 67.33 ms.
        Assert.Equal(66.667 - 34, clock.MillisecondsUntilNextFrame(34), 3);
        Assert.False(clock.TryClaimFrame(66.6, out _));
        Assert.True(clock.TryClaimFrame(66.7, out _));
    }

    [Theory]
    [InlineData(30, 0.7)]
    [InlineData(60, 0.7)]
    [InlineData(30, 5)]
    [InlineData(60, 10)]
    public void ConstantWakeUpLatency_KeepsTheTargetRate(int fps, double latencyMs)
    {
        // Every wait overshoots by the same latency (shorter than one interval). The old
        // "one interval after the last frame" rule turned this into fps = 1000 / (interval +
        // latency); the grid absorbs it.
        var clock = new RecordingClock(fps);
        double now = 0;
        while (now < 10_000)
        {
            clock.TryClaimFrame(now, out _);
            now += clock.MillisecondsUntilNextFrame(now) + latencyMs;
        }

        Assert.InRange(clock.EmittedFrames, (fps * 10) - 1, (fps * 10) + 1);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void CoarseSystemTimerTicks_StillClaimEverySlot(int fps)
    {
        // The default 15.625 ms system tick rounds every wait up to the next tick, which made
        // a 30 fps recording wait 46.9 ms per frame (21.3 fps) and 60 fps wait 31.25 ms.
        const double Tick = 15.625;
        var clock = new RecordingClock(fps);
        var timestamps = new List<double>();
        double now = 0;
        while (now < 10_000)
        {
            if (clock.TryClaimFrame(now, out double timestamp)) timestamps.Add(timestamp);
            double wait = clock.MillisecondsUntilNextFrame(now);
            now = Math.Ceiling((now + Math.Max(wait, 0.001)) / Tick) * Tick;
        }

        Assert.InRange(timestamps.Count, (fps * 10) - 2, (fps * 10) + 1);
        // Real timestamps, never closer than the tick and never a whole interval late.
        double[] gaps = timestamps.Zip(timestamps.Skip(1), (a, b) => b - a).ToArray();
        Assert.All(gaps, gap => Assert.InRange(gap, Tick - 1e-6, (2 * 1000.0 / fps) + 1e-6));
    }

    [Fact]
    public void StallOfSeveralIntervals_ReanchorsInsteadOfBursting()
    {
        var clock = new RecordingClock(30);
        clock.TryClaimFrame(0, out _);

        Assert.True(clock.TryClaimFrame(100, out double resumed));
        Assert.Equal(100, resumed);

        // The three missed slots are dropped, not emitted back to back.
        Assert.False(clock.TryClaimFrame(100.1, out _));
        Assert.False(clock.TryClaimFrame(133, out _));
        Assert.Equal(33.333, clock.MillisecondsUntilNextFrame(100), 3);
        Assert.True(clock.TryClaimFrame(133.34, out _));
        Assert.Equal(3, clock.EmittedFrames);
    }

    [Fact]
    public void ExactlyOneIntervalLate_DropsThatSlot()
    {
        // Claiming the first slot at the second slot's time drops the first slot; the grid
        // restarts one interval later instead of emitting a second frame immediately.
        var clock = new RecordingClock(10);
        clock.TryClaimFrame(0, out _);

        Assert.True(clock.TryClaimFrame(200, out _));

        Assert.False(clock.TryClaimFrame(200, out _));
        Assert.Equal(100, clock.MillisecondsUntilNextFrame(200), 3);
    }

    [Fact]
    public void OnTimeFrames_KeepTheExactGrid()
    {
        var clock = new RecordingClock(30);
        var timestamps = new List<double>();
        for (int slot = 0; slot < 90; slot++)
        {
            double due = slot * 1000.0 / 30;
            Assert.True(clock.TryClaimFrame(due, out double timestamp));
            timestamps.Add(timestamp);
            Assert.False(clock.TryClaimFrame(due + 1, out _));
        }

        Assert.Equal(90, clock.EmittedFrames);
        Assert.Equal(89 * 1000.0 / 30, timestamps[^1], 6);
    }
}
