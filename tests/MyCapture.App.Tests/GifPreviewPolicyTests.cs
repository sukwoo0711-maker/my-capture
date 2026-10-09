using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifPreviewPolicyTests
{
    [Theory]
    [InlineData(0, 6_000, 1, 0, 6_000, true)]
    [InlineData(1_000, 5_000, 4, 1_000, 5_000, true)]
    [InlineData(0, 20_000, 1, 8_500, 11_500, false)]
    [InlineData(0, 20_000, 0.5, 8_500, 11_500, false)]
    [InlineData(0, 20_000, 2, 7_000, 13_000, false)]
    [InlineData(0, 20_000, 4, 7_000, 13_000, false)]
    public void Plan_RendersShortRangesWholeAndCentresAThreeSecondPlaybackWindow(
        double trimIn, double trimOut, double speed, double start, double end, bool whole)
    {
        GifSampleWindow window = GifPreviewSampling.Plan(trimIn, trimOut, speed);

        Assert.Equal(new GifSampleWindow(start, end, whole), window);
    }

    [Fact]
    public void Plan_PositionMovesTheWindowAcrossTheRangeOnTheCentisecondGrid()
    {
        Assert.Equal(new GifSampleWindow(2_000, 5_000, false), GifPreviewSampling.Plan(2_000, 22_000, 1, 0));
        Assert.Equal(new GifSampleWindow(19_000, 22_000, false), GifPreviewSampling.Plan(2_000, 22_000, 1, 1));
        GifSampleWindow third = GifPreviewSampling.Plan(0, 20_003, 1, 1.0 / 3);
        Assert.Equal(0, third.StartMs % 10);
        Assert.Equal(3_000, third.DurationMs);
        Assert.Equal(GifPreviewSampling.Plan(0, 20_000, 1), GifPreviewSampling.Plan(0, 20_000, 1, double.NaN));
    }

    [Fact]
    public void PlanRefinement_AddsStartAndEndSecondsOutsideTheVisualSample()
    {
        GifSampleWindow centre = GifPreviewSampling.Plan(0, 20_000, 1);
        Assert.Equal(
            new[] { new GifSampleWindow(0, 1_000, false), new GifSampleWindow(19_000, 20_000, false) },
            GifPreviewSampling.PlanRefinement(0, 20_000, centre));
        GifSampleWindow leading = GifPreviewSampling.Plan(0, 20_000, 1, 0);
        Assert.Equal(new[] { new GifSampleWindow(19_000, 20_000, false) }, GifPreviewSampling.PlanRefinement(0, 20_000, leading));
        Assert.Empty(GifPreviewSampling.PlanRefinement(0, 5_000, GifPreviewSampling.Plan(0, 5_000, 1)));
    }

    [Fact]
    public void SampleDocument_KeepsLayersInsideAndDropsEdgeSliversThatQuantizationWouldReject()
    {
        VideoEditDocument document = VideoEditDocument.CreateFor(1920, 1080, 20_000);
        document.TextOverlays.Add(new TimedTextOverlay { Text = "inside", StartMs = 9_000, EndMs = 10_000 });
        document.TextOverlays.Add(new TimedTextOverlay { Text = "sliver", StartMs = 0, EndMs = 8_504 });
        document.TextOverlays.Add(new TimedTextOverlay { Text = "crossing", StartMs = 11_000, EndMs = 15_000 });
        document.TextOverlays.Add(new TimedTextOverlay { Text = "outside", StartMs = 0, EndMs = 1_000 });
        document.FrameEditLayers.Add(new FrameEditLayer { StartMs = 11_470, EndMs = 18_000, OverlayPngBase64 = "AA==" });
        GifSampleWindow window = GifPreviewSampling.Plan(0, 20_000, 1);

        VideoEditDocument sample = GifPreviewSampling.CreateSampleDocument(document, window);

        Assert.Equal((8_500d, 11_500d), (sample.TrimInMs, sample.TrimOutMs));
        Assert.Equal(new[] { "inside", "crossing" }, sample.TextOverlays.Select(overlay => overlay.Text));
        Assert.Single(sample.FrameEditLayers);
        Assert.Equal(4, document.TextOverlays.Count);
        Assert.Equal((0d, 20_000d), (document.TrimInMs, document.TrimOutMs));
        Assert.NotSame(document.TextOverlays[0], sample.TextOverlays[0]);
        // The naive trim keeps the 4 ms sliver, which the exporter's schedule rejects.
        VideoEditDocument naive = document.Clone();
        naive.TrimInMs = window.StartMs;
        naive.TrimOutMs = window.EndMs;
        naive.FrameEditLayers.Clear();
        Assert.Throws<GifExportLimitException>(() => AnimatedGifExporter.BuildFrameSchedule(naive));
        // The kept 30 ms edge layer quantizes to three centiseconds and schedules normally.
        _ = AnimatedGifExporter.BuildFrameSchedule(sample);
    }

    [Theory]
    [InlineData(10_000, 10, 100)]
    [InlineData(10_004, 10, 100)]
    [InlineData(10_050, 10, 101)]
    [InlineData(70_000, 5, 350)]
    [InlineData(0, 10, 0)]
    public void FrameCount_FollowsTheCadenceOnTheCentisecondGrid(double span, int fps, int expected) =>
        Assert.Equal(expected, GifPreviewSampling.EstimateFrameCount(span, fps));

    [Fact]
    public void FrameCount_AddsOnlyLayerEdgesBetweenCadenceBoundaries()
    {
        // 500 and 3,500 ms sit on the 10 fps grid (not on the 5 fps one); 1,234 and 2,345 ms
        // add a frame each, the repeated 2,345 ms edge only once, edges at or outside the range none.
        (double, double)[] layers = [(500, 3_500), (1_234, 2_345), (2_345, 9_000), (-100, 0)];

        Assert.Equal(40 + 2, GifPreviewSampling.EstimateFrameCount(4_000, 10, layers));
        Assert.Equal(20 + 4, GifPreviewSampling.EstimateFrameCount(4_000, 5, layers));
        Assert.Equal(40, GifPreviewSampling.EstimateFrameCount(4_000, 10, [(500, 3_500)]));
    }

    [Fact]
    public void Estimate_IsExactForAWholeRangeSampleAndARangeOtherwise()
    {
        Assert.Equal(GifSizeEstimate.Exact(1_234), GifSizeEstimator.FromSample(1_234, 5_000, 5_000));

        GifSizeEstimate estimate = GifSizeEstimator.FromSample(3_000_000, 3_000, 20_000);

        Assert.False(estimate.IsExact);
        Assert.Equal(20_000_000, estimate.ExpectedBytes);
        Assert.Equal(16_600_000, estimate.LowBytes);
        Assert.Equal(25_100_000, estimate.HighBytes);
    }

    [Fact]
    public void Estimate_RangeNarrowsAsTheSamplesCoverMoreOfTheRange()
    {
        double previousWidth = double.MaxValue;
        foreach (double sampled in new[] { 1_000d, 3_000, 5_000, 10_000, 19_000 })
        {
            GifSizeEstimate estimate = GifSizeEstimator.FromSample((long)(sampled * 1_000), sampled, 20_000);
            double width = (double)(estimate.HighBytes - estimate.LowBytes) / estimate.ExpectedBytes;
            Assert.True(width < previousWidth);
            Assert.InRange(estimate.ExpectedBytes, estimate.LowBytes, estimate.HighBytes);
            previousWidth = width;
        }
    }

    [Fact]
    public void Estimate_UsesTheGivenCalibrationSoTheModelCanBeRefitted()
    {
        var tight = new GifEstimateCalibration(0, 0, 0, 1);

        GifSizeEstimate estimate = GifSizeEstimator.FromSample(1_000, 1_000, 4_000, tight);

        Assert.Equal((4_000L, 4_000L, 4_000L), (estimate.LowBytes, estimate.ExpectedBytes, estimate.HighBytes));
        Assert.Equal(0.25 * 0.5, GifSizeEstimator.ScaleFactor(GifExportQuality.Standard, GifExportQuality.Smallest, 1920, 1080, tight), 6);
    }

    [Fact]
    public void Estimate_ScalesOtherPresetsByCadenceAndPixelsWithAnExtraMargin()
    {
        var measured = new GifPresetMeasurement(GifExportQuality.Standard, 4_000_000, 20_000, 0.5);
        double factor = GifSizeEstimator.ScaleFactor(GifExportQuality.Standard, GifExportQuality.Smallest, 1920, 1080);
        Assert.Equal(0.5 * Math.Pow(0.25, GifEstimateCalibration.Default.PixelScalingExponent), factor, 6);
        Assert.Equal(0.5, GifSizeEstimator.ScaleFactor(GifExportQuality.Standard, GifSharePolicy.SharpLowRate, 1920, 1080), 6);
        // A 400 px source is never enlarged, so 960 and 640 px presets are the same size.
        Assert.Equal(1, GifSizeEstimator.ScaleFactor(GifExportQuality.Standard, GifExportQuality.Compact, 400, 300), 6);

        GifSizeEstimate scaled = GifSizeEstimator.FromOtherPreset(measured, GifExportQuality.Smallest, 1920, 1080, 20_000);

        Assert.False(scaled.IsExact);
        Assert.Equal((long)Math.Round(4_000_000 * factor), scaled.ExpectedBytes);
        Assert.True(scaled.LowBytes < scaled.ExpectedBytes && scaled.HighBytes > scaled.ExpectedBytes);
        Assert.Equal(GifSizeEstimate.Exact(4_000_000), GifSizeEstimator.FromOtherPreset(measured, GifExportQuality.Standard, 1920, 1080, 20_000));
        Assert.Equal(10_000, GifSizeEstimator.EstimateCalculationMs(measured, 20_000));
    }

    [Fact]
    public void Measurement_IncludingAddsSegmentBytesAndTimeButKeepsTheRenderRate()
    {
        var measurement = new GifPresetMeasurement(GifExportQuality.Compact, 3_000, 3_000, 0.4);

        GifPresetMeasurement combined = measurement.Including(1_500, 1_000).Including(500, 1_000);

        Assert.Equal((5_000L, 5_000d, 0.4), (combined.Bytes, combined.SourceMs, combined.CalculationMsPerSourceMs));
    }

    [Fact]
    public void Targets_DefaultToGitHubAndIncludeTheFrameLimitedAndUnlimitedChoices()
    {
        IReadOnlyList<GifShareTarget> targets = GifSharePolicy.Targets;

        Assert.Equal(10_000_000, targets[GifSharePolicy.DefaultTargetIndex].Bytes);
        Assert.Equal(new[] { 5_000_000L, 10_000_000, 15_000_000, 20_000_000 }, targets.Where(t => t.Bytes is not null).Select(t => t.Bytes!.Value));
        Assert.Equal(350, Assert.Single(targets, t => t.MaximumFrames is not null).MaximumFrames);
        Assert.Null(targets[^1].Bytes);
        Assert.All(targets, target => Assert.False(string.IsNullOrWhiteSpace(UiText.Get(target.LabelKey))));
    }

    [Theory]
    [InlineData(0.0, "Static")]
    [InlineData(0.049, "Static")]
    [InlineData(0.05, "Moderate")]
    [InlineData(0.15, "Moderate")]
    [InlineData(0.16, "High")]
    public void Classify_UsesTheMeasuredMotionBoundaries(double motion, string expected) =>
        Assert.Equal(expected, GifSharePolicy.Classify(motion).ToString());

    [Fact]
    public void Recommend_KeepsTheCurrentPresetWhenItFits()
    {
        GifRecommendation recommendation = Recommend(standardBytesPerSecond: 500_000, spanMs: 10_000, motion: 0.01);

        Assert.Equal(GifRecommendationKind.CurrentFits, recommendation.Kind);
        Assert.Equal(GifExportQuality.Standard, recommendation.Quality);
        Assert.Null(recommendation.Speed);
    }

    [Theory]
    [InlineData(0.01, "960/5")]
    [InlineData(0.10, "640/10")]
    [InlineData(0.40, "640/10")]
    public void Recommend_OrdersFallbacksByMotion(double motion, string expected)
    {
        // 960/10 needs ~13 to 17 MB for 10 s; 960/5 and 640/10 both fit 10 MB.
        GifRecommendation recommendation = Recommend(standardBytesPerSecond: 1_300_000, spanMs: 10_000, motion: motion);

        Assert.Equal(GifRecommendationKind.SwitchPreset, recommendation.Kind);
        Assert.Equal(expected, $"{recommendation.Quality.LongEdge}/{recommendation.Quality.FramesPerSecond}");
        Assert.True(recommendation.Estimate.HighBytes <= 10_000_000);
    }

    [Fact]
    public void Recommend_HighMotionNeverFallsBackToFiveFramesAtFullWidth()
    {
        Assert.DoesNotContain(GifSharePolicy.SharpLowRate, GifSharePolicy.CandidatesFor(GifMotionClass.High));
        Assert.Equal(GifSharePolicy.SharpLowRate, GifSharePolicy.CandidatesFor(GifMotionClass.Static)[1]);
    }

    [Fact]
    public void Recommend_SuggestsMp4AndTheLongestGifRangeWhenNothingFits()
    {
        GifRecommendation recommendation = Recommend(standardBytesPerSecond: 20_000_000, spanMs: 10_000, motion: 0.1);

        Assert.Equal(GifRecommendationKind.UseMp4, recommendation.Kind);
        Assert.Equal(GifLimitReason.Size, recommendation.Reason);
        Assert.Equal(GifExportQuality.Smallest, recommendation.Quality);
        Assert.True(recommendation.Estimate.HighBytes > 10_000_000);
        double perMs = recommendation.Estimate.HighBytes / 10_000d;
        Assert.InRange(recommendation.MaximumGifSourceMs, (10_000_000 / perMs) - 1_000, 10_000_000 / perMs);
        Assert.Equal(0, recommendation.MaximumGifSourceMs % 1_000);
    }

    [Fact]
    public void Recommend_RespectsTheDestinationFrameLimit()
    {
        GifShareTarget x = GifSharePolicy.Targets.Single(target => target.MaximumFrames is not null);

        // 40 s: 400 frames at 10 fps exceed X's 350; for moving content 480/5 (200 frames) fits.
        GifRecommendation fits = Recommend(standardBytesPerSecond: 50_000, spanMs: 40_000, motion: 0.4, target: x);
        Assert.Equal(GifExportQuality.Smallest, fits.Quality);
        Assert.Equal(GifRecommendationKind.SwitchPreset, fits.Kind);

        // 80 s: 400 frames even at 5 fps.
        GifRecommendation tooLong = Recommend(standardBytesPerSecond: 50_000, spanMs: 80_000, motion: 0.4, target: x);
        Assert.Equal(GifRecommendationKind.UseMp4, tooLong.Kind);
        Assert.Equal(GifLimitReason.DestinationFrames, tooLong.Reason);
        Assert.Equal(70_000, tooLong.MaximumGifSourceMs);
    }

    [Fact]
    public void Recommend_WithoutASizeLimitPicksTheSharpestPresetUnderTheSafetyFrameLimit()
    {
        GifShareTarget unlimited = GifSharePolicy.Targets[^1];

        Assert.Equal(GifExportQuality.Standard, Recommend(standardBytesPerSecond: 50_000_000, spanMs: 60_000, motion: 0.4, target: unlimited).Quality);
        // 130 s exceeds 1,200 frames at 10 fps; still UI keeps 960 px at 5 fps.
        GifRecommendation longStill = Recommend(standardBytesPerSecond: 100_000, spanMs: 130_000, motion: 0.01, target: unlimited);
        Assert.Equal(GifSharePolicy.SharpLowRate, longStill.Quality);
        // 300 s is 1,500 frames even at 5 fps: guidance turns to MP4 and states the limit.
        GifRecommendation tooMany = Recommend(standardBytesPerSecond: 100_000, spanMs: 300_000, motion: 0.01, target: unlimited);
        Assert.Equal(GifRecommendationKind.UseMp4, tooMany.Kind);
        Assert.Equal(GifLimitReason.SafetyFrames, tooMany.Reason);
        Assert.Equal(240_000, tooMany.MaximumGifSourceMs);
    }

    [Fact]
    public void Recommend_PrefersAPresetsOwnMeasurementOverScaling()
    {
        // Scaled from 960/10 alone, 640/10 would look like it fits; its own sample says no.
        var measurements = new Dictionary<GifExportQuality, GifPresetMeasurement>
        {
            [GifExportQuality.Standard] = new(GifExportQuality.Standard, 3_900_000, 3_000, 0.5),
            [GifExportQuality.Compact] = new(GifExportQuality.Compact, 3_000_000, 3_000, 0.5),
        };

        GifRecommendation recommendation = GifSharePolicy.Recommend(10_000, 1920, 1080, measurements,
            GifExportQuality.Compact, 1, GifSharePolicy.Targets[GifSharePolicy.DefaultTargetIndex], 0.4);

        Assert.Equal(GifExportQuality.Smallest, recommendation.Quality);
    }

    [Theory]
    [InlineData(20_000, 1, null)]
    [InlineData(40_000, 1, 1.5)]
    [InlineData(50_000, 1, 2.0)]
    [InlineData(70_000, 1, null)]
    [InlineData(50_000, 2, null)]
    public void SuggestSpeed_OffersOnlyOneAndAHalfOrTwoForLongPlayback(double span, double current, double? expected) =>
        Assert.Equal(expected, GifSharePolicy.SuggestSpeed(span, current));

    private static GifRecommendation Recommend(double standardBytesPerSecond, double spanMs, double motion, GifShareTarget? target = null)
    {
        var measurements = new Dictionary<GifExportQuality, GifPresetMeasurement>
        {
            [GifExportQuality.Standard] = new(GifExportQuality.Standard, (long)(standardBytesPerSecond * 3), 3_000, 0.5),
        };
        return GifSharePolicy.Recommend(spanMs, 1920, 1080, measurements, GifExportQuality.Standard, 1,
            target ?? GifSharePolicy.Targets[GifSharePolicy.DefaultTargetIndex], motion);
    }
}
