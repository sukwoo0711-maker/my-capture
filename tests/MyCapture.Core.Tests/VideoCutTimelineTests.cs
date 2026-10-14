using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class VideoCutTimelineTests
{
    [Fact]
    public void Create_WithoutCutsKeepsTheExistingTrimMapping()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(250, 3_750);

        Assert.Empty(timeline.RemovedRanges);
        Assert.Empty(timeline.SplitPoints);
        Assert.Equal(new VideoTimeRange(250, 3_750), Assert.Single(timeline.KeptRanges));
        Assert.Equal(timeline.KeptRanges, timeline.Segments);
        Assert.Equal(3_500, timeline.OutputDurationMs);
        Assert.Equal(250, timeline.ToSourceTime(0));
        Assert.Equal(1_250, timeline.ToSourceTime(1_000));
        Assert.Equal(3_750, timeline.ToSourceTime(3_500));
        Assert.Equal(1_000, timeline.ToOutputTime(1_250));
    }

    [Fact]
    public void Create_ClipsSortsAndMergesOverlappingAndAdjacentCuts()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(100, 2_000,
        [
            new(1_900, 3_000), new(400, 700), new(600, 800), new(800, 1_000),
            new(-500, 200), new(-300, -200), new(2_100, 2_200),
        ]);

        Assert.Equal(
            [new VideoTimeRange(100, 200), new(400, 1_000), new(1_900, 2_000)],
            timeline.RemovedRanges);
        Assert.Equal([new VideoTimeRange(200, 400), new(1_000, 1_900)], timeline.KeptRanges);
        Assert.Equal(1_100, timeline.OutputDurationMs);
    }

    [Fact]
    public void Create_DropsNonfiniteEmptyAndReversedCuts()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000,
        [
            new(double.NaN, 100), new(100, double.PositiveInfinity),
            new(double.NegativeInfinity, 200), new(200, 200), new(400, 300),
            new(400, 600),
        ]);

        Assert.Equal(new VideoTimeRange(400, 600), Assert.Single(timeline.RemovedRanges));
        Assert.Equal(800, timeline.OutputDurationMs);
    }

    [Fact]
    public void Create_AbsorbsShortKeptHeadTailAndInteriorGaps()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000,
        [
            new(50, 200), new(250, 400), new(700, 950),
        ]);

        Assert.Equal([new VideoTimeRange(0, 400), new(700, 1_000)], timeline.RemovedRanges);
        Assert.Equal(new VideoTimeRange(400, 700), Assert.Single(timeline.KeptRanges));
        Assert.Equal(300, timeline.OutputDurationMs);
    }

    [Fact]
    public void Create_KeepsExactlyMinimumLengthSegments()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 500, [new(100, 200), new(300, 400)]);

        Assert.Equal(3, timeline.KeptRanges.Count);
        Assert.All(timeline.KeptRanges, range => Assert.Equal(TrimSelection.MinimumSpanMs, range.DurationMs));
        Assert.Equal(300, timeline.OutputDurationMs);
    }

    [Theory]
    [InlineData(1_000, 0, 1_000)]
    [InlineData(1_000, 50, 950)]
    [InlineData(75, 25, 50)]
    public void Create_RecoversTheOuterTrimWhenCutsWouldRemoveEverything(double end, double cutStart, double cutEnd)
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, end, [new(cutStart, cutEnd)]);

        Assert.Empty(timeline.RemovedRanges);
        Assert.Equal(end, timeline.OutputDurationMs);
        Assert.Equal(new VideoTimeRange(0, end), Assert.Single(timeline.KeptRanges));
    }

    [Fact]
    public void Create_InvalidOuterBoundsRemainFiniteAndOrdered()
    {
        VideoCutTimeline invalid = VideoCutTimeline.Create(double.NaN, double.PositiveInfinity);
        VideoCutTimeline reversed = VideoCutTimeline.Create(1_000, 100);
        VideoCutTimeline negative = VideoCutTimeline.Create(-100, 500);

        Assert.Equal(0, invalid.StartMs);
        Assert.Equal(0, invalid.EndMs);
        Assert.Equal(0, invalid.OutputDurationMs);
        Assert.Empty(invalid.KeptRanges);
        Assert.Equal(0, invalid.ToSourceTime(50));
        Assert.Equal(0, invalid.ToOutputTime(50));
        Assert.Equal(1_000, reversed.EndMs);
        Assert.Equal(0, reversed.OutputDurationMs);
        Assert.Equal(0, negative.StartMs);
        Assert.Equal(500, negative.OutputDurationMs);
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(0, 200)]
    [InlineData(199, 399)]
    [InlineData(200, 600)]
    [InlineData(399, 799)]
    [InlineData(400, 1_000)]
    [InlineData(600, 1_200)]
    [InlineData(10_000, 1_200)]
    public void ToSourceTime_ClosesCutsAndUsesTheNextKeptRangeAtASeam(double output, double source)
    {
        VideoCutTimeline timeline = CreateMappingTimeline();

        Assert.Equal(source, timeline.ToSourceTime(output));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(199, 0)]
    [InlineData(200, 0)]
    [InlineData(300, 100)]
    [InlineData(400, 200)]
    [InlineData(500, 200)]
    [InlineData(600, 200)]
    [InlineData(800, 400)]
    [InlineData(900, 400)]
    [InlineData(1_000, 400)]
    [InlineData(1_200, 600)]
    [InlineData(1_300, 600)]
    [InlineData(1_400, 600)]
    public void ToOutputTime_RemovedRangesMapToTheirSeams(double source, double output)
    {
        VideoCutTimeline timeline = CreateMappingTimeline();

        Assert.Equal(output, timeline.ToOutputTime(source));
    }

    [Fact]
    public void Mapping_NonfiniteTimesClampToTheAppropriateEndpoint()
    {
        VideoCutTimeline timeline = CreateMappingTimeline();

        Assert.Equal(200, timeline.ToSourceTime(double.NaN));
        Assert.Equal(200, timeline.ToSourceTime(double.NegativeInfinity));
        Assert.Equal(1_200, timeline.ToSourceTime(double.PositiveInfinity));
        Assert.Equal(0, timeline.ToOutputTime(double.NaN));
        Assert.Equal(0, timeline.ToOutputTime(double.NegativeInfinity));
        Assert.Equal(600, timeline.ToOutputTime(double.PositiveInfinity));
    }

    [Fact]
    public void Mapping_RoundTripsKeptSourceTimesAndEveryOutputTimeIncludingSeams()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(10.25, 1_234.5,
            [new(250.75, 450.875), new(700.125, 950.625)]);

        foreach (VideoTimeRange range in timeline.KeptRanges)
        {
            foreach (double source in new[] { range.StartMs, range.StartMs + 1, range.EndMs - 0.01 })
            {
                Assert.Equal(source, timeline.ToSourceTime(timeline.ToOutputTime(source)), 8);
            }
        }

        double seam = 0;
        foreach (VideoTimeRange range in timeline.KeptRanges)
        {
            Assert.Equal(seam, timeline.ToOutputTime(timeline.ToSourceTime(seam)), 8);
            seam += range.DurationMs;
        }

        for (double output = 0; output < timeline.OutputDurationMs; output += 0.625)
        {
            Assert.Equal(output, timeline.ToOutputTime(timeline.ToSourceTime(output)), 8);
        }

        Assert.Equal(timeline.OutputDurationMs, timeline.ToOutputTime(timeline.ToSourceTime(timeline.OutputDurationMs)), 8);
        Assert.Equal(timeline.KeptRanges[^1].EndMs, timeline.ToSourceTime(timeline.OutputDurationMs));
    }

    [Fact]
    public void GetSegmentAt_UsesHalfOpenBoundariesAndAllowsSelectingRemovedRanges()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(100, 1_000, [new(400, 700)], [250]);

        Assert.Equal(new VideoTimeRange(100, 250), timeline.GetSegmentAt(100));
        Assert.Equal(new VideoTimeRange(250, 400), timeline.GetSegmentAt(250));
        Assert.Equal(new VideoTimeRange(400, 700), timeline.GetSegmentAt(400));
        Assert.True(timeline.IsRemoved(400));
        Assert.True(timeline.IsRemoved(699.999));
        Assert.False(timeline.IsRemoved(700));
        Assert.Equal(new VideoTimeRange(700, 1_000), timeline.GetSegmentAt(700));
        Assert.Null(timeline.GetSegmentAt(99));
        Assert.Null(timeline.GetSegmentAt(1_000));
        Assert.Null(timeline.GetSegmentAt(double.NaN));
        Assert.False(timeline.IsRemoved(double.PositiveInfinity));
    }

    [Fact]
    public void Create_NormalizesSplitPointsWithoutCreatingShortKeptSegments()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, null,
            [double.NaN, double.PositiveInfinity, -100, 0, 50, 100, 150, 200, 200, 300, 901, 1_000]);

        Assert.Equal([100d, 200d, 300d], timeline.SplitPoints);
        Assert.Equal(
            [new VideoTimeRange(0, 100), new(100, 200), new(200, 300), new(300, 1_000)],
            timeline.Segments);
    }

    [Fact]
    public void Create_RetainsFeasibleCutEdgeAndInteriorSplitsForRestoration()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, [new(300, 700)],
            [100, 250, 300, 350, 500, 650, 700, 750, 900]);

        Assert.Equal([100d, 300d, 500d, 700d, 900d], timeline.SplitPoints);
        Assert.Equal(
            [new VideoTimeRange(0, 100), new(100, 300), new(700, 900), new(900, 1_000)],
            timeline.Segments);
        Assert.True(timeline.TryRestoreSegment(new(300, 700), out VideoCutTimeline restored));
        Assert.Equal(6, restored.Segments.Count);
        Assert.Equal(timeline.SplitPoints, restored.SplitPoints);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(99.99, false)]
    [InlineData(100, true)]
    [InlineData(500, true)]
    [InlineData(900, true)]
    [InlineData(900.01, false)]
    [InlineData(1_000, false)]
    public void TrySplit_EnforcesTheMinimumOnBothSides(double source, bool expected)
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000);

        Assert.Equal(expected, timeline.TrySplit(source, out VideoCutTimeline changed));
        Assert.Empty(timeline.SplitPoints);
        if (expected)
        {
            Assert.Equal(source, Assert.Single(changed.SplitPoints));
            Assert.Equal(2, changed.Segments.Count);
            Assert.Equal(timeline.OutputDurationMs, changed.OutputDurationMs);
        }
        else
        {
            Assert.Same(timeline, changed);
        }
    }

    [Fact]
    public void TrySplit_AcceptsRepeatedThreeFrameSpansAtThirtyFramesPerSecond()
    {
        const double frameDurationMs = 1_000d / 30;
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000);
        for (int frame = 3; frame < 30; frame += 3)
        {
            Assert.True(timeline.TrySplit(frame * frameDurationMs, out VideoCutTimeline split));
            timeline = split;
        }

        Assert.Equal(9, timeline.SplitPoints.Count);
        Assert.Equal(10, timeline.Segments.Count);
        Assert.True(timeline.SplitPoints[4] > 500);
        Assert.Equal(600, timeline.SplitPoints[5]);
        Assert.All(timeline.Segments, segment => Assert.Equal(100, segment.DurationMs, 8));
        Assert.False(timeline.TrySplit(99.99, out _));
    }

    [Fact]
    public void Create_MinimumSpanRoundoffDoesNotAbsorbKeptSegments()
    {
        const double frameDurationMs = 1_000d / 30;
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000,
        [
            new(100, 9 * frameDurationMs),
            new(12 * frameDurationMs, 15 * frameDurationMs),
            new(18 * frameDurationMs, 27 * frameDurationMs),
        ]);

        Assert.Equal(3, timeline.RemovedRanges.Count);
        Assert.Equal(4, timeline.KeptRanges.Count);
        Assert.All(timeline.KeptRanges, range => Assert.Equal(100, range.DurationMs, 8));
        Assert.Equal(400, timeline.OutputDurationMs, 8);
    }

    [Fact]
    public void TryRemoveSegment_PreservesLastMinimumSpanDespiteFrameGridRoundoff()
    {
        double split = 27 * (1_000d / 30);
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, null, [split]);

        Assert.True(timeline.TryRemoveSegment(new(0, split), out VideoCutTimeline removed));
        Assert.Equal(new VideoTimeRange(split, 1_000), Assert.Single(removed.KeptRanges));
        Assert.Equal(100, removed.OutputDurationMs, 8);
        Assert.False(removed.TryRemoveSegment(new(split, 1_000), out _));
    }

    [Fact]
    public void TrySplit_RejectsRemovedRangesExistingSplitsAndNonfinitePositions()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, [new(400, 600)], [200]);

        Assert.False(timeline.TrySplit(500, out _));
        Assert.False(timeline.TrySplit(200, out _));
        Assert.False(timeline.TrySplit(double.NaN, out _));
        Assert.False(timeline.TrySplit(double.PositiveInfinity, out _));
    }

    [Fact]
    public void SplitRemoveAndRestore_KeepSourceCoordinatesAndPriorSnapshots()
    {
        VideoCutTimeline original = VideoCutTimeline.Create(100, 1_100);
        Assert.True(original.TrySplit(400, out VideoCutTimeline firstSplit));
        Assert.True(firstSplit.TrySplit(700, out VideoCutTimeline secondSplit));
        VideoTimeRange middle = secondSplit.GetSegmentAt(500)!.Value;

        Assert.True(secondSplit.TryRemoveSegment(middle, out VideoCutTimeline removed));
        Assert.Equal(new VideoTimeRange(400, 700), Assert.Single(removed.RemovedRanges));
        Assert.Equal(700, removed.OutputDurationMs);
        Assert.Equal(700, removed.ToSourceTime(300));
        Assert.Equal([400d, 700d], removed.SplitPoints);
        Assert.Equal([new VideoTimeRange(100, 400), new(700, 1_100)], removed.Segments);

        Assert.True(removed.TryRestoreSegment(middle, out VideoCutTimeline restored));
        Assert.Empty(restored.RemovedRanges);
        Assert.Equal(secondSplit.Segments, restored.Segments);
        Assert.Equal(secondSplit.SplitPoints, restored.SplitPoints);
        Assert.Equal(1_000, restored.OutputDurationMs);
        Assert.Empty(original.SplitPoints);
        Assert.Empty(secondSplit.RemovedRanges);
    }

    [Fact]
    public void RemoveAdjacentSegments_MergesCutsAndRestoresTheirSplitPoints()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, null, [200, 400, 600, 800]);

        Assert.True(timeline.TryRemoveSegment(new(200, 400), out VideoCutTimeline firstCut));
        Assert.True(firstCut.TryRemoveSegment(new(400, 600), out VideoCutTimeline merged));
        Assert.Equal(new VideoTimeRange(200, 600), Assert.Single(merged.RemovedRanges));
        Assert.True(merged.TryRestoreSegment(new(200, 600), out VideoCutTimeline restored));
        Assert.Equal(timeline.Segments, restored.Segments);
        Assert.Equal(timeline.SplitPoints, restored.SplitPoints);
    }

    [Fact]
    public void TryRemoveSegment_RejectsUnknownRangesAndTheFinalKeptSegment()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 300, null, [100, 200]);

        Assert.False(timeline.TryRemoveSegment(new(0, 200), out VideoCutTimeline invalid));
        Assert.Same(timeline, invalid);
        Assert.True(timeline.TryRemoveSegment(new(0, 100), out VideoCutTimeline firstCut));
        Assert.True(firstCut.TryRemoveSegment(new(100, 200), out VideoCutTimeline last));
        Assert.Equal(100, last.OutputDurationMs);
        Assert.False(last.TryRemoveSegment(new(200, 300), out VideoCutTimeline unchanged));
        Assert.Same(last, unchanged);
        Assert.False(last.TryRemoveSegment(new(0, 200), out _));
    }

    [Fact]
    public void TryRestoreSegment_OnlyRestoresACompleteCurrentRemovedRange()
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, [new(300, 700)]);

        Assert.False(timeline.TryRestoreSegment(new(300, 500), out VideoCutTimeline unchanged));
        Assert.Same(timeline, unchanged);
        Assert.False(timeline.TryRestoreSegment(new(0, 300), out _));
    }

    [Fact]
    public void Create_DetachesInputListsAndExposesReadOnlyCollections()
    {
        List<VideoTimeRange> cuts = [new(400, 600)];
        List<double> splits = [200];
        VideoCutTimeline timeline = VideoCutTimeline.Create(0, 1_000, cuts, splits);
        cuts.Clear();
        splits.Clear();

        Assert.Single(timeline.RemovedRanges);
        Assert.Single(timeline.SplitPoints);
        Assert.Throws<NotSupportedException>(() => ((IList<VideoTimeRange>)timeline.RemovedRanges).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<double>)timeline.SplitPoints).Clear());
    }

    private static VideoCutTimeline CreateMappingTimeline() => VideoCutTimeline.Create(100, 1_300,
        [new(100, 200), new(400, 600), new(800, 1_000), new(1_200, 1_300)]);
}
