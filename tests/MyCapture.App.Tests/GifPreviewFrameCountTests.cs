using System.IO;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class GifPreviewFrameCountTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> ScheduleCases()
    {
        (string Name, double Span, int TenFps, int FiveFps)[] cases =
        [
            ("cadence", 2_000, 20, 10),
            ("partial", 2_150, 22, 11),
            ("round-down", 2_004, 20, 10),
            ("round-up", 2_005, 21, 11),
            ("off-grid", 2_000, 22, 12),
            ("on-grid", 2_000, 20, 10),
            ("ten-fps-grid", 2_000, 20, 12),
            ("cross-start", 2_000, 21, 11),
            ("cross-end", 2_000, 21, 11),
            ("range-edges", 2_000, 20, 10),
            ("outside", 2_000, 20, 10),
            ("image-edge", 2_000, 22, 12),
            ("cut-seam", 2_000, 21, 11),
        ];
        foreach (var item in cases)
            foreach (int fps in new[] { 10, 5 })
                foreach (double speed in new[] { 0.5, 2.0 })
                    yield return [item.Name, item.Span, fps, speed, fps == 10 ? item.TenFps : item.FiveFps];
    }

    // Parity holds for documents the exporter schedules. A layer that rounds to zero centiseconds
    // is rejected by the schedule instead (SampleDocument_KeepsLayersInsideAndDropsEdgeSliversThatQuantizationWouldReject).
    [Theory]
    [MemberData(nameof(ScheduleCases))]
    public void ScheduledCount_MatchesExporterAndLiteralAcrossRangeLayerAndCutEdges(
        string name, double span, int fps, double speed, int expected)
    {
        var document = VideoEditDocument.CreateFor(320, 180, span + 2_000);
        document.TrimInMs = 1_000;
        document.TrimOutMs = 1_000 + span;
        switch (name)
        {
            case "off-grid": AddText(1_234, 1_789); break;
            case "on-grid": AddText(400, 1_600); break;
            case "ten-fps-grid": AddText(500, 1_500); break;
            case "cross-start": AddText(-500, 234); break;
            case "cross-end": AddText(1_789, 2_500); break;
            case "range-edges": AddText(0, 2_000); break;
            case "outside": AddText(-500, -100); AddText(2_100, 2_500); break;
            case "image-edge":
                document.FrameEditLayers.Add(new FrameEditLayer { StartMs = 1_350, EndMs = 1_789, OverlayPngBase64 = "AA==" });
                break;
            case "cut-seam":
                document.TrimOutMs += 500;
                document.RemovedRanges.Add(new VideoTimeRange(1_734, 2_234));
                // A layer wholly removed by the cut adds no frame; crossing it shares the seam.
                AddText(800, 1_100);
                AddText(0, 1_234);
                break;
        }

        Assert.Equal(span, document.OutputDurationMs);
        int scheduled = AnimatedGifExporter.BuildFrameSchedule(document, speed, fps).SourceTimesMs.Count;
        int estimated = GifPreviewSampling.EstimateFrameCount(span, fps, GifPreviewSampling.LayerIntervals(document));
        Assert.Equal(expected, scheduled);
        Assert.Equal(expected, estimated);
        Assert.Equal(scheduled, estimated);

        void AddText(double start, double end) => document.TextOverlays.Add(new TimedTextOverlay
        {
            Text = "edge", StartMs = 1_000 + start, EndMs = 1_000 + end,
        });
    }

    [Fact]
    public void FrameCount_UsesOnlyTheRequestedPresetsWholeRangeImageMeasurement()
    {
        GifExportQuality quality = GifExportQuality.Standard;
        GifPresetMeasurement whole = GifPresetMeasurement.Of(quality,
            new GifSegmentSize(1_000, 200, 600, 0.5) { Images = 7 }, 2_000, 0, startsRange: true);
        var measurements = new Dictionary<GifExportQuality, GifPresetMeasurement> { [quality] = whole };
        Assert.Equal(7, GifPreviewSampling.FrameCountFor(2_000, quality, measurements));
        Assert.Equal(7, GifPreviewSampling.FrameCountFor(2_000.5, quality, measurements));
        Assert.Equal(21, GifPreviewSampling.FrameCountFor(2_005, quality, measurements));
        Assert.Equal(20, GifPreviewSampling.FrameCountFor(2_000, GifExportQuality.Compact, measurements));

        GifMeasuredSegment segment = whole.Segments[0];
        GifPresetMeasurement[] incomplete =
        [
            new(quality, 1_000, 2_000, 0),
            whole with { Quality = GifExportQuality.Compact },
            whole with { SourceMs = 1_000 },
            whole with { Segments = [segment with { StartsRange = false }] },
            whole with { Segments = [segment with { SourceMs = 1_000 }] },
            whole with { Segments = [segment with { Size = segment.Size with { Images = 0 } }] },
            whole with { Segments = [segment with { SourceMs = 1_000 }, segment with { SourceMs = 1_000, StartsRange = false }] },
        ];
        foreach (GifPresetMeasurement measurement in incomplete)
        {
            measurements[quality] = measurement;
            Assert.Equal(20, GifPreviewSampling.FrameCountFor(2_000, quality, measurements));
        }

        measurements.Clear();
        Assert.Equal(22, GifPreviewSampling.FrameCountFor(2_000, quality, measurements, [(1_234, 1_789)]));
    }

    [Theory]
    [InlineData(2_000, 8)]
    [InlineData(121_000, null)]
    public void Recommendation_UsesTheSameMeasuredCountForDestinationAndSafetyLimits(double span, int? maximumFrames)
    {
        GifExportQuality quality = GifExportQuality.Standard;
        GifPresetMeasurement measurement = GifPresetMeasurement.Of(quality,
            new GifSegmentSize(1_000, 200, 600, 0.5) { Images = 7 }, span, 0, startsRange: true);
        var measurements = new Dictionary<GifExportQuality, GifPresetMeasurement> { [quality] = measurement };
        var target = new GifShareTarget(null, maximumFrames, "test");
        GifRecommendation recommendation = GifSharePolicy.Recommend(span, 320, 180, measurements, quality, 1, target, 0);

        Assert.Equal(7, GifPreviewSampling.FrameCountFor(span, quality, measurements));
        Assert.Equal(GifRecommendationKind.CurrentFits, recommendation.Kind);
        Assert.Equal(quality, recommendation.Quality);
        Assert.Equal(GifLimitReason.None, recommendation.Reason);
        measurements[quality] = measurement with { Segments = [] };
        Assert.NotEqual(GifRecommendationKind.CurrentFits,
            GifSharePolicy.Recommend(span, 320, 180, measurements, quality, 1, target, 0).Kind);
    }

    [Theory]
    [InlineData(2_000, 10, false, 1, 20)]
    [InlineData(2_150, 10, false, 1, 22)]
    [InlineData(2_150, 10, true, 2, 24)]
    [InlineData(2_000, 5, false, 1, 10)]
    [InlineData(2_150, 5, false, 1, 11)]
    [InlineData(2_150, 5, true, 2, 13)]
    public void WholeRange_CountsTheImagesWrittenAfterIdenticalFramesMerge(
        double span, int fps, bool layer, double speed, int expectedScheduled) => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-count-");
        try
        {
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "clip.mp4"),
                GifPreviewFixtureClips.Kind.StillStretch, 320, 180, 20, span);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            if (layer)
                document.TextOverlays.Add(new TimedTextOverlay { Text = "edge", StartMs = 1_234, EndMs = 1_789 });
            GifExportQuality quality = fps == 5 ? GifSharePolicy.SharpLowRate : GifExportQuality.Standard;
            GifSampleWindow window = GifPreviewSampling.Plan(document, speed);
            Assert.True(window.CoversWholeRange);
            using GifPreviewSample sample = GifPreviewSample.Render(recording,
                GifPreviewSampling.CreateSampleDocument(document, window), window, quality, speed, CancellationToken.None);
            GifPresetMeasurement measurement = sample.MeasurementFrom(document.TrimInMs);
            Assert.True(GifSizeEstimator.FromMeasurement(measurement, span).IsExact);

            string full = Path.Combine(root, "full.gif");
            using var session = new GifExportSession(recording, document);
            int exported = session.Export(full, quality, speed);
            GifRenderStatistics statistics = Assert.IsType<GifRenderStatistics>(session.LastStatistics);
            GifSampleFrames decoded = GifSampleDecoder.Decode(full, CancellationToken.None);
            int scheduled = AnimatedGifExporter.BuildFrameSchedule(document, speed, fps).SourceTimesMs.Count;
            List<(double StartMs, double EndMs)> layers = GifPreviewSampling.LayerIntervals(document);
            int estimated = GifPreviewSampling.EstimateFrameCount(span, fps, layers);
            int images = decoded.Frames.Count;
            var measurements = new Dictionary<GifExportQuality, GifPresetMeasurement> { [quality] = measurement };
            int displayed = GifPreviewSampling.FrameCountFor(span, quality, measurements, layers);
            output.WriteLine($"span={span}, fps={fps}, layer={layer}, speed={speed}: scheduled={scheduled}, old estimate={estimated}, statistics.Images={statistics.Images}, preview={sample.Frames.Frames.Count}, displayed={displayed}, exported={images}");
            Assert.Equal(expectedScheduled, scheduled);
            Assert.Equal(scheduled, estimated);
            Assert.Equal(scheduled, exported);
            Assert.Equal(scheduled, statistics.Frames);
            Assert.Equal(images, statistics.Images);
            Assert.Equal(images, sample.Frames.Frames.Count);
            Assert.Equal(images, sample.Size.Images);
            Assert.Equal(images, displayed);
            Assert.True(images < scheduled, $"The still stretch must merge frames: {images} images, {scheduled} scheduled.");
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "STA test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException("STA body threw: " + failure);
    }
}
