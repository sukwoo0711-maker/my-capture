using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in decomposition of <see cref="Mp4Nv12ExportTests.PlainTrim_PassesNv12Through_WithHigherFidelityAndTheSameTimeline"/>
/// (#236). It reruns that test's two trims through the product pipeline, keeps the NV12 frames each
/// arm hands the encoder and the transform Media Foundation chose, and splits each arm's RGB squared
/// error between the macroblocks whose encoder input differs between the arms and the macroblocks
/// whose input is byte-identical. The arms' own frames and two hybrids are replayed through the same
/// encoder configuration: the RGB arm's luma with the NV12 arm's chroma, and the NV12 arm with only
/// its out-of-gamut pixels (and their shared chroma) taken from the RGB arm. Both arms are also
/// replayed at two constant-quality settings. Nothing here asserts the fidelity criterion, which
/// stays in Mp4Nv12ExportTests.
/// </summary>
public sealed class Mp4Nv12QualityDecompositionTests(ITestOutputHelper output)
{
    private const int Fps = 30, SourceFrames = 45, Frames = 30;
    private const double InMs = 300, OutMs = 1300;

    // Nv12FrameConverter's full-size BT.601/BT.709 conversion: 8-bit fixed point, rounded, then clamped.
    private const int Shift = 8, Rounding = 1 << (Shift - 1);

