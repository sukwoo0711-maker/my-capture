using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in measurement behind <see cref="GifFrameDifferencing"/>. Set
/// MYCAPTURE_GIF_SIZE_EVIDENCE to an output directory. Every clip and preset is exported once
/// per differencing mode by the same engine and scored against the frames the writer received
/// (the source decoded at the schedule times, area-averaged to the 960 px base and then to the
/// preset, as the engine does): bytes, time, PSNR of every frame as a browser composites it,
/// flicker of still pixels, and pixels more than <see cref="LargeDeviation"/> levels off in
/// some channel (stale spots that PSNR alone could hide). Optionally set
/// MYCAPTURE_GIF_SIZE_BEFORE to a GifExportEngineBenchmarkTests "before" folder to check that
/// the full-frame mode still writes the earlier bytes.
/// </summary>
public sealed class GifSizeReductionEvidenceTests
{
    private const string EvidenceVariable = "MYCAPTURE_GIF_SIZE_EVIDENCE";
    private const string BeforeVariable = "MYCAPTURE_GIF_SIZE_BEFORE";
    private const long GitHubLimitBytes = 10_000_000;
    private const int LargeDeviation = 48;

    private static readonly (string Name, GifFrameDifferencing Mode)[] Modes =
    [
        ("full", GifFrameDifferencing.None),
        ("merge", GifFrameDifferencing.MergeIdentical),
        ("rect", GifFrameDifferencing.ChangedRectangle),
        ("bounded", GifFrameDifferencing.ErrorBounded),
    ];

