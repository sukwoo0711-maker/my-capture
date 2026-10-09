using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingPauseTimelineTests
{
    [Fact]
    public void WithoutPause_ActiveTimeFollowsElapsedTime()
    {
        var timeline = new RecordingPauseTimeline();

        Assert.Equal(RecordingPauseState.Recording, timeline.State);
        Assert.Equal(1234, timeline.ActiveElapsedMs(1234), 3);
        Assert.Equal(0, timeline.PausedMs(1234), 3);
        Assert.Equal(0, timeline.PauseCount);
    }

    [Fact]
    public void PausedSpan_FreezesActiveTimeAndResumeContinuesFromTheSameValue()
    {
        // 10 s recorded, 5 s paused, 10 s recorded: the clip timeline is 20 s long.
        var timeline = new RecordingPauseTimeline();

        Assert.True(timeline.TryPause(10_000));
        Assert.True(timeline.IsPaused);
        Assert.Equal(10_000, timeline.ActiveElapsedMs(12_000), 3);
        Assert.Equal(2_000, timeline.PausedMs(12_000), 3);
        Assert.Equal(10_000, timeline.ActiveElapsedMs(15_000), 3);

        Assert.True(timeline.TryResume(15_000));
        Assert.False(timeline.IsPaused);
        Assert.Equal(10_000, timeline.ActiveElapsedMs(15_000), 3);
        Assert.Equal(10_001, timeline.ActiveElapsedMs(15_001), 3);

        timeline.End(25_000);
        Assert.Equal(RecordingPauseState.Ended, timeline.State);
        Assert.Equal(20_000, timeline.ActiveElapsedMs(25_000), 3);
        Assert.Equal(5_000, timeline.PausedMs(25_000), 3);
        Assert.Equal(1, timeline.PauseCount);
    }

    [Fact]
    public void FramesClaimedThroughTheTimeline_StayContinuousAcrossAPause()
    {
        // The recorder hands the active time to RecordingClock. A pause must neither leave a
        // gap in the timestamps nor make the next frame due early.
        var timeline = new RecordingPauseTimeline();
        var clock = new RecordingClock(10); // 100 ms interval
        var timestamps = new List<double>();
        void Tick(double elapsed)
        {
            if (clock.TryClaimFrame(timeline.ActiveElapsedMs(elapsed), out double ts)) timestamps.Add(ts);
        }

        for (int t = 0; t <= 1000; t += 100) Tick(t);
        Assert.True(timeline.TryPause(1050));
        // The capture loop parks while paused; even a stray tick cannot claim a new frame
        // because the active time stays at 1050 ms, less than one interval after 1000 ms.
        Tick(4000);
        Assert.True(timeline.TryResume(6050));
        for (int t = 6100; t <= 7050; t += 50) Tick(t);
        timeline.End(7050);

        Assert.Equal(Enumerable.Range(0, 21).Select(i => i * 100.0), timestamps);
        Assert.Equal(2050, timeline.ActiveElapsedMs(7050), 3);
        Assert.Equal(5000, timeline.PausedMs(7050), 3);
    }

    [Fact]
    public void MultiplePauses_AccumulateAndAreCounted()
    {
        var timeline = new RecordingPauseTimeline();

        Assert.True(timeline.TryPause(1_000));
        Assert.True(timeline.TryResume(3_000));
        Assert.True(timeline.TryPause(4_000));
        Assert.True(timeline.TryResume(4_500));
        timeline.End(6_000);

        Assert.Equal(2, timeline.PauseCount);
        Assert.Equal(2_500, timeline.PausedMs(6_000), 3);
        Assert.Equal(3_500, timeline.ActiveElapsedMs(6_000), 3);
    }

    [Fact]
    public void EndWhilePaused_ClosesTheOpenSpanAtTheStopInstant()
    {
        var timeline = new RecordingPauseTimeline();

        Assert.True(timeline.TryPause(800));
        timeline.End(9_800);

        Assert.Equal(800, timeline.ActiveElapsedMs(20_000), 3);
        Assert.Equal(9_000, timeline.PausedMs(20_000), 3);
        Assert.False(timeline.IsPaused);
        Assert.Equal(RecordingPauseState.Ended, timeline.State);
    }

    [Fact]
    public void InvalidTransitions_AreRejectedWithoutChangingState()
    {
        var timeline = new RecordingPauseTimeline();

        Assert.False(timeline.TryResume(100));
        Assert.True(timeline.TryPause(200));
        Assert.False(timeline.TryPause(300));
        Assert.Equal(1, timeline.PauseCount);
        Assert.True(timeline.TryResume(400));
        Assert.False(timeline.TryResume(500));

        timeline.End(600);
        timeline.End(900);
        Assert.False(timeline.TryPause(700));
        Assert.False(timeline.TryResume(800));
        Assert.Equal(RecordingPauseState.Ended, timeline.State);
        Assert.Equal(400, timeline.ActiveElapsedMs(10_000), 3);
        Assert.Equal(200, timeline.PausedMs(10_000), 3);
    }

    [Fact]
    public void TimeNeverRunsBackwards_AndInvalidInputsAreIgnored()
    {
        var timeline = new RecordingPauseTimeline();

        Assert.Equal(1_000, timeline.ActiveElapsedMs(1_000), 3);
        // A late reading from another thread must not move an emitted timestamp backwards.
        Assert.Equal(1_000, timeline.ActiveElapsedMs(900), 3);
        Assert.Equal(1_000, timeline.ActiveElapsedMs(double.NaN), 3);
        Assert.Equal(1_000, timeline.ActiveElapsedMs(-50), 3);
        Assert.True(timeline.TryPause(950));
        Assert.True(timeline.TryResume(double.PositiveInfinity));
        Assert.Equal(0, timeline.PausedMs(1_000), 3);
        Assert.Equal(1_200, timeline.ActiveElapsedMs(1_200), 3);
    }

    [Fact]
    public void PauseAtStartup_KeepsTheTimelineAtZeroUntilResume()
    {
        // A pause requested while the encoder is still starting is recorded at time zero; the
        // recorder still writes its mandatory timestamp-zero frame before parking.
        var timeline = new RecordingPauseTimeline();

        Assert.True(timeline.TryPause(0));
        Assert.Equal(0, timeline.ActiveElapsedMs(750), 3);
        Assert.True(timeline.TryResume(750));
        Assert.Equal(250, timeline.ActiveElapsedMs(1_000), 3);
    }
}