    [Nv12QualityDecompositionTheory]
    [InlineData(640, 360, false, false)]
    [InlineData(640, 360, false, true)]
    [InlineData(960, 540, false, false)]
    [InlineData(960, 540, false, true)]
    [InlineData(1280, 720, false, false)]
    [InlineData(1280, 720, false, true)]
    [InlineData(1280, 720, true, false)]
    [InlineData(1280, 720, true, true)]
    [InlineData(1920, 1080, false, false)]
    [InlineData(1920, 1080, false, true)]
    public void PlainTrim_SplitsEachArmsErrorByWhereTheEncoderInputsDiffer(
        int width, int height, bool softwareSource, bool softwareTrim) => StaTestHost.Run(() =>
    {
        string label = $"{width}x{height}-source-{Kind(softwareSource)}-trim-{Kind(softwareTrim)}";
        string evidence = TestDiagnosticArtifacts.Create("MYCAPTURE_NV12_QUALITY_EVIDENCE", "plain-trim-decomposition-" + label)
            ?? throw new InvalidOperationException("Set MYCAPTURE_NV12_QUALITY_EVIDENCE to a child of the repository's artifacts directory.");
        Assert.False(MediaFoundationVideoEncoder.ForceSoftwareForTest);
        string root = OwnedTestDirectory.Create("mc-nv12-split-");
        try
        {
            RecordingResult recording;
            MediaFoundationVideoEncoder.ForceSoftwareForTest = softwareSource;
            try { recording = SyntheticDocumentClip.Encode(Path.Combine(root, "source.mp4"), width, height, Fps, SourceFrames); }
            finally { MediaFoundationVideoEncoder.ForceSoftwareForTest = false; }

            Arm nv12 = Trim(root, "nv12", recording, null, softwareTrim);
            Arm rgb = Trim(root, "rgb", recording, new Mp4RenderOptions { PassThroughNv12 = false }, softwareTrim);
            Assert.StartsWith("nv12-", nv12.Statistics.FrameSource, StringComparison.Ordinal);
            Assert.Equal("rgb-software", rgb.Statistics.FrameSource);
            Assert.Equal(nv12.Options with { OutputPath = "" }, rgb.Options with { OutputPath = "" });
            Assert.Equal(nv12.Frames.Select(frame => frame.TimestampMs), rgb.Frames.Select(frame => frame.TimestampMs));

            VideoYuvEncoding encoding = VideoFrameRenderPipeline.DefaultYuvEncoding(height);
            var references = new byte[Frames][];
            using (var reader = new MediaFoundationVideoFrameReader(recording.OutputPath))
            {
                for (int index = 0; index < Frames; index++)
                {
                    references[index] = reader.ReadFrameAt(InMs + (index * 1000.0 / Fps)).Pixels;
                }
            }

            InputSplit split = SplitInputs(nv12, rgb, width, height, encoding);
            RegionError nv12Error = Score(nv12.OutputPath, references, split.Differing, width, height);
            RegionError rgbError = Score(rgb.OutputPath, references, split.Differing, width, height);
            byte[][] nv12Frames = nv12.Frames.Select(frame => frame.Pixels).ToArray();
            byte[][] rgbFrames = rgb.Frames.Select(frame => frame.Pixels).ToArray();
            var replays = new List<object>();
            // The trim's own bitrate mode first, then constant quality, where the encoder cannot move
            // bits between macroblocks to balance a fixed budget.
            foreach ((string name, byte[][] frames, int? quality) in new (string, byte[][], int?)[]
                     {
                         ("nv12-replay", nv12Frames, null),
                         ("rgb-replay", rgbFrames, null),
                         ("rgb-luma-nv12-chroma", split.LumaFromRgb, null),
                         ("nv12-with-rgb-out-of-gamut", split.GamutOnly, null),
                         ("nv12-quality-50", nv12Frames, 50),
                         ("rgb-quality-50", rgbFrames, 50),
                         ("nv12-quality-70", nv12Frames, 70),
                         ("rgb-quality-70", rgbFrames, 70),
                     })
            {
                string path = Replay(root, name, nv12, frames, softwareTrim, quality);
                RegionError error = Score(path, references, split.Differing, width, height);
                replays.Add(new { name, constantQuality = quality, bytes = new FileInfo(path).Length, psnr = Psnr(error.Total, width, height),
                    sseInDiffering = error.InDiffering, sseElsewhere = error.Elsewhere,
                    inputPsnr = Psnr(InputError(frames, references, width, height, encoding), width, height) });
            }

            double? nv12Psnr = Psnr(nv12Error.Total, width, height), rgbPsnr = Psnr(rgbError.Total, width, height);
            var report = new
            {
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                platformAssemblySha256 = TestDiagnosticArtifacts.HashFile(typeof(MediaFoundationVideoEncoder).Assembly.Location),
                width, height, fps = Fps, sourceFrames = SourceFrames, trimInMs = InMs, trimOutMs = OutMs,
                softwareSource, softwareTrim, encoding,
                source = new
                {
                    bytes = new FileInfo(recording.OutputPath).Length,
                    sha256 = TestDiagnosticArtifacts.HashFile(recording.OutputPath),
                },
                encoderOptions = nv12.Options with { OutputPath = Path.GetFileName(nv12.Options.OutputPath) },
                arms = new[] { Describe(nv12, nv12Error, references, width, height, encoding), Describe(rgb, rgbError, references, width, height, encoding) },
                inputs = split.Counts,
                macroblocks = new { differing = split.Differing.Sum(map => map.Count(value => value)), total = split.Differing.Sum(map => map.Length) },
                summary = new
                {
                    nv12MinusRgbPsnr = nv12Psnr - rgbPsnr,
                    nv12SseInDifferingAtMostRgb = nv12Error.InDiffering <= rgbError.InDiffering,
                    nv12SseElsewhereAtMostRgb = nv12Error.Elsewhere <= rgbError.Elsewhere,
                    framesWhereRgbIsCloser = Enumerable.Range(0, Frames).Count(index => rgbError.PerFrame[index] < nv12Error.PerFrame[index]),
                },
                replays,
                perFrame = Enumerable.Range(0, Frames).Select(index => new
                {
                    index, nv12Sse = nv12Error.PerFrame[index], rgbSse = rgbError.PerFrame[index],
                    differingMacroblocks = split.Differing[index].Count(value => value),
                }).ToArray(),
            };
            TestDiagnosticArtifacts.WriteJson(evidence, "report.json", report);
            output.WriteLine(FormattableString.Invariant($"DECOMPOSITION {label} nv12={nv12Psnr:F4} rgb={rgbPsnr:F4} delta={nv12Psnr - rgbPsnr:F4} inDiffering nv12/rgb={nv12Error.InDiffering:F0}/{rgbError.InDiffering:F0} elsewhere nv12/rgb={nv12Error.Elsewhere:F0}/{rgbError.Elsewhere:F0} evidence={evidence}"));
        }
        finally
        {
            MediaFoundationVideoEncoder.ForceSoftwareForTest = false;
            OwnedTestDirectory.Delete(root);
        }
    });

