using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class MeasuredRangeCurveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(4.494, 6.843)]
    [InlineData(3.154, 3.587)]
    public void DeviceErrorsCannotSelectAStandardModel(double best, double second) =>
        Assert.False(PresentedFrameRangeModel.ClearlyFits(best, second));

    [Fact]
    public void DecisiveWin11RetainedModelStillSelectsExpand()
    {
        Assert.True(PresentedFrameRangeModel.ClearlyFits(0.211, 10.120));
        var (shown, reference) = Frames(false, standardRetained: true);
        var result = PresentedFrameRangeModel.Compare(shown, reference);
        Assert.Equal("model", result.Kind);
        Assert.Equal(VideoRangeCorrection.Expand, result.Correction);
        Assert.Null(result.Mapping);
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 12)]
    public void DeviceCurvesWithCodecNoiseAndOutliersAreMeasured(bool full, int tolerance)
    {
        var (shown, reference) = Frames(full);
        var result = PresentedFrameRangeModel.Compare(shown, reference);
        output.WriteLine($"full={full}; errors={result.Errors}; {result.MappingDiagnostics?.Summary}");
        Assert.Null(result.Model);
        Assert.Equal("measured", result.Kind);
        Assert.True(result.Mapping is not null, result.Details);
        Assert.InRange(result.MappingDiagnostics!.Residual!.Value, 0, 2.5);
        Assert.InRange(result.MappingDiagnostics.HeldOutResidual!.Value, 0, 2.5);
        Assert.All(result.MappingDiagnostics.Coverage, c => Assert.True(c.Sufficient));
        byte[][] curves = result.Mapping.Channels();
        foreach (byte level in Levels)
        for (int c = 0; c < 3; c++)
        {
            int presented = (int)Math.Round(DeviceValue(level, full, c));
            Assert.True(Math.Abs(curves[c][presented] - level) <= tolerance,
                $"source={level}, shown={presented}, corrected={curves[c][presented]}, channel={c}; {result.Details}");
        }
        Assert.All(curves, curve => Assert.True(curve.Zip(curve.Skip(1), (a, b) => a <= b).All(v => v)));
    }

    [Theory]
    [InlineData("coverage")]
    [InlineData("nonmonotone")]
    [InlineData("split")]
    [InlineData("split-local")]
    public async Task UnsupportedCurvesEndInNoneAfterBoundedRetries(string failure)
    {
        var (shown, reference) = Frames(false, failure: failure);
        var result = PresentedFrameRangeModel.Compare(shown, reference, forceMeasured: true);
        Assert.Null(result.Model); Assert.Null(result.Mapping);
        Assert.StartsWith("mapping-", result.Status);
        if (failure is "split" or "split-local")
        {
            Assert.True(result.MappingDiagnostics!.Residual <= 2.5, result.Details);
            Assert.Equal(failure == "split" ? "mapping-held-out" : "mapping-split-disagreement", result.Status);
        }
        double now = 0;
        var applied = new List<RangeCorrectionDecision>();
        var logs = new List<RangeVerificationResult>();
        using var session = new PresentedFrameVerification(new(VideoPlayerKind.Gallery, true, 0), new(),
            () => now, () => true, () => shown, (_, _) => Task.FromResult(result), applied.Add, logs.Add);
        foreach (int delay in new[] { 300, 500, 2000, 5000 }) { now += delay; await session.TickAsync(); }
        Assert.Equal("fallback", (await session.Completion).Kind);
        Assert.Equal(VideoRangeCorrection.None, applied[^1].Correction);
        Assert.Single(logs); Assert.Equal(4, session.Decodes);
    }

    [Fact]
    public async Task CachedMeasuredCurveSeedsSameHostRangeScaleAndStillVerifiesItself()
    {
        var (shown, reference) = Frames(false);
        var measured = PresentedFrameRangeModel.Compare(shown, reference);
        Assert.NotNull(measured.Mapping);
        var key = new RangeVerificationCacheKey(VideoPlayerKind.Gallery, true, 0);
        var cache = new RangeVerificationCache();
        double now = 0;
        using var first = new PresentedFrameVerification(key, cache, () => now, () => true, () => shown,
            (_, _) => Task.FromResult(measured), _ => { }, _ => { });
        now = 300; await first.TickAsync();
        Assert.Same(measured.Mapping, cache.GuessDecision(key).Mapping);
        foreach (var other in new[] { key with { Player = VideoPlayerKind.Editor }, key with { StudioRange = false }, key with { ScaleBucket = 1 } })
            Assert.Null(cache.GuessDecision(other).Mapping);
        RangeCorrectionDecision? applied = null;
        var rejected = measured with { Mapping = null, Status = "mapping-split-disagreement" };
        using var later = new PresentedFrameVerification(key, cache, () => now, () => true, () => shown,
            (_, _) => Task.FromResult(rejected), d => applied = d, _ => { });
        Assert.Same(measured.Mapping, applied!.Mapping); Assert.False(later.Completion.IsCompleted);
        foreach (int delay in new[] { 300, 500, 2000, 5000 }) { now += delay; await later.TickAsync(); }
        Assert.Equal(VideoRangeCorrection.None, applied.Correction);
        Assert.Null(cache.GuessDecision(key).Mapping);
    }

    [Fact]
    public void MeasuredRangeShaderBytecodeIsItsCheckedInSource() => Assert.Equal(
        ShaderCompiler.Disassemble(ShaderCompiler.Compile(MeasuredRangeVideoEffect.Source, "main", "ps_2_0")),
        ShaderCompiler.Disassemble(MeasuredRangeVideoEffect.BytecodeForTest.ToArray()));

    [Theory]
    [InlineData(0)] [InlineData(128)] [InlineData(255)]
    public void LookupUsesIndependentChannelsTexelCentresAndPremultipliedAlpha(byte alpha) => StaTestHost.Run(() =>
    {
        byte[][] curves = Enumerable.Range(0, 3).Select(c => Enumerable.Range(0, 256)
            .Select(i => (byte)Math.Clamp(Math.Round(Math.Pow(i / 255.0, 0.7 + c * 0.5) * 255), 0, 255)).ToArray()).ToArray();
        var mapping = new MeasuredRangeCurve(Convert.ToBase64String(curves[0]), Convert.ToBase64String(curves[1]), Convert.ToBase64String(curves[2]));
        // One physical pixel per LUT entry isolates addressing/alpha from WPF's separate
        // ImageBrush resampling when the effect surface is smaller than the 256-entry table.
        // The gallery/editor tests cover the actual scaled player surfaces and final pixels.
        byte[] source = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        byte[] pixels = new byte[source.Length * 4];
        for (int i = 0; i < source.Length; i++)
        {
            for (int c = 0; c < 3; c++) pixels[i * 4 + c] = (byte)(source[i] * alpha / 255);
            pixels[i * 4 + 3] = alpha;
        }
        var bitmap = BitmapSource.Create(source.Length, 1, 96, 96, PixelFormats.Pbgra32, null, pixels, pixels.Length);
        var image = new Image { Width = source.Length, Height = 16, Source = bitmap, Stretch = Stretch.Fill,
            Effect = new MeasuredRangeVideoEffect(mapping) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        image.Measure(new Size(image.Width, 16)); image.Arrange(new Rect(0, 0, image.Width, 16));
        var shown = new RenderTargetBitmap((int)image.Width, 16, 96, 96, PixelFormats.Pbgra32);
        shown.Render(image);
        for (int i = 0; i < source.Length; i++)
        {
            byte[] actual = new byte[4]; shown.CopyPixels(new Int32Rect(i, 8, 1, 1), actual, 4, 0);
            Assert.Equal(alpha, actual[3]);
            for (int c = 0; c < 3; c++)
            {
                int straight = alpha == 0 ? 0 : (int)Math.Round(pixels[i * 4 + c] * 255.0 / alpha);
                double expected = curves[c][straight] * alpha / 255.0;
                Assert.True(Math.Abs(actual[c] - expected) <= 2,
                    $"alpha={alpha}, level={source[i]}, channel={c}, straight={straight}, actual={actual[c]}, expected={expected}");
            }
        }
    });

    internal static readonly byte[] Levels = [0, 16, 32, 48, 64, 96, 128, 160, 192, 216, 235, 255];
    private static double DeviceValue(byte value, bool full, int channel)
    {
        int[] x = full ? [0, 16, 32, 64, 128, 192, 235, 255] : [0, 16, 64, 192, 235, 255];
        int[] y = full ? [0, 0, 32, 64, 128, 192, 253, 255] : [12, 30, 74, channel == 0 ? 181 : 183, 232, 255];
        int j = 0; while (j < x.Length - 2 && value > x[j + 1]) j++;
        return y[j] + (value - x[j]) * (y[j + 1] - y[j]) / (double)(x[j + 1] - x[j]);
    }

    private static (RangeVerificationFrame Shown, RangeVerificationFrame Reference) Frames(bool full,
        bool standardRetained = false, string? failure = null)
    {
        const int width = 480, height = 180;
        byte[] shown = new byte[width * height * 4], reference = new byte[shown.Length];
        var random = new Random(84016);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            bool outlier = random.NextDouble() < 0.05;
            for (int c = 0; c < 3; c++)
            {
                byte value = Levels[(x / 40 + c) % Levels.Length];
                reference[offset + c] = value;
                double actual = standardRetained ? 16 + value * 219.0 / 255 : DeviceValue(value, full, c);
                if (failure == "coverage") actual = 80 + value * 0.3;
                if (failure == "nonmonotone") actual = 255 - value;
                if (failure == "split" && ((y - 2) / 4) % 2 != 0) actual += 4;
                if (failure == "split-local" && value == 128 && ((y - 2) / 4) % 2 != 0) actual += 6;
                shown[offset + c] = (byte)Math.Clamp(Math.Round(outlier ? random.Next(256) : actual + random.Next(-1, 2)), 0, 255);
            }
            shown[offset + 3] = reference[offset + 3] = 255;
        }
        return (new(width, height, shown, 0), new(width, height, reference, 0));
    }
}
