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
    public void StepByFrames_ClampsAtDurationWhenSteppingPastEnd()
    {
        double next = FrameStepCalculator.StepByFrames(9_990, 5, 30, 10_000);

        Assert.Equal(10_000, next, 3);
    }

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
