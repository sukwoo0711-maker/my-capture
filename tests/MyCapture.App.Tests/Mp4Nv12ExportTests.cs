using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// MP4 export through decoded NV12: the encoder's NV12 input, the BGRA to NV12 converter, the
/// cached layer compositor and the trim re-encoder's path choice, fidelity and timeline.
/// </summary>
public sealed class Mp4Nv12ExportTests
{
    private static readonly VideoYuvEncoding Bt709 = new(Bt709: true, FullRange: false);
    private static readonly VideoYuvEncoding Bt601 = new(Bt709: false, FullRange: false);

    [Theory]
    [InlineData(true, 104, 98, 182, 182)]
    [InlineData(false, 112, 93, 184, 183)]
    public void BgraToNv12_RoundsToTheNearestCode_WithinOneLevelOfTheMediaFoundationConverter(
        bool bt709, int y, int u, int v, int systemV)
    {
        // The Sink Writer's RGB32 input encoded B=40, G=80, R=200 to Y, U and systemV (1080p as
        // BT.709, 320x240 as BT.601), read back as NV12 without any RGB conversion. Its 8-bit
        // integer formula floors; the exact values are 104.1, 98.4, 182.3 and 111.6, 92.6, 183.6.
        byte[] bgra = Solid(4, 2, 40, 80, 200);
        byte[] nv12 = new byte[BgraToNv12Converter.RequiredLength(4, 2)];
        BgraToNv12Converter.Convert(bgra, 16, 4, 2, new VideoYuvEncoding(bt709, false), nv12);

        Assert.All(nv12[..8], sample => Assert.Equal(y, sample));
        Assert.Equal(new byte[] { (byte)u, (byte)v, (byte)u, (byte)v }, nv12[8..]);
        Assert.InRange(v - systemV, 0, 1);
    }

    [Fact]
    public void BgraToNv12_AveragesEachChromaBlock_AndKeepsTheRangeEndsAndGreyExact()
    {
        // Alternating red and blue columns: the system converter stored the averaged pair 171, 179.
        byte[] grid = new byte[4 * 2 * 4];
        for (int index = 0; index < 8; index++)
        {
            bool red = (index & 1) == 0;
            grid[(index * 4) + 0] = red ? (byte)0 : (byte)255;
            grid[(index * 4) + 2] = red ? (byte)255 : (byte)0;
            grid[(index * 4) + 3] = 255;
        }

        byte[] nv12 = new byte[12];
        BgraToNv12Converter.Convert(grid, 16, 4, 2, Bt709, nv12);
        Assert.Equal(new byte[] { 63, 32, 63, 32, 63, 32, 63, 32, 171, 179, 171, 179 }, nv12);

        foreach ((byte level, byte luma) in new[] { ((byte)255, (byte)235), ((byte)0, (byte)16), ((byte)128, (byte)126) })
        {
            foreach (VideoYuvEncoding encoding in new[] { Bt709, Bt601 })
            {
                BgraToNv12Converter.Convert(Solid(4, 2, level, level, level), 16, 4, 2, encoding, nv12);
                Assert.All(nv12[..8], sample => Assert.Equal(luma, sample));
                Assert.All(nv12[8..], sample => Assert.Equal(128, sample));
            }
        }
    }

    [Fact]
    public void BgraToNv12_ParallelBandsEqualOneThread_AndInvertsTheNv12Converter()
    {
        const int width = 640;
        const int height = 360;
        byte[] bgra = new byte[width * height * 4];
        GifExportEngineBenchmarkTests.PaintDocumentFrame(bgra, width, height, 7);
        foreach (VideoYuvEncoding encoding in new[] { Bt709, Bt601 })
        {
            byte[] parallel = new byte[BgraToNv12Converter.RequiredLength(width, height)];
            byte[] single = new byte[parallel.Length];
            BgraToNv12Converter.Convert(bgra, width * 4, width, height, encoding, parallel);
            BgraToNv12Converter.Convert(bgra, width * 4, width, height, encoding, single, maxThreads: 1);
            Assert.Equal(single, parallel);

            // Flat areas survive the round trip; only chroma at coloured edges is shared by a block.
            byte[] back = Nv12FrameConverter.ToBgra(parallel, width, height, encoding, width, height);
            Assert.True(MeanAbsoluteDifference(bgra, back) < 1.0, "BGRA to NV12 does not invert the NV12 converter.");
        }
    }

