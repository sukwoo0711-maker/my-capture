using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoMulticutRenderTests
{
    [Fact]
    public void Mp4Schedule_MapsOutputFramesAcrossCutsWithoutMovingLayerTimes()
    {
        VideoTimeRange[] cuts = [new(1_500, 2_000), new(2_500, 3_500)];
        var overlay = new TimedTextOverlay { Text = "after cut", StartMs = 2_000, EndMs = 2_200 };
        var request = new VideoFrameRenderRequest(
            "unused.mp4", 1_000, 4_000, 10, 320, 180, 320, 180, [overlay], [], cuts);

        double[] schedule = VideoFrameRenderPipeline.BuildSchedule(request);

        Assert.Equal(new double[]
        {
            1_000, 1_100, 1_200, 1_300, 1_400,
            2_000, 2_100, 2_200, 2_300, 2_400,
            3_500, 3_600, 3_700, 3_800, 3_900,
        }, schedule);
        Assert.True(overlay.IsActiveAt(schedule[5]));
        Assert.False(overlay.IsActiveAt(schedule[7]));
        Assert.Equal(2_000, overlay.StartMs);
    }

    [Fact]
    public void Mp4Schedule_NoCutsPreservesTheOriginalCadenceAndExclusiveEnd()
    {
        var request = new VideoFrameRenderRequest("unused.mp4", 123, 473, 10, 320, 180, 320, 180, [], []);

        Assert.Equal(new double[] { 123, 223, 323, 423 }, VideoFrameRenderPipeline.BuildSchedule(request));
    }

    [Fact]
    public void GifSchedule_UsesKeptDurationAndSourceTimedLayersAtTheSeam()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 1_000);
        document.RemovedRanges = [new(300, 700)];
        document.TextOverlays =
        [
            new() { Text = "crossing", StartMs = 250, EndMs = 750 },
            new() { Text = "removed", StartMs = 330, EndMs = 660 },
        ];
        document.FrameEditLayers = [new() { StartMs = 700, EndMs = 800, OverlayPngBase64 = "AA==" }];

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document);

        Assert.Equal(new double[] { 0, 100, 200, 250, 700, 750, 800, 900 }, schedule.SourceTimesMs);
        Assert.Equal(60, schedule.FrameDelaysCentiseconds.Sum());
        TimedTextOverlay overlay = Assert.Single(schedule.QuantizedTextOverlays);
        Assert.True(overlay.IsActiveAt(250));
        Assert.True(overlay.IsActiveAt(700));
        Assert.False(overlay.IsActiveAt(750));
        Assert.Equal((250d, 750d), (document.TextOverlays[0].StartMs, document.TextOverlays[0].EndMs));
        Assert.True(Assert.Single(schedule.QuantizedFrameEditLayers).IsActiveAt(700));
    }

    [Theory]
    [InlineData(0.5, 120)]
    [InlineData(1.0, 60)]
    [InlineData(2.0, 30)]
    public void GifSchedule_SpeedScalesOutputDurationAfterCuts(double speed, int centiseconds)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 1_000);
        document.RemovedRanges = [new(300, 700)];

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed);

        Assert.Equal(centiseconds, schedule.FrameDelaysCentiseconds.Sum());
        Assert.All(schedule.SourceTimesMs, time => Assert.True(time < 300 || time >= 700));
    }

    [Theory]
    [InlineData(1_004, 2_000)]
    [InlineData(1_006, 2_000)]
    [InlineData(1_000d / 30 * 31, 1_000d / 30 * 61)]
    public void GifSchedule_QuantizedSeamUsesExactNextSourceAndLayerBoundary(double cutStart, double cutEnd)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 3_000);
        document.RemovedRanges = [new(cutStart, cutEnd)];
        document.TextOverlays =
        [
            new() { Text = "before cut", StartMs = cutStart - 100, EndMs = cutStart },
            new() { Text = "after cut", StartMs = cutEnd, EndMs = cutEnd + 100 },
        ];
        document.FrameEditLayers = [new() { StartMs = cutEnd, EndMs = cutEnd + 100, OverlayPngBase64 = "AA==" }];

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document);

        int seamFrame = Array.IndexOf(schedule.SourceTimesMs.ToArray(), cutEnd);
        Assert.True(seamFrame > 0);
        int expectedSeam = (int)Math.Round(cutStart / 10, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedSeam, schedule.FrameDelaysCentiseconds.Take(seamFrame).Sum());
        Assert.True(schedule.SourceTimesMs[seamFrame - 1] < cutStart);
        Assert.Equal(cutEnd, schedule.SourceTimesMs[seamFrame]);
        Assert.False(schedule.QuantizedTextOverlays[0].IsActiveAt(cutEnd));
        Assert.True(schedule.QuantizedTextOverlays[1].IsActiveAt(cutEnd));
        Assert.True(Assert.Single(schedule.QuantizedFrameEditLayers).IsActiveAt(cutEnd));
        Assert.Equal((int)Math.Round(document.OutputDurationMs / 10, MidpointRounding.AwayFromZero),
            schedule.FrameDelaysCentiseconds.Sum());
        Assert.All(schedule.SourceTimesMs, time => Assert.True(time < cutStart || time >= cutEnd));
        Assert.True(schedule.SourceTimesMs.SequenceEqual(schedule.SourceTimesMs.Order()));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void GifSchedule_SourceReferenceFollowsQuantizedOutputDelayAtCut(double speed)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 3_000);
        document.RemovedRanges = [new(1_004, 2_000)];
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed);
        int seamFrame = Array.IndexOf(schedule.SourceTimesMs.ToArray(), 2_000);
        double seamOutputMs = schedule.FrameDelaysCentiseconds.Take(seamFrame).Sum() * 10.0;

        Assert.Equal(2_000, schedule.SourceTimeAtOutput(seamOutputMs));
        Assert.Equal(schedule.SourceTimesMs[seamFrame - 1], schedule.SourceTimeAtOutput(seamOutputMs - 1));
    }

    [Fact]
    public void GifSample_PlansAcrossCutsOnTheOutputClockAndKeepsSourceLayerTimes()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 20_000);
        document.RemovedRanges = [new(5_000, 15_000)];
        document.TextOverlays = [new() { Text = "after cut", StartMs = 15_000, EndMs = 16_000 }];

        GifSampleWindow window = GifPreviewSampling.Plan(document, 1);
        VideoEditDocument sample = GifPreviewSampling.CreateSampleDocument(document, window);

        Assert.Equal((3_500d, 16_500d), (window.StartMs, window.EndMs));
        Assert.Equal(3_000, window.DurationMs);
        Assert.Equal(3_000, sample.OutputDurationMs);
        Assert.False(window.CoversWholeRange);
        Assert.Equal(15_000, Assert.Single(sample.TextOverlays).StartMs);
        Assert.Equal(new[] { (0d, 1_000d), (19_000d, 20_000d) },
            GifPreviewSampling.PlanRefinement(document, window).Select(segment => (segment.StartMs, segment.EndMs)));
        Assert.Equal(300, AnimatedGifExporter.BuildFrameSchedule(sample).FrameDelaysCentiseconds.Sum());
    }

    [Fact]
    public void GifSample_WholeResultUsesKeptLengthEvenWhenSourceIsLong()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 20_000);
        document.RemovedRanges = [new(2_000, 18_000)];

        GifSampleWindow window = GifPreviewSampling.Plan(document, 1);

        Assert.True(window.CoversWholeRange);
        Assert.Equal(4_000, window.DurationMs);
        Assert.Empty(GifPreviewSampling.PlanRefinement(document, window));
        Assert.Equal(4_000, GifPreviewSampling.CreateSampleDocument(document, window).OutputDurationMs);
    }

    [Fact]
    public void GifSample_EdgeNearCutKeepsTheMinimumSegmentAndMeasuresItsActualDuration()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 20_000);
        document.RemovedRanges = [new(5_000, 15_000)];

        GifSampleWindow window = GifPreviewSampling.Plan(document, 1, 4_950d / 7_000);
        VideoEditDocument sample = GifPreviewSampling.CreateSampleDocument(document, window);

        Assert.Equal(4_900, window.StartMs);
        Assert.Equal(3_050, window.DurationMs);
        Assert.Equal(window.DurationMs, sample.OutputDurationMs);
        Assert.Equal(4_900, AnimatedGifExporter.BuildFrameSchedule(sample).SourceTimesMs[0]);
    }
}
