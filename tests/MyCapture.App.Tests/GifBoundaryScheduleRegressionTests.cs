using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifBoundaryScheduleRegressionTests
{
    [Fact]
    public void DenseCaptionEndsAtFractionalTail_CoalesceWithEndpointWithoutMovingPreviousGroup()
    {
        var document = VideoEditDocument.CreateFor(160, 90, 1_451);
        document.TextOverlays = [.. new[] { 1_250, 1_350, 1_380, 1_440 }.Select(end =>
            new TimedTextOverlay { Text = "caption", StartMs = 0, EndMs = end })];
        document = document.NormalizeFor(160, 90, 1_451);
        Assert.Equal(4, document.TextOverlays.Count);

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, 2, 15, 30);

        Assert.Equal(73, schedule.FrameDelaysCentiseconds.Sum());
        Assert.All(schedule.FrameDelaysCentiseconds, delay => Assert.True(delay >= 2));
        Assert.Equal(700, TransitionTime(schedule, schedule.QuantizedTextOverlays[2].EndMs));
        Assert.Equal(730, TransitionTime(schedule, schedule.QuantizedTextOverlays[3].EndMs));
        Assert.True(schedule.QuantizedTextOverlays[3].IsActiveAt(schedule.SourceTimesMs[^1]));
        AssertBoundaryTiming(document, schedule, 2, 15, 30, "exact regression");
    }

    [Fact]
    public void DenseNormalizedCaptionsAcrossRatesAndSpeeds_PreserveTimingOrCoalesceAtOneInstant()
    {
        const int seed = 20261028;
        var random = new Random(seed);
        double[] speeds = [0.25, 0.5, 1, 1.5, 2, 3, 4];
        double[] sourceRates = [10, 15, 24, 29.97, 30, 60, double.PositiveInfinity];
        int cases = 0;
        foreach (int fps in GifExportQuality.FrameRates)
        foreach (double speed in speeds)
        for (int sample = 0; sample < 80; sample++)
        {
            // Even at 4x the smallest clip can represent the GIF's minimum 2 cs image.
            int duration = random.Next(80, 3_001);
            var document = VideoEditDocument.CreateFor(160, 90, duration);
            int cluster = random.Next(1, duration);
            int count = random.Next(4, 41);
            for (int caption = 0; caption < count; caption++)
            {
                int start = (sample % 4) switch
                {
                    0 => 0,
                    1 => Math.Clamp(cluster + random.Next(-20, 21), 0, duration - 1),
                    2 => random.Next(duration),
                    _ => Math.Max(0, duration - random.Next(1, Math.Min(duration, 100) + 1)),
                };
                int end = (sample % 4) switch
                {
                    0 => Math.Clamp(duration - random.Next(0, Math.Min(duration, 70)), 1, duration),
                    2 => random.Next(start + 1, duration + 1),
                    _ => Math.Min(duration, start + random.Next(1, 41)),
                };
                document.TextOverlays.Add(new() { Text = "dense", StartMs = start, EndMs = end });
            }
            document = document.NormalizeFor(160, 90, duration);
            double sourceRate = sourceRates[sample % sourceRates.Length];
            string context = $"seed={seed}, case={cases}, duration={duration}, fps={fps}, speed={speed}, source={sourceRate}";
            GifFrameSchedule? schedule = null;
            Exception? error = Record.Exception(() => schedule =
                AnimatedGifExporter.BuildFrameSchedule(document, speed, fps, sourceRate));
            Assert.True(error is null, $"{context}: {error}");
            Assert.NotNull(schedule);
            Assert.Equal(schedule.SourceTimesMs.Count, schedule.FrameDelaysCentiseconds.Count);
            Assert.All(schedule.FrameDelaysCentiseconds, delay => Assert.True(delay >= 2, context));
            Assert.InRange(Math.Abs(schedule.FrameDelaysCentiseconds.Sum() - duration / speed / 10), 0, 1);
            AssertBoundaryTiming(document, schedule, speed, fps, sourceRate, context);
            cases++;
        }
        Assert.Equal(3_920, cases);
    }

    [Fact]
    public void SubCentisecondCaptionAndBitmapLayer_CoalesceToAnInactiveInterval()
    {
        var document = VideoEditDocument.CreateFor(160, 90, 1_000);
        document.TextOverlays = [new() { Text = "tiny", StartMs = 101, EndMs = 102 }];
        document.FrameEditLayers = [new() { Name = "tiny", StartMs = 101, EndMs = 102, OverlayPngBase64 = "AA==" }];
        document = document.NormalizeFor(160, 90, 1_000);

        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(document, 4, 50, 30);
        TimedTextOverlay caption = Assert.Single(schedule.QuantizedTextOverlays);
        FrameEditLayer layer = Assert.Single(schedule.QuantizedFrameEditLayers);
        Assert.Equal(caption.StartMs, caption.EndMs);
        Assert.Equal(layer.StartMs, layer.EndMs);
        Assert.All(schedule.SourceTimesMs, time =>
        {
            Assert.False(caption.IsActiveAt(time));
            Assert.False(layer.IsActiveAt(time));
        });
        GifFrameSchedule unedited = AnimatedGifExporter.BuildFrameSchedule(VideoEditDocument.CreateFor(160, 90, 1_000), 4, 50, 30);
        Assert.Equal(unedited.SourceTimesMs, schedule.SourceTimesMs);
        Assert.Equal(unedited.FrameDelaysCentiseconds, schedule.FrameDelaysCentiseconds);
    }

    private static void AssertBoundaryTiming(VideoEditDocument document, GifFrameSchedule schedule,
        double speed, int fps, double sourceRate, string context)
    {
        double effectiveRate = GifExportQuality.EffectiveFrameRate(fps, sourceRate, speed);
        double toleranceMs = 1_000 / effectiveRate / speed;
        Assert.Equal(document.TextOverlays.Count, schedule.QuantizedTextOverlays.Count);
        for (int index = 0; index < document.TextOverlays.Count; index++)
        {
            TimedTextOverlay original = document.TextOverlays[index];
            TimedTextOverlay quantized = schedule.QuantizedTextOverlays[index];
            if (quantized.StartMs == quantized.EndMs)
            {
                // This explicitly inactive interval adds no image boundary. Its merge
                // instant, not the next unrelated cadence image, bounds both endpoints.
                double mergedAt = quantized.StartMs / speed;
                Check(original.StartMs, mergedAt, "inactive merged start");
                Check(original.EndMs, mergedAt, "inactive merged end");
                Assert.All(schedule.SourceTimesMs, time => Assert.False(quantized.IsActiveAt(time), context));
                continue;
            }
            double actualStart = TransitionTime(schedule, quantized.StartMs);
            double actualEnd = TransitionTime(schedule, quantized.EndMs);
            Check(original.StartMs, actualStart, "caption start");
            Check(original.EndMs, actualEnd, "caption end");
            if (actualStart == actualEnd)
            {
                // Both boundaries were explicitly coalesced at the same output instant:
                // there must be no emitted image that shows this sub-frame caption.
                Assert.All(schedule.SourceTimesMs, time => Assert.False(quantized.IsActiveAt(time), context));
            }
        }
        for (int index = 1; index * 1_000.0 / effectiveRate < document.OutputDurationMs; index++)
        {
            double sourceTime = index * 1_000.0 / effectiveRate;
            Check(sourceTime, TransitionTime(schedule, sourceTime), "cadence");
        }

        void Check(double sourceTime, double outputTime, string kind) =>
            Assert.True(Math.Abs(outputTime - sourceTime / speed) <= toleranceMs + 0.000001,
                $"{context}: {kind} source={sourceTime}, output={outputTime}, bound={toleranceMs}");
    }

    private static double TransitionTime(GifFrameSchedule schedule, double sourceTime)
    {
        double elapsed = 0;
        for (int index = 0; index < schedule.SourceTimesMs.Count; index++)
        {
            if (schedule.SourceTimesMs[index] >= sourceTime - 0.000001) return elapsed;
            elapsed += schedule.FrameDelaysCentiseconds[index] * 10;
        }
        return elapsed; // A boundary coalesced with the endpoint has no image after it.
    }
}