    [Fact]
    public void LayerBlend_EqualsFullConversionOnTouchedBlocks_AndKeepsEveryOtherSample()
    {
        const int width = 64;
        const int height = 48;
        var random = new Random(4242);
        byte[] source = new byte[BgraToNv12Converter.RequiredLength(width, height)];
        random.NextBytes(source);
        for (int index = 0; index < width * height; index++)
        {
            source[index] = (byte)(16 + (source[index] % 220));
        }

        // A premultiplied layer with holes, inside an odd rectangle so bounds must round outwards.
        byte[] layers = new byte[width * height * 4];
        for (int y = 9; y < 31; y++)
        {
            for (int x = 13; x < 41; x++)
            {
                int alpha = random.Next(5) == 0 ? 0 : random.Next(1, 256);
                int offset = ((y * width) + x) * 4;
                layers[offset] = (byte)random.Next(alpha + 1);
                layers[offset + 1] = (byte)random.Next(alpha + 1);
                layers[offset + 2] = (byte)random.Next(alpha + 1);
                layers[offset + 3] = (byte)alpha;
            }
        }

        var bounds = Nv12LayerCompositor.LayerBounds.Of(layers, width, height);
        Assert.Equal(new Nv12LayerCompositor.LayerBounds(12, 8, 42, 32), bounds);
        foreach (VideoYuvEncoding encoding in new[] { Bt709, Bt601 })
        {
            byte[] blended = (byte[])source.Clone();
            Nv12LayerCompositor.Blend(blended, layers, width, height, bounds, encoding);
            byte[] serial = (byte[])source.Clone();
            Nv12LayerCompositor.Blend(serial, layers, width, height, bounds, encoding, maxThreads: 1);
            Assert.Equal(serial, blended);

            // Reference: convert the whole frame, blend source-over, convert back.
            byte[] bgra = Nv12FrameConverter.ToBgra(source, width, height, encoding, width, height);
            for (int offset = 0; offset < bgra.Length; offset += 4)
            {
                int alpha = layers[offset + 3];
                for (int channel = 0; channel < 3; channel++)
                {
                    bgra[offset + channel] = (byte)(layers[offset + channel] + (((bgra[offset + channel] * (255 - alpha)) + 127) / 255));
                }
            }

            byte[] reference = new byte[source.Length];
            BgraToNv12Converter.Convert(bgra, width * 4, width, height, encoding, reference);
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    bool touched = false;
                    foreach ((int dx, int dy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
                    {
                        int pixel = ((y + dy) * width) + x + dx;
                        bool covered = layers[(pixel * 4) + 3] != 0;
                        touched |= covered;
                        Assert.Equal(covered ? reference[pixel] : source[pixel], blended[pixel]);
                    }

                    int chroma = (width * height) + ((y / 2) * width) + x;
                    Assert.Equal(touched ? reference[chroma] : source[chroma], blended[chroma]);
                    Assert.Equal(touched ? reference[chroma + 1] : source[chroma + 1], blended[chroma + 1]);
                }
            }
        }
    }

