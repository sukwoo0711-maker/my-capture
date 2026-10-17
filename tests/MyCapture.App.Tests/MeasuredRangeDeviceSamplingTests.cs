using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class MeasuredRangeDeviceSamplingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 84016, 8)]
    [InlineData(false, 19045, 8)]
    [InlineData(true, 84016, 12)]
    [InlineData(true, 19045, 12)]
    public void ActualColourPatternGridAcceptsDeviceToneCurves(bool full, int seed, int tolerance)
    {
        var (shown, reference) = Frames(full, seed);
        var result = PresentedFrameRangeModel.Compare(shown, reference);
        output.WriteLine(result.Details);
        Assert.Equal(3096, result.Points);
        Assert.Null(result.Model);
        Assert.True(result.Mapping is not null, result.Details + "\n" + result.Evidence);
        Assert.Equal("verified", result.Status);
        Assert.Equal("measured", result.Kind);
        Assert.InRange(result.MappingDiagnostics!.Residual!.Value, 0, 2.5);
        Assert.InRange(result.MappingDiagnostics.HeldOutResidual!.Value, 0, 2.5);
        Assert.InRange(result.MappingDiagnostics.SplitDisagreement!.Value, 0, 4);
        Assert.All(result.MappingDiagnostics.Coverage, c => Assert.True(c.Sufficient));
        Assert.All(result.MappingDiagnostics.Channels.SelectMany(c => c.Clusters).Where(c => c.Repeated),
            c => Assert.InRange(c.CorrectedError!.Value, 0, 12));
        var curves = result.Mapping.Channels();
        int maximum = 0;
        for (int patch = 0; patch < VideoRangeColourPattern.Patches.Length; patch++)
        for (int channel = 0; channel < 3; channel++)
        {
            var values = new List<int>();
            // Exactly Compare's grid and flat-patch interiors, including alternate rows.
            for (int y = 2; y < 178; y += 4)
            for (int x = 2; x < 318; x += 4)
                if (y / 90 * 8 + x / 40 == patch && x % 40 >= 2 && x % 40 < 38 && y % 90 >= 2 && y % 90 < 88)
                    values.Add(curves[channel][shown.Pixels[(y * 320 + x) * 4 + channel]]);
            values.Sort();
            var p = VideoRangeColourPattern.Patches[patch];
            int expected = channel == 0 ? p.B : channel == 1 ? p.G : p.R;
            int actual = values[values.Count / 2];
            maximum = Math.Max(maximum, Math.Abs(actual - expected));
            Assert.True(Math.Abs(actual - expected) <= tolerance,
                $"{p.Name}, channel={channel}, source={expected}, corrected patch median={actual}; {result.Details}\n{result.Evidence}");
        }
        output.WriteLine($"full={full}, seed={seed}, accepted, points={result.Points}, corrected patch max={maximum}; {result.MappingDiagnostics.Summary}");
        if (full) Assert.Contains(result.MappingDiagnostics.Channels.SelectMany(c => c.AnchorDecisions), d => d == "adjacent-plateau");
        Assert.All(curves, c => Assert.True(c.Zip(c.Skip(1), (a, b) => a <= b).All(v => v)));
    }

    [Theory]
    [InlineData("row-band")]
    [InlineData("nonmonotone")]
    [InlineData("few-levels")]
    [InlineData("clipped")]
    [InlineData("different-halves")]
    public void ActualColourPatternCounterExamplesAreRejected(string failure)
    {
        var (shown, reference) = Frames(false, 84016, failure);
        var result = PresentedFrameRangeModel.Compare(shown, reference, forceMeasured: true);
        output.WriteLine($"{failure}: {result.Details}\n{result.Evidence}");
        Assert.Null(result.Mapping);
        Assert.Null(result.Model);
        Assert.StartsWith("mapping-", result.Status);
        if (failure == "row-band") Assert.True(result.MappingDiagnostics!.SplitDisagreement > 4, result.Details);
        if (failure == "clipped") Assert.True(result.MappingDiagnostics!.MaximumPatchError > 12, result.Details);
    }

    [Fact]
    public void SparseOutliersCannotSupplyCoverageOrAnAdjacentPlateau()
    {
        List<RangeValuePair>[] pairs = [[], [], []];
        foreach (var channel in pairs)
        for (int half = 0; half < 2; half++)
        {
            for (int i = 0; i < 500; i++) channel.Add(new(0, 0, half));
            for (int i = 0; i < 500; i++) channel.Add(new(255, 255, half));
            foreach (byte level in new byte[] { 16, 32, 64, 96, 128, 160, 192, 235 })
                for (int i = 0; i < 4; i++) channel.Add(new(level == 235 ? (byte)253 : level, level, half));
        }
        var (curve, diagnostics) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.Null(curve);
        Assert.Equal("mapping-low-coverage", diagnostics.Status);
        Assert.All(diagnostics.Channels, c => Assert.DoesNotContain(c.AnchorDecisions, d => d == "adjacent-plateau"));
    }

    [Fact]
    public void SupportedClippedPatchCannotHideInTheTrimmedBandResidual()
    {
        List<RangeValuePair>[] pairs = [[], [], []];
        foreach (var channel in pairs)
        for (int half = 0; half < 2; half++)
        {
            foreach (byte value in new byte[] { 0, 32, 64, 96, 128, 160, 192, 224, 255 })
                for (int i = 0; i < 100; i++) channel.Add(new(value, value, half));
            // 29 is separate from 0 but in the same residual band; equal-area balance
            // yields 14/15 error. All old scalar gates pass; the patch gate must reject.
            for (int i = 0; i < 100; i++) channel.Add(new(0, 29, half));
        }
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        output.WriteLine(d.Evidence);
        Assert.Null(curve);
        Assert.Equal("mapping-patch-error", d.Status);
        Assert.InRange(d.Residual!.Value, 0, 2.5);
        Assert.InRange(d.HeldOutResidual!.Value, 0, 2.5);
        Assert.Equal(0, d.SplitDisagreement);
        Assert.True(d.MaximumPatchError > 12);
        Assert.All(d.Channels, c => Assert.DoesNotContain(c.AnchorDecisions, a => a == "adjacent-plateau"));
    }

    [Fact]
    public void RepeatedReferenceStraddlingBinsUsesOneCommonAnchorSet()
    {
        var pairs = AnchorPairs();
        foreach (var channel in pairs)
        for (int half = 0; half < 2; half++)
        for (int i = 0; i < 100; i++)
            channel.Add(new((byte)(i < (half == 0 ? 70 : 30) ? 127 : 128), 128, half));
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(curve);
        Assert.Equal("verified", d.Status);
        Assert.InRange(d.SplitDisagreement!.Value, 0, 4);
        Assert.All(d.Channels, c =>
        {
            Assert.Equal(c.FullAnchors.Length, c.FirstAnchors.Length);
            Assert.Equal(c.FullAnchors.Length, c.SecondAnchors.Length);
            Assert.Equal(128, Assert.Single(c.FullAnchors, a => a.BeforePav == 128).BeforePav);
            Assert.Equal(127, Assert.Single(c.FirstAnchors, a => a.BeforePav == 128).X);
            Assert.Equal(128, Assert.Single(c.SecondAnchors, a => a.BeforePav == 128).X);
            Assert.Contains(c.Clusters, p => p.Reference == 128 && p.Repeated && p.FirstCount == 100 && p.SecondCount == 100);
        });
    }

    [Theory]
    [InlineData(253, 255)] // Same four-level bin.
    [InlineData(251, 253)] // Two adjacent bins.
    public void AdjacentPlateauKeepsEachSupportedMedian(byte low, byte high)
    {
        var pairs = AnchorPairs();
        foreach (var channel in pairs)
        for (int half = 0; half < 2; half++)
        for (int i = 0; i < 100; i++)
        {
            channel.Add(new(low, 235, half));
            channel.Add(new(high, 255, half));
        }
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(curve);
        Assert.Equal("verified", d.Status);
        Assert.All(curve.Channels(), c => { Assert.Equal(235, c[low]); Assert.Equal(255, c[high]); });
        Assert.All(d.Channels, c =>
        {
            Assert.Equal(2, c.AnchorDecisions.Count(v => v == "adjacent-plateau"));
            Assert.Equal(c.FullAnchors.Length, c.FirstAnchors.Length);
            Assert.Equal(c.FullAnchors.Length, c.SecondAnchors.Length);
        });
        Assert.Equal(0, d.MaximumPatchError);
    }

    [Fact]
    public void EqualPresentedMediansStayOneBalancedPlateau()
    {
        var pairs = AnchorPairs();
        foreach (var channel in pairs)
        for (int half = 0; half < 2; half++)
        for (int i = 0; i < 100; i++)
        {
            channel.Add(new(253, 235, half));
            channel.Add(new(253, 255, half));
        }
        var (curve, d) = MeasuredRangeCurveFitter.Fit(pairs);
        Assert.NotNull(curve);
        Assert.All(d.Channels, c =>
        {
            var plateau = Assert.Single(c.FullAnchors, a => a.X == 253);
            Assert.Equal(245, plateau.BeforePav);
            Assert.DoesNotContain("adjacent-plateau", c.AnchorDecisions);
        });
        Assert.Equal(10, d.MaximumPatchError);
    }

    private static List<RangeValuePair>[] AnchorPairs()
    {
        List<RangeValuePair>[] pairs = [[], [], []];
        foreach (var channel in pairs)
        foreach (byte value in new byte[] { 0, 32, 64, 96, 160, 192, 224 })
        for (int half = 0; half < 2; half++)
        for (int i = 0; i < 100; i++) channel.Add(new(value, value, half));
        return pairs;
    }

    private static (RangeVerificationFrame Shown, RangeVerificationFrame Reference) Frames(bool full, int seed, string? failure = null)
    {
        // Start with the actual 1280x720 8x2 pattern, then use the production area sampler.
        const int width = 1280, height = 720;
        byte[] source = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var p = VideoRangeColourPattern.Patches[y * 2 / height * 8 + x * 8 / width];
            int i = (y * width + x) * 4;
            source[i] = p.B; source[i + 1] = p.G; source[i + 2] = p.R; source[i + 3] = 255;
        }
        var reference = PresentedFrameRangeModel.AreaDownscale(source, width, height, width * 4, 320, 180, 500);
        byte[] presented = new byte[reference.Pixels.Length];
        var random = new Random(seed);
        for (int y = 0; y < 180; y++)
        for (int x = 0; x < 320; x++)
        {
            int i = (y * 320 + x) * 4;
            bool outlier = random.NextDouble() < 0.015;
            for (int c = 0; c < 3; c++)
            {
                int value = reference.Pixels[i + c];
                double actual = DeviceValue(value, full, c);
                if (failure == "row-band" && value == 128 && ((y - 2) / 4) % 2 != 0) actual += 10;
                if (failure == "different-halves" && ((y - 2) / 4) % 2 != 0) actual += 12;
                if (failure == "nonmonotone") actual = 255 - value;
                if (failure == "few-levels") actual = value / 64 * 80;
                if (failure == "clipped" && value <= 29) actual = 0;
                // Flat H.264 patches have concentrated rounding noise, with rarer tails.
                int noise = random.NextDouble() < 0.04 ? random.Next(-4, 5) : random.Next(-1, 2) + random.Next(-1, 2);
                presented[i + c] = (byte)Math.Clamp(Math.Round(outlier ? random.Next(256) : actual + noise), 0, 255);
            }
            presented[i + 3] = 255;
        }
        return (reference with { Pixels = presented }, reference);
    }

    private static double DeviceValue(int value, bool full, int channel)
    {
        int[] x = [0, 16, 64, 128, 192, 235, 255];
        int[] y = full ? [0, 0, 68, 131, 188, 253, 255] : [12, 30, 74, 130, channel == 0 ? 181 : 183, 232, 255];
        int j = 0;
        while (j < x.Length - 2 && value > x[j + 1]) j++;
        return y[j] + (value - x[j]) * (y[j + 1] - y[j]) / (double)(x[j + 1] - x[j]);
    }
}
