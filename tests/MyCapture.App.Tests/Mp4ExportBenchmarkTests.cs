using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in MP4 export benchmark on the shared synthetic 1080p 30fps 10 s document clip.
/// Set <c>MYCAPTURE_MP4_EXPORT_BENCHMARK</c> to an output directory to run it, and
/// <c>MYCAPTURE_MP4_EXPORT_LABEL</c> to name the result set (<c>before</c> is the baseline
/// later sets are compared with). Timing assertions run only when
/// <c>MYCAPTURE_MP4_EXPORT_PERF_ASSERT=1</c>, so ordinary CI never depends on machine speed.
/// </summary>
/// <remarks>
/// Stages are timed around the encoder: opening it, <see cref="IVideoEncoder.WriteFrame"/>
/// (colour conversion inside the Sink Writer plus H.264 submission) and
/// <see cref="IVideoEncoder.Complete"/> (drain and MP4 finalise). Everything else in the export
/// is decode, colour conversion and composition; decode-only passes over the same schedule
/// split that remainder.
/// </remarks>
public sealed class Mp4ExportBenchmarkTests
{
    private const string OutputVariable = "MYCAPTURE_MP4_EXPORT_BENCHMARK";
    private const string LabelVariable = "MYCAPTURE_MP4_EXPORT_LABEL";
    private const string RoundsVariable = "MYCAPTURE_MP4_EXPORT_ROUNDS";
    private const string AssertVariable = "MYCAPTURE_MP4_EXPORT_PERF_ASSERT";

    private const int Width = GifExportEngineBenchmarkTests.SourceWidth;
    private const int Height = GifExportEngineBenchmarkTests.SourceHeight;
    private const int Fps = GifExportEngineBenchmarkTests.SourceFps;
    private const double TrimInMs = 1000;
    private const double TrimOutMs = 9000;
    private const int ReductionPercent = 50;

    private sealed record Scenario(
        string Name,
        IReadOnlyList<TimedTextOverlay> Overlays,
        int Reduction,
        Mp4RenderOptions? Options = null);

    private static readonly Mp4RenderOptions RgbPath = new() { PassThroughNv12 = false };
    private static readonly Mp4RenderOptions SoftwareDecode = new() { PreferHardwareDecode = false };
    private static readonly Mp4RenderOptions FullCompose = new() { BlendCachedLayers = false };

    /// <summary>
    /// The production scenarios, then the same work through the original RGB path, through
    /// software NV12 decode and with full-frame WPF composition of layered frames, so one run also
    /// compares the paths under identical conditions.
    /// </summary>
    private static readonly Scenario[] Scenarios =
    [
        new("a-trim", [], 0),
        new("b-text-3s", [Caption(3000, 6000)], 0),
        new("b-text-all", [Caption(TrimInMs, TrimOutMs)], 0),
        new("c-reduce-50", [], ReductionPercent),
        new("a-trim-rgb-path", [], 0, RgbPath),
        new("b-text-3s-rgb-path", [Caption(3000, 6000)], 0, RgbPath),
        new("b-text-all-rgb-path", [Caption(TrimInMs, TrimOutMs)], 0, RgbPath),
        new("a-trim-software-decode", [], 0, SoftwareDecode),
        new("b-text-3s-software-decode", [Caption(3000, 6000)], 0, SoftwareDecode),
        new("b-text-3s-full-compose", [Caption(3000, 6000)], 0, FullCompose),
        new("b-text-all-full-compose", [Caption(TrimInMs, TrimOutMs)], 0, FullCompose),
    ];