    [Fact]
    public void LayerCompositor_MatchesFullWpfComposition_AndRendersOncePerActiveSet() => StaTestHost.Run(() =>
    {
        const int width = 640;
        const int height = 360;
        byte[] painted = new byte[width * height * 4];
        GifExportEngineBenchmarkTests.PaintDocumentFrame(painted, width, height, 11);
        byte[] nv12 = new byte[BgraToNv12Converter.RequiredLength(width, height)];
        BgraToNv12Converter.Convert(painted, width * 4, width, height, Bt601, nv12);
        byte[] original = (byte[])nv12.Clone();

        var caption = new TimedTextOverlay { Text = "MyCapture 시간 텍스트 Wg", StartMs = 0, EndMs = 600 };
        var graphic = new FrameEditLayer
        {
            StartMs = 300,
            EndMs = 1000,
            OverlayPngBase64 = GradientPng(90, 60),
            Bounds = new VideoLayerBounds(0.21, 0.13, 0.3, 0.33),
        };
        var request = new VideoFrameRenderRequest("unused.mp4", 0, 1000, 10, width, height, width, height, [caption], [graphic]);
        IReadOnlyDictionary<Guid, BitmapSource> bitmaps = FrameEditLayerRenderer.Decode(request.FrameEditLayers);
        var compositor = new Nv12LayerCompositor(width, height);
        var wpf = new VideoFrameCompositor(width, height);
        byte[] actual = new byte[nv12.Length];
        foreach (double time in new[] { 100.0, 200.0, 400.0, 500.0, 700.0 })
        {
            compositor.Compose(request, bitmaps, nv12, Bt601, time, actual);
            Assert.Equal(original, nv12);

            byte[] bgra = Nv12FrameConverter.ToBgra(nv12, width, height, Bt601, width, height);
            byte[] composed = VideoFrameRenderPipeline.ToBgra32(
                wpf.Compose(request, bitmaps, bgra, width, height, width * 4, time), width, height, out int stride);
            byte[] expected = new byte[nv12.Length];
            BgraToNv12Converter.Convert(composed, stride, width, height, Bt601, expected);

            int maximum = 0;
            long changed = 0;
            for (int index = 0; index < expected.Length; index++)
            {
                maximum = Math.Max(maximum, Math.Abs(expected[index] - actual[index]));
                changed += actual[index] != original[index] ? 1 : 0;
            }

            // Text antialiasing blended in one step instead of two, plus the RGB round trip the
            // full composition applies to untouched pixels, stays within a few levels.
            Assert.True(maximum <= 4, $"t={time}: layer blend differs from WPF composition by {maximum} levels.");
            Assert.True(changed > 1_000, $"t={time}: the active layers were not drawn.");
        }

        // [0,300) caption, [300,600) caption and graphic, [600,1000) graphic: three renders.
        Assert.Equal(3, compositor.LayerRenderCount);
        compositor.Compose(request, bitmaps, nv12, Bt601, 100, actual);
        byte[] again = new byte[nv12.Length];
        compositor.Compose(request, bitmaps, nv12, Bt601, 150, again);
        Assert.Equal(actual, again);
    });

