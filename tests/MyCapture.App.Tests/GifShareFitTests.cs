using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifShareFitTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(5_000_000, 4_998_976)]
    [InlineData(10_000_000, 9_998_976)]
    [InlineData(15_000_000, 14_998_500)]
    [InlineData(20_000_000, 19_998_000)]
    public void UpperLimit_IsBelowTheTargetByAtLeastAKilobyteAndOneTenThousandth(long target, long expected)
    {
        Assert.Equal(expected, GifShareFit.UpperLimit(target));
        Assert.True(GifShareFit.UpperLimit(target) < target);
    }

    [Fact]
    public void ShareTargetsAreDecimalMegabytesLikeTheirLabelsAndTheSizeText()
    {
        Assert.Equal([5_000_000L, 10_000_000L, 15_000_000L, 20_000_000L],
            GifSharePolicy.Targets.Where(target => target.Bytes is not null).Select(target => target.Bytes!.Value));
        using var scope = UiText.UseLanguage("ko-KR");
        Assert.Equal("10.0MB", VideoExportDialog.FormatSize(10_000_000));
    }

    [Theory]
    [InlineData(9_999_999, "ko-KR", "9.9MB")]
    [InlineData(9_998_976, "ko-KR", "9.9MB")]
    [InlineData(9_999_999, "en-US", "9.9 MB")]
    [InlineData(999_999, "ko-KR", "999KB")]
    public void FormatSize_CutsInsteadOfRoundingSoAFileUnderALimitNeverReadsAsTheLimit(long bytes, string language, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(expected, VideoExportDialog.FormatSize(bytes));
    }

    [Fact]
    public void RecommendationUsesTheUpperLimitNotTheTarget()
    {
        var measurement = GifPresetMeasurement.Of(GifExportQuality.Standard, new GifSegmentSize(9_999_500, 0, 0, 0), 5_000, 0, startsRange: true);
        GifRecommendation advice = GifSharePolicy.Recommend(
            5_000, 1920, 1080, new Dictionary<GifExportQuality, GifPresetMeasurement> { [GifExportQuality.Standard] = measurement },
            GifExportQuality.Standard, 1, GifSharePolicy.Targets[1], 0.01);
        Assert.NotEqual(GifRecommendationKind.CurrentFits, advice.Kind);
    }

    private static double Square(GifExportQuality q) => (double)q.LongEdge * q.LongEdge * q.FramesPerSecond;

    private static readonly Dictionary<string, Func<GifExportQuality, double>> Models = new()
    {
        ["square"] = Square,
        ["nonlinear"] = q => (Math.Pow(q.LongEdge, 1.5) * q.FramesPerSecond * 400) + (q.FramesPerSecond * 30_000),
        ["noisy"] = q => Square(q) * (1 + (0.04 * Math.Sin(q.LongEdge * 0.37))),
    };

    [Theory]
    [InlineData("square", 5_000_000, 1.2)]
    [InlineData("square", 10_000_000, 1.0)]
    [InlineData("square", 10_000_000, 2.5)]
    [InlineData("square", 20_000_000, 1.6)]
    [InlineData("nonlinear", 10_000_000, 1.3)]
    [InlineData("nonlinear", 15_000_000, 2.0)]
    [InlineData("noisy", 10_000_000, 1.5)]
    [InlineData("noisy", 5_000_000, 3.0)]
    public void Fit_KeepsTheFrameRateAndLandsJustUnderTheLimit(string model, long target, double overshoot)
    {
        Func<GifExportQuality, double> shape = Models[model];
        GifExportQuality start = GifExportQuality.Standard;
        double scale = target * overshoot / shape(start);
        int encodes = 0;
        long Encode(GifExportQuality quality)
        {
            encodes++;
            return (long)(shape(quality) * scale);
        }

        long limit = GifShareFit.UpperLimit(target);
        GifShareFitResult result = GifShareFit.Fit(start, Encode(start), target, Encode);
        Assert.True(result.Fits);
        Assert.Equal(start.FramesPerSecond, result.Quality.FramesPerSecond);
        Assert.True(result.Bytes <= limit);
        Assert.InRange((double)result.Bytes / limit, 0.9, 1.0);
        Assert.True(result.Encodes <= GifShareFit.MaximumEncodes);
        Assert.Equal(result.Encodes, encodes);
        Assert.Equal($"{result.Quality.LongEdge} px · {result.Quality.FramesPerSecond} fps", result.Quality.Label);
    }

    [Fact]
    public void Fit_DropsTheFrameRateOnlyBelowTheSmallestPresetEdge()
    {
        GifExportQuality start = GifExportQuality.Standard;
        long limit = GifShareFit.UpperLimit(10_000_000);
        // At 10 fps even the smallest edge is 1.5 times the limit; at 5 fps it is 0.75 times.
        double scale = limit * 1.5 / Square(new GifExportQuality("m", GifShareFit.MinimumLongEdge, 10));
        long Encode(GifExportQuality quality) => (long)(Square(quality) * scale);
        GifShareFitResult result = GifShareFit.Fit(start, Encode(start), 10_000_000, Encode);
        Assert.True(result.Fits);
        Assert.Equal(5, result.Quality.FramesPerSecond);
        Assert.InRange(result.Quality.LongEdge, GifShareFit.MinimumLongEdge, 960);
        Assert.InRange((double)result.Bytes / limit, 0.9, 1.0);
        Assert.True(result.Encodes <= GifShareFit.MaximumEncodes);
    }

    [Fact]
    public void Fit_KeepsAnExportThatAlreadyFitsWithoutAnotherEncode()
    {
        GifShareFitResult result = GifShareFit.Fit(GifExportQuality.Compact, 9_000_000, 10_000_000,
            _ => throw new InvalidOperationException("No re-encode expected."));
        Assert.True(result.Fits);
        Assert.Same(GifExportQuality.Compact, result.Quality);
        Assert.Equal(1, result.Encodes);
    }

    [Theory]
    [InlineData(9_998_976, true)]
    [InlineData(9_998_977, false)]
    [InlineData(10_000_000, false)]
    [InlineData(12_000_000, false)]
    public void Fit_TreatsTheBoundarySizesCorrectly(long first, bool keepsStart)
    {
        GifShareFitResult result = GifShareFit.Fit(GifExportQuality.Standard, first, 10_000_000, q => (long)(Square(q) * 0.9));
        Assert.True(result.Fits);
        Assert.True(result.Bytes <= 9_998_976);
        Assert.Equal(keepsStart, result.Encodes == 1);
    }

    [Fact]
    public void Fit_ReportsFailureWhenNoEdgeAndNoRateFits()
    {
        GifShareFitResult result = GifShareFit.Fit(GifExportQuality.Standard, 30_000_000, 10_000_000, _ => 11_000_000);
        Assert.False(result.Fits);
        Assert.True(result.Encodes <= GifShareFit.MaximumEncodes);
    }

    [Fact]
    public void Fit_StaysWithinTheEncodeBudgetOnAnAdversarialModel()
    {
        int encodes = 0;
        long Encode(GifExportQuality quality)
        {
            encodes++;
            return quality.LongEdge >= 600 ? 12_000_000 : 9_000_000;
        }

        GifShareFitResult result = GifShareFit.Fit(GifExportQuality.Standard, 30_000_000, 10_000_000, Encode);
        Assert.True(result.Fits);
        Assert.True(encodes + 1 <= GifShareFit.MaximumEncodes);
    }

    [Fact]
    public void RealEncoderResultsStayUnderTheUpperLimit() => RunSta(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("gifshare-").FullName;
        try
        {
            string source = Path.Combine(root, "noise.mp4");
            RecordingResult recording = EncodeNoisyClip(source, 640, 360, 10, 30);
            var document = VideoEditDocument.CreateFor(640, 360, recording.DurationMs);
            using var session = new GifExportSession(recording, document, NullLogger.Instance);
            string current = Path.Combine(root, "fit.gif");
            long Encode(GifExportQuality quality)
            {
                File.Delete(current);
                session.Export(current, quality);
                return new FileInfo(current).Length;
            }

            long standard = Encode(GifExportQuality.Standard);
            Assert.True(standard > 100_000, "The noisy clip must give a size worth fitting.");
            foreach (double fraction in new[] { 0.9, 0.6, 0.35 })
            {
                long target = (long)(standard * fraction);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                GifShareFitResult result = GifShareFit.Fit(GifExportQuality.Standard, standard, target, Encode);
                Assert.True(result.Fits, $"target {target} result {result.Bytes}");
                long final = Encode(result.Quality);
                output.WriteLine($"GSF target={target} before(Standard)={standard / (double)target:P1} after={final / (double)target:P1} quality={result.Quality.LongEdge}px/{result.Quality.FramesPerSecond}fps encodes={result.Encodes} ms={watch.ElapsedMilliseconds}");
                Assert.True(final <= GifShareFit.UpperLimit(target), $"target {target} file {final}");
                Assert.True(result.Encodes <= GifShareFit.MaximumEncodes);
            }
        }
        finally
        {
            TestRecycleBin.DeleteDirectory(root, recursive: true);
        }
    });

    private static RecordingResult EncodeNoisyClip(string path, int width, int height, int fps, int frames)
    {
        var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            for (int index = 0; index < frames; index++)
            {
                var random = new Random(index);
                for (int offset = 0; offset < pixels.Length; offset += 4)
                {
                    pixels[offset] = (byte)(random.Next(0, 256) & 0xE0);
                    pixels[offset + 1] = (byte)(random.Next(0, 256) & 0xE0);
                    pixels[offset + 2] = (byte)(random.Next(0, 256) & 0xE0);
                    pixels[offset + 3] = 255;
                }

                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, index * 1000.0 / fps));
            }

            encoder.Complete();
        }

        return new RecordingResult(path, frames * 1000.0 / fps, fps, frames, width, height);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
    }
}
