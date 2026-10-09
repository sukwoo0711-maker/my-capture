using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in measurement behind <see cref="GifEstimateCalibration.Default"/> and the motion
/// classes. Set MYCAPTURE_GIF_ESTIMATE_EVIDENCE to an output directory; the report compares
/// the dialog's samples (centred visual sample plus start and end segments) with full exports
/// of 1080p30 synthetic recordings. Re-run it whenever the GIF engine changes.
/// </summary>
public sealed class GifPreviewEstimateEvidenceTests
{
    private const string EvidenceVariable = "MYCAPTURE_GIF_ESTIMATE_EVIDENCE";

    [Fact]
    public void GifPreviewEstimate_EvidenceReport()
    {
        string? requested = Environment.GetEnvironmentVariable(EvidenceVariable);
        if (string.IsNullOrWhiteSpace(requested)) return;
        string output = DiagnosticOutputPaths.Create(requested);
        GifEstimateCalibration calibration = GifEstimateCalibration.Default;
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"MyCapture GIF preview estimate evidence · {DateTimeOffset.Now:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Calibration: {calibration}");
        var failures = new List<string>();
        string work = OwnedTestDirectory.Create("mc-gif-estimate-evidence-");
        try
        {
            RunSta(() =>
            {
                (GifPreviewFixtureClips.Kind Kind, double DurationMs)[] clips =
                [
                    (GifPreviewFixtureClips.Kind.DocumentScroll, 10_000),
                    (GifPreviewFixtureClips.Kind.Mixed, 20_000),
                    (GifPreviewFixtureClips.Kind.Photo, 12_000),
                    (GifPreviewFixtureClips.Kind.StillPage, 15_000),
                ];
                foreach ((GifPreviewFixtureClips.Kind kind, double duration) in clips)
                {
                    MeasureClip(work, kind, duration, calibration, report, failures);
                }
            });
        }
        finally { OwnedTestDirectory.Delete(work); }
        report.AppendLine(failures.Count == 0 ? "\nRESULT: PASS (every combined estimate inside its range)" : "\nRESULT: OUTSIDE " + string.Join(", ", failures));
        File.WriteAllText(DiagnosticOutputPaths.Child(output, "gif-estimate-evidence.txt"), report.ToString());
        Assert.Empty(failures);
    }

    private static void MeasureClip(string work, GifPreviewFixtureClips.Kind kind, double duration, GifEstimateCalibration calibration,
        StringBuilder report, List<string> failures)
    {
        string source = Path.Combine(work, $"{kind}.mp4");
        var encode = Stopwatch.StartNew();
        RecordingResult recording = GifPreviewFixtureClips.Encode(source, kind, 1920, 1080, 30, duration);
        report.AppendLine(CultureInfo.InvariantCulture, $"\n## {kind}: 1920x1080 30fps {duration / 1000:0.#}s, encoded in {encode.ElapsedMilliseconds} ms, {new FileInfo(source).Length:N0} bytes");
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        var fullBytes = new Dictionary<GifExportQuality, long>();
        foreach (GifExportQuality quality in GifSharePolicy.Presets)
        {
            (long full, double fullMs, _) = Export(work, recording, document, quality, 1, decode: false);
            fullBytes[quality] = full;
            GifSampleWindow window = GifPreviewSampling.Plan(document.TrimInMs, document.TrimOutMs, 1);
            (long sample, double sampleMs, double motion) = Export(work, recording, GifPreviewSampling.CreateSampleDocument(document, window), quality, 1, decode: true);
            var measurement = new GifPresetMeasurement(quality, sample, window.DurationMs, sampleMs / window.DurationMs);
            GifSizeEstimate centre = GifSizeEstimator.FromSample(sample, window.DurationMs, duration, calibration);
            double segmentsMs = 0;
            foreach (GifSampleWindow segment in GifPreviewSampling.PlanRefinement(document.TrimInMs, document.TrimOutMs, window))
            {
                (long bytes, double ms, _) = Export(work, recording, GifPreviewSampling.CreateSampleDocument(document, segment), quality, 1, decode: false);
                measurement = measurement.Including(bytes, segment.DurationMs);
                segmentsMs += ms;
            }

            GifSizeEstimate combined = GifSizeEstimator.FromSample(measurement.Bytes, measurement.SourceMs, duration, calibration);
            double calculation = GifSizeEstimator.EstimateCalculationMs(measurement, duration);
            bool inside = full >= combined.LowBytes && full <= combined.HighBytes;
            int frames = GifPreviewSampling.EstimateFrameCount(duration, quality.FramesPerSecond);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{quality.LongEdge}px/{quality.FramesPerSecond}fps: full {full:N0} B in {fullMs:0} ms ({frames} frames); centre {window.StartMs:0}-{window.EndMs:0}ms {sample:N0} B in {sampleMs:0} ms, motion {motion:P1} ({GifSharePolicy.Classify(motion)}), actual/expected {(double)full / centre.ExpectedBytes:0.000}; "
                + $"+segments in {segmentsMs:0} ms: estimate {combined.LowBytes:N0}..{combined.ExpectedBytes:N0}..{combined.HighBytes:N0}, actual/expected {(double)full / combined.ExpectedBytes:0.000}; calc estimate {calculation / fullMs:0.00}x; {(inside ? "INSIDE" : "OUTSIDE")}");
            if (!inside) failures.Add($"{kind} {quality.LongEdge}/{quality.FramesPerSecond}");
            if (quality == GifExportQuality.Standard)
                report.AppendLine(CultureInfo.InvariantCulture, $"  centre luma threshold sweep {LastThresholdSweep}");
            if (quality == GifExportQuality.Standard)
            {
                foreach ((string label, double start) in new[] { ("start", 0d), ("end", duration - window.DurationMs) })
                {
                    var edge = new GifSampleWindow(start, start + window.DurationMs, false);
                    (long edgeBytes, _, double edgeMotion) = Export(work, recording, GifPreviewSampling.CreateSampleDocument(document, edge), quality, 1, decode: true);
                    GifSizeEstimate edgeEstimate = GifSizeEstimator.FromSample(edgeBytes, edge.DurationMs, duration, calibration);
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"  {label} 3 s sample {edge.StartMs:0}-{edge.EndMs:0}ms alone: actual/expected {(double)full / edgeEstimate.ExpectedBytes:0.000}, motion {edgeMotion:P1} ({GifSharePolicy.Classify(edgeMotion)}); luma threshold sweep {LastThresholdSweep}");
                }

                (long fast, double fastMs, _) = Export(work, recording, GifPreviewSampling.CreateSampleDocument(document, window), quality, 4, decode: false);
                report.AppendLine(CultureInfo.InvariantCulture, $"  centre sample at 4x speed: {fast:N0} B in {fastMs:0} ms (1x {sample:N0} B)");
            }
        }

        foreach (GifExportQuality from in GifSharePolicy.Presets)
        {
            foreach (GifExportQuality to in GifSharePolicy.Presets.Where(to => to != from))
            {
                double predicted = fullBytes[from] * GifSizeEstimator.ScaleFactor(from, to, recording.Width, recording.Height, calibration);
                (int fw, int fh) = AnimatedGifExporter.FitWithin(recording.Width, recording.Height, from.LongEdge);
                (int tw, int th) = AnimatedGifExporter.FitWithin(recording.Width, recording.Height, to.LongEdge);
                double pixelRatio = (double)tw * th / (fw * fh);
                double cadence = (double)to.FramesPerSecond / from.FramesPerSecond;
                string exponent = Math.Abs(pixelRatio - 1) < 1e-9 ? "n/a (same size)"
                    : (Math.Log(fullBytes[to] / (fullBytes[from] * cadence)) / Math.Log(pixelRatio)).ToString("0.000", CultureInfo.InvariantCulture);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"scale {from.LongEdge}/{from.FramesPerSecond}->{to.LongEdge}/{to.FramesPerSecond}: actual/predicted {fullBytes[to] / predicted:0.000}, implied exponent {exponent}");
            }
        }
    }

    [ThreadStatic]
    private static string? LastThresholdSweep;

    private static (long Bytes, double ElapsedMs, double Motion) Export(string work, RecordingResult recording, VideoEditDocument document,
        GifExportQuality quality, double speed, bool decode)
    {
        string path = Path.Combine(work, $"gif-{Guid.NewGuid():N}.gif");
        var stopwatch = Stopwatch.StartNew();
        AnimatedGifExporter.Export(recording, document, path, playbackSpeed: speed, quality: quality);
        double elapsed = stopwatch.Elapsed.TotalMilliseconds;
        long bytes = new FileInfo(path).Length;
        double motion = 0;
        if (decode)
        {
            GifSampleFrames frames = GifSampleDecoder.Decode(path);
            motion = GifMotionAnalysis.ChangedFraction(frames);
            if (quality == GifExportQuality.Standard)
            {
                LastThresholdSweep = string.Join(", ", new[] { 2d, 3, 4, 6, 8, 12 }.Select(threshold =>
                    string.Create(CultureInfo.InvariantCulture, $"{threshold}:{GifMotionAnalysis.ChangedFraction(frames, threshold):P1}")));
            }
        }
        return (bytes, elapsed, motion); // The owned work directory is recycled at the end.
    }

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
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException("STA body threw: " + failure);
    }
}