    private static string Kind(bool software) => software ? "software" : "default";

    private static Arm Trim(string root, string name, RecordingResult recording, Mp4RenderOptions? renderOptions, bool softwareTrim)
    {
        string path = Path.Combine(root, name + ".mp4");
        var encoders = new List<RecordingEncoder>();
        Mp4RenderStatistics statistics;
        MediaFoundationVideoEncoder.ForceSoftwareForTest = softwareTrim;
        try
        {
            statistics = TrimReencoder.ReencodeWithStatistics(
                recording.OutputPath,
                path,
                InMs,
                OutMs,
                recording,
                options =>
                {
                    var encoder = new RecordingEncoder(options);
                    encoders.Add(encoder);
                    return encoder;
                },
                NullLogger.Instance,
                renderOptions: renderOptions);
        }
        finally
        {
            MediaFoundationVideoEncoder.ForceSoftwareForTest = false;
        }

        RecordingEncoder completed = Assert.Single(encoders, encoder => encoder.Completed);
        Assert.Equal(Frames, completed.Frames.Count);
        return new Arm(name, path, statistics, completed.Options, completed.Frames, completed.Transforms, encoders.Count);
    }

    private static string Replay(string root, string name, Arm template, byte[][] frames, bool softwareTrim, int? constantQuality)
    {
        string path = Path.Combine(root, name + ".mp4");
        VideoEncoderOptions options = template.Options with
        {
            OutputPath = path,
            ConstantQuality = constantQuality ?? template.Options.ConstantQuality,
        };
        MediaFoundationVideoEncoder.ForceSoftwareForTest = softwareTrim;
        try
        {
            using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
            for (int index = 0; index < frames.Length; index++)
            {
                encoder.WriteFrame(new EncoderFrame(frames[index], options.Width, options.Height, options.Width, template.Frames[index].TimestampMs)
                {
                    Format = VideoEncoderInputFormat.Nv12,
                });
            }

            encoder.Complete();
        }
        finally
        {
            MediaFoundationVideoEncoder.ForceSoftwareForTest = false;
        }

        return path;
    }

    private static InputSplit SplitInputs(Arm nv12, Arm rgb, int width, int height, VideoYuvEncoding encoding)
    {
        int luma = width * height, columns = (width + 15) / 16, rows = (height + 15) / 16;
        Nv12FrameConverter.Coefficients c = Nv12FrameConverter.Coefficients.For(encoding);
        var differing = new bool[Frames][];
        var gamutOnly = new byte[Frames][];
        var lumaFromRgb = new byte[Frames][];
        long outOfGamut = 0, redHigh = 0, redLow = 0, greenHigh = 0, greenLow = 0, blueHigh = 0, blueLow = 0;
        long lumaDiffering = 0, lumaDifferingInGamut = 0, chromaDiffering = 0, chromaDifferingWithoutOutOfGamut = 0, largestLumaChange = 0;
        for (int index = 0; index < Frames; index++)
        {
            byte[] direct = nv12.Frames[index].Pixels, roundTrip = rgb.Frames[index].Pixels;
            bool[] map = differing[index] = new bool[columns * rows];
            byte[] gamut = gamutOnly[index] = (byte[])direct.Clone();
            byte[] hybrid = lumaFromRgb[index] = (byte[])direct.Clone();
            Array.Copy(roundTrip, 0, hybrid, 0, luma);
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    int pair = luma + ((y / 2) * width) + x;
                    int u = direct[pair] - 128, v = direct[pair + 1] - 128;
                    bool anyOutOfGamut = false;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sample = ((y + dy) * width) + x + dx;
                            int l = c.Luma * (direct[sample] - c.LumaOffset);
                            int blue = (l + (c.BlueU * u) + Rounding) >> Shift;
                            int green = (l + Rounding - (c.GreenU * u) - (c.GreenV * v)) >> Shift;
                            int red = (l + (c.RedV * v) + Rounding) >> Shift;
                            bool clipped = blue is < 0 or > 255 || green is < 0 or > 255 || red is < 0 or > 255;
                            if (direct[sample] != roundTrip[sample])
                            {
                                map[((y + dy) / 16 * columns) + ((x + dx) / 16)] = true;
                                lumaDiffering++;
                                if (!clipped) lumaDifferingInGamut++;
                                largestLumaChange = Math.Max(largestLumaChange, Math.Abs(direct[sample] - roundTrip[sample]));
                            }

                            if (!clipped) continue;
                            outOfGamut++;
                            anyOutOfGamut = true;
                            gamut[sample] = roundTrip[sample];
                            if (red > 255) redHigh++; else if (red < 0) redLow++;
                            if (green > 255) greenHigh++; else if (green < 0) greenLow++;
                            if (blue > 255) blueHigh++; else if (blue < 0) blueLow++;
                        }
                    }