    [Fact]
    public void Benchmark_SyntheticDocumentClip() => BenchmarkSta.Run(() =>
    {
        string? root = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        root = Path.GetFullPath(root);
        string label = Environment.GetEnvironmentVariable(LabelVariable) is { Length: > 0 } named ? named : "run";
        int rounds = int.TryParse(Environment.GetEnvironmentVariable(RoundsVariable), out int parsed)
            ? Math.Clamp(parsed, 1, 10)
            : 3;
        string output = Path.Combine(root, label);
        Directory.CreateDirectory(output);
        RecordingResult recording = GifExportEngineBenchmarkTests.EnsureSyntheticClip(
            Path.Combine(root, "source-1080p30-10s.mp4"));

        // Warm the codec, D3D and JIT paths once so the first measured round is not a cold start.
        _ = Export(recording, Scenarios[0], Path.Combine(output, "warmup.mp4"), new StageClock());

        var samples = new Dictionary<string, List<double>>();
        var details = new Dictionary<string, object>();
        void Add(string name, double ms) =>
            (samples.TryGetValue(name, out List<double>? list) ? list : samples[name] = []).Add(ms);

        for (int round = 0; round < rounds; round++)
        {
            Add("decode-rgb-software", MeasureDecode(recording.OutputPath, null));
            Add("decode-nv12-software", MeasureDecode(
                recording.OutputPath,
                new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 }));
            Add("decode-nv12-hardware", MeasureDecode(
                recording.OutputPath,
                new MediaFoundationVideoFrameReaderOptions
                {
                    PreferHardwareDecode = true,
                    OutputFormat = DecodedVideoPixelFormat.Nv12,
                }));

            foreach (Scenario scenario in Scenarios)
            {
                var clock = new StageClock();
                long started = Stopwatch.GetTimestamp();
                ScenarioResult result = Export(recording, scenario, Path.Combine(output, scenario.Name + ".mp4"), clock);
                double total = Stopwatch.GetElapsedTime(started).TotalMilliseconds - result.CopyMs;
                Add(scenario.Name, total);
                Add(scenario.Name + "/encoder-open", clock.OpenMs);
                Add(scenario.Name + "/encoder-write", clock.WriteMs);
                Add(scenario.Name + "/encoder-complete", clock.CompleteMs);
                Add(scenario.Name + "/decode-compose", total - clock.OpenMs - clock.WriteMs - clock.CompleteMs);
                details[scenario.Name] = result with { CopyMs = 0 };
            }
        }

        var summary = samples.ToDictionary(
            pair => pair.Key,
            pair => new { MedianMs = Median(pair.Value), MinMs = pair.Value.Min(), RawMs = pair.Value });
        details["quality"] = MeasureQuality(recording, output);
        File.WriteAllText(
            Path.Combine(output, "results.json"),
            JsonSerializer.Serialize(
                new
                {
                    Label = label,
                    Utc = DateTimeOffset.UtcNow,
                    Environment.ProcessorCount,
                    Rounds = rounds,
                    Summary = summary,
                    Details = details,
                },
                JsonOptions));

        CompareWithBaseline(root, label);
        if (Environment.GetEnvironmentVariable(AssertVariable) == "1")
        {
            // Opt-in only. Reference PC (lead baseline before NV12): trim 3,063 ms, 3 s caption
            // 5,590 ms. Require a clear win over the RGB path measured in the same run.
            Assert.True(
                Median(samples["a-trim"]) < 0.6 * Median(samples["a-trim-rgb-path"]),
                "A plain trim through NV12 did not clearly beat the RGB path.");
            Assert.True(
                Median(samples["b-text-3s"]) < 0.5 * Median(samples["b-text-3s-rgb-path"]),
                "A captioned export through NV12 did not clearly beat the RGB path.");
        }
    });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static TimedTextOverlay Caption(double startMs, double endMs) => new()
    {
        Text = "MyCapture 시간 텍스트 레이어",
        StartMs = startMs,
        EndMs = endMs,
        Placement = VideoTextPlacement.Bottom,
    };

    private sealed record ScenarioResult(
        int Frames,
        string FrameSource,
        int ComposedFrames,
        long Bytes,
        long? ReducedBytes,
        int? ReducedBitrate,
        double CopyMs);

    private static ScenarioResult Export(RecordingResult recording, Scenario scenario, string destination, StageClock clock)
    {
        if (scenario.Reduction == 0)
        {
            Mp4RenderStatistics single = Reencode(recording, scenario, destination, clock, bitrate: null);
            return new ScenarioResult(
                single.Frames, single.FrameSource, single.ComposedFrames, new FileInfo(destination).Length, null, null, 0);
        }

        int standard = VideoEncoderOptions.DeriveBitrate(recording.Width, recording.Height, recording.Fps);
        Mp4RenderStatistics? statistics = null;
        int? reducedBitrate = null;
        double copyMs = 0;
        using VideoExportCalculation calculation = VideoExportCalculation.Calculate(
            scenario.Reduction,
            TrimOutMs - TrimInMs,
            standard,
            (path, bitrate, baseline, token) =>
            {
                statistics = Reencode(recording, scenario, path, clock, bitrate);
                long copyStarted = Stopwatch.GetTimestamp();
                File.Copy(path, Path.ChangeExtension(destination, baseline ? ".standard.mp4" : ".reduced.mp4"), overwrite: true);
                copyMs += Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
                if (!baseline)
                {
                    reducedBitrate = bitrate;
                }
            },
            CancellationToken.None);
        return new ScenarioResult(
            statistics!.Frames,
            statistics.FrameSource,
            statistics.ComposedFrames,
            calculation.BaselineBytes,
            calculation.ResultBytes,
            reducedBitrate,
            copyMs);
    }

    private static Mp4RenderStatistics Reencode(
        RecordingResult recording,
        Scenario scenario,
        string destination,
        StageClock clock,
        int? bitrate) =>
        TrimReencoder.ReencodeWithStatistics(
            recording.OutputPath,
            destination,
            TrimInMs,
            TrimOutMs,
            recording,
            options =>
            {
                long started = Stopwatch.GetTimestamp();
                var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
                clock.OpenMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return new TimingEncoder(encoder, clock);
            },
            NullLogger.Instance,
            scenario.Overlays,
            bitrateBitsPerSecond: bitrate,
            renderOptions: scenario.Options);

    private sealed class StageClock
    {
        internal double OpenMs { get; set; }
        internal double WriteMs { get; set; }
        internal double CompleteMs { get; set; }
    }

    private sealed class TimingEncoder(IVideoEncoder inner, StageClock clock) : IVideoEncoder
    {
        public int Width => inner.Width;

        public int Height => inner.Height;

        public void WriteFrame(in EncoderFrame frame)
        {
            long started = Stopwatch.GetTimestamp();
            inner.WriteFrame(frame);
            clock.WriteMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        public void Complete()
        {
            long started = Stopwatch.GetTimestamp();
            inner.Complete();
            clock.CompleteMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        public void Dispose() => inner.Dispose();
    }

    private static double MeasureDecode(string path, MediaFoundationVideoFrameReaderOptions? options)
    {
        long started = Stopwatch.GetTimestamp();
        using var reader = new MediaFoundationVideoFrameReader(path, options);
        foreach (double time in Schedule())
        {
            _ = reader.ReadFrameAt(time);
        }

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static double[] Schedule()
    {
        double frameMs = 1000.0 / Fps;
        int count = (int)Math.Ceiling(((TrimOutMs - TrimInMs) / frameMs) - 1e-9);
        return Enumerable.Range(0, count).Select(index => TrimInMs + (index * frameMs)).ToArray();
    }

    private sealed record OutputQuality(
        int Frames,
        int SampleCount,
        double ContainerDurationMs,
        string SampleTimesHash,
        string AvcConfiguration,
        double PsnrVsSourceDb,
        double PsnrVsPaintedDb,
        double LayeredPsnrVsSourceDb,
        double PlainPsnrVsSourceDb);

    /// <summary>
    /// Scores every output of the last round by RGB PSNR, as a player shows it: each output is
    /// decoded with the default reader and compared with the ideal frame — the source decoded the
    /// same way (or the exact painted frame before H.264), composed with the scenario's text where
    /// it is active.
    /// </summary>
    private static Dictionary<string, OutputQuality> MeasureQuality(RecordingResult recording, string output)
    {
        var outputs = new List<(string Name, Scenario Scenario)>();
        foreach (Scenario scenario in Scenarios)
        {
            if (scenario.Reduction == 0)
            {
                outputs.Add((scenario.Name, scenario));
            }
            else
            {
                outputs.Add((scenario.Name + ".standard", scenario));
                outputs.Add((scenario.Name + ".reduced", scenario));
            }
        }

        double[] schedule = Schedule();
        var request = new VideoFrameRenderRequest(
            recording.OutputPath, TrimInMs, TrimOutMs, Fps, Width, Height, Width, Height, [], []);
        var compositor = new VideoFrameCompositor(Width, Height);
        var noLayers = new Dictionary<Guid, BitmapSource>();
        var readers = outputs.Select(item => new MediaFoundationVideoFrameReader(Path.Combine(output, item.Name + ".mp4"))).ToArray();
        var sourceError = new double[outputs.Count];
        var truthError = new double[outputs.Count];
        var layeredError = new double[outputs.Count];
        var plainError = new double[outputs.Count];
        var layeredFrames = new int[outputs.Count];
        byte[] painted = new byte[Width * Height * 4];
        try
        {
            using var source = new MediaFoundationVideoFrameReader(recording.OutputPath);
            for (int index = 0; index < schedule.Length; index++)
            {
                double sourceTime = schedule[index];
                DecodedVideoFrame reference = source.ReadFrameAt(sourceTime);
                GifExportEngineBenchmarkTests.PaintDocumentFrame(painted, Width, Height, (int)Math.Round(sourceTime * Fps / 1000.0));
                for (int item = 0; item < outputs.Count; item++)
                {
                    Scenario scenario = outputs[item].Scenario;
                    DecodedVideoFrame actual = readers[item].ReadFrameAt(sourceTime - TrimInMs);
                    bool layered = scenario.Overlays.Any(overlay => overlay.IsActiveAt(sourceTime));
                    byte[] expected = reference.Pixels;
                    byte[] truth = painted;
                    if (layered)
                    {
                        VideoFrameRenderRequest composed = request with { TextOverlays = scenario.Overlays };
                        expected = Pixels(compositor.Compose(composed, noLayers, reference, sourceTime));
                        truth = Pixels(compositor.Compose(composed, noLayers, painted, Width, Height, Width * 4, sourceTime));
                        layeredFrames[item]++;
                    }

                    double error = SquaredError(expected, actual.Pixels);
                    sourceError[item] += error;
                    truthError[item] += SquaredError(truth, actual.Pixels);
                    if (layered)
                    {
                        layeredError[item] += error;
                    }
                    else
                    {
                        plainError[item] += error;
                    }
                }
            }
        }
        finally
        {
            foreach (MediaFoundationVideoFrameReader reader in readers)
            {
                reader.Dispose();
            }
        }

        double frameSamples = (double)Width * Height * 3;
        var report = new Dictionary<string, OutputQuality>();
        for (int item = 0; item < outputs.Count; item++)
        {
            string path = Path.Combine(output, outputs[item].Name + ".mp4");
            Mp4SampleTable table = Mp4SampleTable.Read(path);
            int plainFrames = schedule.Length - layeredFrames[item];
            report[outputs[item].Name] = new OutputQuality(
                schedule.Length,
                table.PresentationMs.Count,
                MediaFoundationVideoFrameReader.ReadContainerDurationMs(path),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    table.PresentationMs.SelectMany(BitConverter.GetBytes).ToArray()))[..16],
                Convert.ToHexString(ReadAvcConfiguration(path)),
                Psnr(sourceError[item] / (schedule.Length * frameSamples)),
                Psnr(truthError[item] / (schedule.Length * frameSamples)),
                layeredFrames[item] == 0 ? double.NaN : Psnr(layeredError[item] / (layeredFrames[item] * frameSamples)),
                plainFrames == 0 ? double.NaN : Psnr(plainError[item] / (plainFrames * frameSamples)));
        }

        return report;
    }

    /// <summary>Shape and quality of the labelled set against <c>before</c>: same sample count, times and duration.</summary>
    private static void CompareWithBaseline(string root, string label)
    {
        string beforeResults = Path.Combine(root, "before", "results.json");
        if (label == "before" || !File.Exists(beforeResults))
        {
            return;
        }

        var report = new Dictionary<string, object>();
        foreach (Scenario scenario in Scenarios)
        {
            foreach (string name in scenario.Reduction == 0
                         ? [scenario.Name]
                         : new[] { scenario.Name + ".standard", scenario.Name + ".reduced" })
            {
                string beforePath = Path.Combine(root, "before", name + ".mp4");
                string afterPath = Path.Combine(root, label, name + ".mp4");
                if (!File.Exists(beforePath) || !File.Exists(afterPath))
                {
                    continue;
                }

                Mp4SampleTable before = Mp4SampleTable.Read(beforePath);
                Mp4SampleTable after = Mp4SampleTable.Read(afterPath);
                report[name] = new
                {
                    SameSampleTimes = before.PresentationMs.SequenceEqual(after.PresentationMs),
                    SameKeyFrames = before.SyncSampleIndexes.SequenceEqual(after.SyncSampleIndexes),
                    BeforeDurationMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(beforePath),
                    AfterDurationMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(afterPath),
                    SameAvcConfiguration = ReadAvcConfiguration(beforePath).AsSpan().SequenceEqual(ReadAvcConfiguration(afterPath)),
                    BeforeBytes = new FileInfo(beforePath).Length,
                    AfterBytes = new FileInfo(afterPath).Length,
                    BeforeVsAfterPsnrDb = PsnrBetween(beforePath, afterPath),
                };
            }
        }

        File.WriteAllText(Path.Combine(root, label, "comparison.json"), JsonSerializer.Serialize(report, JsonOptions));
    }

    private static double PsnrBetween(string first, string second)
    {
        using var a = new MediaFoundationVideoFrameReader(first);
        using var b = new MediaFoundationVideoFrameReader(second);
        double error = 0;
        double[] schedule = Schedule();
        foreach (double sourceTime in schedule)
        {
            error += SquaredError(a.ReadFrameAt(sourceTime - TrimInMs).Pixels, b.ReadFrameAt(sourceTime - TrimInMs).Pixels);
        }

        return Psnr(error / (schedule.Length * (double)Width * Height * 3));
    }

    /// <summary>The avcC body (profile, level, SPS and PPS): the colour description players read.</summary>
    internal static byte[] ReadAvcConfiguration(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        (int Start, int End) moov = Mp4SampleTable.Find(file, 0, file.Length, "moov") ?? throw new InvalidDataException("moov");
        (int Start, int End) trak = Mp4SampleTable.Find(file, moov.Start, moov.End, "trak") ?? throw new InvalidDataException("trak");
        (int Start, int End) mdia = Mp4SampleTable.Find(file, trak.Start, trak.End, "mdia") ?? throw new InvalidDataException("mdia");
        (int Start, int End) minf = Mp4SampleTable.Find(file, mdia.Start, mdia.End, "minf") ?? throw new InvalidDataException("minf");
        (int Start, int End) stbl = Mp4SampleTable.Find(file, minf.Start, minf.End, "stbl") ?? throw new InvalidDataException("stbl");
        (int Start, int End) stsd = Mp4SampleTable.Find(file, stbl.Start, stbl.End, "stsd") ?? throw new InvalidDataException("stsd");
        (int Start, int End) avc1 = Mp4SampleTable.Find(file, stsd.Start + 8, stsd.End, "avc1") ?? throw new InvalidDataException("avc1");
        (int Start, int End) avcC = Mp4SampleTable.Find(file, avc1.Start + 78, avc1.End, "avcC") ?? throw new InvalidDataException("avcC");
        return file[avcC.Start..avcC.End];
    }

    private static byte[] Pixels(BitmapSource frame)
    {
        byte[] pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return pixels;
    }

    private static double SquaredError(byte[] expected, byte[] actual)
    {
        double sum = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = expected[offset + channel] - actual[offset + channel];
                sum += difference * difference;
            }
        }

        return sum;
    }

    private static double Psnr(double meanSquaredError) =>
        meanSquaredError <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / meanSquaredError);

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
