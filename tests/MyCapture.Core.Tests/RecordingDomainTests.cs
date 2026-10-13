using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class FrameStepCalculatorTests
{
    [Theory]
    [InlineData(30, 33.333)]
    [InlineData(15, 66.666)]
    [InlineData(24, 41.666)]
    public void FrameDurationMs_MatchesFps(int fps, double expected)
    {
        Assert.Equal(expected, FrameStepCalculator.FrameDurationMs(fps), 2);
    }

    [Fact]
    public void StepByFrames_MovesExactlyOneFrameForward()
    {
        // 30fps -> 33.333ms per frame. Starting at a boundary, +1 frame lands on the next.
        double next = FrameStepCalculator.StepByFrames(0, 1, 30, 10_000);

        Assert.Equal(FrameStepCalculator.FrameDurationMs(30), next, 3);
    }

    [Fact]
    public void StepByFrames_ShiftTenFramesMovesTenIntervals()
    {
        double next = FrameStepCalculator.StepByFrames(0, 10, 30, 10_000);

        Assert.Equal(10 * FrameStepCalculator.FrameDurationMs(30), next, 3);
    }

    [Fact]
    public void StepByFrames_ClampsAtZeroWhenSteppingBackFromStart()
    {
        double next = FrameStepCalculator.StepByFrames(0, -5, 30, 10_000);

        Assert.Equal(0, next);
    }

    [Fact]
    public void StepByFrames_StopsOnTheLastFrameWhenSteppingPastEnd()
    {
        // The clip end shows the last frame (frame 299 at 9966.7 ms); parking there would make
        // the next press change nothing.
        double next = FrameStepCalculator.StepByFrames(9_990, 5, 30, 10_000);

        Assert.Equal(299 * FrameStepCalculator.FrameDurationMs(30), next, 3);
    }

    [Theory]
    // From the second half of frame 1 (40-66 ms at 30 fps) the next frame is frame 2.
    [InlineData(50, 1, 30, 10_000, 2)]
    [InlineData(50, -1, 30, 10_000, 0)]
    // From the clip end, which shows the last frame, one step back is the frame before it.
    [InlineData(3_000, -1, 30, 3_000, 88)]
    // A container a hair short of the grid (2999.98 ms at 60 fps) still has 180 frames.
    [InlineData(2_999.9833, -1, 60, 2_999.9833, 178)]
    [InlineData(2_983.3167, -1, 60, 2_999.9833, 177)]
    [InlineData(2_966.6667, 1, 60, 2_999.9833, 179)]
    [InlineData(1_600, 1, 15, 3_000, 25)]
    [InlineData(1_600, 1, 30, 3_000, 49)]
    public void StepByFrames_MovesFromTheFrameShownToItsNeighbour(double position, int frames, int fps, double duration, int expectedFrame)
    {
        double next = FrameStepCalculator.StepByFrames(position, frames, fps, duration);

        Assert.Equal(expectedFrame * FrameStepCalculator.FrameDurationMs(fps), next, 3);
        Assert.Equal(expectedFrame, FrameStepCalculator.FrameIndexAt(next, fps, duration));
    }

    [Fact]
    public void StepByFrames_VisitsEveryFrameOnceInEachDirection()
    {
        foreach ((int fps, double duration) in new[] { (15, 2_999.9333), (30, 3_000.0), (60, 2_999.9833), (24, 1_041.6667) })
        {
            int last = FrameStepCalculator.LastFrameIndex(fps, duration);
            double position = 0;
            for (int frame = 1; frame <= last + 2; frame++)
            {
                position = FrameStepCalculator.StepByFrames(position, 1, fps, duration);
                Assert.Equal(Math.Min(frame, last), FrameStepCalculator.FrameIndexAt(position, fps, duration));
            }

            position = duration;
            for (int frame = last - 1; frame >= -2; frame--)
            {
                position = FrameStepCalculator.StepByFrames(position, -1, fps, duration);
                Assert.Equal(Math.Max(frame, 0), FrameStepCalculator.FrameIndexAt(position, fps, duration));
            }
        }
    }

    [Theory]
    [InlineData(30, 3_000, 89)]
    [InlineData(60, 2_999.9833, 179)]
    [InlineData(15, 2_999.9333, 44)]
    [InlineData(30, 3_000.5, 90)]
    [InlineData(30, 10, 0)]
    [InlineData(30, 0, 0)]
    [InlineData(0, 3_000, 0)]
    public void LastFrameIndex_IsTheLastFrameStartingBeforeTheEnd(int fps, double duration, int expected) =>
        Assert.Equal(expected, FrameStepCalculator.LastFrameIndex(fps, duration));

    [Fact]
    public void SnapToFrame_LandsOnNearestFrameBoundary()
    {
        // 40ms is between frame 1 (33.3) and frame 2 (66.6) at 30fps; nearest is frame 1.
        double snapped = FrameStepCalculator.SnapToFrame(40, 30, 10_000);

        Assert.Equal(FrameStepCalculator.FrameDurationMs(30), snapped, 3);
    }

    [Fact]
    public void CoarseStepMs_UsesFallbackForLongClips()
    {
        // Two-minute clip: proportional (6s) exceeds the 5s fallback, so the fallback wins.
        double step = FrameStepCalculator.CoarseStepMs(120_000, 5.0);

        Assert.Equal(5_000, step, 1);
    }

    [Fact]
    public void CoarseStepMs_ScalesDownForShortClipsIntoUsableBand()
    {
        // Three-second clip: proportional is 150ms, clamped up to the 250ms floor so an arrow
        // press is a proportional nudge rather than skipping the whole clip.
        double step = FrameStepCalculator.CoarseStepMs(3_000, 5.0);

        Assert.Equal(250, step, 1);
    }

    [Fact]
    public void CoarseStepMs_NeverExceedsClipDuration()
    {
        double step = FrameStepCalculator.CoarseStepMs(120, 5.0);

        Assert.True(step <= 120);
    }

    [Fact]
    public void StepCoarse_MovesForwardAndClampsToDuration()
    {
        double next = FrameStepCalculator.StepCoarse(119_000, 1, 120_000, 5.0);

        Assert.Equal(120_000, next, 1);
    }

    [Fact]
    public void FrameIndexAt_ReturnsZeroBasedFrame()
    {
        // Just past frame 3 at 30fps.
        double pos = (3 * FrameStepCalculator.FrameDurationMs(30)) + 1;

        Assert.Equal(3, FrameStepCalculator.FrameIndexAt(pos, 30, 10_000));
    }
}

