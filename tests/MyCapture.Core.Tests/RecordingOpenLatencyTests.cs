using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingOpenLatencyTests
{
    [Fact]
    public void Marks_ChargeEachIntervalToOneStageAndAddUpToTheTotal()
    {
        long now = 1_000;
        var latency = new RecordingOpenLatency(now, () => now, ticksPerSecond: 1_000);

        now += 6;
        latency.Mark(RecordingOpenStage.Finalize);
        now += 300;
        latency.Mark(RecordingOpenStage.Validate);
        now += 20;
        latency.Mark(RecordingOpenStage.Publish);
        now += 130;
        latency.Mark(RecordingOpenStage.Thumbnail);
        now += 15;
        latency.Mark(RecordingOpenStage.Publish);
        now += 90;
        latency.Mark(RecordingOpenStage.EditorOpen);

        Assert.Equal(6, latency[RecordingOpenStage.Finalize], 6);
        Assert.Equal(300, latency[RecordingOpenStage.Validate], 6);
        Assert.Equal(130, latency[RecordingOpenStage.Thumbnail], 6);
        Assert.Equal(35, latency[RecordingOpenStage.Publish], 6);
        Assert.Equal(90, latency[RecordingOpenStage.EditorOpen], 6);
        Assert.Equal(0, latency[RecordingOpenStage.EditorFirstRender], 6);
        Assert.Equal(561, latency.TotalMs, 6);
        double sum = Enum.GetValues<RecordingOpenStage>().Sum(stage => latency[stage]);
        Assert.Equal(latency.TotalMs, sum, 6);
    }

    [Fact]
    public void Mark_ConvertsClockTicksToMilliseconds()
    {
        long now = 0;
        var latency = new RecordingOpenLatency(0, () => now, ticksPerSecond: 10_000_000);

        now = 47_370_000;
        latency.Mark(RecordingOpenStage.Validate);

        Assert.Equal(4_737, latency[RecordingOpenStage.Validate], 6);
    }

    [Fact]
    public void Mark_NeverRecordsNegativeTimeWhenTheClockStepsBack()
    {
        long now = 500;
        var latency = new RecordingOpenLatency(500, () => now, ticksPerSecond: 1_000);

        now = 400;
        latency.Mark(RecordingOpenStage.Finalize);
        now = 450;
        latency.Mark(RecordingOpenStage.Validate);

        Assert.Equal(0, latency[RecordingOpenStage.Finalize], 6);
        Assert.Equal(0, latency[RecordingOpenStage.Validate], 6);
        Assert.Equal(0, latency.TotalMs, 6);
    }

    [Fact]
    public void Constructor_AndMark_RejectInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new RecordingOpenLatency(0, null!, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordingOpenLatency(0, () => 0, 0));
        var latency = new RecordingOpenLatency(0, () => 0, 1_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => latency.Mark((RecordingOpenStage)99));
    }
}
