using System.Buffers.Binary;
using System.IO;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifPreviewReviewTests
{
    [Theory]
    [InlineData(30, 4_000)]
    [InlineData(50, 3_200)]
    public void IndependentlyEncodedStaticSamples_AreAnEstimateEvenWhenTheyCoverTheWholeRange(int fps, double duration) => RunSta(() =>
    {
        GifExportQuality quality = GifExportQuality.Standard with { FramesPerSecond = fps };
        GifSampleWindow visual = GifPreviewSampling.Plan(0, duration, 1, framesPerSecond: fps);
        Assert.False(visual.CoversWholeRange);
        GifSegmentSize visualSize = EncodeStatic(visual.DurationMs, fps);
        GifPresetMeasurement measurement = GifPresetMeasurement.Of(quality, visualSize, visual.DurationMs, 0, startsRange: false);
        foreach (GifSampleWindow refinement in GifPreviewSampling.PlanRefinement(0, duration, visual))
        {
            measurement = measurement.Including(EncodeStatic(refinement.DurationMs, fps),
                refinement.DurationMs, refinement.StartMs == 0);
        }

        GifSegmentSize full = EncodeStatic(duration, fps);
        Assert.Equal(3, measurement.Segments.Count);
        Assert.Equal(duration, measurement.SourceMs);
        Assert.All(measurement.Segments, segment => Assert.Equal(1, segment.Size.Images));
        Assert.Equal(1, full.Images);
        Assert.Equal(3 * full.Bytes, measurement.Bytes);

        GifSizeEstimate estimate = GifSizeEstimator.FromMeasurement(measurement, duration);
        Assert.False(estimate.IsExact);
        Assert.Equal(full.Bytes, estimate.ExpectedBytes);
        Assert.InRange(full.Bytes, estimate.LowBytes, estimate.HighBytes);
        Assert.True(estimate.LowBytes < estimate.HighBytes);
        Assert.Equal(GifSizeEstimate.Exact(full.Bytes), GifSizeEstimator.FromMeasurement(
            GifPresetMeasurement.Of(quality, full, duration, 0, startsRange: true), duration));
    });

    [Theory]
    [InlineData(960, 600, 30, 4_000, 55)]
    [InlineData(960, 960, 10, 4_000, 33)]
    [InlineData(960, 960, 10, 6_000, 33)]
    public void VisualSample_RespectsTheDecoderCanvasBudget(int width, int height, int fps, double duration, int capacity)
    {
        var document = VideoEditDocument.CreateFor(width, height, duration);
        GifSampleWindow window = GifPreviewSampling.Plan(document, 1, framesPerSecond: fps, pixelWidth: width, pixelHeight: height);
        Assert.False(window.CoversWholeRange);
        Assert.Equal(capacity, GifSampleDecoder.FrameCapacity(width, height));
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(
            GifPreviewSampling.CreateSampleDocument(document, window), 1, fps);
        int frames = schedule.SourceTimesMs.Count;
        Assert.InRange(frames, 1, capacity);
        Assert.True((long)width * height * 4 * (frames + 3L) <= GifSampleDecoder.MaximumDecodedBytes);
        // The old 60-frame plan fails this same decoder guard for all three reproductions.
        Assert.True((long)width * height * 4 * (60 + 3L) > GifSampleDecoder.MaximumDecodedBytes);
    }

    [Fact]
    public void Exactness_RequiresOneConsistentEncodeFromTheRangeStart()
    {
        GifPresetMeasurement whole = GifPresetMeasurement.Of(GifExportQuality.Standard,
            new GifSegmentSize(1_234, 500, 700, 0.5), 4_000, 0, startsRange: true);
        Assert.True(whole.IsWholeRange(4_000));
        GifPresetMeasurement[] invalid =
        [
            whole with { Segments = [] },
            whole with { SourceMs = 3_000 },
            whole with { Bytes = 2_468 },
            whole with { Segments = [whole.Segments[0] with { StartsRange = false }] },
            whole with { Segments = [whole.Segments[0] with { SourceMs = 3_000 }] },
            whole.Including(new GifSegmentSize(1_234, 500, 700, 0.5), 4_000, startsRange: false),
        ];
        foreach (GifPresetMeasurement measurement in invalid)
        {
            Assert.False(measurement.IsWholeRange(4_000));
            Assert.False(GifSizeEstimator.FromMeasurement(measurement, 4_000).IsExact);
        }
    }

    [Theory]
    [InlineData(960, 600)]
    [InlineData(960, 960)]
    public void PreviewDecode_StillMeasuresEveryEncodedImageWhenExtraBoundariesExceedTheSnapshotBudget(int width, int height) => RunSta(() =>
    {
        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(output, 16, 12, 5, leaveOpen: true))
        {
            for (int index = 0; index < 60; index++)
            {
                byte[] pixels = new byte[16 * 12 * 4];
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    pixels[offset] = (byte)(index % 2 == 0 ? 0 : 255);
                    pixels[offset + 1] = (byte)(index % 3 == 0 ? 255 : 0);
                    pixels[offset + 3] = 255;
                }

                writer.AddFrame(pixels, 16 * 4, 5);
            }

            writer.Complete();
        }

        // A large logical screen with small partial images exercises the same full-canvas
        // snapshot budget without a video fixture, staging files or recycle-bin cleanup.
        byte[] data = output.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), (ushort)height);
        GifSegmentSize measurement = GifSegmentSize.Read(data);
        Assert.Equal(60, measurement.Images);
        Assert.Throws<GifExportLimitException>(() => GifSampleDecoder.Decode(data));

        GifSampleFrames preview = GifSampleDecoder.Decode(data, fitToMemoryBudget: true);
        Assert.InRange(preview.Frames.Count, 1, GifSampleDecoder.FrameCapacity(width, height));
        Assert.Equal(30, preview.Frames.Count);
        Assert.Equal(3_000, preview.Timeline.TotalMs);
        Assert.Equal((width, height), (preview.PixelWidth, preview.PixelHeight));
        Assert.All(preview.Frames, frame => Assert.True(frame.IsFrozen));
        Assert.Equal(data.Length, measurement.Bytes);
    });

    private static GifSegmentSize EncodeStatic(double duration, int fps)
    {
        const int width = 16, height = 12;
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 40;
            pixels[offset + 1] = 100;
            pixels[offset + 2] = 180;
            pixels[offset + 3] = 255;
        }

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(VideoEditDocument.CreateFor(width, height, duration), 1, fps);
        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(output, width, height, 10, leaveOpen: true))
        {
            foreach (int delay in schedule.FrameDelaysCentiseconds)
            {
                writer.AddFrame(pixels, width * 4, delay);
            }

            writer.Complete();
        }

        return GifSegmentSize.Read(output.ToArray());
    }

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