public sealed class TrimSelectionTests
{
    [Fact]
    public void NewSelection_CoversWholeClip()
    {
        var trim = new TrimSelection(10_000);

        Assert.Equal(0, trim.InMs);
        Assert.Equal(10_000, trim.OutMs);
        Assert.True(trim.IsFullClip);
    }

    [Fact]
    public void SetIn_CannotCrossOutMinimumSpan()
    {
        var trim = new TrimSelection(10_000);
        trim.SetOut(5_000);

        // Try to push In past Out; it must stop a minimum span short of Out.
        trim.SetIn(9_000);

        Assert.Equal(5_000 - TrimSelection.MinimumSpanMs, trim.InMs, 3);
        Assert.True(trim.SelectedDurationMs >= TrimSelection.MinimumSpanMs);
    }

    [Fact]
    public void SetOut_CannotCrossInMinimumSpan()
    {
        var trim = new TrimSelection(10_000);
        trim.SetIn(4_000);

        trim.SetOut(100); // below In

        Assert.Equal(4_000 + TrimSelection.MinimumSpanMs, trim.OutMs, 3);
    }

    [Fact]
    public void SetIn_ClampsToZeroFloor()
    {
        var trim = new TrimSelection(10_000);

        trim.SetIn(-500);

        Assert.Equal(0, trim.InMs);
    }

    [Fact]
    public void SetOut_ClampsToDurationCeiling()
    {
        var trim = new TrimSelection(10_000);

        trim.SetOut(50_000);

        Assert.Equal(10_000, trim.OutMs);
    }

    [Fact]
    public void Reset_RestoresFullClip()
    {
        var trim = new TrimSelection(10_000);
        trim.SetIn(2_000);
        trim.SetOut(8_000);

        trim.Reset();

        Assert.True(trim.IsFullClip);
    }

    [Fact]
    public void ClampToSelection_KeepsPositionInsideInOut()
    {
        var trim = new TrimSelection(10_000);
        trim.SetIn(2_000);
        trim.SetOut(8_000);

        Assert.Equal(2_000, trim.ClampToSelection(0));
        Assert.Equal(8_000, trim.ClampToSelection(9_999));
        Assert.Equal(5_000, trim.ClampToSelection(5_000));
    }
}

public sealed class RecordingClockTests
{
    [Fact]
    public void FirstClaim_EmitsFrameAtZero()
    {
        var clock = new RecordingClock(30);

        bool due = clock.TryClaimFrame(0, out double ts);

        Assert.True(due);
        Assert.Equal(0, ts);
        Assert.Equal(1, clock.EmittedFrames);
    }

    [Fact]
    public void SecondClaimBeforeInterval_IsNotDue()
    {
        var clock = new RecordingClock(30); // 33.3ms interval
        clock.TryClaimFrame(0, out _);

        bool due = clock.TryClaimFrame(10, out _);

        Assert.False(due);
        Assert.Equal(1, clock.EmittedFrames);
    }

    [Fact]
    public void ClaimAfterInterval_IsDueAndCarriesElapsedTimestamp()
    {
        var clock = new RecordingClock(30);
        clock.TryClaimFrame(0, out _);

        bool due = clock.TryClaimFrame(40, out double ts);

        Assert.True(due);
        Assert.Equal(40, ts);
        Assert.Equal(2, clock.EmittedFrames);
    }

    [Fact]
    public void LongStall_EmitsOnlyOneFrameAndKeepsRealTimestamp()
    {
        // The loop was blocked for ~10 frame intervals. Adaptive drop: only one frame is
        // emitted, at the real elapsed time, so playback does not speed up.
        var clock = new RecordingClock(30);
        clock.TryClaimFrame(0, out _);

        bool due = clock.TryClaimFrame(333, out double ts);

        Assert.True(due);
        Assert.Equal(333, ts);
        Assert.Equal(2, clock.EmittedFrames); // not 11

        // The next frame is only due one interval after the stall's timestamp.
        Assert.False(clock.TryClaimFrame(340, out _));
        Assert.True(clock.TryClaimFrame(333 + 34, out _));
    }

    [Fact]
    public void MillisecondsUntilNextFrame_CountsDownFromLastEmit()
    {
        var clock = new RecordingClock(10); // 100ms interval
        clock.TryClaimFrame(0, out _);

        Assert.Equal(60, clock.MillisecondsUntilNextFrame(40), 3);
        Assert.Equal(0, clock.MillisecondsUntilNextFrame(150), 3);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveFps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingClock(0));
    }
}
