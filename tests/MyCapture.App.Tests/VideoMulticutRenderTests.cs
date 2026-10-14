using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoMulticutRenderTests
{
    [Fact]
    public void Mp4AndGifExports_DecodeOnlyKeptFramesAndPreserveLayerTimingAcrossCuts() => StaTestHost.Run(() =>
    {
        const int width = 160, height = 96, fps = 10;
        // Keep generated media in the test output directory for diagnosis, without binary fixtures.
        string root = Path.Combine(AppContext.BaseDirectory, "multicut-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.mp4");
        byte[] sourceLevels = [20, 40, 60, 0, 0, 100, 120, 140, 0, 0, 180, 200];
        byte[] expectedLevels = [20, 40, 60, 100, 120, 140, 180, 200];
        var options = new VideoEncoderOptions(source, width, height, fps,
            VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            for (int index = 0; index < sourceLevels.Length; index++)
            {
                byte[] pixels = new byte[width * height * 4];
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    byte level = sourceLevels[index];
                    pixels[offset] = level;
                    pixels[offset + 1] = level;
                    pixels[offset + 2] = level == 0 ? (byte)240 : level;
                    pixels[offset + 3] = 255;
                }

                encoder.WriteFrame(new EncoderFrame(pixels, width, height, width * 4, index * 100.0));
            }

            encoder.Complete();
        }

        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
        using (var reader = new MediaFoundationVideoFrameReader(source))
        {
            // Verify the fixture really contains distinguishable footage in both removed ranges.
            foreach (double sourceTime in new double[] { 300, 400, 800, 900 })
            {
                DecodedVideoFrame frame = reader.ReadFrameAt(sourceTime);
                AssertPatch(frame.Pixels, frame.Stride, 112, 64, 0, 0, 240);
            }
        }

        var recording = new RecordingResult(source, 1_200, fps, sourceLevels.Length, width, height);
        VideoEditDocument document = VideoEditDocument.CreateFor(width, height, recording.DurationMs);
        document.RemovedRanges = [new(300, 500), new(800, 1_000)];
        document.FrameEditLayers =
        [
            new()
            {
                StartMs = 200,
                EndMs = 700,
                OverlayPngBase64 = CreateSeamLayer(width, height),
            },
        ];

        string mp4 = Path.Combine(root, "export.mp4");
        int mp4Frames = TrimReencoder.Reencode(source, mp4, document.TrimInMs, document.TrimOutMs,
            recording, exportOptions => new MediaFoundationVideoEncoder(exportOptions, NullLogger.Instance),
            NullLogger.Instance, document.TextOverlays, document.FrameEditLayers,
            removedRanges: document.RemovedRanges);

        Assert.Equal(expectedLevels.Length, mp4Frames);
        Assert.InRange(MediaFoundationVideoFrameReader.ReadContainerDurationMs(mp4), 799.8, 800.2);
        using (var reader = new MediaFoundationVideoFrameReader(mp4))
        {
            for (int index = 0; index < expectedLevels.Length; index++)
            {
                // Exact boundaries and the end of each displayed interval must agree, including seams.
                foreach (double outputTime in new[] { index * 100.0, index * 100.0 + 99 })
                {
                    DecodedVideoFrame frame = reader.ReadFrameAt(outputTime);
                    AssertExportedFrame(frame.Pixels, frame.Stride, expectedLevels[index], index is >= 2 and <= 4);
                }
            }
        }

        string gif = Path.Combine(root, "export.gif");
        int gifFrames = AnimatedGifExporter.Export(recording, document, gif);
        GifSampleFrames decoded = GifSampleDecoder.Decode(gif);
        Assert.Equal(expectedLevels.Length, gifFrames);
        Assert.Equal(expectedLevels.Length, decoded.Frames.Count);
        Assert.Equal((width, height), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(80, decoded.DelaysCentiseconds.Sum());
        Assert.Equal(800, decoded.Timeline.TotalMs);
        for (int index = 0; index < expectedLevels.Length; index++)
        {
            foreach (double outputTime in new[] { index * 100.0, index * 100.0 + 99 })
            {
                BitmapSource frame = decoded.Frames[decoded.Timeline.IndexAt(outputTime)];
                BitmapSource bgra = frame.Format == PixelFormats.Bgra32
                    ? frame
                    : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[width * height * 4];
                bgra.CopyPixels(pixels, width * 4, 0);
                AssertExportedFrame(pixels, width * 4, expectedLevels[index], index is >= 2 and <= 4);
            }
        }

        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.Equal((200d, 700d), (document.FrameEditLayers[0].StartMs, document.FrameEditLayers[0].EndMs));
    });

    private static string CreateSeamLayer(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 16; y < 48; y++)
        {
            for (int x = 16; x < 64; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset + 1] = 240;
                pixels[offset + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void AssertExportedFrame(byte[] pixels, int stride, byte level, bool layerActive)
    {
        AssertPatch(pixels, stride, 112, 64, level, level, level);
        if (layerActive)
        {
            AssertPatch(pixels, stride, 32, 24, 0, 240, 0);
        }
        else
        {
            AssertPatch(pixels, stride, 32, 24, level, level, level);
        }
    }

    private static void AssertPatch(byte[] pixels, int stride, int x, int y, byte b, byte g, byte r)
    {
        byte[] expected = [b, g, r];
        for (int channel = 0; channel < expected.Length; channel++)
        {
            double sum = 0;
            for (int row = y; row < y + 8; row++)
            {
                for (int column = x; column < x + 8; column++)
                {
                    sum += pixels[row * stride + column * 4 + channel];
                }
            }

            // Two lossy H.264 passes and GIF quantization may shift colour by a few levels.
            Assert.InRange(sum / 64, expected[channel] - 12, expected[channel] + 12);
        }
    }

    [Fact]
    public void Mp4Schedule_MapsOutputFramesAcrossCutsWithoutMovingLayerTimes()
    {
        VideoTimeRange[] cuts = [new(1_500, 2_000), new(2_500, 3_500)];
        var overlay = new TimedTextOverlay { Text = "after cut", StartMs = 2_000, EndMs = 2_200 };
        var request = new VideoFrameRenderRequest(
            "unused.mp4", 1_000, 4_000, 10, 320, 180, 320, 180, [overlay], [], cuts);

        double[] schedule = VideoFrameRenderPipeline.BuildSchedule(request);

        Assert.Equal(new double[]
        {
            1_000, 1_100, 1_200, 1_300, 1_400,
            2_000, 2_100, 2_200, 2_300, 2_400,
            3_500, 3_600, 3_700, 3_800, 3_900,
        }, schedule);
        Assert.True(overlay.IsActiveAt(schedule[5]));
        Assert.False(overlay.IsActiveAt(schedule[7]));
        Assert.Equal(2_000, overlay.StartMs);
    }

    [Fact]
    public void Mp4Schedule_NoCutsPreservesTheOriginalCadenceAndExclusiveEnd()
    {
        var request = new VideoFrameRenderRequest("unused.mp4", 123, 473, 10, 320, 180, 320, 180, [], []);

        Assert.Equal(new double[] { 123, 223, 323, 423 }, VideoFrameRenderPipeline.BuildSchedule(request));
    }

    [Fact]
    public void GifSchedule_UsesKeptDurationAndSourceTimedLayersAtTheSeam()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 1_000);
        document.RemovedRanges = [new(300, 700)];
        document.TextOverlays =
        [
            new() { Text = "crossing", StartMs = 250, EndMs = 750 },
            new() { Text = "removed", StartMs = 330, EndMs = 660 },
        ];
        document.FrameEditLayers = [new() { StartMs = 700, EndMs = 800, OverlayPngBase64 = "AA==" }];

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document);

        Assert.Equal(new double[] { 0, 100, 200, 250, 700, 750, 800, 900 }, schedule.SourceTimesMs);
        Assert.Equal(60, schedule.FrameDelaysCentiseconds.Sum());
        TimedTextOverlay overlay = Assert.Single(schedule.QuantizedTextOverlays);
        Assert.True(overlay.IsActiveAt(250));
        Assert.True(overlay.IsActiveAt(700));
        Assert.False(overlay.IsActiveAt(750));
        Assert.Equal((250d, 750d), (document.TextOverlays[0].StartMs, document.TextOverlays[0].EndMs));
        Assert.True(Assert.Single(schedule.QuantizedFrameEditLayers).IsActiveAt(700));
    }

    [Theory]
    [InlineData(0.5, 120)]
    [InlineData(1.0, 60)]
    [InlineData(2.0, 30)]
    public void GifSchedule_SpeedScalesOutputDurationAfterCuts(double speed, int centiseconds)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 1_000);
        document.RemovedRanges = [new(300, 700)];

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed);

        Assert.Equal(centiseconds, schedule.FrameDelaysCentiseconds.Sum());
        Assert.All(schedule.SourceTimesMs, time => Assert.True(time < 300 || time >= 700));
    }

    [Theory]
    [InlineData(1_004, 2_000)]
    [InlineData(1_006, 2_000)]
    [InlineData(1_000d / 30 * 31, 1_000d / 30 * 61)]
    public void GifSchedule_QuantizedSeamUsesExactNextSourceAndLayerBoundary(double cutStart, double cutEnd)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 3_000);
        document.RemovedRanges = [new(cutStart, cutEnd)];
        document.TextOverlays =
        [
            new() { Text = "before cut", StartMs = cutStart - 100, EndMs = cutStart },
            new() { Text = "after cut", StartMs = cutEnd, EndMs = cutEnd + 100 },
        ];
        document.FrameEditLayers = [new() { StartMs = cutEnd, EndMs = cutEnd + 100, OverlayPngBase64 = "AA==" }];

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document);

        int seamFrame = Array.IndexOf(schedule.SourceTimesMs.ToArray(), cutEnd);
        Assert.True(seamFrame > 0);
        int expectedSeam = (int)Math.Round(cutStart / 10, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedSeam, schedule.FrameDelaysCentiseconds.Take(seamFrame).Sum());
        Assert.True(schedule.SourceTimesMs[seamFrame - 1] < cutStart);
        Assert.Equal(cutEnd, schedule.SourceTimesMs[seamFrame]);
        Assert.False(schedule.QuantizedTextOverlays[0].IsActiveAt(cutEnd));
        Assert.True(schedule.QuantizedTextOverlays[1].IsActiveAt(cutEnd));
        Assert.True(Assert.Single(schedule.QuantizedFrameEditLayers).IsActiveAt(cutEnd));
        Assert.Equal((int)Math.Round(document.OutputDurationMs / 10, MidpointRounding.AwayFromZero),
            schedule.FrameDelaysCentiseconds.Sum());
        Assert.All(schedule.SourceTimesMs, time => Assert.True(time < cutStart || time >= cutEnd));
        Assert.True(schedule.SourceTimesMs.SequenceEqual(schedule.SourceTimesMs.Order()));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void GifSchedule_SourceReferenceFollowsQuantizedOutputDelayAtCut(double speed)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 3_000);
        document.RemovedRanges = [new(1_004, 2_000)];
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed);
        int seamFrame = Array.IndexOf(schedule.SourceTimesMs.ToArray(), 2_000);
        double seamOutputMs = schedule.FrameDelaysCentiseconds.Take(seamFrame).Sum() * 10.0;

        Assert.Equal(2_000, schedule.SourceTimeAtOutput(seamOutputMs));
        Assert.Equal(schedule.SourceTimesMs[seamFrame - 1], schedule.SourceTimeAtOutput(seamOutputMs - 1));
    }

    [Fact]
    public void GifSample_PlansAcrossCutsOnTheOutputClockAndKeepsSourceLayerTimes()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 20_000);
        document.RemovedRanges = [new(5_000, 15_000)];
        document.TextOverlays = [new() { Text = "after cut", StartMs = 15_000, EndMs = 16_000 }];

        GifSampleWindow window = GifPreviewSampling.Plan(document, 1);
        VideoEditDocument sample = GifPreviewSampling.CreateSampleDocument(document, window);

        Assert.Equal((3_500d, 16_500d), (window.StartMs, window.EndMs));
        Assert.Equal(3_000, window.DurationMs);
        Assert.Equal(3_000, sample.OutputDurationMs);
        Assert.False(window.CoversWholeRange);
        Assert.Equal(15_000, Assert.Single(sample.TextOverlays).StartMs);
        Assert.Equal(new[] { (0d, 1_000d), (19_000d, 20_000d) },
            GifPreviewSampling.PlanRefinement(document, window).Select(segment => (segment.StartMs, segment.EndMs)));
        Assert.Equal(300, AnimatedGifExporter.BuildFrameSchedule(sample).FrameDelaysCentiseconds.Sum());
    }

    [Fact]
    public void GifSample_WholeResultUsesKeptLengthEvenWhenSourceIsLong()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 20_000);
        document.RemovedRanges = [new(2_000, 18_000)];

        GifSampleWindow window = GifPreviewSampling.Plan(document, 1);

        Assert.True(window.CoversWholeRange);
        Assert.Equal(4_000, window.DurationMs);
        Assert.Empty(GifPreviewSampling.PlanRefinement(document, window));
        Assert.Equal(4_000, GifPreviewSampling.CreateSampleDocument(document, window).OutputDurationMs);
    }

    [Fact]
    public void GifSample_EdgeNearCutKeepsTheMinimumSegmentAndMeasuresItsActualDuration()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(320, 180, 20_000);
        document.RemovedRanges = [new(5_000, 15_000)];

        GifSampleWindow window = GifPreviewSampling.Plan(document, 1, 4_950d / 7_000);
        VideoEditDocument sample = GifPreviewSampling.CreateSampleDocument(document, window);

        Assert.Equal(4_900, window.StartMs);
        Assert.Equal(3_050, window.DurationMs);
        Assert.Equal(window.DurationMs, sample.OutputDurationMs);
        Assert.Equal(4_900, AnimatedGifExporter.BuildFrameSchedule(sample).SourceTimesMs[0]);
    }
}
