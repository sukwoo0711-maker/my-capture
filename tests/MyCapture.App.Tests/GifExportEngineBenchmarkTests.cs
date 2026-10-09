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
            Add("decode-all-frames", MeasureDecode(recording.OutputPath));
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

        if (Environment.GetEnvironmentVariable(AssertVariable) == "1")
        {
            // Lead baseline on the reference PC: 6,303 ms. Require a clear, not marginal, win.
            Assert.True(Median(samples["960px-10fps"]) < 3_000, "960px/10fps GIF export did not improve enough.");
        }
    });

    private static double MeasureDecode(string path)
    {
        long started = Stopwatch.GetTimestamp();
        using var reader = new MediaFoundationVideoFrameReader(path);
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
