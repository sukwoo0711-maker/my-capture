using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifExportEngineTests
{
    private static readonly GifEngineOptions SoftwareFourThreads = new() { PreferHardwareDecode = false, EncoderThreads = 4 };

    [Fact]
    public void AreaScaler_KeepsFlatColoursAndAveragesCoveredPixels()
    {
        byte[] flat = Solid(7, 5, 10, 200, 90);
        byte[] flatScaled = BgraFrameScaler.Downscale(flat, 7, 5, 28, 3, 2);
        for (int offset = 0; offset < flatScaled.Length; offset += 4)
        {
            Assert.Equal([10, 200, 90, 255], flatScaled[offset..(offset + 4)]);
        }

        // 4x2 -> 2x1: each output pixel is the exact mean of a 2x2 block.
        byte[] blocks =
        [
            0, 0, 0, 0, 100, 100, 100, 0, 10, 20, 30, 0, 30, 40, 50, 0,
            200, 200, 200, 0, 100, 100, 100, 0, 50, 60, 70, 0, 70, 80, 90, 0,
        ];
        byte[] halved = BgraFrameScaler.Downscale(blocks, 4, 2, 16, 2, 1);
        Assert.Equal([100, 100, 100, 255, 40, 50, 60, 255], halved);

        // A 3 -> 2 reduction weights the shared middle pixel by half for both outputs.
        byte[] row = [0, 0, 0, 0, 90, 90, 90, 0, 180, 180, 180, 0];
        byte[] twoThirds = BgraFrameScaler.Downscale(row, 3, 1, 12, 2, 1);
        Assert.Equal([30, 30, 30, 255, 150, 150, 150, 255], twoThirds);

        Assert.All(BgraFrameScaler.Contributions(1920, 640), cell => Assert.Equal(4096, cell.Weights.Sum()));
        Assert.Throws<ArgumentOutOfRangeException>(() => BgraFrameScaler.Downscale(flat, 7, 5, 28, 8, 5));
    }

    [Fact]
    public void BypassedFrame_EncodesIdenticallyToComposedFrame() => RunSta(() =>
    {
        const int width = 64;
        const int height = 36;
        byte[] noise = Noise(width, height, seed: 7, alpha: 0);
        var request = new VideoFrameRenderRequest("unused.mp4", 0, 1000, 10, width, height, width, height, [], []);
        var layers = new Dictionary<Guid, BitmapSource>();
        Assert.True(VideoFrameRenderPipeline.CanBypassComposition(request, layers, width, height, 500));

        var compositor = new VideoFrameCompositor(width, height);
        BitmapSource composed = compositor.Compose(request, layers, noise, width, height, width * 4, 500);
        byte[] composedPixels = Pixels(composed);
        for (int offset = 0; offset < noise.Length; offset += 4)
        {
            Assert.Equal(noise[offset..(offset + 3)], composedPixels[offset..(offset + 3)]);
            Assert.Equal(255, composedPixels[offset + 3]);
        }

        VideoFrameCompositor? unused = null;
        byte[] bypassed = GifExportEngine.EncodeOutputFrame(
            new GifSourceFrame(noise, width, height, width * 4), request, layers, 500, ref unused);
        Assert.Null(unused);
        Assert.Equal(AnimatedGifWriter.EncodeFrame(composed).ToArray(), bypassed);
    });

    [Fact]
    public void LayerFrame_ComposesExactlyLikeTheExistingRenderer() => RunSta(() =>
    {
        const int width = 160;
        const int height = 90;
        byte[] noise = Noise(width, height, seed: 3, alpha: 0);
        var overlay = new TimedTextOverlay { Text = "LAYER", StartMs = 0, EndMs = 1000, Placement = VideoTextPlacement.Bottom };
        var layer = new FrameEditLayer { StartMs = 0, EndMs = 1000, OverlayPngBase64 = RedSquarePng(40, 40), Bounds = new(0.5, 0.1, 0.25, 0.25) };
        var request = new VideoFrameRenderRequest("unused.mp4", 0, 1000, 10, width, height, 320, 180, [overlay], [layer]);
        IReadOnlyDictionary<Guid, BitmapSource> bitmaps = FrameEditLayerRenderer.Decode([layer]);
        Assert.False(VideoFrameRenderPipeline.CanBypassComposition(request, bitmaps, width, height, 500));
        Assert.True(VideoFrameRenderPipeline.CanBypassComposition(request, bitmaps, width, height, 1000));

        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, noise, width * 4);
        BitmapSource expected = VideoFrameRenderPipeline.RenderFrame(
            source, width, height, 320, 180, [overlay], [layer], bitmaps, 500);
        var compositor = new VideoFrameCompositor(width, height);
        BitmapSource actual = compositor.Compose(request, bitmaps, noise, width, height, width * 4, 500);
        Assert.Equal(Pixels(expected), Pixels(actual));

        VideoFrameCompositor? workerCompositor = null;
        byte[] encoded = GifExportEngine.EncodeOutputFrame(
            new GifSourceFrame(noise, width, height, width * 4), request, bitmaps, 500, ref workerCompositor);
        Assert.NotNull(workerCompositor);
        Assert.Equal(AnimatedGifWriter.EncodeFrame(expected).ToArray(), encoded);
    });

    [Theory]
    [InlineData(960, 1.0)]
    [InlineData(480, 2.0)]
    public void ParallelEngine_MatchesTheSequentialPipelineByteForByte(int preset, double speed) => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mycapture-gif-engine-");
        try
        {
            RecordingResult recording = EncodeMovingClip(Path.Combine(root, "source.mp4"), 320, 180, 10, 12);
            VideoEditDocument document = EditedDocument(recording);
            GifExportQuality quality = preset == 960 ? GifExportQuality.Standard : GifExportQuality.Smallest;

            // With the original software RGB converter the only differences left are parallel
            // encoding, per-frame composition bypass and worker composition: none may change a byte.
            byte[] legacy = LegacySequentialGif(recording, document, quality, speed);
            foreach (int threads in new[] { 1, 4 })
            {
                string path = Path.Combine(root, $"engine-{threads}.gif");
                int frames = AnimatedGifExporter.ExportCore(
                    recording, document, path, null, default, speed, quality, null,
                    new GifEngineOptions { PreferHardwareDecode = false, DecodeNv12 = false, EncoderThreads = threads },
                    NullLogger.Instance, out GifRenderStatistics statistics);
                Assert.Equal("software", statistics.FrameSource);
                Assert.Equal(threads, statistics.EncoderThreads);
                Assert.Equal(legacy, File.ReadAllBytes(path));
                Assert.Equal(DecodeFrameCount(path), frames);
            }

            // The production default — GPU decode when available, NV12 read back and converted
            // on the workers — reproduces the original bytes as well.
            string production = Path.Combine(root, "production.gif");
            _ = AnimatedGifExporter.Export(recording, document, production, playbackSpeed: speed, quality: quality);
            Assert.Equal(legacy, File.ReadAllBytes(production));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Nv12Engine_GivesIdenticalGifsForHardwareAndSoftwareDecode() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mycapture-gif-nv12-");
        try
        {
            RecordingResult recording = EncodeMovingClip(Path.Combine(root, "source.mp4"), 1280, 720, 30, 15);
            VideoEditDocument document = EditedDocument(recording);
            string hardware = Path.Combine(root, "hardware.gif");
            string software = Path.Combine(root, "software.gif");
            _ = AnimatedGifExporter.ExportCore(
                recording, document, hardware, null, default, 1.0, GifExportQuality.Compact, null,
                GifEngineOptions.Default, NullLogger.Instance, out GifRenderStatistics hardwareRun);
            _ = AnimatedGifExporter.ExportCore(
                recording, document, software, null, default, 1.0, GifExportQuality.Compact, null,
                SoftwareFourThreads, NullLogger.Instance, out GifRenderStatistics softwareRun);
            Assert.Contains(hardwareRun.FrameSource, new[] { "hardware", "software" });
            Assert.Equal("software", softwareRun.FrameSource);
            Assert.Equal(File.ReadAllBytes(software), File.ReadAllBytes(hardware));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(320, 180)]
    [InlineData(1280, 720)]
    public void Nv12Converter_MatchesTheSoftwareColourConverter(int width, int height)
    {
        string root = OwnedTestDirectory.Create("mycapture-nv12-colour-");
        try
        {
            string source = Path.Combine(root, "source.mp4");
            _ = EncodeMovingClip(source, width, height, 10, 6);
            using var rgb = new MediaFoundationVideoFrameReader(source);
            using var software = new MediaFoundationVideoFrameReader(
                source, new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 });
            using var hardware = new MediaFoundationVideoFrameReader(
                source,
                new MediaFoundationVideoFrameReaderOptions { PreferHardwareDecode = true, OutputFormat = DecodedVideoPixelFormat.Nv12 });
            Assert.Equal(DecodedVideoPixelFormat.Nv12, software.OutputFormat);
            Assert.Equal(height >= 720, software.YuvEncoding.Bt709);
            for (int index = 0; index < 6; index++)
            {
                DecodedVideoFrame expected = rgb.ReadFrameAt(index * 100.0);
                DecodedVideoFrame nv12 = software.ReadFrameAt(index * 100.0);
                DecodedVideoFrame gpu = hardware.ReadFrameAt(index * 100.0);
                Assert.Equal(DecodedVideoPixelFormat.Nv12, nv12.Format);
                Assert.Equal((width, height, width), (nv12.Width, nv12.Height, nv12.Stride));
                Assert.Equal((width * height) + (width * height / 2), nv12.Pixels.Length);

                // H.264 decoding is bit-exact, so GPU and CPU decode agree byte for byte.
                Assert.Equal(nv12.Pixels, gpu.Pixels);
                // The integer converter reproduces the Media Foundation software converter exactly.
                byte[] converted = Nv12FrameConverter.ToBgra(nv12.Pixels, width, height, software.YuvEncoding, width, height);
                double difference = MeanAbsoluteDifference(expected.Pixels, converted);
                Assert.True(difference == 0, $"NV12 conversion drifted from the system converter by {difference:F4} at frame {index}.");

                // The fused 2x path equals full-size conversion followed by a 2x area average.
                byte[] halved = Nv12FrameConverter.ToBgra(nv12.Pixels, width, height, software.YuvEncoding, width / 2, height / 2);
                Assert.Equal(BgraFrameScaler.Downscale(converted, width, height, width * 4, width / 2, height / 2), halved);
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Nv12Converter_UsesStudioRangeMatricesAndAveragesWhenHalving()
    {
        Assert.Equal(new Nv12FrameConverter.Coefficients(16, 298, 459, 55, 136, 541), Nv12FrameConverter.Coefficients.For(new VideoYuvEncoding(true, false)));
        Assert.Equal(new Nv12FrameConverter.Coefficients(16, 298, 409, 100, 208, 516), Nv12FrameConverter.Coefficients.For(new VideoYuvEncoding(false, false)));

        // Studio-range black, white and BT.709 / BT.601 primaries.
        Assert.Equal([0, 0, 0, 255], Convert1x1(16, 128, 128, bt709: true));
        Assert.Equal([255, 255, 255, 255], Convert1x1(235, 128, 128, bt709: true));
        AssertNear([0, 0, 255], Convert1x1(63, 102, 240, bt709: true));
        AssertNear([0, 0, 255], Convert1x1(81, 90, 240, bt709: false));
        AssertNear([255, 0, 0], Convert1x1(41, 240, 110, bt709: false));

        // A 4x2 frame halves to 2x1: the luma of each 2x2 block is averaged with its chroma pair.
        byte[] nv12 = [16, 16, 235, 235, 16, 16, 235, 235, 128, 128, 128, 128];
        byte[] halved = Nv12FrameConverter.ToBgra(nv12, 4, 2, new VideoYuvEncoding(true, false), 2, 1);
        Assert.Equal([0, 0, 0, 255, 255, 255, 255, 255], halved);
        byte[] mixed = [16, 235, 16, 235, 235, 16, 235, 16, 128, 128, 128, 128];
        byte[] grey = Nv12FrameConverter.ToBgra(mixed, 4, 2, new VideoYuvEncoding(false, false), 2, 1);
        Assert.All(grey.Where((_, index) => index % 4 != 3), channel => Assert.InRange(channel, 127, 128));

        static byte[] Convert1x1(byte y, byte u, byte v, bool bt709) =>
            Nv12FrameConverter.ToBgra([y, y, y, y, u, v], 2, 2, new VideoYuvEncoding(bt709, false), 2, 2)[..4];

        static void AssertNear(byte[] expectedBgr, byte[] actual)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.InRange(actual[channel], Math.Max(0, expectedBgr[channel] - 2), Math.Min(255, expectedBgr[channel] + 2));
            }
        }
    }

    [Fact]
    public void Session_ReusesFramesAcrossPresetsAndMatchesOneShotExports() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mycapture-gif-session-");
        try
        {
            RecordingResult recording = EncodeMovingClip(Path.Combine(root, "source.mp4"), 1280, 720, 30, 30);
            VideoEditDocument document = EditedDocument(recording);
            using var session = new GifExportSession(recording, document);
            var steps = new (GifExportQuality Quality, double Speed, string ExpectedSource)[]
            {
                (GifExportQuality.Standard, 1.0, "decode"),
                (GifExportQuality.Compact, 1.0, "cache-decoded"),
                (GifExportQuality.Smallest, 1.0, "cache-decoded"),
                (GifExportQuality.Standard, 2.0, "cache-encoded"),
                (GifExportQuality.Standard, 1.0, "cache-encoded"),
            };

            for (int step = 0; step < steps.Length; step++)
            {
                (GifExportQuality quality, double speed, string expected) = steps[step];
                string viaSession = Path.Combine(root, $"session-{step}.gif");
                string oneShot = Path.Combine(root, $"oneshot-{step}.gif");
                int frames = session.Export(viaSession, quality, speed);
                GifRenderStatistics statistics = session.LastStatistics!;
                if (expected == "decode")
                {
                    Assert.Contains(statistics.FrameSource, new[] { "hardware", "software" });
                    Assert.True(statistics.DecodedFrames > 0);
                }
                else
                {
                    Assert.Equal(expected, statistics.FrameSource);
                    Assert.Equal(0, statistics.DecodedFrames);
                }

                _ = AnimatedGifExporter.Export(recording, document, oneShot, playbackSpeed: speed, quality: quality);
                Assert.Equal(File.ReadAllBytes(oneShot), File.ReadAllBytes(viaSession));
                GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(
                    document.NormalizeFor(recording.Width, recording.Height, recording.DurationMs), speed, quality.FramesPerSecond);
                Assert.Equal(schedule.SourceTimesMs.Count, frames);
                (int width, int height) = AnimatedGifExporter.FitWithin(recording.Width, recording.Height, quality.LongEdge);
                AssertGifShape(viaSession, width, height, schedule.FrameDelaysCentiseconds);
            }

            Assert.True(session.Cache.DecodedBytes > 0);
            Assert.True(session.Cache.EncodedBytes > 0);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Session_DropsCacheWhenSourceChangesAndRespectsBudgets() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mycapture-gif-session-");
        try
        {
            string source = Path.Combine(root, "source.mp4");
            RecordingResult recording = EncodeMovingClip(source, 640, 360, 10, 10);
            VideoEditDocument document = VideoEditDocument.CreateFor(640, 360, recording.DurationMs);

            using (var session = new GifExportSession(recording, document))
            {
                _ = session.Export(Path.Combine(root, "a.gif"));
                _ = session.Export(Path.Combine(root, "b.gif"), GifExportQuality.Smallest);
                Assert.Equal("cache-decoded", session.LastStatistics!.FrameSource);

                File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddMinutes(1));
                _ = session.Export(Path.Combine(root, "c.gif"), GifExportQuality.Smallest);
                Assert.True(session.LastStatistics!.DecodedFrames > 0, "A rewritten source must not reuse cached frames.");
            }

            using (var tiny = new GifExportSession(recording, document, decodedBudgetBytes: 1))
            {
                _ = tiny.Export(Path.Combine(root, "d.gif"));
                Assert.Equal(0, tiny.Cache.DecodedBytes);

                // A 640x360 source gives Standard and Compact the same 640x360 10 fps output,
                // so the encoded tier serves Compact without decoding.
                _ = tiny.Export(Path.Combine(root, "e.gif"), GifExportQuality.Compact);
                Assert.Equal("cache-encoded", tiny.LastStatistics!.FrameSource);

                _ = tiny.Export(Path.Combine(root, "f.gif"), GifExportQuality.Smallest);
                Assert.True(tiny.LastStatistics!.DecodedFrames > 0, "Frames over the decoded budget are decoded again.");
                _ = tiny.Export(Path.Combine(root, "g.gif"), GifExportQuality.Smallest, playbackSpeed: 2.0);
                Assert.Equal("cache-encoded", tiny.LastStatistics!.FrameSource);
            }

            using (var noEncoded = new GifExportSession(recording, document, encodedBudgetBytes: 0))
            {
                _ = noEncoded.Export(Path.Combine(root, "h1.gif"));
                _ = noEncoded.Export(Path.Combine(root, "h2.gif"), playbackSpeed: 2.0);
                Assert.Equal("cache-decoded", noEncoded.LastStatistics!.FrameSource);
                Assert.Equal(0, noEncoded.Cache.EncodedBytes);
            }

            var closed = new GifExportSession(recording, document);
            closed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => closed.Export(Path.Combine(root, "i.gif")));
            Assert.Equal(0, closed.Cache.DecodedBytes);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Cancellation_StopsWorkersAndLeavesNoPartialFile() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mycapture-gif-cancel-");
        try
        {
            RecordingResult recording = EncodeMovingClip(Path.Combine(root, "source.mp4"), 640, 360, 10, 20);
            VideoEditDocument document = VideoEditDocument.CreateFor(640, 360, recording.DurationMs);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(report =>
            {
                if (report.CompletedFrames == 3)
                {
                    cancellation.Cancel();
                }
            });
            string destination = Path.Combine(root, "cancelled.gif");
            Assert.ThrowsAny<OperationCanceledException>(() => AnimatedGifExporter.ExportCore(
                recording, document, destination, progress, cancellation.Token, 1.0, null, null,
                SoftwareFourThreads, NullLogger.Instance, out _));
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(0, GifExportEngine.LiveWorkerThreads);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void RawTrimRender_BypassesOnlyTextFreeFramesWithoutChangingPixels() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mycapture-raw-bypass-");
        try
        {
            RecordingResult recording = EncodeMovingClip(Path.Combine(root, "source.mp4"), 160, 90, 10, 10);
            var overlay = new TimedTextOverlay { Text = "TEXT", StartMs = 200, EndMs = 500, Placement = VideoTextPlacement.Center };
            var request = new VideoFrameRenderRequest(recording.OutputPath, 0, 1000, 10, 160, 90, 160, 90, [overlay], []);
            double[] times = Enumerable.Range(0, 10).Select(index => index * 100.0).ToArray();

            var composedOnly = new byte[10][];
            _ = VideoFrameRenderPipeline.RenderScheduled(request, times, (frame, index) => composedOnly[index] = Pixels(frame));

            var mixed = new byte[10][];
            var raw = new bool[10];
            _ = VideoFrameRenderPipeline.RenderScheduled(
                request,
                times,
                (frame, index) => mixed[index] = Pixels(frame),
                acceptRawFrame: (decoded, index) =>
                {
                    raw[index] = true;
                    mixed[index] = (byte[])decoded.Pixels.Clone();
                });

            Assert.Equal([true, true, false, false, false, true, true, true, true, true], raw);
            for (int index = 0; index < 10; index++)
            {
                for (int offset = 0; offset < mixed[index].Length; offset += 4)
                {
                    Assert.Equal(composedOnly[index][offset..(offset + 3)], mixed[index][offset..(offset + 3)]);
                }
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void HardwareReader_MatchesSoftwareColoursOrFallsBackWithReason()
    {
        string root = OwnedTestDirectory.Create("mycapture-hw-reader-");
        try
        {
            string source = Path.Combine(root, "source.mp4");
            _ = EncodeMovingClip(source, 320, 180, 10, 10);
            using var software = new MediaFoundationVideoFrameReader(source);
            using var hardware = new MediaFoundationVideoFrameReader(
                source,
                new MediaFoundationVideoFrameReaderOptions { PreferHardwareDecode = true });
            Assert.False(software.IsHardwareAccelerated);
            Assert.Null(software.HardwareFallbackReason);
            Assert.True(hardware.IsHardwareAccelerated || hardware.HardwareFallbackReason is not null);
            Assert.Equal((software.Width, software.Height), (hardware.Width, hardware.Height));
            for (int index = 0; index < 10; index++)
            {
                DecodedVideoFrame expected = software.ReadFrameAt(index * 100.0);
                DecodedVideoFrame actual = hardware.ReadFrameAt(index * 100.0);
                Assert.Equal(expected.TimestampMs, actual.TimestampMs, 3);
                Assert.Equal((expected.Width, expected.Height, expected.Stride), (actual.Width, actual.Height, actual.Stride));
                Assert.True(
                    MeanAbsoluteDifference(expected.Pixels, actual.Pixels) < 1.5,
                    $"Hardware decode colours drifted at frame {index}.");
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void HardwareReader_GpuResizeFillsTheWholeFrameWithoutShift()
    {
        string root = OwnedTestDirectory.Create("mycapture-hw-resize-");
        try
        {
            string source = Path.Combine(root, "source.mp4");
            _ = EncodeMovingClip(source, 1280, 720, 10, 5);
            using var software = new MediaFoundationVideoFrameReader(source);
            using var resized = new MediaFoundationVideoFrameReader(
                source,
                new MediaFoundationVideoFrameReaderOptions { PreferHardwareDecode = true, OutputWidth = 640, OutputHeight = 360 });
            DecodedVideoFrame full = software.ReadFrameAt(200);
            DecodedVideoFrame small = resized.ReadFrameAt(200);
            if (!resized.IsHardwareAccelerated)
            {
                Assert.NotNull(resized.HardwareFallbackReason);
                Assert.Equal((1280, 720), (small.Width, small.Height));
                return;
            }

            Assert.Equal((640, 360, 640 * 4), (small.Width, small.Height, small.Stride));
            byte[] reference = BgraFrameScaler.Downscale(full.Pixels, full.Width, full.Height, full.Stride, 640, 360);
            Assert.True(MeanAbsoluteDifference(reference, small.Pixels) < 8, "GPU resize did not match the source geometry.");
            for (int row = 0; row < 360; row += 359)
            {
                // A letterbox or a stretched coded height would darken or shift the edge rows.
                double edge = MeanAbsoluteDifference(
                    reference.AsSpan(row * 2560, 2560).ToArray(),
                    small.Pixels.AsSpan(row * 2560, 2560).ToArray());
                Assert.True(edge < 12, $"Row {row} differs by {edge:F1} after the GPU resize.");
            }
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void OutputPixelAspect_MapsTheDisplayRectangleOntoTheWholeFrame()
    {
        Assert.Equal((1UL << 32) | 1, MediaFoundationVideoFrameReader.OutputPixelAspect(1, 1, 1920, 1080, 960, 540));
        Assert.Equal((2049UL << 32) | 2048, MediaFoundationVideoFrameReader.OutputPixelAspect(1, 1, 1366, 768, 960, 540));
        Assert.Equal((2UL << 32) | 1, MediaFoundationVideoFrameReader.OutputPixelAspect(2, 1, 640, 360, 320, 180));
        Assert.Equal(44, MediaFoundationD3D11DeviceManager.NativeTextureDescriptionSize);
        Assert.Equal(16, MediaFoundationD3D11DeviceManager.NativeMappedSubresourceSize);
        Assert.True(GifExportEngine.IsOrderedSubset([0, 200, 400], [0, 100, 200, 300, 400]));
        Assert.False(GifExportEngine.IsOrderedSubset([0, 250], [0, 100, 200, 300]));
    }

    internal static byte[] LegacySequentialGif(
        RecordingResult recording,
        VideoEditDocument document,
        GifExportQuality quality,
        double speed)
    {
        VideoEditDocument normalized = document.NormalizeFor(recording.Width, recording.Height, recording.DurationMs);
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(normalized, speed, quality.FramesPerSecond);
        (int width, int height) = AnimatedGifExporter.FitWithin(recording.Width, recording.Height, quality.LongEdge);
        using var stream = new MemoryStream();
        using (var writer = new AnimatedGifWriter(stream, width, height, 10, 0, leaveOpen: true))
        {
            _ = VideoFrameRenderPipeline.RenderScheduled(
                new VideoFrameRenderRequest(
                    recording.OutputPath,
                    normalized.TrimInMs,
                    normalized.TrimOutMs,
                    quality.FramesPerSecond,
                    width,
                    height,
                    recording.Width,
                    recording.Height,
                    schedule.QuantizedTextOverlays,
                    schedule.QuantizedFrameEditLayers),
                schedule.SourceTimesMs,
                (frame, index) => writer.AddFrame(frame, schedule.FrameDelaysCentiseconds[index]));
            writer.Complete();
        }

        return stream.ToArray();
    }

    private static VideoEditDocument EditedDocument(RecordingResult recording)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        document.TextOverlays.Add(new TimedTextOverlay
        {
            Text = "GIF ENGINE",
            StartMs = 230,
            EndMs = 610,
            Placement = VideoTextPlacement.Bottom,
        });
        document.FrameEditLayers.Add(new FrameEditLayer
        {
            StartMs = 400,
            EndMs = 800,
            OverlayPngBase64 = RedSquarePng(40, 40),
            Bounds = new(0.55, 0.1, 0.25, 0.25),
        });
        return document;
    }

    private static RecordingResult EncodeMovingClip(string path, int width, int height, int fps, int frames)
    {
        var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            for (int frame = 0; frame < frames; frame++)
            {
                GifExportEngineBenchmarkTests.PaintDocumentFrame(pixels, width, height, frame * 4);
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, frame * 1000.0 / fps));
            }

            encoder.Complete();
        }

        return new RecordingResult(path, frames * 1000.0 / fps, fps, frames, width, height);
    }

    private static void AssertGifShape(string path, int width, int height, IReadOnlyList<int> delays)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(width, bytes[6] | (bytes[7] << 8));
        Assert.Equal(height, bytes[8] | (bytes[9] << 8));
        var found = new List<int>();
        for (int index = 13; index + 7 < bytes.Length; index++)
        {
            // Graphic Control Extensions written by AnimatedGifWriter: 21 F9 04 <packed> <delay>.
            if (bytes[index] == 0x21 && bytes[index + 1] == 0xF9 && bytes[index + 2] == 0x04
                && bytes[index + 7] == 0x00 && (bytes[index + 3] & 0x1C) == 0x04)
            {
                found.Add(bytes[index + 4] | (bytes[index + 5] << 8));
            }
        }

        Assert.Equal(delays, found);
        Assert.Equal(delays.Count, DecodeFrameCount(path));
    }

    private static int DecodeFrameCount(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new GifBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames.Count;
    }

    private static double MeanAbsoluteDifference(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        long sum = 0;
        long count = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                sum += Math.Abs(expected[offset + channel] - actual[offset + channel]);
                count++;
            }
        }

        return sum / (double)count;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
        }

        return pixels;
    }

    private static byte[] Noise(int width, int height, int seed, byte alpha)
    {
        var random = new Random(seed);
        byte[] pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (int offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = alpha;
        }

        return pixels;
    }

    private static string RedSquarePng(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 8; y < height - 8; y++)
        {
            for (int x = 8; x < width - 8; x++)
            {
                int offset = ((y * width) + x) * 4;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromMinutes(3));
        Assert.False(thread.IsAlive, "STA test timed out");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("STA body threw: " + failure);
        }
    }

    /// <summary>Reports synchronously on the export thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress(Action<VideoFrameRenderProgress> report) : IProgress<VideoFrameRenderProgress>
    {
        public void Report(VideoFrameRenderProgress value) => report(value);
    }
}
