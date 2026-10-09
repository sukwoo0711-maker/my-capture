using System.IO;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Always-run accuracy checks of the GIF preview estimate against real exports. The opt-in
/// GifPreviewEstimateEvidenceTests report covers 1080p clips and every preset.
/// </summary>
public sealed class GifPreviewEstimateTests
{
    [Fact]
    public void CombinedSamples_PutTheFullExportInsideTheEstimatedRange() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-estimate-");
        try
        {
            // Still page, scrolling, then a dark code page: the centre sample alone sees only scrolling.
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "mixed.mp4"),
                GifPreviewFixtureClips.Kind.Mixed, 640, 360, 15, 16_000);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            // Ends 4 ms into the centre sample: the sample drops it, the full export keeps it.
            document.TextOverlays.Add(new TimedTextOverlay { Text = "edge", StartMs = 2_000, EndMs = 6_504 });
            document.TextOverlays.Add(new TimedTextOverlay { Text = "inside", StartMs = 7_000, EndMs = 8_000 });
            GifExportQuality quality = GifExportQuality.Compact;
            GifSampleWindow window = GifPreviewSampling.Plan(0, recording.DurationMs, 1);
            Assert.Equal(new GifSampleWindow(6_500, 9_500, false), window);
            VideoEditDocument sampleDocument = GifPreviewSampling.CreateSampleDocument(document, window);

            using GifPreviewSample sample = GifPreviewSample.Render(recording, sampleDocument, window, quality, 1, CancellationToken.None);
            Assert.Null(sample.TakeWholeRangeResult());
            Assert.InRange(sample.Frames.Frames.Count, 30, 40);
            Assert.Equal((640, 360), (sample.Frames.PixelWidth, sample.Frames.PixelHeight));
            GifPresetMeasurement measurement = sample.Measurement;
            foreach (GifSampleWindow segment in GifPreviewSampling.PlanRefinement(0, recording.DurationMs, window))
            {
                measurement = measurement.Including(GifPreviewSample.MeasureSegment(recording,
                    GifPreviewSampling.CreateSampleDocument(document, segment), quality, 1, CancellationToken.None), segment.DurationMs);
            }

            Assert.Equal(5_000, measurement.SourceMs);
            GifSizeEstimate estimate = GifSizeEstimator.FromSample(measurement.Bytes, measurement.SourceMs, recording.DurationMs);
            string full = Path.Combine(root, "full.gif");
            AnimatedGifExporter.Export(recording, document, full, quality: quality);
            long actual = new FileInfo(full).Length;
            Assert.InRange(actual, estimate.LowBytes, estimate.HighBytes);

            // The speed advice says speed barely changes the size; keep that true for this engine.
            long fast = GifPreviewSample.MeasureSegment(recording, sampleDocument, quality, 4, CancellationToken.None);
            Assert.InRange(fast, sample.Bytes * 0.98, sample.Bytes * 1.02);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void WholeRangeSample_IsTheExactSaveableExport() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-whole-sample-");
        try
        {
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "short.mp4"),
                GifPreviewFixtureClips.Kind.DocumentScroll, 320, 180, 10, 4_000);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            GifSampleWindow window = GifPreviewSampling.Plan(0, recording.DurationMs, 1);
            Assert.True(window.CoversWholeRange);

            using GifPreviewSample sample = GifPreviewSample.Render(recording,
                GifPreviewSampling.CreateSampleDocument(document, window), window, GifExportQuality.Standard, 1, CancellationToken.None);
            using VideoExportCalculation result = Assert.IsType<VideoExportCalculation>(sample.TakeWholeRangeResult());
            Assert.Null(sample.TakeWholeRangeResult());

            string full = Path.Combine(root, "full.gif");
            AnimatedGifExporter.Export(recording, document, full, quality: GifExportQuality.Standard);
            Assert.Equal(new FileInfo(full).Length, result.ResultBytes);
            Assert.Equal(sample.Bytes, result.ResultBytes);
            Assert.Equal(40, sample.Frames.Frames.Count);
            Assert.Equal(GifSizeEstimate.Exact(sample.Bytes), GifSizeEstimator.FromSample(sample.Bytes, window.DurationMs, recording.DurationMs));
            string saved = Path.Combine(root, "saved.gif");
            result.SaveCopy(saved, recording.OutputPath, CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(full), File.ReadAllBytes(saved));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
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
