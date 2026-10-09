using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in stop-to-editor latency measurement on a 1080p 30fps synthetic document clip (bright
/// page, scrolling dark text bars, moving rectangle), the same corpus the recording-open
/// baseline was measured with. Timing depends on the decoder and GPU, so it never runs in CI;
/// set <c>MYCAPTURE_RECORDING_OPEN_PERF=1</c> on the validation host.
/// </summary>
public sealed class RecordingOpenLatencyPerformanceTests(ITestOutputHelper output)
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Fps = 30;

    [RecordingOpenPerformanceFact]
    public async Task StopToEditorPath_StaysFlatAsTheClipGetsLonger()
    {
        string root = TestRecycleBin.CreateTempSubdirectory("mycapture-open-latency-").FullName;
        var report = new StringBuilder();
        var probeMedians = new Dictionary<int, double>();
        var completionTimes = new Dictionary<int, double>();
        try
        {
            foreach (int seconds in new[] { 10, 60 })
            {
                string clip = Path.Combine(root, $"document-{seconds}s.mp4");
                long started = Stopwatch.GetTimestamp();
                RecordingResult encoded = SyntheticDocumentClip.Encode(clip, Width, Height, Fps, seconds * Fps);
                double encodeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                if (seconds == 10)
                {
                    // Reference: decode every frame in order, as the pre-fix probe effectively did.
                    started = Stopwatch.GetTimestamp();
                    using var sequential = new MediaFoundationVideoFrameReader(clip)
                    {
                        ForwardSeekPolicy = new ForwardSeekPolicy(double.PositiveInfinity),
                    };
                    _ = sequential.ReadFrameAt(0);
                    _ = sequential.ReadFrameAt(sequential.DurationMs - 1);
                    report.AppendLine(CultureInfo.InvariantCulture, $"sequential decode of all {sequential.DecodedFrameCount} frames: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms");
                }

                var probes = new double[3];
                for (int run = 0; run < probes.Length; run++)
                {
                    started = Stopwatch.GetTimestamp();
                    VideoMediaInfo info = await StaThreadTask.RunAsync(() => VideoFrameRenderPipeline.Probe(clip));
                    probes[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Assert.Equal(Width, info.Width);
                    Assert.InRange(info.DurationMs, (seconds * 1000) - 50, (seconds * 1000) + 50);
                }

                var thumbnails = new double[3];
                for (int run = 0; run < thumbnails.Length; run++)
                {
                    started = Stopwatch.GetTimestamp();
                    BitmapSource thumbnail = await StaThreadTask.RunAsync(
                        () => VideoFrameRenderPipeline.RenderSingleFrame(clip, 100, 640, 360));
                    thumbnails[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Assert.Equal(640, thumbnail.PixelWidth);
                }

                AppPaths paths = AppPaths.CreateForRoot(Path.Combine(root, $"library-{seconds}"));
                var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
                queue.Load();
                var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
                using VideoCaptureWriteSession capture = library.BeginCapture(Fps);
                File.Copy(clip, capture.StagingOutputPath);
                RecordingResult staged = encoded with { OutputPath = capture.StagingOutputPath };
                started = Stopwatch.GetTimestamp();
                var latency = new RecordingOpenLatency(started);
                VideoLibraryItem item = await library.CompleteCaptureAsync(capture, staged, openLatency: latency);
                double completeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.Equal(Width, item.Record.Width);

                Array.Sort(probes);
                Array.Sort(thumbnails);
                probeMedians[seconds] = probes[1];
                completionTimes[seconds] = completeMs;
                report.AppendLine(CultureInfo.InvariantCulture, $"clip={seconds}s frames={encoded.EmittedFrames} encode={encodeMs:0}ms");
                report.AppendLine(CultureInfo.InvariantCulture, $"  probe runs(ms)={string.Join(", ", probes.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))} median={probes[1]:0}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  thumbnail runs(ms)={string.Join(", ", thumbnails.Select(v => v.ToString("0", CultureInfo.InvariantCulture)))} median={thumbnails[1]:0}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  CompleteCaptureAsync={completeMs:0}ms (validate {latency[RecordingOpenStage.Validate]:0}ms, thumbnail {latency[RecordingOpenStage.Thumbnail]:0}ms, publish {latency[RecordingOpenStage.Publish]:0}ms)");
            }

            output.WriteLine(report.ToString());
            string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_RECORDING_OPEN_PERF_OUT");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                File.WriteAllText(evidence, report.ToString());
            }

            // Validation decodes about one key-frame interval at each end, so a six-times longer
            // clip must not take materially longer to validate or publish.
            Assert.True(
                probeMedians[60] <= (probeMedians[10] * 2) + 250,
                $"60 s probe {probeMedians[60]:0}ms grew with clip length (10 s: {probeMedians[10]:0}ms).");
            Assert.True(
                completionTimes[60] <= (completionTimes[10] * 2) + 500,
                $"60 s completion {completionTimes[60]:0}ms grew with clip length (10 s: {completionTimes[10]:0}ms).");
            Assert.True(probeMedians[60] < 1_500, $"60 s probe took {probeMedians[60]:0}ms.");
        }
        finally
        {
            try { TestRecycleBin.DeleteDirectory(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class RecordingOpenPerformanceFactAttribute : FactAttribute
{
    public RecordingOpenPerformanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_RECORDING_OPEN_PERF") != "1")
            Skip = "Opt-in recording-open timing: set MYCAPTURE_RECORDING_OPEN_PERF=1 on the validation host.";
    }
}
