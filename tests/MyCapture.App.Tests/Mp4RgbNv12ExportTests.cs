using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class Mp4RgbNv12ExportTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(320, 180, true)]
    [InlineData(320, 180, false)]
    [InlineData(1280, 720, true)]
    [InlineData(1280, 720, false)]
    public void RgbExport_RawAndComposedFramesKeepDeclaredStudioGreys(
        int width, int height, bool allowHardwareEncoder) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-rgb-nv12-");
        try
        {
            RecordingResult source = EncodePattern(Path.Combine(root, "source.mp4"), width, height, 30);
            string path = Path.Combine(root, "export.mp4");
            var reports = new List<VideoFrameRenderProgress>();
            InspectingEncoder? inspected = null;
            Mp4RenderStatistics stats = TrimReencoder.ReencodeWithStatistics(
                source.OutputPath, path, 0, 1000, source,
                options =>
                {
                    Assert.Equal(VideoEncoderInputFormat.Nv12, options.InputFormat);
                    return inspected = new InspectingEncoder(new MediaFoundationVideoEncoder(
                        options with { AllowHardwareEncoder = allowHardwareEncoder }, NullLogger.Instance));
                },
                NullLogger.Instance, [Caption(500, 1000)],
                progress: new InlineProgress(reports.Add),
                renderOptions: new Mp4RenderOptions { PassThroughNv12 = false });

            Assert.Equal(new Mp4RenderStatistics("rgb-software", 30, 15), stats);
            Assert.NotNull(inspected);
            Assert.Equal(30, inspected.Frames);
            Assert.Equal(Enumerable.Range(1, 30), reports.Select(p => p.CompletedFrames));
            Assert.All(reports, p => Assert.Equal(30, p.TotalFrames));
            using var reader = new MediaFoundationVideoFrameReader(path,
                new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 });
            Assert.Equal(DecodedVideoPixelFormat.Nv12, reader.OutputFormat);
            Assert.Equal(VideoFrameRenderPipeline.DefaultYuvEncoding(height), reader.YuvEncoding);
            foreach (double time in new[] { 0.0, 500.0, 900.0 })
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(time);
                for (int patch = 0; patch < 7; patch++)
                {
                    int x = ((2 * patch) + 1) * width / 16;
                    int y = height / 4;
                    int uv = (frame.Stride * height) + ((y / 2) * frame.Stride) + (x & ~1);
                    byte grey = VideoRangeColourPattern.Patches[patch].R;
                    byte expected = BgraToNv12ConverterTests.Reference(grey, grey, grey, height >= 720).Y;
                    Assert.InRange((int)frame.Pixels[(y * frame.Stride) + x], expected - 1, expected + 1);
                    Assert.Equal(128, frame.Pixels[uv]);
                    Assert.Equal(128, frame.Pixels[uv + 1]);
                    output.WriteLine($"hardware allowed={allowHardwareEncoder}, {width}x{height}, " +
                        $"t={time}, grey={grey}: Y/Cb/Cr={frame.Pixels[(y * frame.Stride) + x]}/" +
                        $"{frame.Pixels[uv]}/{frame.Pixels[uv + 1]}");
                }
            }
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RgbExport_CancellationStopsAfterReportedFrame(bool compose) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-rgb-nv12-cancel-");
        try
        {
            RecordingResult source = EncodePattern(Path.Combine(root, "source.mp4"), 320, 180, 30);
            using var cancellation = new CancellationTokenSource();
            InspectingEncoder? inspected = null;
            int reported = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => TrimReencoder.Reencode(
                source.OutputPath, Path.Combine(root, "cancel.mp4"), 0, 1000, source,
                options => inspected = new InspectingEncoder(new MediaFoundationVideoEncoder(options, NullLogger.Instance)),
                NullLogger.Instance, compose ? [Caption(0, 1000)] : [],
                progress: new InlineProgress(p =>
                {
                    reported = p.CompletedFrames;
                    if (reported == 5) cancellation.Cancel();
                }),
                cancellationToken: cancellation.Token,
                renderOptions: new Mp4RenderOptions { PassThroughNv12 = false }));
            Assert.Equal(5, reported);
            Assert.NotNull(inspected);
            Assert.Equal(5, inspected.Frames);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void RgbExport_FallsBackToRgb32InputWhenTheEncoderRejectsNv12() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-rgb-nv12-fallback-");
        try
        {
            RecordingResult source = EncodePattern(Path.Combine(root, "source.mp4"), 320, 180, 30);
            var formats = new List<VideoEncoderInputFormat>();
            string output = Path.Combine(root, "fallback.mp4");
            Mp4RenderStatistics stats = TrimReencoder.ReencodeWithStatistics(source.OutputPath, output, 0, 1000, source,
                options =>
                {
                    formats.Add(options.InputFormat);
                    if (options.InputFormat == VideoEncoderInputFormat.Nv12)
                        throw new InvalidOperationException("Test encoder rejects NV12 input.");
                    return new MediaFoundationVideoEncoder(options, NullLogger.Instance);
                },
                NullLogger.Instance, renderOptions: new Mp4RenderOptions { PassThroughNv12 = false });
            Assert.Equal([VideoEncoderInputFormat.Nv12, VideoEncoderInputFormat.Bgra32], formats);
            Assert.Equal("rgb-software", stats.FrameSource);
            Assert.Equal(30, stats.Frames);
            Assert.True(new FileInfo(output).Length > 0);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    // Opt-in diagnostic only: no wall-clock gate. Run unchanged before and after the pipeline edit.
    [Fact]
    public void Timing_RgbExport1080p60Frames() => StaTestHost.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_RGB_NV12_TIMING") != "1") return;
        string root = OwnedTestDirectory.Create("mc-rgb-nv12-timing-");
        try
        {
            RecordingResult source = EncodePattern(Path.Combine(root, "source.mp4"), 1920, 1080, 60);
            foreach (bool allowHardware in new[] { true, false })
            {
                foreach (bool compose in new[] { false, true })
                {
                    var times = new List<double>();
                    // One warm-up plus three measured exports, each including finalisation.
                    for (int run = 0; run < 4; run++)
                    {
                        long started = Stopwatch.GetTimestamp();
                        Mp4RenderStatistics stats = TrimReencoder.ReencodeWithStatistics(
                            source.OutputPath, Path.Combine(root, $"export-{allowHardware}-{compose}-{run}.mp4"),
                            0, 2000, source,
                            options => new MediaFoundationVideoEncoder(options with { AllowHardwareEncoder = allowHardware },
                                NullLogger.Instance),
                            NullLogger.Instance, compose ? [Caption(0, 2000)] : [],
                            renderOptions: new Mp4RenderOptions { PassThroughNv12 = false });
                        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        Assert.Equal(new Mp4RenderStatistics("rgb-software", 60, compose ? 60 : 0), stats);
                        if (run > 0) times.Add(elapsed);
                    }
                    output.WriteLine($"1080p, 60 frames, hardware allowed={allowHardware}, composed={compose}: " +
                        $"median={times.Order().ElementAt(1):F1} ms, samples=[{string.Join(", ", times.Select(t => t.ToString("F1")))}]");
                }
            }
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    private static TimedTextOverlay Caption(double start, double end) => new()
    {
        Text = "NV12", StartMs = start, EndMs = end,
        Bounds = new VideoLayerBounds(0.8, 0.8, 0.15, 0.15),
    };

    // The source is encoded directly from an independent NV12 oracle. Using an RGB source
    // encoder here would bake the OS converter's bias into the test before export even starts.
    private static RecordingResult EncodePattern(string path, int width, int height, int frames)
    {
        bool bt709 = height >= 720;
        var colours = VideoRangeColourPattern.Patches.Select(p =>
            BgraToNv12ConverterTests.Reference(p.R, p.G, p.B, bt709)).ToArray();
        byte[] nv12 = new byte[width * height * 3 / 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var colour = colours[(y * 2 / height * 8) + (x * 8 / width)];
                nv12[(y * width) + x] = colour.Y;
                if ((x & 1) == 0 && (y & 1) == 0)
                {
                    int uv = (width * height) + ((y / 2) * width) + x;
                    nv12[uv] = colour.Cb;
                    nv12[uv + 1] = colour.Cr;
                }
            }
        }
        using var encoder = new MediaFoundationVideoEncoder(new VideoEncoderOptions(
            path, width, height, 30, VideoEncoderOptions.DeriveBitrate(width, height, 30))
        {
            InputFormat = VideoEncoderInputFormat.Nv12, AllowHardwareEncoder = false,
        }, NullLogger.Instance);
        for (int i = 0; i < frames; i++)
            encoder.WriteFrame(new EncoderFrame(nv12, width, height, width, i * (1000.0 / 30))
            { Format = VideoEncoderInputFormat.Nv12 });
        encoder.Complete();
        return new RecordingResult(path, frames * (1000.0 / 30), 30, frames, width, height);
    }

    private sealed class InspectingEncoder(IVideoEncoder inner) : IVideoEncoder
    {
        private byte[]? _buffer;
        internal int Frames { get; private set; }
        public int Width => inner.Width;
        public int Height => inner.Height;
        public void WriteFrame(in EncoderFrame frame)
        {
            Assert.Equal(VideoEncoderInputFormat.Nv12, frame.Format);
            Assert.Equal(Width, frame.Stride);
            Assert.Equal(Width * Height * 3 / 2, frame.Pixels.Length);
            if (_buffer is not null) Assert.Same(_buffer, frame.Pixels);
            _buffer = frame.Pixels;
            Assert.Equal(Frames * (1000.0 / 30), frame.TimestampMs);
            Frames++;
            inner.WriteFrame(frame);
        }
        public void Complete() => inner.Complete();
        public void Dispose() => inner.Dispose();
    }

    private sealed class InlineProgress(Action<VideoFrameRenderProgress> report) : IProgress<VideoFrameRenderProgress>
    {
        public void Report(VideoFrameRenderProgress value) => report(value);
    }
}
