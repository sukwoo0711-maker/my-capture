using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MeasuredRangeMixedPlateauTests
{
    [Fact]
    public void AClippedPairDoesNotEraseTheDistinctNearWhitePatch()
    {
        List<RangeValuePair>[] pairs = [[], [], []];
        foreach (var channel in pairs)
        {
            foreach (byte value in new byte[] { 0, 32, 64, 96, 128, 160, 192, 224 })
            {
                Add(channel, value, value, 100, 0); Add(channel, value, value, 100, 1);
            }
            // The three supported R clusters observed on the Windows10 device. Only
            // the last two share a shown value;253 remains independently distinguishable.
            Add(channel, 253, 234, 99, 0); Add(channel, 253, 234, 99, 1);
            Add(channel, 255, 241, 90, 0); Add(channel, 255, 241, 99, 1);
            Add(channel, 255, 254, 378, 0); Add(channel, 255, 254, 396, 1);
        }
        int[] counts = pairs.Select(c => c.Count).ToArray();
        var (curve, diagnostics) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.True(curve is not null, diagnostics.Evidence);
        Assert.Equal("verified", diagnostics.Status);
        Assert.InRange(diagnostics.Residual!.Value, 0, 2.5);
        Assert.InRange(diagnostics.HeldOutResidual!.Value, 0, 2.5);
        Assert.Equal(0, diagnostics.SplitDisagreement);
        Assert.Equal(7, diagnostics.MaximumPatchError);
        Assert.All(diagnostics.Coverage, c => Assert.True(c.Sufficient));
        Assert.Equal(counts, pairs.Select(c => c.Count));
        Assert.All(curve!.Channels(), c => { Assert.Equal(234, c[253]); Assert.Equal(248, c[255]); });
        foreach (var evidence in diagnostics.Channels)
        {
            foreach (var anchors in new[] { evidence.FullAnchors, evidence.FirstAnchors, evidence.SecondAnchors })
            {
                var low = Assert.Single(anchors, a => a.X == 253);
                Assert.Equal(234, low.BeforePav); Assert.Equal(234, low.AfterPav);
                var clipped = Assert.Single(anchors, a => a.X == 255);
                Assert.Equal(247.5, clipped.BeforePav); Assert.Equal(247.5, clipped.AfterPav);
            }
            Assert.Equal(198, Assert.Single(evidence.FullAnchors, a => a.X == 253).Count);
            Assert.Equal(963, Assert.Single(evidence.FullAnchors, a => a.X == 255).Count);
            Assert.Contains(evidence.Clusters, c => c.Shown == 253 && c.Reference == 234
                && c.Decision == "adjacent-plateau" && c.CorrectedError == 0);
            Assert.Contains(evidence.Clusters, c => c.Shown == 255 && c.Reference == 241
                && c.Decision == "balanced-plateau" && c.CorrectedError == 7);
            Assert.Contains(evidence.Clusters, c => c.Shown == 255 && c.Reference == 254
                && c.Decision == "balanced-plateau" && c.CorrectedError == 6);
        }
    }

    private static void Add(List<RangeValuePair> pairs, byte shown, byte reference, int count, int split)
    { for (int i = 0; i < count; i++) pairs.Add(new(shown, reference, split)); }
}
