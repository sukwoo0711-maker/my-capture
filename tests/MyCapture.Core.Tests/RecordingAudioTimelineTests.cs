using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingAudioTimelineTests
{
    [Fact]
    public void SharedEpoch_ConvertsRawQpcAndPacketQpcWithoutOverflow()
    {
        const long frequency = 3_579_545;
        const long epoch = 123_456_789_000;
        var timeline = new RecordingAudioTimeline(epoch, frequency);
        Assert.Equal(0, timeline.Elapsed100ns(epoch - 1));
        Assert.Equal(18_000_000_000, timeline.Elapsed100ns(epoch + frequency * 1800));
        long packetQpc = checked((long)((Int128)(epoch + frequency * 1800) * 10_000_000 / frequency));
        Assert.InRange(timeline.ElapsedFromPacketQpc100ns(packetQpc) - 18_000_000_000, -1, 1);
        Assert.Equal(86_400_000, RecordingAudioFormat.SampleAt100ns(timeline.Active100ns(epoch + frequency * 1800)));
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void PacketCrossingPause_IsSplitAtNativeSamplesWithoutPausedContent(int rate)
    {
        const long pause = 100_100, resume = 200_300, end = 1_000_000;
        var timeline = new RecordingAudioTimeline(0, 10_000_000);
        Assert.True(timeline.TryPause(pause));
        Assert.True(timeline.TryResume(resume));
        timeline.End(end);
        var slices = timeline.SplitPacket(0, rate / 10, rate);
        Assert.Equal(2, slices.Count);
        Assert.Equal(0, slices[0].OutputStartSample);
        Assert.Equal(slices[0].OutputEndSample, slices[1].OutputStartSample);
        Assert.Equal(RecordingAudioFormat.SampleAt100ns(end - resume + pause), slices[^1].OutputEndSample);
        foreach (var slice in slices)
        for (int n = slice.InputOffsetFrames; n < slice.InputOffsetFrames + slice.InputFrameCount; n++)
        {
            double timestamp = n * 10_000_000d / rate;
            Assert.False(timestamp >= pause && timestamp < resume);
            Assert.InRange(timestamp, 0, end);
        }
    }

    [Theory]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void ManyOffGridPauses_HaveContiguousCumulativeOutputAndNoRoundingDrift(int rate)
    {
        var timeline = new RecordingAudioTimeline(0, 10_000_000);
        const int pauses = 10_000;
        long removed = 0;
        for (long i = 0; i < pauses; i++)
        {
            long start = i * 20_003 + 10_001;
            long stop = start + 7_007;
            timeline.TryPause(start); timeline.TryResume(stop);
            removed += stop - start;
        }
        long end = pauses * 20_003L;
        timeline.End(end);
        int nativeFrames = checked((int)Math.Ceiling(end * (double)rate / 10_000_000));
        var slices = timeline.SplitPacket(0, nativeFrames, rate);
        long count = 0, previous = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            Assert.Equal(previous, slice.OutputStartSample);
            count += slice.OutputFrameCount;
            previous = slice.OutputEndSample;
            long firstScaled = (long)slice.InputOffsetFrames * 10_000_000;
            long lastScaled = (long)(slice.InputOffsetFrames + slice.InputFrameCount - 1) * 10_000_000;
            long activeStart = i == 0 ? 0 : (i - 1) * 20_003L + 17_008;
            long activeEnd = i == pauses ? end : i * 20_003L + 10_001;
            Assert.True(lastScaled >= firstScaled);
            Assert.True(firstScaled >= activeStart * rate);
            Assert.True(lastScaled < activeEnd * rate);
        }
        Assert.Equal(RecordingAudioFormat.SampleAt100ns(end - removed), count);
        Assert.Equal(timeline.Active100ns(end), end - removed);
    }

    [Fact]
    public void StopWhilePaused_FreezesBothTimelineAndPacketAcceptanceBoundary()
    {
        var timeline = new RecordingAudioTimeline(0, 10_000_000);
        timeline.TryPause(8_000_000);
        timeline.End(98_000_000);
        Assert.Equal(8_000_000, timeline.Active100ns(200_000_000));
        Assert.Equal(RecordingPauseState.Ended, timeline.State);
        Assert.False(timeline.TryResume(100_000_000));
        var late = timeline.SplitPacket(0, 480_000, 48_000);
        Assert.Single(late);
        Assert.Equal(38_400, late[0].InputFrameCount);
        Assert.Equal(38_400, late[0].OutputEndSample);
        Assert.Empty(timeline.SplitPacket(9_000_000, 4800, 48_000));
        timeline.End(300_000_000);
        Assert.Equal(8_000_000, timeline.Active100ns(300_000_000));
    }

    [Fact]
    public void SubSampleActiveSpans_PreserveBudgetWithoutBorrowingPausedSamples()
    {
        var timeline = new RecordingAudioTimeline(0, 10_000_000);
        for (int i = 0; i < 1000; i++)
        {
            timeline.TryPause(i * 300 + 100);
            timeline.TryResume((i + 1) * 300);
        }
        timeline.End(300_000);
        var slices = timeline.SplitPacket(0, 1323, 44_100);
        Assert.Equal(480, slices.Sum(slice => slice.OutputFrameCount));
        Assert.Contains(slices, slice => slice.InputFrameCount == 0 && slice.OutputFrameCount > 0);
        foreach (var slice in slices)
        for (int n = slice.InputOffsetFrames; n < slice.InputOffsetFrames + slice.InputFrameCount; n++)
            Assert.True((n * 10_000_000L % (300 * 44_100L)) < 100 * 44_100L);
    }

    [Fact]
    public void LatePacketQueries_DoNotMovePauseCommands_AndPreEpochSamplesAreTrimmed()
    {
        var timeline = new RecordingAudioTimeline(100, 10_000_000);
        timeline.TryPause(10_100);
        timeline.TryResume(20_100);
        Assert.Equal(5000, timeline.ActiveAtElapsed100ns(5000));
        Assert.Equal(10_000, timeline.ActiveAtElapsed100ns(15_000));
        Assert.True(timeline.TryPause(30_100));
        Assert.Equal(20_000, timeline.Active100ns(long.MaxValue));
        var early = timeline.SplitPacket(-10_000, 96, 48_000);
        Assert.Single(early);
        Assert.Equal(48, early[0].InputOffsetFrames);
        Assert.Equal(0, early[0].OutputStartSample);
    }

    [Fact]
    public void ManySubSampleCuts_RoundCumulativeDurationOnlyOnce()
    {
        const int count = 20_000;
        var ranges = Enumerable.Range(0, count).Select(i => new VideoTimeRange(i * 0.2, i * 0.2 + 0.031234567)).ToArray();
        var map = RecordingAudioCutMap.Create(ranges);
        Assert.Equal(count, map.Count);
        long previous = 0, sum = 0;
        foreach (var range in map)
        {
            Assert.Equal(previous, range.OutputStartSample);
            Assert.True(range.SourceEnd100ns > range.SourceStart100ns);
            previous = range.OutputEndSample;
            sum += range.OutputFrameCount;
        }
        long expected = (long)Math.Round(count * 0.031234567 * 48, MidpointRounding.AwayFromZero);
        Assert.InRange(Math.Abs(sum - expected), 0, 1);
        Assert.NotEqual(count * (long)Math.Round(0.031234567 * 48), sum);
    }

    [Fact]
    public void CutMap_UsesKeptRangesFromExistingVideoTimeline_AndRejectsOverlaps()
    {
        var video = VideoCutTimeline.Create(0, 3000, [new(1000, 2000)]);
        var map = RecordingAudioCutMap.Create(video.KeptRanges);
        Assert.Equal(2, map.Count);
        Assert.Equal(new RecordingAudioKeptRange(0, 10_000_000, 0, 48_000), map[0]);
        Assert.Equal(new RecordingAudioKeptRange(20_000_000, 30_000_000, 48_000, 96_000), map[1]);
        Assert.Throws<ArgumentException>(() => RecordingAudioCutMap.Create([new(0, 100), new(50, 150)]));
        Assert.Throws<ArgumentException>(() => RecordingAudioCutMap.Create([new(double.NaN, 100)]));
    }
}
