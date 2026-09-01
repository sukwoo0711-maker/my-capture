using System.IO;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

internal sealed class GifExportLimitException : InvalidOperationException
{
    internal GifExportLimitException(string message) : base(message)
    {
    }
}

internal sealed record GifFrameSchedule(
    IReadOnlyList<double> SourceTimesMs,
    IReadOnlyList<int> FrameDelaysCentiseconds,
    IReadOnlyList<TimedTextOverlay> QuantizedTextOverlays);

/// <summary>
/// Streams a trimmed, text-composited recording to animated GIF using only Windows/WPF codecs.
/// Product caps keep CPU, file size and palette-quantization work predictable on modest PCs.
/// </summary>
internal static class AnimatedGifExporter
{
    internal const double MaximumDurationMs = 20_000;
    internal const int MaximumLongEdge = 960;
    internal const int FramesPerSecond = 10;
    internal const int MaximumFrames = 200;

    internal static int Export(
        RecordingResult recording,
        VideoEditDocument editDocument,
        string destinationPath,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(editDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        VideoEditDocument document = editDocument.NormalizeFor(
            recording.Width,
            recording.Height,
            recording.DurationMs);
        double duration = document.TrimOutMs - document.TrimInMs;
        if (duration > MaximumDurationMs + 0.5)
        {
            throw new GifExportLimitException(
                "GIF는 최대 20초까지 내보낼 수 있습니다. 타임라인의 시작/끝 지점을 줄여 주세요.");
        }

        GifFrameSchedule schedule = BuildFrameSchedule(document);

        (int width, int height) = FitWithin(recording.Width, recording.Height, MaximumLongEdge);
        string destination = Path.GetFullPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            throw new ArgumentException("A destination directory is required.", nameof(destinationPath));
        }

        Directory.CreateDirectory(destinationDirectory);
        string temporary = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            int emitted;
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new AnimatedGifWriter(
                       output,
                       width,
                       height,
                       frameDelayCentiseconds: 10,
                       loopCount: 0,
                       leaveOpen: true))
            {
                try
                {
                    emitted = VideoFrameRenderPipeline.RenderScheduled(
                        new VideoFrameRenderRequest(
                            recording.OutputPath,
                            document.TrimInMs,
                            document.TrimOutMs,
                            FramesPerSecond,
                            width,
                            height,
                            recording.Width,
                            recording.Height,
                            schedule.QuantizedTextOverlays),
                        schedule.SourceTimesMs,
                        (frame, index) => writer.AddFrame(
                            frame,
                            schedule.FrameDelaysCentiseconds[index]),
                        progress,
                        cancellationToken);
                    writer.Complete();
                    output.Flush(flushToDisk: true);
                }
                catch
                {
                    // Preserve cancellation/render failures as the primary exception; disposal
                    // must not try to complete a deliberately abandoned partial animation.
                    writer.Abort();
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination))
            {
                try
                {
                    File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, destination, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, destination);
            }

            return emitted;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static (int Width, int Height) FitWithin(int width, int height, int longEdge)
    {
        int safeWidth = Math.Max(1, width);
        int safeHeight = Math.Max(1, height);
        int sourceLongEdge = Math.Max(safeWidth, safeHeight);
        if (sourceLongEdge <= longEdge)
        {
            return (safeWidth, safeHeight);
        }

        double scale = (double)longEdge / sourceLongEdge;
        return (
            Math.Max(1, (int)Math.Round(safeWidth * scale)),
            Math.Max(1, (int)Math.Round(safeHeight * scale)));
    }

    /// <summary>
    /// Builds a 10fps base cadence plus every timed-text boundary. GIF delays are quantized to
    /// centiseconds, so text starts/ends within 5ms of the requested source time instead of being
    /// shifted to the next 100ms video sample.
    /// </summary>
    internal static GifFrameSchedule BuildFrameSchedule(VideoEditDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        double durationMs = document.TrimOutMs - document.TrimInMs;
        int totalCentiseconds = Math.Max(
            1,
            checked((int)Math.Round(
                durationMs / 10.0,
                MidpointRounding.AwayFromZero)));
        var boundaries = new SortedSet<int> { 0, totalCentiseconds };
        int cadenceCentiseconds = 100 / FramesPerSecond;
        for (int boundary = cadenceCentiseconds;
             boundary < totalCentiseconds;
             boundary += cadenceCentiseconds)
        {
            _ = boundaries.Add(boundary);
        }

        var quantizedOverlays = new List<TimedTextOverlay>();
        foreach (TimedTextOverlay overlay in document.TextOverlays)
        {
            double start = Math.Max(document.TrimInMs, overlay.StartMs);
            double end = Math.Min(document.TrimOutMs, overlay.EndMs);
            if (end <= start)
            {
                continue;
            }

            int startCs = Math.Clamp(
                checked((int)Math.Round(
                    (start - document.TrimInMs) / 10.0,
                    MidpointRounding.AwayFromZero)),
                0,
                totalCentiseconds);
            int endCs = Math.Clamp(
                checked((int)Math.Round(
                    (end - document.TrimInMs) / 10.0,
                    MidpointRounding.AwayFromZero)),
                0,
                totalCentiseconds);
            if (endCs <= startCs)
            {
                throw new GifExportLimitException(
                    "GIF 시간 해상도는 0.01초입니다. 0.01초보다 짧은 시간 텍스트 구간을 늘려 주세요.");
            }

            _ = boundaries.Add(startCs);
            _ = boundaries.Add(endCs);
            TimedTextOverlay adjusted = overlay.Clone();
            adjusted.StartMs = document.TrimInMs + (startCs * 10.0);
            adjusted.EndMs = document.TrimInMs + (endCs * 10.0);
            quantizedOverlays.Add(adjusted);
        }

        int frameCount = boundaries.Count - 1;
        if (frameCount > MaximumFrames)
        {
            throw new GifExportLimitException(
                $"텍스트 시간 경계를 포함한 GIF 프레임 수가 최대 {MaximumFrames}개를 초과합니다. " +
                "구간을 줄이거나 텍스트 시작/끝 시간을 0.1초 눈금에 가깝게 조정해 주세요.");
        }

        int[] ordered = boundaries.ToArray();
        var sourceTimes = new double[frameCount];
        var delays = new int[frameCount];
        for (int index = 0; index < frameCount; index++)
        {
            sourceTimes[index] = document.TrimInMs + (ordered[index] * 10.0);
            delays[index] = ordered[index + 1] - ordered[index];
            if (delays[index] <= 0)
            {
                throw new InvalidOperationException("GIF frame schedule contains a non-positive delay.");
            }
        }

        return new GifFrameSchedule(sourceTimes, delays, quantizedOverlays);
    }
}
