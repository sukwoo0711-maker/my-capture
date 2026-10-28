using System.IO;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class GifFrameRateTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Durations() =>
        from fps in GifExportQuality.FrameRates
        from seconds in new[] { 1, 7, 61 }
        select new object[] { fps, seconds };

    [Theory]
    [MemberData(nameof(Durations))]
    public void CumulativeSchedule_PreservesDurationAndSourceCadence(int fps, int seconds)
    {
        var document = VideoEditDocument.CreateFor(320, 180, seconds * 1000);
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, framesPerSecond: fps);
        Assert.Equal(fps * seconds, schedule.SourceTimesMs.Count);
        Assert.InRange(Math.Abs(schedule.FrameDelaysCentiseconds.Sum() - seconds * 100), 0, 1);
        Assert.All(schedule.FrameDelaysCentiseconds, delay => Assert.True(delay >= 2));
        Assert.Equal(schedule.SourceTimesMs.Count, GifPreviewSampling.EstimateFrameCount(seconds * 1000, fps));
        for (int i = 0; i < schedule.SourceTimesMs.Count; i++)
            Assert.Equal(i * 1000.0 / fps, schedule.SourceTimesMs[i], 6);
    }

    [Theory]
    [InlineData(50, 15, 1, 15)]
    [InlineData(30, 10, 1, 10)]
    [InlineData(50, 60, 2, 25)]
    [InlineData(50, 60, 4, 12.5)]
    [InlineData(30, 0, 1, 10)]
    public void SourceAndPlaybackCeiling_CapSamples(int requested, int source, double speed, double expected)
    {
        Assert.Equal(expected, GifExportQuality.EffectiveFrameRate(requested, source, speed));
        var document = VideoEditDocument.CreateFor(320, 180, 2000);
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed, requested, source);
        Assert.Equal((int)(2 * expected), schedule.SourceTimesMs.Count);
        Assert.Equal((int)(200 / speed), schedule.FrameDelaysCentiseconds.Sum());
        Assert.All(schedule.FrameDelaysCentiseconds, delay => Assert.True(delay >= 2));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void CloseLayerBoundariesAndPartialTail_NeverWriteBrowserClampedDelays(double speed)
    {
        var document = VideoEditDocument.CreateFor(320, 180, 1005);
        document.TextOverlays.Add(new TimedTextOverlay { Text = "short", StartMs = 55, EndMs = 85 });
        foreach (int fps in GifExportQuality.FrameRates)
        {
            GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed, fps);
            Assert.All(schedule.FrameDelaysCentiseconds, delay => Assert.True(delay >= 2));
            Assert.InRange(Math.Abs(schedule.FrameDelaysCentiseconds.Sum() - 100.5 / speed), 0, 1);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Writer_RejectsBrowserClampedDelays(int delay)
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnimatedGifWriter(stream, 2, 2, delay, 0));
        using var writer = new AnimatedGifWriter(stream, 2, 2, 2, 0, leaveOpen: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddFrame(new byte[16], 8, delay));
    }

    [Fact]
    public Task MergedLongHold_DoesNotLeaveOneCentisecondAfterOverflow() => StaThreadTask.RunAsync(() =>
    {
        using var stream = new MemoryStream();
        byte[] pixels = [0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255];
        using (var writer = new AnimatedGifWriter(stream, 2, 2, 2, 0, leaveOpen: true))
        {
            writer.AddFrame(pixels, 8, 65_534);
            writer.AddFrame(pixels, 8, 2);
            writer.Complete();
        }
        stream.Position = 0;
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        int[] delays = decoder.Frames.Select(frame => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery("/grctlext/Delay"))).ToArray();
        Assert.Equal(new[] { 65_534, 2 }, delays);
        Assert.Equal(65_536, delays.Sum());
        return true;
    });

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(50)]
    public void HighRatePreview_RetainsTheExistingSixtySampleBudget(int fps)
    {
        GifSampleWindow window = GifPreviewSampling.Plan(0, 20_000, 1, framesPerSecond: fps);
        Assert.False(window.CoversWholeRange);
        Assert.InRange(window.DurationMs * fps / 1000, 1, 60);
    }

    [Fact]
    public Task OptionChange_CancelsOldEstimateAndUpdatesFramesWithoutOpeningMedia() => StaThreadTask.RunAsync(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var recording = new RecordingResult(Path.Combine(Path.GetTempPath(), "never-opened-gif-fps.mp4"), 7000, 15, 105, 320, 180);
        var document = VideoEditDocument.CreateFor(320, 180, 7000);
        document.TrimInMs = 1000;
        document.TrimOutMs = 4000;
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: true);
        var tokens = new List<CancellationToken>();
        dialog.SampleRendererForTest = async (_, token) =>
        {
            tokens.Add(token);
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("A cancelled render cannot publish a sample.");
        };
        try
        {
            Assert.Empty(tokens); // Construction never decodes or encodes.
            Assert.Equal(30, dialog.DisplayedFrameCount);
            dialog.StartSample();
            Assert.Single(tokens);
            dialog.SelectedFrameRate = 50;
            Assert.True(tokens[0].IsCancellationRequested);
            Assert.Equal(15, dialog.EffectiveFrameRate);
            Assert.Equal(45, dialog.DisplayedFrameCount);
            Assert.Contains("15", dialog.EstimateText, StringComparison.Ordinal);
            Pump(dialog.SampleCompletion);
            Assert.False(dialog.SamplePanel.HasSample);
            dialog.StartSample();
            Assert.Equal(2, tokens.Count);
            dialog.QualityIndex = 2;
            Assert.True(tokens[1].IsCancellationRequested);
            Assert.Equal(50, dialog.SelectedFrameRate);
            Pump(dialog.SampleCompletion);
            Assert.False(dialog.HasMeasuredResult);
        }
        finally { dialog.Close(); }
        return true;
    });

    [Fact]
    public Task ThirtyFpsExport_DecodesThirtyImagesAndOneSecond() => StaThreadTask.RunAsync(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-fps-");
        try
        {
            string source = Path.Combine(root, "source.mp4");
            var options = new VideoEncoderOptions(source, 160, 90, 30, 2_000_000);
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance))
            {
                for (int i = 0; i < 30; i++)
                {
                    var pixels = new byte[160 * 90 * 4];
                    for (int offset = 0; offset < pixels.Length; offset += 4)
                    {
                        pixels[offset + i % 3] = (byte)(64 + i * 6);
                        pixels[offset + 3] = 255;
                    }
                    encoder.WriteFrame(new EncoderFrame(pixels, 160, 90, 640, i * (1000.0 / 30)));
                }
                encoder.Complete();
            }
            var recording = new RecordingResult(source, 1000, 30, 30, 160, 90);
            var document = VideoEditDocument.CreateFor(160, 90, 1000);
            string destination = Path.Combine(root, "30fps.gif");
            Assert.Equal(30, AnimatedGifExporter.Export(recording, document, destination,
                quality: GifExportQuality.Standard with { FramesPerSecond = 30 }));
            GifSampleFrames decoded = GifSampleDecoder.Decode(destination);
            Assert.Equal(30, decoded.Frames.Count);
            Assert.Equal(100, decoded.DelaysCentiseconds.Sum());
            Assert.All(decoded.DelaysCentiseconds, delay => Assert.True(delay >= 2));
            // A deliberately tiny cache must stream the same output without retaining
            // an over-budget encoded set. The reference array fits, but payloads do not.
            using var session = new GifExportSession(recording, document, decodedBudgetBytes: 0, encodedBudgetBytes: 2000);
            string cached = Path.Combine(root, "tiny-cache.gif");
            Assert.Equal(30, session.Export(cached, GifExportQuality.Standard with { FramesPerSecond = 30 }));
            Assert.Equal(0, session.Cache.EncodedBytes);
            Assert.Equal(0, session.Cache.DecodedBytes);
            Assert.Equal(File.ReadAllBytes(destination), File.ReadAllBytes(cached));
            output.WriteLine($"Encoded/decoded PASS: 30 images, 100 cs; {new FileInfo(destination).Length} bytes; {destination}");
            output.WriteLine("Tiny-cache streaming matches the one-shot GIF byte for byte.");
        }
        finally { OwnedTestDirectory.Delete(root); }
        return true;
    });

    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(task.IsCompleted, "Preview cancellation did not finish.");
        task.GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ActualSampleEncoding_MeasuresHighRateEstimateError(bool mixed) => StaThreadTask.RunAsync(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-fps-estimate-");
        try
        {
            RecordingResult recording = GifPreviewFixtureClips.Encode(Path.Combine(root, "source.mp4"),
                mixed ? GifPreviewFixtureClips.Kind.Mixed : GifPreviewFixtureClips.Kind.DocumentScroll,
                640, 360, 30, 10_000);
            var document = VideoEditDocument.CreateFor(640, 360, recording.DurationMs);
            GifExportQuality quality = GifExportQuality.Compact with { FramesPerSecond = 30 };
            GifSampleWindow window = GifPreviewSampling.Plan(document, 1, framesPerSecond: 30);
            using GifPreviewSample sample = GifPreviewSample.Render(recording,
                GifPreviewSampling.CreateSampleDocument(document, window), window, quality, 1, CancellationToken.None);
            GifPresetMeasurement measurement = sample.MeasurementFrom(0);
            foreach (GifSampleWindow segment in GifPreviewSampling.PlanRefinement(document, window))
            {
                measurement = measurement.Including(GifPreviewSample.MeasureSegment(recording,
                    GifPreviewSampling.CreateSampleDocument(document, segment), quality, 1, CancellationToken.None),
                    segment.DurationMs, segment.StartMs == 0);
            }
            GifSizeEstimate estimate = GifSizeEstimator.FromMeasurement(measurement, document.OutputDurationMs);
            string full = Path.Combine(root, "full.gif");
            AnimatedGifExporter.Export(recording, document, full, quality: quality);
            long actual = new FileInfo(full).Length;
            output.WriteLine($"MEASUREMENT: clip={(mixed ? "mixed" : "scroll")}; 640x360; source=30fps; GIF=30fps; sourceMs={recording.DurationMs}; sampledMs={measurement.SourceMs}; expected={estimate.ExpectedBytes}; actual={actual}; error={(estimate.ExpectedBytes / (double)actual - 1) * 100:F2}%; range={estimate.LowBytes}..{estimate.HighBytes}; source={recording.OutputPath}");
            Assert.False(estimate.IsExact);
            Assert.InRange(actual, estimate.LowBytes, estimate.HighBytes);
            output.WriteLine("Estimate range assertion PASS before fixture cleanup.");
        }
        finally { OwnedTestDirectory.Delete(root); }
        return true;
    });
}
