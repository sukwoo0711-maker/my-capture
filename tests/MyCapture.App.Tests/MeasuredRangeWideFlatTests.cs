using System.Text.Json;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MeasuredRangeWideFlatTests
{
    [Fact]
    public void ActualGalleryPairsUseTheBoundedFallbackWithoutDroppingAnySample()
    {
        var pairs = MeasuredRangeGalleryRawFixture.Create();
        AssertCounts(pairs);
        var primary = MeasuredRangeCurveFitter.Fit(pairs, allowWideFlatFallback: false);
        Assert.Null(primary.Curve);
        Assert.Equal("mapping-residual", primary.Diagnostics.Status);
        Assert.Equal(3.0611442975188212, primary.Diagnostics.Residual!.Value, 9);
        Assert.Equal(3.0478774168574745, primary.Diagnostics.HeldOutResidual!.Value, 9);
        var actual = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.True(actual.Curve is not null, actual.Diagnostics.Evidence);
        Assert.Equal("wide-flat-endpoints", actual.Diagnostics.SelectedPlan);
        Assert.Equal(JsonSerializer.Serialize(primary.Diagnostics), JsonSerializer.Serialize(actual.Diagnostics.RejectedPrimary));
        Assert.Equal(2.35711417504282, actual.Diagnostics.Residual!.Value, 9);
        Assert.Equal(2.3536644538945115, actual.Diagnostics.HeldOutResidual!.Value, 9);
        Assert.InRange(actual.Diagnostics.SplitDisagreement!.Value, 0, 4);
        Assert.Equal(8, actual.Diagnostics.MaximumPatchError);
        Assert.All(actual.Diagnostics.Coverage, c => Assert.True(c.Sufficient));
        Assert.All(actual.Curve!.Channels(), c => Assert.Equal(8, c[0]));
        Assert.Equal(234, actual.Curve.Channels()[2][253]);
        Assert.Equal(248, actual.Curve.Channels()[2][255]);
        for (int c = 0; c < 3; c++)
        {
            var before = primary.Diagnostics.Channels[c].Clusters;
            var after = actual.Diagnostics.Channels[c].Clusters;
            // Patch evidence still contains the original complete clusters, not just endpoint pixels.
            Assert.Equal(before.Select(p => (p.Shown, p.Reference, p.Count, p.FirstCount, p.SecondCount, p.Repeated)),
                after.Select(p => (p.Shown, p.Reference, p.Count, p.FirstCount, p.SecondCount, p.Repeated)));
            Assert.All(after.Where(p => p.Repeated), p => Assert.InRange(p.CorrectedError!.Value, 0, 12));
            Assert.Equal(primary.Diagnostics.Channels[c].Bands.Select(b => b.Count),
                actual.Diagnostics.Channels[c].Bands.Select(b => b.Count));
            Assert.All(actual.Curve.Channels()[c].Zip(actual.Curve.Channels()[c].Skip(1)), p => Assert.True(p.First <= p.Second));
        }
        AssertCounts(pairs);
    }

    [Fact]
    public void VerifiedTwoBinReferenceRetainsExactlyThePrimaryCurveAndEvidence()
    {
        List<RangeValuePair>[] pairs = [[], [], []];
        foreach (var channel in pairs)
        for (int half = 0; half < 2; half++)
        {
            foreach (byte value in new byte[] { 0, 32, 64, 96, 160, 192, 224 })
                Add(channel, value, value, half, 100);
            Add(channel, 127, 128, half, half == 0 ? 70 : 30);
            Add(channel, 128, 128, half, half == 0 ? 30 : 70);
        }
        var primary = MeasuredRangeCurveFitter.Fit(pairs, allowWideFlatFallback: false);
        var actual = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(primary.Curve);
        Assert.Equal(primary.Curve, actual.Curve);
        AssertSameDiagnostics(primary.Diagnostics, actual.Diagnostics);
        Assert.Null(actual.Diagnostics.SelectedPlan);
        foreach (var c in actual.Diagnostics.Channels)
        {
            Assert.Single(c.FullAnchors, a => a.BeforePav == 128);
            Assert.Equal(127, Assert.Single(c.FirstAnchors, a => a.BeforePav == 128).X);
            Assert.Equal(128, Assert.Single(c.SecondAnchors, a => a.BeforePav == 128).X);
        }
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(16, 2)]
    public void ThirdBinMustMeetEachHalfGlobalSupport(int secondHalfCount, int expectedAnchors)
    {
        var pairs = MeasuredRangeGalleryRawFixture.Create();
        // Half counts1512/1584 both require16. G110 bin27 was18/21.
        int retained = 0;
        for (int i = 0; i < pairs[1].Count; i++)
        {
            var p = pairs[1][i];
            if (p.Reference == 110 && p.Split == 1 && p.Shown / 4 == 27 && ++retained > secondHalfCount)
                pairs[1][i] = p with { Shown = 115 };
        }
        Assert.Equal(secondHalfCount, pairs[1].Count(p => p.Reference == 110 && p.Split == 1 && p.Shown / 4 == 27));
        var actual = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(actual.Curve);
        Assert.Equal("wide-flat-endpoints", actual.Diagnostics.SelectedPlan);
        Assert.Equal(expectedAnchors, actual.Diagnostics.Channels[1].FullAnchors.Count(a => a.BeforePav == 110));
        AssertCounts(pairs);
    }

    [Fact]
    public void LosingTheActualThirdBinSupportPreservesThePrimaryFailure()
    {
        var pairs = MeasuredRangeGalleryRawFixture.Create();
        Assert.Equal(16, pairs[2].Count(p => p.Reference == 128 && p.Split == 0 && p.Shown / 4 == 33));
        int index = pairs[2].FindIndex(p => p.Reference == 128 && p.Split == 0 && p.Shown / 4 == 33);
        pairs[2][index] = pairs[2][index] with { Shown = 128 };
        Assert.Equal(15, pairs[2].Count(p => p.Reference == 128 && p.Split == 0 && p.Shown / 4 == 33));
        AssertRejectedWithPrimary(pairs);
        AssertCounts(pairs);
    }

    [Fact]
    public void ASevenLevelReferenceRangeKeepsTheOriginalBinEstimator()
    {
        var pairs = MeasuredRangeGalleryRawFixture.Create();
        for (int i = 0; i < pairs[1].Count; i++)
        {
            var p = pairs[1][i];
            if (p.Reference == 110) pairs[1][i] = p with { Reference = (byte)(107 + p.Shown % 8) };
        }
        var actual = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(actual.Curve);
        Assert.Equal("wide-flat-endpoints", actual.Diagnostics.SelectedPlan);
        var green = actual.Diagnostics.Channels[1];
        var broad = green.FullAnchors.Select((a, i) => (Anchor: a, Decision: green.AnchorDecisions[i]))
            .Where(p => p.Anchor.BeforePav is >= 107 and <= 114).ToArray();
        Assert.Equal(3, broad.Length);
        Assert.All(broad, p => Assert.Equal("shared-reference-bin", p.Decision));
        AssertCounts(pairs);
    }

    [Theory]
    [InlineData("half-sparse")]
    [InlineData("reverse")]
    [InlineData("clipped")]
    public void GalleryCounterExamplesCannotBeAcceptedByTheFallback(string kind)
    {
        var pairs = MeasuredRangeGalleryRawFixture.Create();
        for (int c = 0; c < 3; c++)
        for (int i = 0; i < pairs[c].Count; i++)
        {
            var p = pairs[c][i];
            if (kind == "reverse") pairs[c][i] = p with { Shown = (byte)(255 - p.Shown) };
            if (kind == "clipped" && c == 2 && p.Reference == 29) pairs[c][i] = p with { Shown = 0 };
            if (kind == "half-sparse" && c == 2 && p.Reference is 46 or 128)
            {
                int middle = p.Reference == 46 ? 41 : 128;
                int shown = p.Split == 1 ? middle : p.Shown < middle ? p.Shown - 10
                    : p.Shown > middle + 1 ? p.Shown + 10 : p.Shown;
                pairs[c][i] = p with { Shown = (byte)shown };
            }
        }
        AssertRejectedWithPrimary(pairs);
        AssertCounts(pairs);
    }

    [Fact]
    public void CandidateCannotReplaceThePrimaryWhenThePatchGateStillFails()
    {
        List<RangeValuePair>[] Build(byte clipped)
        {
            List<RangeValuePair>[] pairs = [[], [], []];
            foreach (var channel in pairs)
            for (int half = 0; half < 2; half++)
            {
                Add(channel, 0, 0, half, 1000); Add(channel, 0, clipped, half, 100);
                Add(channel, 255, 255, half, 1000);
                foreach (byte value in new byte[] { 64, 96, 128, 160, 192, 224 })
                foreach (int offset in new[] { -8, -4, 0, 4, 8 })
                    Add(channel, (byte)(value + offset), value, half, 100);
            }
            return pairs;
        }
        var control = MeasuredRangeCurveFitter.Fit(Build(16));
        Assert.NotNull(control.Curve);
        Assert.Equal("wide-flat-endpoints", control.Diagnostics.SelectedPlan);
        // Identical wide islands, but0/26 share shown0 and require13 error after balancing.
        // Endpoint residual/held-out are13/7=1.857; the unchanged patch12 gate must reject.
        var clipped = Build(26);
        var primary = MeasuredRangeCurveFitter.Fit(clipped, allowWideFlatFallback: false);
        Assert.Equal("mapping-residual", primary.Diagnostics.Status);
        Assert.Equal(13, primary.Diagnostics.MaximumPatchError);
        var actual = MeasuredRangeCurveFitter.Fit(clipped);
        Assert.Null(actual.Curve);
        AssertSameDiagnostics(primary.Diagnostics, actual.Diagnostics);
    }

    private static void AssertRejectedWithPrimary(List<RangeValuePair>[] pairs)
    {
        var primary = MeasuredRangeCurveFitter.Fit(pairs, allowWideFlatFallback: false);
        var actual = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.Null(primary.Curve);
        Assert.Null(actual.Curve);
        AssertSameDiagnostics(primary.Diagnostics, actual.Diagnostics);
        Assert.Null(actual.Diagnostics.SelectedPlan);
        Assert.Null(actual.Diagnostics.RejectedPrimary);
    }

    private static void AssertSameDiagnostics(RangeMappingDiagnostics expected, RangeMappingDiagnostics actual)
    {
        Assert.Equal(expected.Evidence, actual.Evidence);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    private static void AssertCounts(List<RangeValuePair>[] pairs) => Assert.All(pairs, p =>
    {
        Assert.Equal(3096, p.Count);
        Assert.Equal(1512, p.Count(v => v.Split == 0));
        Assert.Equal(1584, p.Count(v => v.Split == 1));
    });

    private static void Add(List<RangeValuePair> pairs, byte shown, byte reference, int half, int count)
    { for (int i = 0; i < count; i++) pairs.Add(new(shown, reference, half)); }
}
