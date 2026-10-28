using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifTimingReviewTests
{
    public static IEnumerable<object[]> RatesAndSpeeds() =>
        from fps in GifExportQuality.FrameRates
        from speed in new[] { 0.25, 0.5, 1, 2, 4 }
        select new object[] { fps, speed };

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void DenseCaptionAndCutBoundaries_KeepActualOutputIntervalsWithinOneFrame(int fps, double speed)
    {
        var document = DenseDocument();
        document.TrimOutMs = document.SourceDurationMs = 2_000;
        document.RemovedRanges = [new(605, 1_605)];
        // Captions cross the cut, so their source ends move to the following kept range.
        foreach (TimedTextOverlay overlay in document.TextOverlays)
            if (overlay.EndMs > 605) overlay.EndMs += 1_000;
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed, fps);
        VideoCutTimeline timeline = document.CreateTimeline();
        double tolerance = 1_000 / GifExportQuality.EffectiveFrameRate(fps, double.PositiveInfinity, speed) / speed;
        Assert.All(schedule.FrameDelaysCentiseconds, delay => Assert.True(delay >= 2));
        Assert.InRange(Math.Abs(schedule.FrameDelaysCentiseconds.Sum() * 10 - document.OutputDurationMs / speed), 0, 5);
        for (int index = 0; index < document.TextOverlays.Count; index++)
        {
            TimedTextOverlay original = document.TextOverlays[index];
            TimedTextOverlay adjusted = schedule.QuantizedTextOverlays[index];
            (double start, double end) = ActiveRange(schedule, adjusted.IsActiveAt);
            Assert.InRange(Math.Abs(start - timeline.ToOutputTime(original.StartMs) / speed), 0, tolerance);
            Assert.InRange(Math.Abs(end - timeline.ToOutputTime(original.EndMs) / speed), 0, tolerance);
        }
        (double cutStart, _) = ActiveRange(schedule, source => source >= 1_605);
        Assert.InRange(Math.Abs(cutStart - 605 / speed), 0, tolerance);
        Assert.All(schedule.SourceTimesMs, source => Assert.True(source < 605 || source >= 1_605));
    }

    [Fact]
    public Task DenseCaptions_WrittenGifShowsEachCaptionAtItsOwnStartAndEnd() => StaThreadTask.RunAsync(() =>
    {
        var document = DenseDocument();
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document);
        byte[] black = new byte[160 * 90 * 4];
        for (int i = 3; i < black.Length; i += 4) black[i] = 255;
        BitmapSource background = BitmapSource.Create(160, 90, 96, 96, PixelFormats.Bgra32, null, black, 640);
        background.Freeze();
        for (int caption = 0; caption < document.TextOverlays.Count; caption++)
        {
            using var stream = new MemoryStream();
            using (var writer = new AnimatedGifWriter(stream, 160, 90, 10, 0, leaveOpen: true))
            {
                for (int index = 0; index < schedule.SourceTimesMs.Count; index++)
                {
                    BitmapSource composed = VideoFrameRenderPipeline.RenderFrame(background, 160, 90, 160, 90,
                        [schedule.QuantizedTextOverlays[caption]], [], new Dictionary<Guid, BitmapSource>(), schedule.SourceTimesMs[index]);
                    writer.AddFrame(composed, schedule.FrameDelaysCentiseconds[index]);
                }
                writer.Complete();
            }
            GifSampleFrames decoded = GifSampleDecoder.Decode(stream.ToArray());
            double elapsed = 0, start = -1, end = -1;
            for (int index = 0; index < decoded.Frames.Count; index++)
            {
                var bitmap = new FormatConvertedBitmap(decoded.Frames[index], PixelFormats.Bgra32, null, 0);
                var pixels = new byte[black.Length];
                bitmap.CopyPixels(pixels, 640, 0);
                bool visible = Enumerable.Range(0, pixels.Length / 4).Any(pixel => pixels[pixel * 4] > 180);
                if (visible) { if (start < 0) start = elapsed; end = elapsed + decoded.DelaysCentiseconds[index] * 10; }
                elapsed += decoded.DelaysCentiseconds[index] * 10;
            }
            Assert.Equal(1_000, elapsed);
            Assert.InRange(start, document.TextOverlays[caption].StartMs - 20, document.TextOverlays[caption].StartMs + 20);
            Assert.InRange(end, document.TextOverlays[caption].EndMs - 20, document.TextOverlays[caption].EndMs + 20);
        }
        return true;
    });

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void FractionalTail_KeepsRetainedBoundariesWithinOneFrame(int fps, double speed)
    {
        foreach (double duration in new[] { 976d, 995, 1_005, 1_016 })
        {
            var document = DenseDocument();
            document.TrimOutMs = document.SourceDurationMs = duration;
            document.TextOverlays.Add(new() { Text = "tail", StartMs = duration - 6, EndMs = duration });
            GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, speed, fps);
            double tolerance = 1_000 / GifExportQuality.EffectiveFrameRate(fps, double.PositiveInfinity, speed) / speed;
            double elapsed = 0;
            for (int index = 0; index < schedule.SourceTimesMs.Count; index++)
            {
                Assert.InRange(Math.Abs(elapsed - schedule.SourceTimesMs[index] / speed), 0, tolerance);
                Assert.True(schedule.FrameDelaysCentiseconds[index] >= 2);
                elapsed += schedule.FrameDelaysCentiseconds[index] * 10;
            }
            Assert.InRange(Math.Abs(elapsed - duration / speed), 0, 5);
        }
    }

    [Fact]
    public void FractionalEndAtHighSpeed_CoalescesUnrepresentableTailAtTheEnd()
    {
        var document = VideoEditDocument.CreateFor(160, 90, 976);
        document.TextOverlays = [new() { Text = "tail", StartMs = 970, EndMs = 976 }];
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, 4, 50);
        Assert.Equal(24, schedule.FrameDelaysCentiseconds.Sum());
        // 242.5ms is beyond the rounded 240ms endpoint. It must not start at 220ms.
        Assert.All(schedule.SourceTimesMs, source => Assert.False(schedule.QuantizedTextOverlays[0].IsActiveAt(source)));
    }

    [Fact]
    public void CutBesideCadence_CoalescesLocallyWithoutSamplingRemovedFrames()
    {
        var document = VideoEditDocument.CreateFor(160, 90, 1_200);
        document.RemovedRanges = [new(110, 210)];
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, 1, 50);
        Assert.Equal(220, schedule.SourceTimesMs.First(source => source >= 210));
        (double start, double end) = ActiveRange(schedule, source => source >= 210);
        Assert.Equal(120, start); // The 110ms seam collides with the 120ms cadence; error is 10ms.
        Assert.Equal(1_100, end);
        Assert.All(schedule.SourceTimesMs, source => Assert.True(source < 110 || source >= 210));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task QuantizedAwayLayer_DoesNotThrowWhenOpeningOrSwitchingToGif(bool startAsGif) => StaThreadTask.RunAsync(() =>
    {
        var document = VideoEditDocument.CreateFor(320, 180, 2_000);
        document.TrimInMs = 999;
        document.TextOverlays = [new() { Text = "edge", StartMs = 0, EndMs = 1_000 }];
        Assert.Throws<GifExportLimitException>(() => AnimatedGifExporter.BuildFrameSchedule(document));
        var recording = new RecordingResult("never-opened-review.mp4", 2_000, 30, 60, 320, 180);
        var dialog = new VideoExportDialog(recording, document, NullLoggerFactory.Instance, gif: startAsGif);
        try
        {
            if (!startAsGif)
            {
                ToggleButton gif = Logical(dialog).OfType<ToggleButton>().Single(toggle => AutomationProperties.GetName(toggle) == "GIF");
                gif.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            Assert.True(dialog.IsGif);
            Assert.InRange(dialog.DisplayedFrameCount, 1, 11);
            Assert.True(dialog.SampleCompletion.IsCompleted);
            Assert.False(dialog.SamplePanel.HasSample);
        }
        finally { dialog.Close(); }
        return true;
    });

    [Fact]
    public void LongClipSummary_UsesOnlyLayerSizedStorageAndHandlesInvalidIntervals()
    {
        _ = GifPreviewSampling.EstimateFrameCount(1_000, 50); // JIT before the allocation check.
        (double, double)[] layers = [(double.NaN, 10), (999, 1_000), (5_000, 5_100)];
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = GifPreviewSampling.EstimateFrameCount(3_600_000, 50, layers);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(180_000, count);
        Assert.InRange(allocated, 0, 16_384);
        Assert.Equal(0, GifPreviewSampling.EstimateFrameCount(double.NaN, 30));
        Assert.Equal(0, GifPreviewSampling.EstimateFrameCount(1_000, double.PositiveInfinity));
    }

    private static VideoEditDocument DenseDocument()
    {
        var document = VideoEditDocument.CreateFor(160, 90, 1_000);
        for (int i = 0; i < 10; i++)
            document.TextOverlays.Add(new() { Text = "X", StartMs = 500 + 10 * i, EndMs = 600 + 10 * i });
        return document;
    }

    private static (double Start, double End) ActiveRange(GifFrameSchedule schedule, Func<double, bool> isActive)
    {
        double elapsed = 0, start = -1, end = -1;
        for (int i = 0; i < schedule.SourceTimesMs.Count; i++)
        {
            double next = elapsed + schedule.FrameDelaysCentiseconds[i] * 10;
            if (isActive(schedule.SourceTimesMs[i])) { if (start < 0) start = elapsed; end = next; }
            elapsed = next;
        }
        Assert.True(start >= 0, "The interval disappeared from the emitted frames.");
        return (start, end);
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Logical(element)) yield return descendant;
        }
    }
}
