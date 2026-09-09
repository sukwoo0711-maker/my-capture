using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoCompositionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportDialogDiscardsCancelledOrOutdatedCalculationAndCanRetry(bool close) => RunSta(() =>
    {
        string root = NewRoot();
        VideoExportDialog? dialog = null;
        try
        {
            string source = Path.Combine(root, "dialog-source.mp4");
            RecordingResult recording = EncodeIndexedClip(source, 320, 180, 10, IndexedColors);
            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dialog = new VideoExportDialog(recording, VideoEditDocument.CreateFor(320, 180, recording.DurationMs), NullLoggerFactory.Instance, false);
            Assert.False(dialog.CanExport);
            Task pending = dialog.CalculateAsync();
            Assert.True(dialog.CalculateAsync().IsCompleted, "A second request must not start another calculation");
            if (close) dialog.Close();
            else dialog.TargetReductionPercent = 50;
            PumpTask(pending);
            Assert.False(dialog.CanExport, "Cancelled or stale output must not become saveable");
            if (!close)
            {
                PumpTask(dialog.CalculateAsync());
                Assert.True(dialog.CanExport, "The current settings must be calculable after cancellation");
                dialog.TargetReductionPercent = 75;
                Assert.False(dialog.CanExport, "Changing settings invalidates the previous measured result immediately");
            }
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally { dialog?.Close(); DeleteRoot(root); }
    });

    private static void PumpTask(Task task)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(20) };
        timeout.Tick += (_, _) => frame.Continue = false;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.True(task.IsCompleted, "The video export did not drain within 20 seconds");
        task.GetAwaiter().GetResult();
    }

    [Fact]
    public void PercentageExport_EncodesSameEditsAtRequestedBitrateAndPreservesSource() => RunSta(() =>
    {
        string root = NewRoot();
        string source = Path.Combine(root, "percentage-source.mp4");
        try
        {
            RecordingResult recording = EncodeIndexedClip(source, 320, 180, 10, IndexedColors);
            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
            var document = VideoEditDocument.CreateFor(320, 180, 1000);
            document.TrimInMs = 200;
            document.TrimOutMs = 800;
            document.TextOverlays.Add(new TimedTextOverlay { Text = "TEXT", StartMs = 200, EndMs = 800, Bounds = new(0.1, 0.6, 0.8, 0.3) });
            document.FrameEditLayers.Add(new FrameEditLayer { StartMs = 200, EndMs = 800, OverlayPngBase64 = CreateRedOverlayPng(40, 40), Bounds = new(0.5, 0.1, 0.25, 0.25) });
            var requests = new List<VideoEncoderOptions>();
            using var calculation = VideoExportCalculation.Calculate(75, 600, 1_000_000, (path, bitrate, baseline, token) =>
            {
                int frames = TrimReencoder.Reencode(source, path, 200, 800, recording,
                    options => { requests.Add(options); return new MediaFoundationVideoEncoder(options, NullLogger.Instance); },
                    NullLogger.Instance, document.TextOverlays, document.FrameEditLayers, cancellationToken: token, bitrateBitsPerSecond: bitrate);
                Assert.Equal(6, frames);
                var info = VideoFrameRenderPipeline.Probe(path);
                Assert.Equal(320, info.Width);
                Assert.Equal(180, info.Height);
                Assert.InRange(info.DurationMs, 599, 601);
                BitmapSource frame = VideoFrameRenderPipeline.RenderSingleFrame(path, 200, 320, 180);
                Assert.True(CountBrightPixels(frame) > 40, "Caption missing from MP4 pass");
                Assert.True(CountRedPixels(new CroppedBitmap(frame, new System.Windows.Int32Rect(160, 18, 80, 45))) > 80,
                    "Spatial layer missing from MP4 pass");
            }, CancellationToken.None);
            Assert.Equal(2, requests.Count);
            Assert.Equal(1_000_000, requests[0].BitrateBitsPerSecond);
            Assert.InRange(requests[1].BitrateBitsPerSecond, 64_000, 999_999);
            Assert.All(requests, request => { Assert.Equal(320, request.Width); Assert.Equal(180, request.Height); Assert.Equal(10, request.Fps); });
            Assert.True(calculation.ResultBytes <= calculation.BaselineBytes);
            Assert.Equal(calculation.ResultBytes, new FileInfo(calculation.ResultPath).Length);
            calculation.SaveCopy(Path.Combine(root, "smaller.mp4"), source, CancellationToken.None);
            using var gif = VideoExportCalculation.CalculateGif(path => AnimatedGifExporter.Export(recording, document, path), CancellationToken.None);
            using var stream = File.OpenRead(gif.ResultPath);
            var decoded = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Contains(decoded.Frames, frame => CountBrightPixels(frame) > 40 && CountRedPixels(frame) > 80);
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally { DeleteRoot(root); }
    });

    [Fact]
    public void ReopenedSpatialLayers_AppearInMp4AndGifAtSavedPosition() => RunSta(() =>
    {
        string root = NewRoot();
        string source = Path.Combine(root, "source.mp4");
        string mp4 = Path.Combine(root, "spatial.mp4");
        string gif = Path.Combine(root, "spatial.gif");
        try
        {
            RecordingResult recording = EncodeBlackClip(source, 160, 90, 10, 10);
            var document = VideoEditDocument.CreateFor(160, 90, 1000);
            document.TextOverlays.Add(new TimedTextOverlay { Text = "TEXT", StartMs = 200, EndMs = 800, Bounds = new(0.1, 0.6, 0.8, 0.3) });
            document.FrameEditLayers.Add(new FrameEditLayer { StartMs = 200, EndMs = 800, OverlayPngBase64 = CreateRedOverlayPng(40, 40), Bounds = new(0.5, 0.1, 0.25, 0.25) });
            document = System.Text.Json.JsonSerializer.Deserialize<VideoEditDocument>(System.Text.Json.JsonSerializer.Serialize(document))!.NormalizeFor(160, 90, 1000);
            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
            _ = TrimReencoder.Reencode(source, mp4, 0, 1000, recording,
                options => new MediaFoundationVideoEncoder(options, NullLogger.Instance), NullLogger.Instance,
                document.TextOverlays, document.FrameEditLayers);
            BitmapSource active = VideoFrameRenderPipeline.RenderSingleFrame(mp4, 400, 160, 90);
            Assert.True(CountBrightPixels(active) > 40);
            Assert.True(CountRedPixels(active) > 80);
            Assert.True(CountRedPixels(new CroppedBitmap(active, new System.Windows.Int32Rect(80, 9, 40, 24))) > 80,
                "MP4 graphic did not retain its saved position");
            Assert.Equal(0, CountRedPixels(VideoFrameRenderPipeline.RenderSingleFrame(mp4, 900, 160, 90)));
            _ = AnimatedGifExporter.Export(recording, document, gif);
            using var stream = File.OpenRead(gif);
            var decoded = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Contains(decoded.Frames, frame => CountBrightPixels(frame) > 40 && CountRedPixels(frame) > 80);
            Assert.Contains(decoded.Frames, frame => CountRedPixels(new CroppedBitmap(frame, new System.Windows.Int32Rect(80, 9, 40, 24))) > 80);
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
        }
        finally { DeleteRoot(root); }
    });

    private static readonly (byte B, byte G, byte R)[] IndexedColors =
    [
        (32, 32, 192),
        (32, 192, 32),
        (192, 32, 32),
        (32, 192, 192),
        (192, 32, 192),
        (192, 192, 32),
        (40, 40, 40),
        (150, 150, 150),
        (32, 96, 220),
        (150, 48, 90),
    ];

    [Fact]
    public void RemovingAllPriorEdits_RemainsACommitWorthyDocumentChange()
    {
        VideoEditDocument initial = VideoEditDocument.CreateFor(320, 180, 1000);
        initial.TextOverlays.Add(new TimedTextOverlay
        {
            Text = "remove me",
            StartMs = 100,
            EndMs = 800,
        });
        VideoEditDocument cleared = VideoEditDocument.CreateFor(320, 180, 1000);

        Assert.True(VideoEditorWindow.DocumentsEquivalent(initial, initial.Clone()));
        Assert.False(VideoEditorWindow.DocumentsEquivalent(initial, cleared));
    }

    [Fact]
    public void MediaFoundationSourceReader_ReturnsTimestampedIndexedFramesWithoutDuplicates()
    {
        const int width = 160;
        const int height = 90;
        const int fps = 10;
        string root = NewRoot();
        string source = Path.Combine(root, "indexed-source.mp4");
        try
        {
            _ = EncodeIndexedClip(source, width, height, fps, IndexedColors);
            using var reader = new MediaFoundationVideoFrameReader(source);

            Assert.Equal(width, reader.Width);
            Assert.Equal(height, reader.Height);
            for (int index = 0; index < IndexedColors.Length; index++)
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(index * 100.0);
                Assert.InRange(frame.TimestampMs, (index * 100.0) - 0.1, (index * 100.0) + 0.1);
                Assert.InRange(frame.SampleDurationMs, 99.9, 100.1);
                Assert.InRange(frame.EndTimestampMs, ((index + 1) * 100.0) - 0.1, ((index + 1) * 100.0) + 0.1);
                Assert.Equal(index, NearestIndexedColor(frame.Pixels, frame.Stride, width / 2, height / 2));
            }

            VideoMediaInfo info = RunStaWithResult(() => VideoFrameRenderPipeline.Probe(source));
            Assert.Equal(width, info.Width);
            Assert.Equal(height, info.Height);
            Assert.InRange(info.DurationMs, 999.9, 1000.1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ProbeTailValidation_RejectsAnOldSampleReturnedForTheAdvertisedEnd()
    {
        var pixels = new byte[4];
        var stale = new DecodedVideoFrame(pixels, 1, 1, 4, TimestampMs: 300, SampleDurationMs: 100);
        var final = new DecodedVideoFrame(pixels, 1, 1, 4, TimestampMs: 900, SampleDurationMs: 100);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(
            () => VideoFrameRenderPipeline.ValidateDecodedTail(1000, stale));
        Assert.Contains("ends at", failure.Message, StringComparison.Ordinal);
        VideoFrameRenderPipeline.ValidateDecodedTail(1000, final);
    }

    [Fact]
    public void MediaFoundationInteropLayouts_MatchX64NativeAbi()
    {
        Assert.Equal(24, MediaFoundationVideoFrameReader.NativePropVariantSize);
        (int size, int fractionOffset, int valueOffset) =
            MediaFoundationVideoFrameReader.NativeVideoOffsetLayout;
        Assert.Equal(4, size);
        Assert.Equal(0, fractionOffset);
        Assert.Equal(2, valueOffset);
    }

    [Fact]
    public void PositiveStartTimeline_HoldsFirstFrameUntilItsPresentationTimestamp()
    {
        var pixels = new byte[4];
        var frames = new Queue<DecodedVideoFrame>(
        [
            new DecodedVideoFrame(pixels, 1, 1, 4, TimestampMs: 100, SampleDurationMs: 33),
            new DecodedVideoFrame(pixels, 1, 1, 4, TimestampMs: 133, SampleDurationMs: 33),
            new DecodedVideoFrame(pixels, 1, 1, 4, TimestampMs: 166, SampleDurationMs: 33),
        ]);
        int reads = 0;
        var selector = new DecodedVideoFrameSelector();
        DecodedVideoFrame? ReadNext()
        {
            reads++;
            return frames.TryDequeue(out DecodedVideoFrame? frame) ? frame : null;
        }

        Assert.Equal(100, selector.Select(0, ReadNext).TimestampMs);
        Assert.Equal(100, selector.Select(33, ReadNext).TimestampMs);
        Assert.Equal(100, selector.Select(66, ReadNext).TimestampMs);
        Assert.Equal(1, reads);
        Assert.Equal(100, selector.Select(100, ReadNext).TimestampMs);
        Assert.Equal(2, reads);
        Assert.Equal(133, selector.Select(140, ReadNext).TimestampMs);
    }

    [Fact]
    public void MissingSampleDuration_UsesFollowingPtsForVariableFrameRate()
    {
        double duration = MediaFoundationVideoFrameReader.ResolveMissingSampleDuration(
            timestampMs: 100,
            nextTimestampMs: 250,
            containerDurationMs: 1000,
            previousResolvedDurationMs: 100);
        double finalDuration = MediaFoundationVideoFrameReader.ResolveMissingSampleDuration(
            timestampMs: 900,
            nextTimestampMs: null,
            containerDurationMs: 1000,
            previousResolvedDurationMs: 150);

        Assert.Equal(150, duration);
        Assert.Equal(100, finalDuration);
    }

    [Fact]
    public void TimedText_IsBurnedIntoRenderedMp4_WithoutChangingSource() => RunSta(() =>
    {
        const int width = 320;
        const int height = 180;
        const int fps = 10;
        const int frames = 10;
        string root = NewRoot();
        string source = Path.Combine(root, "source.mp4");
        string rendered = Path.Combine(root, "rendered.mp4");
        try
        {
            RecordingResult recording = EncodeBlackClip(source, width, height, fps, frames);
            // codeql[cs/path-injection] -- isolated GUID test workspace
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source));
            var overlay = new TimedTextOverlay
            {
                Text = "MyCapture TEXT",
                StartMs = 100,
                EndMs = 800,
                Placement = VideoTextPlacement.Center,
            };

            int emitted = TrimReencoder.Reencode(
                source,
                rendered,
                0,
                1000,
                recording,
                options => new MediaFoundationVideoEncoder(options, NullLogger.Instance),
                NullLogger.Instance,
                [overlay]);

            Assert.Equal(frames, emitted);
            Assert.True(new FileInfo(rendered).Length > 1_024);
            // codeql[cs/path-injection] -- isolated GUID test workspace
            Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(source)));

            BitmapSource frame = VideoFrameRenderPipeline.RenderSingleFrame(
                rendered,
                sourceTimeMs: 400,
                width,
                height);
            Assert.True(
                CountBrightPixels(frame) > 150,
                "the rendered MP4 frame did not contain the expected burned-in white text");
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void AnimatedGifExporter_ProducesPlayableMultiFrameGifWithTimedText() => RunSta(() =>
    {
        const int width = 320;
        const int height = 180;
        const int fps = 10;
        const int frames = 10;
        string root = NewRoot();
        string source = Path.Combine(root, "source.mp4");
        string gif = Path.Combine(root, "result.gif");
        try
        {
            RecordingResult recording = EncodeIndexedClip(source, width, height, fps, IndexedColors);
            VideoEditDocument document = VideoEditDocument.CreateFor(width, height, 1000);
            document.TextOverlays.Add(new TimedTextOverlay
            {
                Text = "GIF TEXT",
                StartMs = 200,
                EndMs = 500,
                Placement = VideoTextPlacement.Bottom,
            });

            int emitted = AnimatedGifExporter.Export(recording, document, gif);

            Assert.Equal(frames, emitted);
            Assert.True(new FileInfo(gif).Length > 1_024);
            var decoder = new GifBitmapDecoder(
                new Uri(gif, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            Assert.Equal(frames, decoder.Frames.Count);
            Assert.All(decoder.Frames, frame =>
            {
                Assert.Equal(width, frame.PixelWidth);
                Assert.Equal(height, frame.PixelHeight);
            });
            for (int index = 0; index < decoder.Frames.Count; index++)
            {
                BitmapFrame frame = decoder.Frames[index];
                Assert.Equal(index, NearestIndexedColor(frame, width / 2, 8));
                int whitePixels = CountWhitePixels(frame);
                if (index is >= 2 and < 5)
                {
                    Assert.True(whitePixels > 40, $"GIF frame {index} omitted its active timed text");
                }
                else
                {
                    Assert.True(whitePixels < 10, $"GIF frame {index} displayed timed text outside [200,500)");
                }
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void AnimatedGifExporter_PreservesShortTimedTextWithVariableFrameDelay() => RunSta(() =>
    {
        const int width = 160;
        const int height = 90;
        string root = NewRoot();
        string source = Path.Combine(root, "source.mp4");
        string gif = Path.Combine(root, "result.gif");
        try
        {
            RecordingResult recording = EncodeBlackClip(source, width, height, 10, 10);
            VideoEditDocument document = VideoEditDocument.CreateFor(width, height, 1000);
            document.TextOverlays.Add(new TimedTextOverlay
            {
                Text = "short text",
                StartMs = 51,
                EndMs = 79,
                Placement = VideoTextPlacement.Center,
            });

            int emitted = AnimatedGifExporter.Export(recording, document, gif);
            GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document);
            var decoder = new GifBitmapDecoder(
                new Uri(gif, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            Assert.Equal(schedule.SourceTimesMs.Count, emitted);
            Assert.Equal([5, 3, 2, 10, 10, 10, 10, 10, 10, 10, 10, 10], schedule.FrameDelaysCentiseconds);
            Assert.Equal(emitted, decoder.Frames.Count);
            Assert.True(
                decoder.Frames.Max(CountWhitePixels) > 40,
                "the 28ms timed text interval was omitted from the variable-delay GIF");
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void AnimatedGifSchedule_ScalesDelaysWithoutChangingSourceFrameTimes()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(160, 90, 1_000);
        document.FrameEditLayers.Add(new FrameEditLayer
        {
            StartMs = 55,
            EndMs = 85,
            OverlayPngBase64 = "AA==",
        });

        GifFrameSchedule normal = AnimatedGifExporter.BuildFrameSchedule(document, 1.0);
        GifFrameSchedule doubleSpeed = AnimatedGifExporter.BuildFrameSchedule(document, 2.0);
        GifFrameSchedule halfSpeed = AnimatedGifExporter.BuildFrameSchedule(document, 0.5);

        Assert.Equal(normal.SourceTimesMs, doubleSpeed.SourceTimesMs);
        Assert.Equal(normal.SourceTimesMs, halfSpeed.SourceTimesMs);
        Assert.Contains(60, normal.SourceTimesMs);
        Assert.Contains(90, normal.SourceTimesMs);
        FrameEditLayer quantized = Assert.Single(normal.QuantizedFrameEditLayers);
        Assert.Equal(60, quantized.StartMs);
        Assert.Equal(90, quantized.EndMs);
        Assert.Equal(normal.FrameDelaysCentiseconds.Sum() / 2, doubleSpeed.FrameDelaysCentiseconds.Sum());
        Assert.Equal(normal.FrameDelaysCentiseconds.Sum() * 2, halfSpeed.FrameDelaysCentiseconds.Sum());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AnimatedGifExporter.BuildFrameSchedule(document, 4.1));
    }

    [Fact]
    public void AnimatedGifExporter_SmallestPresetProducesSmallerCanvasAndFewerFrames() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            string source = Path.Combine(root, "source.mp4");
            string gif = Path.Combine(root, "small.gif");
            string standardGif = Path.Combine(root, "standard.gif");
            RecordingResult recording = EncodeBlackClip(source, 640, 360, 10, 10);
            VideoEditDocument document = VideoEditDocument.CreateFor(640, 360, 1000);
            int frames = AnimatedGifExporter.Export(recording, document, gif, quality: GifExportQuality.Smallest);
            _ = AnimatedGifExporter.Export(recording, document, standardGif, quality: GifExportQuality.Standard);
            long smallBytes = new FileInfo(gif).Length;
            long standardBytes = new FileInfo(standardGif).Length;
            Assert.True(smallBytes < standardBytes,
                $"Smallest GIF should reduce file size: smallest={smallBytes} bytes, standard={standardBytes} bytes.");
            var decoder = new GifBitmapDecoder(new Uri(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(5, frames);
            Assert.Equal(5, decoder.Frames.Count);
            Assert.All(decoder.Frames, frame =>
            {
                Assert.Equal(480, frame.PixelWidth);
                Assert.Equal(270, frame.PixelHeight);
            });
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void FrameEditLayer_IsCompositedAtItsSourceInterval() => RunSta(() =>
    {
        const int width = 80;
        const int height = 50;
        string root = NewRoot();
        string source = Path.Combine(root, "source.mp4");
        try
        {
            _ = EncodeBlackClip(source, width, height, fps: 10, frames: 10);
            string overlayPng = CreateRedOverlayPng(width, height);
            var layer = new FrameEditLayer
            {
                StartMs = 100,
                EndMs = 200,
                OverlayPngBase64 = overlayPng,
            };

            BitmapSource before = VideoFrameRenderPipeline.RenderSingleFrame(
                source, 50, width, height, [], [layer]);
            BitmapSource active = VideoFrameRenderPipeline.RenderSingleFrame(
                source, 150, width, height, [], [layer]);

            Assert.Equal(0, CountRedPixels(before));
            Assert.True(CountRedPixels(active) >= 350, "active frame edit layer was not composited");
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    private static RecordingResult EncodeBlackClip(
        string path,
        int width,
        int height,
        int fps,
        int frames)
    {
        var options = new VideoEncoderOptions(
            path,
            width,
            height,
            fps,
            VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index + 3] = 255;
            }

            double frameMs = 1000.0 / fps;
            for (int index = 0; index < frames; index++)
            {
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, index * frameMs));
            }

            encoder.Complete();
        }

        return new RecordingResult(path, frames * 1000.0 / fps, fps, frames, width, height);
    }

    private static RecordingResult EncodeIndexedClip(
        string path,
        int width,
        int height,
        int fps,
        IReadOnlyList<(byte B, byte G, byte R)> colors)
    {
        var options = new VideoEncoderOptions(
            path,
            width,
            height,
            fps,
            VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            double frameMs = 1000.0 / fps;
            for (int frameIndex = 0; frameIndex < colors.Count; frameIndex++)
            {
                (byte b, byte g, byte r) = colors[frameIndex];
                for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                {
                    pixels[pixel] = b;
                    pixels[pixel + 1] = g;
                    pixels[pixel + 2] = r;
                    pixels[pixel + 3] = 255;
                }

                encoder.WriteFrame(new EncoderFrame(
                    pixels,
                    width,
                    height,
                    stride,
                    frameIndex * frameMs));
            }

            encoder.Complete();
        }

        return new RecordingResult(path, colors.Count * 1000.0 / fps, fps, colors.Count, width, height);
    }

    private static int CountBrightPixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] >= 180 && pixels[index + 1] >= 180 && pixels[index + 2] >= 180)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountWhitePixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] >= 230 && pixels[index + 1] >= 230 && pixels[index + 2] >= 230)
            {
                count++;
            }
        }

        return count;
    }

    private static string CreateRedOverlayPng(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 10; y < 30; y++)
        {
            for (int x = 10; x < 30; x++)
            {
                int offset = (y * stride) + (x * 4);
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static int CountRedPixels(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 2] >= 220 && pixels[index + 1] <= 50 && pixels[index] <= 50)
            {
                count++;
            }
        }

        return count;
    }

    private static int NearestIndexedColor(BitmapSource source, int x, int y)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return NearestIndexedColor(pixel, 4, 0, 0);
    }

    private static int NearestIndexedColor(byte[] pixels, int stride, int x, int y)
    {
        int offset = checked((y * stride) + (x * 4));
        int bestIndex = -1;
        long bestDistance = long.MaxValue;
        for (int index = 0; index < IndexedColors.Length; index++)
        {
            (byte b, byte g, byte r) = IndexedColors[index];
            long db = pixels[offset] - b;
            long dg = pixels[offset + 1] - g;
            long dr = pixels[offset + 2] - r;
            long distance = (db * db) + (dg * dg) + (dr * dr);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
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
        thread.Join(TimeSpan.FromMinutes(2));
        Assert.False(thread.IsAlive, "STA integration test timed out");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("STA body threw: " + failure);
        }
    }

    private static T RunStaWithResult<T>(Func<T> action)
    {
        T? result = default;
        RunSta(() => result = action());
        return result!;
    }

    private static string NewRoot() => OwnedTestDirectory.Create("mycapture-compositor-");

    private static void DeleteRoot(string root) => OwnedTestDirectory.Delete(root);
}
