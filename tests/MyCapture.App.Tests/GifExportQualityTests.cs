using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifExportQualityTests
{
    [Fact]
    public void SmallestPreset_ReducesSamplesWithoutLosingTextBoundariesOrDuration()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(1920, 1080, 2000);
        document.TextOverlays.Add(new TimedTextOverlay { StartMs = 51, EndMs = 79, Text = "short" });
        GifFrameSchedule standard = AnimatedGifExporter.BuildFrameSchedule(document);
        GifFrameSchedule small = AnimatedGifExporter.BuildFrameSchedule(document, framesPerSecond: GifExportQuality.Smallest.FramesPerSecond);

        Assert.True(small.SourceTimesMs.Count < standard.SourceTimesMs.Count);
        Assert.Equal(standard.FrameDelaysCentiseconds.Sum(), small.FrameDelaysCentiseconds.Sum());
        Assert.Contains(50d, small.SourceTimesMs);
        Assert.Contains(80d, small.SourceTimesMs);
        Assert.All(small.FrameDelaysCentiseconds, delay => Assert.True(delay > 0));
        Assert.Equal((480, 270), AnimatedGifExporter.FitWithin(1920, 1080, GifExportQuality.Smallest.LongEdge));
        Assert.Equal((80, 50), AnimatedGifExporter.FitWithin(80, 50, GifExportQuality.Smallest.LongEdge));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(60)]
    public void UnsupportedCadence_IsRejectedBeforeRendering(int fps)
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(80, 50, 1000);
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimatedGifExporter.BuildFrameSchedule(document, framesPerSecond: fps));
    }
}