    [Fact]
    public void GifFrameDifferencing_EvidenceReport()
    {
        string? requested = Environment.GetEnvironmentVariable(EvidenceVariable);
        if (string.IsNullOrWhiteSpace(requested))
        {
            return;
        }

        string output = DiagnosticOutputPaths.Create(requested);
        string? before = Environment.GetEnvironmentVariable(BeforeVariable);
        var report = new StringBuilder();
        var rows = new List<Row>();
        report.AppendLine(CultureInfo.InvariantCulture, $"MyCapture GIF frame differencing evidence · {DateTimeOffset.Now:O}");
        report.AppendLine("mode | bytes | images/frames | export ms | PSNR dB | vs full: mean dPSNR, min frame dPSNR, frames < 0 dB | still-pixel flicker | pixels > 48 levels off (full) | 10 MB length s");
        string work = OwnedTestDirectory.Create("mc-gif-size-evidence-");
        try
        {
            BenchmarkSta.Run(() =>
            {
                var clips = new List<(string Name, RecordingResult Recording)>();
                foreach ((GifPreviewFixtureClips.Kind kind, double duration) in new[]
                         {
                             (GifPreviewFixtureClips.Kind.DocumentScroll, 10_000d),
                             (GifPreviewFixtureClips.Kind.Mixed, 20_000d),
                             (GifPreviewFixtureClips.Kind.Photo, 12_000d),
                             (GifPreviewFixtureClips.Kind.StillPage, 15_000d),
                         })
                {
                    clips.Add((kind.ToString(), GifPreviewFixtureClips.Encode(Path.Combine(work, $"{kind}.mp4"), kind, 1920, 1080, 30, duration)));
                }

                clips.Add(("BenchmarkDocument", GifExportEngineBenchmarkTests.EnsureSyntheticClip(Path.Combine(work, "benchmark-1080p30-10s.mp4"))));
                foreach ((string name, RecordingResult recording) in clips)
                {
                    foreach (GifExportQuality quality in GifSharePolicy.Presets)
                    {
                        rows.AddRange(MeasureClip(work, name, recording, quality, before, report));
                    }
                }
            });
        }
        finally
        {
            OwnedTestDirectory.Delete(work);
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(output, "gif-size-evidence.txt"), report.ToString());
        File.WriteAllText(
            DiagnosticOutputPaths.Child(output, "gif-size-evidence.json"),
            JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }));
    }

    private static List<Row> MeasureClip(string work, string clip, RecordingResult recording, GifExportQuality quality, string? before, StringBuilder report)
    {
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(
            document.NormalizeFor(recording.Width, recording.Height, recording.DurationMs), 1.0, quality.FramesPerSecond);
        (int width, int height) = AnimatedGifExporter.FitWithin(recording.Width, recording.Height, quality.LongEdge);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"\n## {clip} {recording.DurationMs / 1000:0.#}s -> {width}x{height} {quality.FramesPerSecond}fps ({schedule.SourceTimesMs.Count} frames)");

        var players = new List<GifPlayer>();
        var measured = new List<(string Name, long Bytes, double Ms, int Images, long FirstImageBytes)>();
        foreach ((string name, GifFrameDifferencing mode) in Modes)
        {
            string path = Path.Combine(work, $"{clip}-{quality.LongEdge}-{quality.FramesPerSecond}-{name}.gif");
            var options = new GifEngineOptions { Differencing = mode };
            var stopwatch = Stopwatch.StartNew();
            _ = AnimatedGifExporter.ExportCore(recording, document, path, null, default, 1.0, quality, null, options, NullLogger.Instance, out GifRenderStatistics statistics);
            double ms = stopwatch.Elapsed.TotalMilliseconds;
            var player = new GifPlayer(path);
            players.Add(player);
            measured.Add((name, new FileInfo(path).Length, ms, statistics.Images, player.FirstImageBytes));
            if (name == "full" && before is not null && clip == "BenchmarkDocument" && quality != GifSharePolicy.SharpLowRate)
            {
                string baseline = Path.Combine(before, $"{quality.LongEdge}px-{quality.FramesPerSecond}fps.gif");
                if (File.Exists(baseline))
                {
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"full-frame mode equals the pre-change export {Path.GetFileName(baseline)}: {File.ReadAllBytes(baseline).AsSpan().SequenceEqual(File.ReadAllBytes(path))}");
                }
            }

            if (name == "bounded" && quality == GifExportQuality.Standard)
            {
                foreach (double speed in new[] { 2.0, 4.0 })
                {
                    string fast = Path.Combine(work, $"{clip}-speed{speed}.gif");
                    _ = AnimatedGifExporter.Export(recording, document, fast, playbackSpeed: speed, quality: quality);
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"bounded at {speed}x speed: {new FileInfo(fast).Length:N0} B (1x {new FileInfo(path).Length:N0} B)");
                }
            }
        }

        int count = schedule.SourceTimesMs.Count;
        int modes = Modes.Length;
        var frameError = new double[modes][];
        var flickerChanged = new long[modes];
        long stillPixels = 0;
        var largeDeviations = new long[modes];
        for (int mode = 0; mode < modes; mode++)
        {
            frameError[mode] = new double[count];
        }

        byte[]? previousReference = null;
        var previousOutput = new byte[modes][];
        int startCs = 0;
        using (var reader = new MediaFoundationVideoFrameReader(recording.OutputPath))
        {
            for (int index = 0; index < count; index++)
            {
                DecodedVideoFrame source = reader.ReadFrameAt(schedule.SourceTimesMs[index]);
                (int baseWidth, int baseHeight) = AnimatedGifExporter.FitWithin(source.Width, source.Height, AnimatedGifExporter.MaximumLongEdge);
                byte[] reference = BgraFrameScaler.Downscale(source.Pixels, source.Width, source.Height, source.Stride, baseWidth, baseHeight);
                if ((baseWidth, baseHeight) != (width, height))
                {
                    reference = BgraFrameScaler.Downscale(reference, baseWidth, baseHeight, baseWidth * 4, width, height);
                }

                bool[]? still = previousReference is null ? null : StillMask(previousReference, reference);
                if (still is not null)
                {
                    stillPixels += still.Count(value => value);
                }

                for (int mode = 0; mode < modes; mode++)
                {
                    byte[] canvas = players[mode].At(startCs, out _);
                    frameError[mode][index] = SquaredError(reference, canvas);
                    largeDeviations[mode] += CountLargeDeviations(reference, canvas);
                    if (still is not null)
                    {
                        flickerChanged[mode] += CountChanged(previousOutput[mode]!, canvas, still);
                    }

                    previousOutput[mode] = (byte[])canvas.Clone();
                }

                previousReference = reference;
                startCs += schedule.FrameDelaysCentiseconds[index];
            }
        }

        double samplesPerFrame = (double)width * height * 3;
        var rows = new List<Row>();
        for (int mode = 0; mode < modes; mode++)
        {
            (string name, long bytes, double ms, int images, long firstImage) = measured[mode];
            double psnr = Psnr(frameError[mode].Sum() / (samplesPerFrame * count));
            double[] delta = Enumerable.Range(0, count)
                .Select(index => Psnr(frameError[mode][index] / samplesPerFrame) - Psnr(frameError[0][index] / samplesPerFrame))
                .ToArray();
            double flicker = stillPixels == 0 ? 0 : (double)flickerChanged[mode] / stillPixels;
            double large = (double)largeDeviations[mode] / ((double)width * height * count);
            double largeFull = (double)largeDeviations[0] / ((double)width * height * count);
            double seconds = recording.DurationMs / 1000;
            double linear = seconds * GitHubLimitBytes / bytes;
            double variable = Math.Max(1, bytes - firstImage) / seconds;
            double withOpening = (GitHubLimitBytes - firstImage) / variable;
            var row = new Row(clip, quality.LongEdge, quality.FramesPerSecond, name, bytes, images, count, ms, psnr,
                delta.Average(), delta.Min(), delta.Count(value => value < 0), flicker, large, largeFull,
                firstImage, linear, withOpening);
            rows.Add(row);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{name,-8} | {bytes,12:N0} | {images,4}/{count,-4} | {ms,6:0} | {psnr,6:0.00} | {row.MeanDeltaPsnr,6:+0.000;-0.000} {row.MinFrameDeltaPsnr,7:+0.000;-0.000} {row.FramesWorse,3} | {flicker,7:P2} | {large,7:P3} ({largeFull:P3}) | {linear,6:0.0} (opening model {withOpening:0.0})");
        }

        return rows;
    }

    /// <summary>Pixels whose ideal colour did not change since the previous frame.</summary>
    private static bool[] StillMask(byte[] previous, byte[] current)
    {
        var still = new bool[current.Length / 4];
        for (int pixel = 0; pixel < still.Length; pixel++)
        {
            int offset = pixel * 4;
            still[pixel] = previous[offset] == current[offset] && previous[offset + 1] == current[offset + 1] && previous[offset + 2] == current[offset + 2];
        }

        return still;
    }

    private static long CountChanged(byte[] previous, byte[] current, bool[] mask)
    {
        long changed = 0;
        for (int pixel = 0; pixel < mask.Length; pixel++)
        {
            int offset = pixel * 4;
            if (mask[pixel] && (previous[offset] != current[offset] || previous[offset + 1] != current[offset + 1] || previous[offset + 2] != current[offset + 2]))
            {
                changed++;
            }
        }

        return changed;
    }

    private static long CountLargeDeviations(byte[] expected, byte[] actual)
    {
        long count = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            if (Math.Abs(expected[offset] - actual[offset]) > LargeDeviation
                || Math.Abs(expected[offset + 1] - actual[offset + 1]) > LargeDeviation
                || Math.Abs(expected[offset + 2] - actual[offset + 2]) > LargeDeviation)
            {
                count++;
            }
        }

        return count;
    }

    private static double SquaredError(byte[] expected, byte[] actual)
    {
        double sum = 0;
        for (int pixel = 0; pixel < expected.Length / 4; pixel++)
        {
            int offset = pixel * 4;
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = expected[offset + channel] - actual[offset + channel];
                sum += difference * difference;
            }
        }

        return sum;
    }

    private static double Psnr(double meanSquaredError) =>
        meanSquaredError <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / meanSquaredError);

    internal sealed record Row(
        string Clip,
        int LongEdge,
        int Fps,
        string Mode,
        long Bytes,
        int Images,
        int Frames,
        double ExportMs,
        double PsnrDb,
        double MeanDeltaPsnr,
        double MinFrameDeltaPsnr,
        int FramesWorse,
        double StillFlicker,
        double LargeDeviationShare,
        double LargeDeviationShareFullFrames,
        long FirstImageBytes,
        double GitHubSecondsLinear,
        double GitHubSecondsWithOpening);

    /// <summary>
    /// Plays a GIF forward as a browser composites it (offsets, transparency, disposal 1),
    /// decoding one image at a time.
    /// </summary>
    internal sealed class GifPlayer
    {
        private readonly GifBitmapDecoder _decoder;
        private readonly List<(GifPixelRect Bounds, int Delay)> _images;
        private readonly byte[] _canvas;
        private readonly int _width;
        private int _next;
        private int _endCs;

        internal GifPlayer(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            _width = bytes[6] | (bytes[7] << 8);
            int height = bytes[8] | (bytes[9] << 8);
            _images = ReadImages(bytes, out long firstImageBytes);
            FirstImageBytes = firstImageBytes;
            _canvas = new byte[_width * height * 4];
            _decoder = new GifBitmapDecoder(new MemoryStream(bytes, writable: false), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
            Assert.Equal(_images.Count, _decoder.Frames.Count);
        }

        /// <summary>Header, first Graphic Control Extension and first image, plus the trailer.</summary>
        internal long FirstImageBytes { get; }

        internal IReadOnlyList<(GifPixelRect Bounds, int Delay)> Images => _images;

        /// <summary>Canvas shown <paramref name="centiseconds"/> after the start; only moves forward.</summary>
        internal byte[] At(int centiseconds, out GifPixelRect? newImage)
        {
            newImage = null;
            while (_next < _images.Count && (_next == 0 || centiseconds >= _endCs))
            {
                Draw(_next);
                newImage = _images[_next].Bounds;
                _endCs += _images[_next].Delay;
                _next++;
            }

            return _canvas;
        }

        private void Draw(int index)
        {
            BitmapSource frame = _decoder.Frames[index];
            BitmapSource bgra = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            GifPixelRect bounds = _images[index].Bounds;
            Assert.Equal((bounds.Width, bounds.Height), (bgra.PixelWidth, bgra.PixelHeight));
            byte[] pixels = new byte[bounds.Width * bounds.Height * 4];
            bgra.CopyPixels(pixels, bounds.Width * 4, 0);
            for (int y = 0; y < bounds.Height; y++)
            {
                for (int x = 0; x < bounds.Width; x++)
                {
                    int source = ((y * bounds.Width) + x) * 4;
                    if (pixels[source + 3] != 0)
                    {
                        Buffer.BlockCopy(pixels, source, _canvas, (((bounds.Top + y) * _width) + bounds.Left + x) * 4, 4);
                    }
                }
            }
        }

        internal static List<(GifPixelRect Bounds, int Delay)> ReadImages(byte[] bytes, out long firstImageBytes)
        {
            var images = new List<(GifPixelRect, int)>();
            int index = 13 + (((bytes[10] & 0x80) != 0) ? 3 * (1 << ((bytes[10] & 0x07) + 1)) : 0);
            int delay = 0;
            firstImageBytes = 0;
            while (bytes[index] != 0x3B)
            {
                if (bytes[index] == 0x21)
                {
                    if (bytes[index + 1] == 0xF9)
                    {
                        delay = bytes[index + 4] | (bytes[index + 5] << 8);
                    }

                    index = SkipSubBlocks(bytes, index + 2);
                }
                else
                {
                    Assert.Equal(0x2C, bytes[index]);
                    int left = bytes[index + 1] | (bytes[index + 2] << 8);
                    int top = bytes[index + 3] | (bytes[index + 4] << 8);
                    int width = bytes[index + 5] | (bytes[index + 6] << 8);
                    int height = bytes[index + 7] | (bytes[index + 8] << 8);
                    byte packed = bytes[index + 9];
                    index += 10 + (((packed & 0x80) != 0) ? 3 * (1 << ((packed & 0x07) + 1)) : 0);
                    index = SkipSubBlocks(bytes, index + 1);
                    if (images.Count == 0)
                    {
                        firstImageBytes = index + 1; // everything before, plus the trailer
                    }

                    images.Add((new GifPixelRect(left, top, width, height), delay));
                }
            }

            return images;
        }

        private static int SkipSubBlocks(byte[] bytes, int index)
        {
            while (bytes[index] != 0)
            {
                index += bytes[index] + 1;
            }

            return index + 1;
        }
    }
}