    [Fact]
    public void Encoder_RejectsOddNv12Sizes_AndFramesInTheOtherFormat()
    {
        string root = OwnedTestDirectory.Create("mc-nv12-encoder-");
        try
        {
            Assert.Throws<ArgumentException>(() => new MediaFoundationVideoEncoder(
                new VideoEncoderOptions(Path.Combine(root, "odd.mp4"), 321, 240, 15, 1_000_000) { InputFormat = VideoEncoderInputFormat.Nv12 },
                NullLogger.Instance));

            using var encoder = new MediaFoundationVideoEncoder(
                new VideoEncoderOptions(Path.Combine(root, "even.mp4"), 320, 240, 15, 1_000_000) { InputFormat = VideoEncoderInputFormat.Nv12 },
                NullLogger.Instance);
            Assert.Throws<ArgumentException>(() => encoder.WriteFrame(new EncoderFrame(new byte[320 * 240 * 4], 320, 240, 320 * 4, 0)));
            Assert.Throws<ArgumentException>(() => encoder.WriteFrame(
                new EncoderFrame(new byte[320 * 240], 320, 240, 320, 0) { Format = VideoEncoderInputFormat.Nv12 }));
            encoder.WriteFrame(new EncoderFrame(new byte[320 * 360], 320, 240, 320, 0) { Format = VideoEncoderInputFormat.Nv12 });
            encoder.Complete();
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Theory]
    [InlineData(30, 3)]
    [InlineData(30, 31)]
    [InlineData(15, 32)]
    public void Encoder_Nv12Input_IsUprightAndKeepsTheRgbInputTimeline(int fps, int frames)
    {
        const int width = 320;
        const int height = 240;
        string root = OwnedTestDirectory.Create("mc-nv12-timeline-");
        try
        {
            // Bright top-left quadrant, neutral chroma.
            byte[] nv12 = new byte[BgraToNv12Converter.RequiredLength(width, height)];
            Array.Fill(nv12, (byte)128);
            Array.Fill(nv12, (byte)40, 0, width * height);
            for (int y = 0; y < height / 2; y++)
            {
                Array.Fill(nv12, (byte)220, y * width, width / 2);
            }

            string viaNv12 = Path.Combine(root, "nv12.mp4");
            string viaRgb = Path.Combine(root, "rgb.mp4");
            byte[] bgra = new byte[width * height * 4];
            Array.Fill(bgra, (byte)90);
            Encode(viaNv12, VideoEncoderInputFormat.Nv12, nv12, width);
            Encode(viaRgb, VideoEncoderInputFormat.Bgra32, bgra, width * 4);

            Mp4SampleTable nv12Table = Mp4SampleTable.Read(viaNv12);
            Mp4SampleTable rgbTable = Mp4SampleTable.Read(viaRgb);
            Assert.Equal(frames, nv12Table.PresentationMs.Count);
            Assert.Equal(rgbTable.PresentationMs, nv12Table.PresentationMs);
            Assert.Equal(
                MediaFoundationVideoFrameReader.ReadContainerDurationMs(viaRgb),
                MediaFoundationVideoFrameReader.ReadContainerDurationMs(viaNv12));

            using var reader = new MediaFoundationVideoFrameReader(viaNv12);
            DecodedVideoFrame frame = reader.ReadFrameAt(0);
            Assert.True(Luma(frame, width / 4, height / 4) > 180, "NV12 input lost its top-left quadrant.");
            Assert.True(Luma(frame, width * 3 / 4, height / 4) < 80, "NV12 input is mirrored.");
            Assert.True(Luma(frame, width / 4, height * 3 / 4) < 80, "NV12 input is upside down.");
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }

        void Encode(string path, VideoEncoderInputFormat format, byte[] pixels, int stride)
        {
            var options = new VideoEncoderOptions(path, width, height, fps, 1_000_000) { InputFormat = format };
            using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
            for (int index = 0; index < frames; index++)
            {
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, index * 1000.0 / fps) { Format = format });
            }

            encoder.Complete();
        }
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(1280, 720)]
    public void PlainTrim_PassesNv12Through_WithHigherFidelityAndTheSameTimeline(int width, int height) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-nv12-trim-");
        try
        {
            RecordingResult recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), width, height, 30, 45);
            (Mp4RenderStatistics nv12, string nv12Path) = Trim(root, "nv12.mp4", recording, [], null);
            (Mp4RenderStatistics rgb, string rgbPath) = Trim(root, "rgb.mp4", recording, [], new Mp4RenderOptions { PassThroughNv12 = false });

            Assert.StartsWith("nv12-", nv12.FrameSource, StringComparison.Ordinal);
            Assert.Equal("rgb-software", rgb.FrameSource);
            Assert.Equal((30, 0), (nv12.Frames, nv12.ComposedFrames));
            AssertSameTimeline(rgbPath, nv12Path);
            double nv12Psnr = PsnrAgainstSource(recording.OutputPath, nv12Path, 300, nv12.Frames);
            double rgbPsnr = PsnrAgainstSource(recording.OutputPath, rgbPath, 300, rgb.Frames);
            Assert.True(nv12Psnr >= rgbPsnr, $"NV12 pass-through {nv12Psnr:F2} dB is below the RGB path's {rgbPsnr:F2} dB.");
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void LayeredTrim_ComposesOnlyLayeredFrames_AndKeepsFidelity() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-nv12-layered-");
        try
        {
            RecordingResult recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), 640, 360, 30, 45);
            TimedTextOverlay[] caption = [new() { Text = "MyCapture TEXT", StartMs = 500, EndMs = 1000 }];
            (Mp4RenderStatistics nv12, string nv12Path) = Trim(root, "nv12.mp4", recording, caption, null);
            (Mp4RenderStatistics full, string fullPath) = Trim(root, "full.mp4", recording, caption, new Mp4RenderOptions { BlendCachedLayers = false });
            (Mp4RenderStatistics rgb, string rgbPath) = Trim(root, "rgb.mp4", recording, caption, new Mp4RenderOptions { PassThroughNv12 = false });

            // Source [300, 1300): the caption covers output frames 6..20.
            Assert.Equal(15, nv12.ComposedFrames);
            Assert.Equal(15, full.ComposedFrames);
            Assert.Equal(15, rgb.ComposedFrames);
            AssertSameTimeline(rgbPath, nv12Path);
            double nv12Psnr = PsnrAgainstComposedSource(recording, nv12Path, caption);
            double fullPsnr = PsnrAgainstComposedSource(recording, fullPath, caption);
            double rgbPsnr = PsnrAgainstComposedSource(recording, rgbPath, caption);
            Assert.True(nv12Psnr >= rgbPsnr, $"NV12 with blended layers {nv12Psnr:F2} dB is below the RGB path's {rgbPsnr:F2} dB.");
            Assert.True(fullPsnr >= rgbPsnr, $"NV12 with full composition {fullPsnr:F2} dB is below the RGB path's {rgbPsnr:F2} dB.");

            BitmapSource captioned = VideoFrameRenderPipeline.RenderSingleFrame(nv12Path, 400, 640, 360);
            BitmapSource plain = VideoFrameRenderPipeline.RenderSingleFrame(nv12Path, 100, 640, 360);
            Assert.True(CountWhite(captioned) > CountWhite(plain) + 100, "The caption is missing from the NV12 export.");
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void SourceOfAnotherSize_TakesTheRgbPath() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-nv12-resize-");
        try
        {
            RecordingResult source = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), 640, 360, 15, 10);
            // Recording metadata that disagrees with the stream asks the compositor to stretch.
            var recording = source with { Width = 320, Height = 180 };
            (Mp4RenderStatistics statistics, string path) = Trim(root, "out.mp4", recording, [], null, 0, 600);
            Assert.Equal("rgb-software", statistics.FrameSource);
            Assert.Equal(9, statistics.ComposedFrames);
            Assert.Equal((320, 180), (VideoFrameRenderPipeline.Probe(path).Width, VideoFrameRenderPipeline.Probe(path).Height));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void HardwareDecodeFailure_RetriesInSoftwareIntoAFreshEncoder() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-nv12-retry-");
        try
        {
            RecordingResult recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), 320, 240, 15, 30);
            string output = Path.Combine(root, "out.mp4");
            var encoders = new List<FailingEncoder>();
            Mp4RenderStatistics statistics = TrimReencoder.ReencodeWithStatistics(
                recording.OutputPath,
                output,
                0,
                2000,
                recording,
                options =>
                {
                    var encoder = new FailingEncoder(
                        new MediaFoundationVideoEncoder(options, NullLogger.Instance),
                        failAtFrame: encoders.Count == 0 ? 5 : -1);
                    encoders.Add(encoder);
                    return encoder;
                },
                NullLogger.Instance);

            Assert.Equal(2, encoders.Count);
            Assert.All(encoders, encoder => Assert.True(encoder.Disposed));
            Assert.Equal(VideoEncoderInputFormat.Nv12, encoders[1].Format);
            Assert.Equal("nv12-software", statistics.FrameSource);
            Assert.Equal(30, Mp4SampleTable.Read(output).PresentationMs.Count);
            Assert.InRange(MediaFoundationVideoFrameReader.ReadContainerDurationMs(output), 1999.8, 2000.01);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void FrameDecoder_DeliversTheScheduleInOrder_AndStopsEarlyOnDispose()
    {
        string root = OwnedTestDirectory.Create("mc-nv12-decoder-");
        try
        {
            RecordingResult recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), 320, 240, 15, 30);
            double[] schedule = Enumerable.Range(0, 30).Select(index => index * 1000.0 / 15).ToArray();
            var options = new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 };
            using (Mp4FrameDecoder decoder = Mp4FrameDecoder.Start(recording.OutputPath, options, schedule, 2, CancellationToken.None))
            {
                Mp4ReaderInfo info = decoder.WaitForOpen(CancellationToken.None);
                Assert.Equal((DecodedVideoPixelFormat.Nv12, 320, 240, Bt601), (info.Format, info.Width, info.Height, info.Yuv));
                for (int index = 0; index < 3; index++)
                {
                    Assert.Equal(schedule[index], decoder.Take(CancellationToken.None).TimestampMs, 3);
                }

                // Twenty-seven frames remain undecoded or queued; disposal must not wait for them.
            }

            string garbage = Path.Combine(root, "garbage.mp4");
            File.WriteAllBytes(garbage, new byte[4096]);
            using Mp4FrameDecoder broken = Mp4FrameDecoder.Start(garbage, options, schedule, 2, CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => broken.WaitForOpen(CancellationToken.None));
            Assert.Throws<InvalidOperationException>(() => broken.Take(CancellationToken.None));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void CancellingAnNv12Export_StopsPromptly() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-nv12-cancel-");
        try
        {
            RecordingResult recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), 320, 240, 15, 30);
            using var cancellation = new CancellationTokenSource();
            int reported = 0;
            var progress = new InlineProgress(value =>
            {
                reported = value.CompletedFrames;
                if (value.CompletedFrames == 5)
                {
                    cancellation.Cancel();
                }
            });

            Assert.ThrowsAny<OperationCanceledException>(() => TrimReencoder.Reencode(
                recording.OutputPath,
                Path.Combine(root, "out.mp4"),
                0,
                2000,
                recording,
                options => new MediaFoundationVideoEncoder(options, NullLogger.Instance),
                NullLogger.Instance,
                [new TimedTextOverlay { Text = "TEXT", StartMs = 0, EndMs = 2000 }],
                progress: progress,
                cancellationToken: cancellation.Token));
            Assert.Equal(5, reported);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Nv12Export_FeedsTheEncoderIdenticalFrames_AcrossRunsAndDecoders() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-nv12-determinism-");
        try
        {
            RecordingResult recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), 640, 360, 30, 30);
            TimedTextOverlay[] caption = [new() { Text = "MyCapture 시간 텍스트", StartMs = 200, EndMs = 600 }];
            FrameEditLayer[] graphic =
            [
                new() { StartMs = 400, EndMs = 900, OverlayPngBase64 = GradientPng(90, 60), Bounds = new VideoLayerBounds(0.6, 0.1, 0.3, 0.3) },
            ];

            List<string> Run(Mp4RenderOptions? options)
            {
                var hashes = new List<string>();
                _ = TrimReencoder.Reencode(
                    recording.OutputPath,
                    Path.Combine(root, "unused.mp4"),
                    0,
                    1000,
                    recording,
                    encoderOptions => new HashingEncoder(encoderOptions, hashes),
                    NullLogger.Instance,
                    caption,
                    graphic,
                    renderOptions: options);
                return hashes;
            }

            List<string> first = Run(null);
            Assert.Equal(30, first.Count);
            Assert.Equal(first, Run(null));
            Assert.Equal(first, Run(new Mp4RenderOptions { PreferHardwareDecode = false }));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    /// <summary>Records each submitted frame's format, timestamp and pixel hash; writes no file.</summary>
    private sealed class HashingEncoder(VideoEncoderOptions options, List<string> hashes) : IVideoEncoder
    {
        public int Width => options.Width;

        public int Height => options.Height;

        public void WriteFrame(in EncoderFrame frame)
        {
            int length = frame.Format == VideoEncoderInputFormat.Nv12
                ? frame.Stride * frame.Height * 3 / 2
                : frame.Stride * frame.Height;
            hashes.Add($"{frame.Format}:{frame.TimestampMs:R}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame.Pixels.AsSpan(0, length)))}");
        }

        public void Complete()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class InlineProgress(Action<VideoFrameRenderProgress> report) : IProgress<VideoFrameRenderProgress>
    {
        public void Report(VideoFrameRenderProgress value) => report(value);
    }

    private sealed class FailingEncoder(MediaFoundationVideoEncoder inner, int failAtFrame) : IVideoEncoder
    {
        private int _written;

        internal bool Disposed { get; private set; }

        internal VideoEncoderInputFormat Format { get; private set; }

        public int Width => inner.Width;

        public int Height => inner.Height;

        public void WriteFrame(in EncoderFrame frame)
        {
            Format = frame.Format;
            if (_written++ == failAtFrame)
            {
                // What the pipeline raises when a DXVA decoder fails after opening.
                throw new Mp4HardwareDecodeException(new InvalidOperationException("Simulated device loss."));
            }

            inner.WriteFrame(frame);
        }

        public void Complete() => inner.Complete();

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    private static (Mp4RenderStatistics Statistics, string Path) Trim(
        string root,
        string name,
        RecordingResult recording,
        IReadOnlyList<TimedTextOverlay> overlays,
        Mp4RenderOptions? options,
        double inMs = 300,
        double outMs = 1300)
    {
        string path = Path.Combine(root, name);
        Mp4RenderStatistics statistics = TrimReencoder.ReencodeWithStatistics(
            recording.OutputPath,
            path,
            inMs,
            outMs,
            recording,
            encoderOptions => new MediaFoundationVideoEncoder(encoderOptions, NullLogger.Instance),
            NullLogger.Instance,
            overlays,
            renderOptions: options);
        return (statistics, path);
    }

    private static void AssertSameTimeline(string expected, string actual)
    {
        Assert.Equal(Mp4SampleTable.Read(expected).PresentationMs, Mp4SampleTable.Read(actual).PresentationMs);
        Assert.Equal(Mp4SampleTable.Read(expected).SyncSampleIndexes, Mp4SampleTable.Read(actual).SyncSampleIndexes);
        Assert.Equal(
            MediaFoundationVideoFrameReader.ReadContainerDurationMs(expected),
            MediaFoundationVideoFrameReader.ReadContainerDurationMs(actual));
        Assert.Equal(Mp4ExportBenchmarkTests.ReadAvcConfiguration(expected), Mp4ExportBenchmarkTests.ReadAvcConfiguration(actual));
    }

    private static double PsnrAgainstSource(string source, string output, double inMs, int frames)
    {
        using var expected = new MediaFoundationVideoFrameReader(source);
        using var actual = new MediaFoundationVideoFrameReader(output);
        double error = 0;
        long samples = 0;
        for (int index = 0; index < frames; index++)
        {
            DecodedVideoFrame reference = expected.ReadFrameAt(inMs + (index * 1000.0 / 30));
            DecodedVideoFrame encoded = actual.ReadFrameAt(index * 1000.0 / 30);
            error += SquaredError(reference.Pixels, encoded.Pixels);
            samples += reference.Width * reference.Height * 3L;
        }

        return Psnr(error / samples);
    }

    private static double PsnrAgainstComposedSource(RecordingResult recording, string output, IReadOnlyList<TimedTextOverlay> overlays)
    {
        var request = new VideoFrameRenderRequest(
            recording.OutputPath, 300, 1300, 30, recording.Width, recording.Height, recording.Width, recording.Height, overlays, []);
        var compositor = new VideoFrameCompositor(recording.Width, recording.Height);
        var noLayers = new Dictionary<Guid, BitmapSource>();
        using var expected = new MediaFoundationVideoFrameReader(recording.OutputPath);
        using var actual = new MediaFoundationVideoFrameReader(output);
        double error = 0;
        long samples = 0;
        for (int index = 0; index < 30; index++)
        {
            double sourceTime = 300 + (index * 1000.0 / 30);
            DecodedVideoFrame reference = expected.ReadFrameAt(sourceTime);
            byte[] pixels = overlays.Any(overlay => overlay.IsActiveAt(sourceTime))
                ? VideoFrameRenderPipeline.ToBgra32(compositor.Compose(request, noLayers, reference, sourceTime), recording.Width, recording.Height, out _)
                : reference.Pixels;
            error += SquaredError(pixels, actual.ReadFrameAt(index * 1000.0 / 30).Pixels);
            samples += recording.Width * recording.Height * 3L;
        }

        return Psnr(error / samples);
    }

    private static double SquaredError(byte[] expected, byte[] actual)
    {
        double sum = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = expected[offset + channel] - actual[offset + channel];
                sum += difference * difference;
            }
        }

        return sum;
    }

    private static double Psnr(double meanSquaredError) =>
        meanSquaredError <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / meanSquaredError);

    private static double MeanAbsoluteDifference(byte[] expected, byte[] actual)
    {
        double sum = 0;
        long count = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                sum += Math.Abs(expected[offset + channel] - actual[offset + channel]);
                count++;
            }
        }

        return sum / count;
    }

    private static byte[] Solid(int width, int height, byte blue, byte green, byte red)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private static int Luma(DecodedVideoFrame frame, int x, int y)
    {
        int offset = (y * frame.Stride) + (x * 4);
        return (frame.Pixels[offset] + frame.Pixels[offset + 1] + frame.Pixels[offset + 2]) / 3;
    }

    private static int CountWhite(BitmapSource frame)
    {
        byte[] pixels = VideoFrameRenderPipeline.ToBgra32(frame, frame.PixelWidth, frame.PixelHeight, out _);
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            count += pixels[offset] > 220 && pixels[offset + 1] > 220 && pixels[offset + 2] > 220 ? 1 : 0;
        }

        return count;
    }

    private static string GradientPng(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte alpha = (byte)(x * 255 / (width - 1));
                pixels[offset] = 30;
                pixels[offset + 1] = 200;
                pixels[offset + 2] = 90;
                pixels[offset + 3] = alpha;
            }
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
}