                    for (int offset = 0; offset < 2; offset++)
                    {
                        if (direct[pair + offset] == roundTrip[pair + offset]) continue;
                        map[(y / 16 * columns) + (x / 16)] = true;
                        chromaDiffering++;
                        if (!anyOutOfGamut) chromaDifferingWithoutOutOfGamut++;
                    }

                    if (anyOutOfGamut)
                    {
                        gamut[pair] = roundTrip[pair];
                        gamut[pair + 1] = roundTrip[pair + 1];
                    }
                }
            }
        }

        var counts = new
        {
            pixels = (long)luma * Frames,
            outOfGamutPixels = outOfGamut,
            clippedChannels = new { redHigh, redLow, greenHigh, greenLow, blueHigh, blueLow },
            lumaSamplesDiffering = lumaDiffering,
            lumaSamplesDifferingInGamut = lumaDifferingInGamut,
            largestLumaChange,
            chromaSamplesDiffering = chromaDiffering,
            chromaSamplesDifferingWithoutOutOfGamutPixel = chromaDifferingWithoutOutOfGamut,
        };
        return new InputSplit(differing, gamutOnly, lumaFromRgb, counts);
    }

    private static RegionError Score(string path, byte[][] references, bool[][] differing, int width, int height)
    {
        int columns = (width + 15) / 16, luma = width * height;
        var perFrame = new double[Frames];
        double inDiffering = 0, elsewhere = 0;
        using var reader = new MediaFoundationVideoFrameReader(path);
        for (int index = 0; index < Frames; index++)
        {
            byte[] expected = references[index], actual = reader.ReadFrameAt(index * 1000.0 / Fps).Pixels;
            Assert.Equal(expected.Length, actual.Length);
            for (int pixel = 0; pixel < luma; pixel++)
            {
                double error = 0;
                for (int channel = 0; channel < 3; channel++)
                {
                    double difference = expected[(pixel * 4) + channel] - actual[(pixel * 4) + channel];
                    error += difference * difference;
                }

                perFrame[index] += error;
                if (differing[index][((pixel / width) / 16 * columns) + ((pixel % width) / 16)]) inDiffering += error;
                else elsewhere += error;
            }
        }

        return new RegionError(perFrame, inDiffering, elsewhere);
    }

    private static double InputError(byte[][] frames, byte[][] references, int width, int height, VideoYuvEncoding encoding)
    {
        double error = 0;
        for (int index = 0; index < frames.Length; index++)
        {
            byte[] decoded = Nv12FrameConverter.ToBgra(frames[index], width, height, encoding, width, height);
            for (int offset = 0; offset < decoded.Length; offset += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    double difference = references[index][offset + channel] - decoded[offset + channel];
                    error += difference * difference;
                }
            }
        }

        return error;
    }

    /// <summary>The trim test's PSNR (RGB channels of all frames); null when the arm is exact.</summary>
    private static double? Psnr(double squaredError, int width, int height) =>
        squaredError <= 0 ? null : 10 * Math.Log10(255.0 * 255.0 / (squaredError / (width * height * 3.0 * Frames)));

    private static object Describe(Arm arm, RegionError error, byte[][] references, int width, int height, VideoYuvEncoding encoding) => new
    {
        arm.Name,
        arm.Statistics,
        arm.EncodersCreated,
        transforms = arm.Transforms,
        bytes = new FileInfo(arm.OutputPath).Length,
        psnr = Psnr(error.Total, width, height),
        sse = error.Total,
        sseInDiffering = error.InDiffering,
        sseElsewhere = error.Elsewhere,
        inputPsnr = Psnr(InputError(arm.Frames.Select(frame => frame.Pixels).ToArray(), references, width, height, encoding), width, height),
    };

    private static object DescribeTransforms(IReadOnlyDictionary<string, object?> diagnostics)
    {
        var transforms = new List<object>();
        if (diagnostics.TryGetValue("transforms", out object? value) && value is List<object> entries)
        {
            foreach (Dictionary<string, object?> entry in entries.OfType<Dictionary<string, object?>>())
            {
                transforms.Add(new
                {
                    index = entry.GetValueOrDefault("index"),
                    classId = entry.GetValueOrDefault("classId"),
                    persistClassId = entry.GetValueOrDefault("persistClassId"),
                    friendlyName = entry.GetValueOrDefault("friendlyName"),
                    friendlyNameHresult = entry.GetValueOrDefault("friendlyNameHresult"),
                    hardwareUrlPresent = entry.GetValueOrDefault("hardwareUrlPresent"),
                });
            }
        }

        return new { transforms, enumerationEnd = diagnostics.GetValueOrDefault("transformEnumerationEnd") };
    }

    private sealed record Arm(
        string Name,
        string OutputPath,
        Mp4RenderStatistics Statistics,
        VideoEncoderOptions Options,
        IReadOnlyList<RecordedFrame> Frames,
        object? Transforms,
        int EncodersCreated);

    private sealed record RecordedFrame(byte[] Pixels, double TimestampMs);

    private sealed record InputSplit(bool[][] Differing, byte[][] GamutOnly, byte[][] LumaFromRgb, object Counts);

    private sealed record RegionError(double[] PerFrame, double InDiffering, double Elsewhere)
    {
        internal double Total => InDiffering + Elsewhere;
    }

    /// <summary>Hands every call to a real Media Foundation encoder and keeps an exact copy of each NV12 input.</summary>
    private sealed class RecordingEncoder(VideoEncoderOptions options) : IVideoEncoder
    {
        private readonly MediaFoundationVideoEncoder _inner = new(options, NullLogger.Instance);

        internal VideoEncoderOptions Options => options;
        internal List<RecordedFrame> Frames { get; } = [];
        internal object? Transforms { get; private set; }
        internal bool Completed { get; private set; }

        public int Width => _inner.Width;
        public int Height => _inner.Height;

        public void WriteFrame(in EncoderFrame frame)
        {
            Assert.Equal(VideoEncoderInputFormat.Nv12, frame.Format);
            Assert.Equal((options.Width, options.Height, options.Width), (frame.Width, frame.Height, frame.Stride));
            int length = BgraToNv12Converter.RequiredLength(options.Width, options.Height);
            Assert.True(frame.Pixels.Length >= length, "The encoder received a short NV12 frame.");
            Frames.Add(new RecordedFrame(frame.Pixels.AsSpan(0, length).ToArray(), frame.TimestampMs));
            _inner.WriteFrame(frame);
        }

        public void Complete()
        {
            Transforms = DescribeTransforms(_inner.CaptureDiagnostics());
            _inner.Complete();
            Completed = true;
        }

        public void Dispose() => _inner.Dispose();
    }
}

internal sealed class Nv12QualityDecompositionTheoryAttribute : TheoryAttribute
{
    public Nv12QualityDecompositionTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_NV12_DECOMPOSITION") != "1")
            Skip = "Set MYCAPTURE_NV12_DECOMPOSITION=1 and MYCAPTURE_NV12_QUALITY_EVIDENCE to a repository artifacts child for the #236 decomposition.";
    }
}
