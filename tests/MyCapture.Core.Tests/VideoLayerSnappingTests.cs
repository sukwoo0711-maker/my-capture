using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class VideoLayerSnappingTests
{
    [Fact]
    public void IndexedAndStreamingCandidatesPreserveTheSameStableTies()
    {
        VideoSnapCandidate[] candidates =
        [new(1070, VideoSnapKind.KeptStart), new(1040, VideoSnapKind.Playhead), new(960, VideoSnapKind.LayerEdge)];
        var viewport = new TimelineViewport(10000, 30);
        VideoLayerSnapResult indexed = VideoLayerSnapping.Apply(1000, 2000, 0, 10000, VideoLayerDrag.TrimStart,
            viewport, 1000, candidates);
        VideoLayerSnapResult streaming = VideoLayerSnapping.Apply(1000, 2000, 0, 10000, VideoLayerDrag.TrimStart,
            viewport, 1000, candidates.Where(static _ => true));
        Assert.Equal(indexed, streaming);
        Assert.Equal(candidates[1], indexed.Snap);
    }

    [Fact]
    public void IndexedCandidatesDoNotAllocateOnRepeatedDragUpdates()
    {
        var candidates = new List<VideoSnapCandidate>
        {
            new(3000, VideoSnapKind.Playhead), new(0, VideoSnapKind.KeptStart), new(10000, VideoSnapKind.KeptEnd),
        };
        var viewport = new TimelineViewport(10000, 30);
        for (int i = 0; i < 100; i++) Apply();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 400; i++) Apply();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Apply() => VideoLayerSnapping.Apply(1000, 2000, 990, 10000, VideoLayerDrag.Move,
            viewport, 400, candidates);
    }

    [Theory]
    [InlineData(VideoSnapKind.Playhead)]
    [InlineData(VideoSnapKind.KeptStart)]
    [InlineData(VideoSnapKind.KeptEnd)]
    [InlineData(VideoSnapKind.LayerEdge)]
    public void MoveSnapsEitherEdgeAndPreservesLength(VideoSnapKind kind)
    {
        var viewport = new TimelineViewport(10000, 30);
        var candidate = new VideoSnapCandidate(3050, kind);
        var result = VideoLayerSnapping.Apply(1000, 2000, 1000, 10000,
            VideoLayerDrag.Move, viewport, 1000, [candidate]);
        Assert.Equal(2050, result.StartMs);
        Assert.Equal(3050, result.EndMs);
        Assert.Equal(candidate, result.Snap);
    }

    [Theory]
    [InlineData(VideoLayerDrag.TrimStart, 1050, 2000)]
    [InlineData(VideoLayerDrag.TrimEnd, 1000, 2050)]
    public void TrimmingKeepsTheOppositeEdge(VideoLayerDrag drag, double start, double end)
    {
        double target = drag == VideoLayerDrag.TrimStart ? start : end;
        var result = VideoLayerSnapping.Apply(1000, 2000, 0, 10000, drag,
            new TimelineViewport(10000, 30), 1000, [new(target, VideoSnapKind.LayerEdge)]);
        Assert.Equal(start, result.StartMs);
        Assert.Equal(end, result.EndMs);
        Assert.NotNull(result.Snap);
    }

    [Fact]
    public void ThresholdIsEightDipAtTheCurrentZoomAndAltBypassesSnapping()
    {
        var viewport = new TimelineViewport(10000, 30);
        viewport.SetView(1000, 3000);
        VideoLayerSnapResult Snap(double target, bool disabled = false) => VideoLayerSnapping.Apply(
            1500, 2000, 0, 10000, VideoLayerDrag.TrimStart, viewport, 1000,
            [new(target, VideoSnapKind.Playhead)], disabled);
        Assert.Equal(1516, Snap(1516).StartMs); // 16 ms / 2 ms per DIP = 8 DIP.
        Assert.Null(Snap(1516.01).Snap);
        Assert.Equal(1500, Snap(1516, disabled: true).StartMs);
    }

    [Fact]
    public void ClosestCandidateWinsAndTiesAreStable()
    {
        var result = VideoLayerSnapping.Apply(1000, 2000, 0, 10000, VideoLayerDrag.TrimStart,
            new TimelineViewport(10000, 30), 1000,
            [new(1070, VideoSnapKind.KeptStart), new(1040, VideoSnapKind.Playhead), new(960, VideoSnapKind.LayerEdge)]);
        Assert.Equal(new VideoSnapCandidate(1040, VideoSnapKind.Playhead), result.Snap);
    }

    [Fact]
    public void OffscreenCandidatesAndInvalidRangesCannotAttractAnEdge()
    {
        var viewport = new TimelineViewport(10000, 30);
        viewport.SetView(1000, 3000);
        var result = VideoLayerSnapping.Apply(1000, 2000, 0, 10000, VideoLayerDrag.TrimStart,
            viewport, 1000, [new(990, VideoSnapKind.LayerEdge), new(double.NaN, VideoSnapKind.Playhead)]);
        Assert.Null(result.Snap);
        result = VideoLayerSnapping.Apply(1000, 2000, 995, 10000, VideoLayerDrag.TrimStart,
            viewport, 1000, [new(2000, VideoSnapKind.LayerEdge)]);
        Assert.Equal(1990, result.StartMs);
        Assert.Null(result.Snap);
    }

    [Fact]
    public void FractionalFrameMoveToClipEndRemainsValidForSubsequentGestures()
    {
        const double duration = 1000;
        double frame = duration / 30;
        var viewport = new TimelineViewport(duration, 30);
        var moved = VideoLayerSnapping.Apply(7 * frame, 10 * frame, duration, duration,
            VideoLayerDrag.Move, viewport, 1000, [], disabled: true);
        Assert.Equal(duration, moved.EndMs);
        Assert.Equal(3 * frame, moved.EndMs - moved.StartMs, precision: 10);
        Assert.Null(moved.Snap);

        foreach (VideoLayerDrag drag in Enum.GetValues<VideoLayerDrag>())
        {
            var next = VideoLayerSnapping.Apply(moved.StartMs, moved.EndMs, -frame, duration,
                drag, viewport, 1000, [], disabled: true);
            Assert.InRange(next.StartMs, 0, next.EndMs);
            Assert.InRange(next.EndMs, next.StartMs, duration);
        }
    }

    [Fact]
    public void MovingAtClipBoundsRejectsSnapsThatWouldChangeDuration()
    {
        var result = VideoLayerSnapping.Apply(1000, 2000, -5000, 10000, VideoLayerDrag.Move,
            new TimelineViewport(10000, 30), 1000, [new(950, VideoSnapKind.LayerEdge)]);
        Assert.Equal(0, result.StartMs);
        Assert.Equal(1000, result.EndMs);
        Assert.Null(result.Snap);
    }
}