public sealed class RecordingFrameMetricsTests
{
    [Fact]
    public void Metrics_UseRecordedTimeSoAPauseIsNotReportedAsDroppedFrames()
    {
        // 20 s recorded at 15 fps with every frame written, plus 5 s paused. Wall-clock time
        // (25 s) would wrongly expect 375 frames and report 75 drops at 12 fps.
        var metrics = new RecordingFrameMetrics(RecordedMs: 20_000, Fps: 15, EmittedFrames: 300);

        Assert.Equal(300, metrics.ExpectedFrames);
        Assert.Equal(0, metrics.DroppedFrames);
        Assert.Equal(15, metrics.EffectiveFps, 3);
        Assert.Equal(0, metrics.DropRate, 3);
    }

    [Fact]
    public void Metrics_ReportRealDropsWithinTheRecordedTime()
    {
        var metrics = new RecordingFrameMetrics(2_000, 30, 45);

        Assert.Equal(60, metrics.ExpectedFrames);
        Assert.Equal(15, metrics.DroppedFrames);
        Assert.Equal(22.5, metrics.EffectiveFps, 3);
        Assert.Equal(0.25, metrics.DropRate, 3);
    }

    [Theory]
    [InlineData(0, 30, 1)]
    [InlineData(1, 30, 1)]
    [InlineData(-5, 30, 0)]
    [InlineData(1_000, 0, 3)]
    public void Metrics_NeverReportNegativeOrUndefinedValues(double recordedMs, int fps, long emitted)
    {
        var metrics = new RecordingFrameMetrics(recordedMs, fps, emitted);

        Assert.True(metrics.ExpectedFrames >= emitted);
        Assert.Equal(0, metrics.DroppedFrames);
        Assert.True(double.IsFinite(metrics.EffectiveFps) && metrics.EffectiveFps >= 0);
        Assert.Equal(0, metrics.DropRate, 3);
    }
}
