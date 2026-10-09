using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in GIF export benchmark on the shared synthetic 1080p 30fps 10 s document clip.
/// Set <c>MYCAPTURE_GIF_ENGINE_BENCHMARK</c> to an output directory to run it, and
/// <c>MYCAPTURE_GIF_ENGINE_LABEL</c> to name the result set. Timing assertions run only when
/// <c>MYCAPTURE_GIF_ENGINE_PERF_ASSERT=1</c>, so ordinary CI never depends on machine speed.
/// </summary>
public sealed class GifExportEngineBenchmarkTests
{
    internal const string OutputVariable = "MYCAPTURE_GIF_ENGINE_BENCHMARK";
    private const string LabelVariable = "MYCAPTURE_GIF_ENGINE_LABEL";
    private const string RoundsVariable = "MYCAPTURE_GIF_ENGINE_ROUNDS";
    private const string AssertVariable = "MYCAPTURE_GIF_ENGINE_PERF_ASSERT";

    internal const int SourceWidth = 1920;
    internal const int SourceHeight = 1080;
    internal const int SourceFps = 30;
    internal const int SourceFrames = 300;

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
        RecordingResult recording = EnsureSyntheticClip(Path.Combine(root, "source-1080p30-10s.mp4"));
        VideoEditDocument document = VideoEditDocument.CreateFor(SourceWidth, SourceHeight, recording.DurationMs);

        var scenarios = new (string Name, GifExportQuality Quality, double Speed)[]
        {
            ("960px-10fps", GifExportQuality.Standard, 1.0),
            ("640px-10fps", GifExportQuality.Compact, 1.0),
            ("480px-5fps", GifExportQuality.Smallest, 1.0),
            ("960px-10fps-2x", GifExportQuality.Standard, 2.0),
        };

        // Warm the codec, D3D and JIT paths once so the first measured round is not a cold start.
        _ = AnimatedGifExporter.Export(recording, document, Path.Combine(output, "warmup.gif"), quality: GifExportQuality.Smallest);

        var samples = new Dictionary<string, List<double>>();
        var details = new Dictionary<string, object>();
        void Add(string name, double ms) =>
            (samples.TryGetValue(name, out List<double>? list) ? list : samples[name] = []).Add(ms);

