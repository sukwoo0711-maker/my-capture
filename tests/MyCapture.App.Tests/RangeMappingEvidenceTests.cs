using System.Text.Json;
using System.Text.Json.Serialization;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RangeMappingEvidenceTests
{
    [Theory]
    [InlineData(10, 8)]
    [InlineData(20, 2)]
    public void AnchorSupportedInOnlyOneHalfIsExcludedFromEveryFit(int firstCount, int secondCount)
    {
        // Before repeated support, this one-half anchor alone produced split 7 (a "gap" artefact,
        // supported/aligned split < 4). A cluster now contributes only when both halves support it,
        // so the full fit and both half fits share one anchor set and the artefact disappears.
        var pairs = IdentityPairs(include128: false);
        Add(pairs, 128, 135, firstCount, 0); Add(pairs, 128, 135, secondCount, 1);
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(curve);
        Assert.Equal("verified", d.Status);
        Assert.True(d.SplitDisagreement < 4, d.Evidence);
        Assert.True(d.Residual <= 2.5 && d.HeldOutResidual <= 2.5, d.Evidence);
        Assert.All(d.Channels, c =>
        {
            Assert.DoesNotContain(c.FullAnchors, a => a.Bin == 32);
            Assert.DoesNotContain(c.FirstAnchors, a => a.Bin == 32);
            Assert.DoesNotContain(c.SecondAnchors, a => a.Bin == 32);
            var excluded = Assert.Single(c.Clusters, p => p.Reference == 135);
            Assert.False(excluded.Repeated);
            Assert.Equal("insufficient-repeat", excluded.Decision);
            Assert.Equal(firstCount, excluded.FirstCount);
            Assert.Equal(secondCount, excluded.SecondCount);
        });
    }

    [Fact]
    public void SupportedRowDifferenceRemainsARejectedSplit()
    {
        var pairs = IdentityPairs(include128: false);
        Add(pairs, 128, 128, 100, 0); Add(pairs, 128, 136, 100, 1);
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.Null(curve); Assert.Equal("mapping-split-disagreement", d.Status);
        Assert.Equal(8, d.SplitDisagreement);
        Assert.Equal("supported", d.MaximumSplit!.Classification);
        Assert.Equal(8, d.SupportedSplit); Assert.Equal(8, d.AlignedSplit);
        Assert.True(d.MaximumSplit.First.NearestAnchorDistance <= 4);
        Assert.True(d.MaximumSplit.Second.NearestAnchorDistance <= 4);
    }

    [Fact]
    public void ClippingShowsBothReferenceClustersAndTheWorstLowBand()
    {
        var pairs = IdentityPairs(include128: true);
        Add(pairs, 0, 16, 100, 0); Add(pairs, 0, 16, 100, 1);
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        // Band 0 contributes 8 / 8 = 1; evidence does not turn this accepted fit into a rejection.
        Assert.NotNull(curve); Assert.Equal("verified", d.Status);
        Assert.Equal(1, d.Residual); Assert.Equal(1, d.HeldOutResidual); Assert.Equal(0, d.SplitDisagreement);
        Assert.All(d.Channels, c =>
        {
            var bin = Assert.Single(c.MultiClusterBins);
            Assert.Equal(0, bin.Bin); Assert.Equal(0, bin.Shown);
            Assert.Equal(new double[] { 0, 16 }, bin.Clusters.Select(v => v.Median));
            Assert.All(bin.Clusters, v => Assert.Equal(200, v.Count));
            Assert.Equal(0, c.WorstBand!.Band); Assert.Equal(8, c.WorstBand.Error);
            Assert.Equal(400, c.WorstBand.Count); Assert.Equal(320, c.WorstBand.RetainedCount);
        });
    }

    [Fact]
    public void ColourDependentShownValuesAppearAsSeparatePatchContributors()
    {
        var pairs = IdentityPairs(include128: true);
        // The same reference red can be presented differently in two colour patches.
        // Neighbouring reference clusters make a one-channel inverse compromise visible.
        Add(pairs, 96, 128, 100, 0); Add(pairs, 96, 128, 100, 1);
        Add(pairs, 128, 160, 100, 0); Add(pairs, 128, 160, 100, 1);
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.Null(curve); Assert.Equal("mapping-residual", d.Status);
        Assert.All(d.Channels, c =>
        {
            Assert.Contains(c.TopContributors, p => p.Shown == 96 && p.Reference == 128 && p.Count == 200 && p.Error == 16);
            Assert.Contains(c.TopContributors, p => p.Shown == 128 && p.Reference == 128);
            Assert.Equal(3, c.TopContributors.Length);
        });
    }

    [Fact]
    public void SplitLowCoverageStillReportsAllChannelsAndMissingBins()
    {
        var pairs = IdentityPairs(include128: true);
        foreach (var channel in pairs) channel.RemoveAll(p => p.Split == 1 && p.Shown >= 192);
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.Null(curve); Assert.Equal("mapping-split-low-coverage", d.Status);
        Assert.Null(d.SplitDisagreement); Assert.Null(d.MaximumSplit);
        Assert.Equal(3, d.Channels.Length);
        Assert.All(d.Channels, c => Assert.Contains(c.AlignedSecondAnchors,
            a => a.X == 224 && a.Count == 0 && a.AfterPav == null));
    }

    [Fact]
    public void AnchorsRetainReferenceStatisticsBeforeAndAfterPav()
    {
        var pairs = IdentityPairs(include128: true);
        foreach (var channel in pairs)
            for (int i = 0; i < channel.Count; i++)
                if (channel[i].Shown is 96 or 128)
                    channel[i] = channel[i] with { Reference = (byte)(channel[i].Shown == 96 ? 140 : 110) };
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.Null(curve); Assert.Equal("mapping-residual", d.Status);
        Assert.All(d.Channels, c =>
        {
            var left = Assert.Single(c.FullAnchors, a => a.X == 96);
            var right = Assert.Single(c.FullAnchors, a => a.X == 128);
            Assert.Equal(140, left.BeforePav); Assert.Equal(110, right.BeforePav);
            Assert.Equal(125, left.AfterPav); Assert.Equal(125, right.AfterPav);
            Assert.Equal(200, left.Count); Assert.Equal(200, right.Count);
            Assert.Equal(8, c.Bands.Length);
            Assert.Contains(c.Bands, b => b.Band == 5 && b.Count > 0);
        });
    }

    [Fact]
    public void FullEvidenceSurvivesTheDiagnosticJsonRoundTripAndSummaryStaysCompact()
    {
        var pairs = IdentityPairs(include128: false);
        Add(pairs, 128, 135, 20, 0); Add(pairs, 128, 135, 2, 1);
        var fit = MeasuredRangeCurveFitter.Fit(pairs);
        var result = new RangeVerificationResult(null, new(3, 4, 5), [], 100, 255, 4, 10, fit.Diagnostics.Status)
            { MappingDiagnostics = fit.Diagnostics, IsFallback = true };
        // These are VideoRangeSelfTest's options, including the child->parent deserialisation.
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        string json = JsonSerializer.Serialize(result, options);
        var restored = JsonSerializer.Deserialize<RangeVerificationResult>(json, options)!;
        Assert.Equal(result.Evidence, restored.Evidence);
        Assert.Equal(3, restored.MappingDiagnostics!.Channels.Length);
        Assert.Contains("\"BeforePav\"", json); Assert.Contains("\"NearestAnchorDistance\"", json);
        Assert.Contains("\"CorrectedError\"", json); Assert.Contains("\"AnchorDecisions\"", json);
        Assert.Contains("insufficient-repeat", restored.Evidence);
        Assert.Equal(fit.Diagnostics.MaximumPatchError, restored.MappingDiagnostics.MaximumPatchError);
        Assert.Contains("\"Evidence\"", json); Assert.Contains("aligned-second", result.Evidence);
        Assert.DoesNotContain("\n", fit.Diagnostics.Summary);
        Assert.DoesNotContain("full (bin", result.Details);
        Assert.True(fit.Diagnostics.Summary.Length < 450, fit.Diagnostics.Summary);
    }

    private static List<RangeValuePair>[] IdentityPairs(bool include128)
    {
        List<RangeValuePair>[] pairs = [[], [], []];
        foreach (byte value in new byte[] { 0, 32, 64, 96, 128, 160, 192, 224, 255 })
        {
            if (value == 128 && !include128) continue;
            Add(pairs, value, value, 100, 0); Add(pairs, value, value, 100, 1);
        }
        return pairs;
    }

    private static void Add(List<RangeValuePair>[] pairs, byte shown, byte reference, int count, int split)
    {
        foreach (var channel in pairs)
            for (int i = 0; i < count; i++) channel.Add(new(shown, reference, split));
    }
}