        for (int round = 0; round < rounds; round++)
        {
            Add("decode-all-frames", MeasureDecode(recording.OutputPath, null));
            Add("decode-all-frames-hardware-rgb", MeasureDecode(
                recording.OutputPath,
                new MediaFoundationVideoFrameReaderOptions { PreferHardwareDecode = true }));
            Add("decode-all-frames-software-nv12", MeasureDecode(
                recording.OutputPath,
                new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 }));
            Add("decode-all-frames-hardware-nv12", MeasureDecode(
                recording.OutputPath,
                new MediaFoundationVideoFrameReaderOptions
                {
                    PreferHardwareDecode = true,
                    OutputFormat = DecodedVideoPixelFormat.Nv12,
                }));
            foreach ((string name, GifExportQuality quality, double speed) in scenarios)
            {
                string gif = Path.Combine(output, $"{name}.gif");
                long started = Stopwatch.GetTimestamp();
                int frames = AnimatedGifExporter.Export(recording, document, gif, playbackSpeed: speed, quality: quality);
                Add(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                details[name] = new { Frames = frames, Bytes = new FileInfo(gif).Length };
            }

            // The export dialog's preset walk. Every step is an independent Export call here.
            long sequenceStarted = Stopwatch.GetTimestamp();
            var stepMs = new List<double>();
            foreach (GifExportQuality quality in new[]
                     {
                         GifExportQuality.Standard, GifExportQuality.Compact,
                         GifExportQuality.Smallest, GifExportQuality.Standard,
                     })
            {
                long stepStarted = Stopwatch.GetTimestamp();
                _ = AnimatedGifExporter.Export(
                    recording, document, Path.Combine(output, $"sequence-{quality.LongEdge}.gif"), quality: quality);
                stepMs.Add(Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds);
            }

            Add("sequence-960-640-480-960", Stopwatch.GetElapsedTime(sequenceStarted).TotalMilliseconds);
            details[$"sequence-steps-round{round}"] = stepMs;

            // The same walk through one dialog session (decoded/encoded frame reuse), then a
            // speed change on the last preset.
            using (var session = new GifExportSession(recording, document))
            {
                long sessionStarted = Stopwatch.GetTimestamp();
                var sessionSteps = new List<object>();
                foreach ((GifExportQuality quality, double speed) in new[]
                         {
                             (GifExportQuality.Standard, 1.0), (GifExportQuality.Compact, 1.0),
                             (GifExportQuality.Smallest, 1.0), (GifExportQuality.Standard, 1.0),
                             (GifExportQuality.Standard, 2.0),
                         })
                {
                    long stepStarted = Stopwatch.GetTimestamp();
                    _ = session.Export(Path.Combine(output, $"session-{quality.LongEdge}-{speed}x.gif"), quality, speed);
                    sessionSteps.Add(new
                    {
                        quality.LongEdge,
                        Speed = speed,
                        Ms = Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds,
                        session.LastStatistics!.FrameSource,
                        session.LastStatistics.DecodedFrames,
                    });
                    if (sessionSteps.Count == 4)
                    {
                        Add("session-sequence-960-640-480-960", Stopwatch.GetElapsedTime(sessionStarted).TotalMilliseconds);
                    }
                }

                details[$"session-steps-round{round}"] = sessionSteps;
                details["session-cache-bytes"] = new { session.Cache.DecodedBytes, session.Cache.EncodedBytes };
            }

            foreach ((string name, GifEngineOptions options) in new[]
                     {
                         ("960px-10fps-software-decode", new GifEngineOptions { PreferHardwareDecode = false }),
                         ("960px-10fps-legacy-converter", new GifEngineOptions { PreferHardwareDecode = false, DecodeNv12 = false }),
                         ("960px-10fps-gpu-video-processor", new GifEngineOptions { DecodeNv12 = false }),
                         ("960px-10fps-gpu-resize", new GifEngineOptions { GpuResize = true }),
                         ("960px-10fps-1-pixel-thread", new GifEngineOptions { WorkerThreads = 1 }),
                     })
            {
                string gif = Path.Combine(output, $"{name}.gif");
                long started = Stopwatch.GetTimestamp();
                _ = AnimatedGifExporter.ExportCore(
                    recording, document, gif, null, default, 1.0, GifExportQuality.Standard, null,
                    options, NullLogger.Instance, out GifRenderStatistics statistics);
                Add(name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                details[name] = new { statistics.FrameSource, Bytes = new FileInfo(gif).Length };
                if (name == "960px-10fps-software-decode")
                {
                    details["hardware-equals-software-decode"] = File.ReadAllBytes(gif)
                        .AsSpan()
                        .SequenceEqual(File.ReadAllBytes(Path.Combine(output, "960px-10fps.gif")));
                }
            }

            details["parallel-equals-single-thread"] =
                File.ReadAllBytes(Path.Combine(output, "960px-10fps.gif"))
                    .AsSpan()
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(output, "960px-10fps-1-pixel-thread.gif")));
        }

        var summary = samples.ToDictionary(
            pair => pair.Key,
            pair => new { MedianMs = Median(pair.Value), MinMs = pair.Value.Min(), RawMs = pair.Value });
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
                new JsonSerializerOptions { WriteIndented = true }));

        CompareWithBaseline(root, label);
        if (Environment.GetEnvironmentVariable(AssertVariable) == "1")
        {
            // Lead baseline on the reference PC: 6,303 ms. Require a clear, not marginal, win.
            Assert.True(Median(samples["960px-10fps"]) < 3_000, "960px/10fps GIF export did not improve enough.");
        }
    });

    /// <summary>
    /// Quality comparison of the <c>before</c> result set with the labelled one: GIF
    /// shape (size, frame count, delays) must match, and both are scored by PSNR against an
    /// ideal reference (software-decoded source frames, area-averaged to the output size,
    /// without palette quantisation).
    /// </summary>
    private static void CompareWithBaseline(string root, string label)
    {
        if (label == "before" || !Directory.Exists(Path.Combine(root, "before")))
        {
            return;
        }

        RecordingResult recording = EnsureSyntheticClip(Path.Combine(root, "source-1080p30-10s.mp4"));
        VideoEditDocument document = VideoEditDocument.CreateFor(SourceWidth, SourceHeight, recording.DurationMs);
        var report = new Dictionary<string, object>();
        foreach ((string name, string baseline, GifExportQuality quality, double speed) in new[]
                 {
                     ("960px-10fps", "960px-10fps", GifExportQuality.Standard, 1.0),
                     ("640px-10fps", "640px-10fps", GifExportQuality.Compact, 1.0),
                     ("480px-5fps", "480px-5fps", GifExportQuality.Smallest, 1.0),
                     ("960px-10fps-2x", "960px-10fps-2x", GifExportQuality.Standard, 2.0),
                     ("960px-10fps-software-decode", "960px-10fps", GifExportQuality.Standard, 1.0),
                     ("960px-10fps-legacy-converter", "960px-10fps", GifExportQuality.Standard, 1.0),
                     ("960px-10fps-gpu-video-processor", "960px-10fps", GifExportQuality.Standard, 1.0),
                     ("960px-10fps-gpu-resize", "960px-10fps", GifExportQuality.Standard, 1.0),
                 })
        {
            string beforePath = Path.Combine(root, "before", baseline + ".gif");
            string afterPath = Path.Combine(root, label, name + ".gif");
            if (!File.Exists(beforePath) || !File.Exists(afterPath))
            {
                continue;
            }

            GifShape before = ReadShape(File.ReadAllBytes(beforePath));
            GifShape after = ReadShape(File.ReadAllBytes(afterPath));
            GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(
                document.NormalizeFor(SourceWidth, SourceHeight, recording.DurationMs), speed, quality.FramesPerSecond);
            (int width, int height) = AnimatedGifExporter.FitWithin(SourceWidth, SourceHeight, quality.LongEdge);
            double beforeError = 0;
            double afterError = 0;
            double betweenError = 0;
            double beforeTruthError = 0;
            double afterTruthError = 0;
            byte[] painted = new byte[SourceWidth * SourceHeight * 4];
            using (var reader = new MediaFoundationVideoFrameReader(recording.OutputPath))
            {
                List<byte[]> beforeFrames = DecodeGif(beforePath);
                List<byte[]> afterFrames = DecodeGif(afterPath);
                for (int index = 0; index < schedule.SourceTimesMs.Count; index++)
                {
                    double time = schedule.SourceTimesMs[index];
                    DecodedVideoFrame source = reader.ReadFrameAt(time);
                    byte[] reference = BgraFrameScaler.Downscale(source.Pixels, source.Width, source.Height, source.Stride, width, height);
                    beforeError += SquaredError(reference, beforeFrames[index]);
                    afterError += SquaredError(reference, afterFrames[index]);
                    betweenError += SquaredError(beforeFrames[index], afterFrames[index]);

                    // Ground truth: the exact painted frame before H.264, area-averaged.
                    PaintDocumentFrame(painted, SourceWidth, SourceHeight, (int)Math.Round(time * SourceFps / 1000.0));
                    byte[] truth = BgraFrameScaler.Downscale(painted, SourceWidth, SourceHeight, SourceWidth * 4, width, height);
                    beforeTruthError += SquaredError(truth, beforeFrames[index]);
                    afterTruthError += SquaredError(truth, afterFrames[index]);
                }
            }

            double samples = (double)schedule.SourceTimesMs.Count * width * height * 3;
            report[name] = new
            {
                SameShape = before == after,
                Before = before,
                After = after,
                BeforePsnrVsPaintedDb = Psnr(beforeTruthError / samples),
                AfterPsnrVsPaintedDb = Psnr(afterTruthError / samples),
                BeforePsnrDb = Psnr(beforeError / samples),
                AfterPsnrDb = Psnr(afterError / samples),
                BeforeVsAfterPsnrDb = Psnr(betweenError / samples),
                BeforeBytes = new FileInfo(beforePath).Length,
                AfterBytes = new FileInfo(afterPath).Length,
            };
        }

        File.WriteAllText(
            Path.Combine(root, label, "quality.json"),
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
                }));
    }

    private static double Psnr(double meanSquaredError) =>
        meanSquaredError <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / meanSquaredError);

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

    private static List<byte[]> DecodeGif(string path)
    {
        using FileStream stream = File.OpenRead(path);
        var decoder = new System.Windows.Media.Imaging.GifBitmapDecoder(
            stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frames = new List<byte[]>();
        foreach (System.Windows.Media.Imaging.BitmapFrame frame in decoder.Frames)
        {
            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            frames.Add(pixels);
        }

        return frames;
    }

    internal sealed record GifShape(int Width, int Height, int Frames, string Delays);

    /// <summary>Walks the GIF block structure and returns canvas size, frame count and delays.</summary>
    internal static GifShape ReadShape(byte[] bytes)
    {
        int width = bytes[6] | (bytes[7] << 8);
        int height = bytes[8] | (bytes[9] << 8);
        int index = 13 + (((bytes[10] & 0x80) != 0) ? 3 * (1 << ((bytes[10] & 0x07) + 1)) : 0);
        var delays = new List<int>();
        int frames = 0;
        while (index < bytes.Length && bytes[index] != 0x3B)
        {
            if (bytes[index] == 0x21)
            {
                if (bytes[index + 1] == 0xF9)
                {
                    delays.Add(bytes[index + 4] | (bytes[index + 5] << 8));
                }

                index += 2;
                index = SkipSubBlocks(bytes, index);
            }
            else if (bytes[index] == 0x2C)
            {
                byte packed = bytes[index + 9];
                index += 10 + (((packed & 0x80) != 0) ? 3 * (1 << ((packed & 0x07) + 1)) : 0);
                index = SkipSubBlocks(bytes, index + 1);
                frames++;
            }
            else
            {
                throw new InvalidDataException($"Unexpected GIF block 0x{bytes[index]:X2}.");
            }
        }

        return new GifShape(width, height, frames, string.Join(",", delays));
    }

    private static int SkipSubBlocks(byte[] bytes, int index)
    {
        while (bytes[index] != 0)
        {
            index += bytes[index] + 1;
        }

        return index + 1;
    }

    private static double MeasureDecode(string path, MediaFoundationVideoFrameReaderOptions? options)
    {
        long started = Stopwatch.GetTimestamp();
        using var reader = new MediaFoundationVideoFrameReader(path, options);
        for (int index = 0; index < SourceFrames; index++)
        {
            _ = reader.ReadFrameAt(index * 1000.0 / SourceFps);
        }

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// Bright document background, dark text bars scrolling upward and a moving rectangle,
    /// encoded once and reused by later runs so before/after compare the same bytes.
    /// </summary>
    internal static RecordingResult EnsureSyntheticClip(string path)
    {
        if (!File.Exists(path))
        {
            string temporary = path + ".partial.mp4";
            var options = new VideoEncoderOptions(
                temporary,
                SourceWidth,
                SourceHeight,
                SourceFps,
                VideoEncoderOptions.DeriveBitrate(SourceWidth, SourceHeight, SourceFps));
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                int stride = SourceWidth * 4;
                byte[] pixels = new byte[stride * SourceHeight];
                for (int frame = 0; frame < SourceFrames; frame++)
                {
                    PaintDocumentFrame(pixels, SourceWidth, SourceHeight, frame);
                    encoder.WriteFrame(new EncoderFrame(pixels, SourceWidth, SourceHeight, stride, frame * 1000.0 / SourceFps));
                }

                encoder.Complete();
            }

            File.Move(temporary, path, overwrite: true);
        }

        return new RecordingResult(path, SourceFrames * 1000.0 / SourceFps, SourceFps, SourceFrames, SourceWidth, SourceHeight);
    }

    internal static void PaintDocumentFrame(byte[] pixels, int width, int height, int frame)
    {
        int stride = width * 4;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 238;
            pixels[offset + 1] = 244;
            pixels[offset + 2] = 245;
            pixels[offset + 3] = 255;
        }

        const int lineSpacing = 36;
        const int barHeight = 14;
        int scroll = frame * 3;
        for (int line = 0; line < (height / lineSpacing) + 2; line++)
        {
            int documentLine = line + (scroll / lineSpacing);
            int y0 = 40 + (line * lineSpacing) - (scroll % lineSpacing);
            int x = 120;
            int right = width - 120 - ((documentLine * 37) % 400);
            int word = documentLine * 7;
            while (x < right)
            {
                int wordWidth = 40 + ((word * 53) % 120);
                FillRect(pixels, stride, width, height, x, y0, Math.Min(wordWidth, right - x), barHeight, 48, 40, 40);
                x += wordWidth + 14;
                word++;
            }
        }

        int boxX = 100 + ((frame * 5) % (width - 400));
        int boxY = 640 + (int)(120 * Math.Sin(frame / 20.0));
        FillRect(pixels, stride, width, height, boxX, boxY, 220, 150, 220, 110, 30);
        FillRect(pixels, stride, width, height, boxX + 30, boxY + 30, 80, 40, 40, 190, 250);
    }

    private static void FillRect(
        byte[] pixels, int stride, int width, int height, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(width, x + w);
        int bottom = Math.Min(height, y + h);
        for (int row = top; row < bottom; row++)
        {
            int offset = (row * stride) + (left * 4);
            for (int column = left; column < right; column++, offset += 4)
            {
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }
    }
}

/// <summary>Runs long STA benchmark bodies without the two-minute integration timeout.</summary>
internal static class BenchmarkSta
{
    internal static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromMinutes(20));
        Assert.False(thread.IsAlive, "STA benchmark timed out");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("STA body threw: " + failure);
        }
    }
}
