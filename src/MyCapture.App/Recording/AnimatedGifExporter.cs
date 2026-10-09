using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    IReadOnlyList<TimedTextOverlay> QuantizedTextOverlays,
    IReadOnlyList<FrameEditLayer> QuantizedFrameEditLayers);

/// <summary>
/// Streams a trimmed, text-composited recording to animated GIF using only Windows/WPF codecs.
/// There is no hard duration cap: a long GIF is the user's call, and its cost grows linearly
/// with frame count, so the export dialog warns beyond 20 s while the quality presets keep
/// CPU, file size and palette-quantization work predictable on modest PCs. Decoding prefers
/// D3D11 hardware decode and encoding runs on a few worker threads (see
/// <see cref="GifExportEngine"/>); <see cref="GifExportSession"/> reuses frames across option
/// changes in one dialog.
/// </summary>
internal static class AnimatedGifExporter
{
    internal const int MaximumLongEdge = 960;
    internal const int FramesPerSecond = 10;

    internal static int Export(
        RecordingResult recording,
        VideoEditDocument editDocument,
        string destinationPath,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        double playbackSpeed = 1.0,
        GifExportQuality? quality = null) =>
        ExportCore(
            recording,
            editDocument,
            destinationPath,
            progress,
            cancellationToken,
            playbackSpeed,
            quality,
            cache: null,
            GifEngineOptions.Default,
            NullLogger.Instance,
            out _);

    /// <summary>
    /// Shared implementation of one-shot and <see cref="GifExportSession"/> exports. With a
    /// cache it also decodes the densest (10 fps) schedule so later preset and speed changes
    /// can reuse the frames. A hardware decode failure after open is retried once in software.
    /// </summary>
    internal static int ExportCore(
        RecordingResult recording,
        VideoEditDocument editDocument,
        string destinationPath,
        IProgress<VideoFrameRenderProgress>? progress,
        CancellationToken cancellationToken,
        double playbackSpeed,
        GifExportQuality? quality,
        GifFrameCache? cache,
        GifEngineOptions engineOptions,
        ILogger log,
        out GifRenderStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(editDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(engineOptions);
        ArgumentNullException.ThrowIfNull(log);
        VideoLayerResourceBudget.Validate(editDocument.FrameEditLayers);
        quality ??= GifExportQuality.Standard;
        quality.Validate();

        VideoEditDocument document = editDocument.NormalizeFor(
            recording.Width,
            recording.Height,
            recording.DurationMs);

        GifFrameSchedule schedule = BuildFrameSchedule(document, playbackSpeed, quality.FramesPerSecond);
        IReadOnlyList<double>? decodeSuperset = cache is null
            ? null
            : BuildFrameSchedule(document, 1.0, FramesPerSecond).SourceTimesMs;

        (int width, int height) = FitWithin(recording.Width, recording.Height, quality.LongEdge);
        (int baseWidth, int baseHeight) = FitWithin(recording.Width, recording.Height, MaximumLongEdge);
        var plan = new GifRenderPlan(
            new VideoFrameRenderRequest(
                recording.OutputPath,
                document.TrimInMs,
                document.TrimOutMs,
                quality.FramesPerSecond,
                width,
                height,
                recording.Width,
                recording.Height,
                schedule.QuantizedTextOverlays,
                schedule.QuantizedFrameEditLayers),
            baseWidth,
            baseHeight,
            schedule.SourceTimesMs,
            schedule.FrameDelaysCentiseconds);

        string destination = Path.GetFullPath(destinationPath);
        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            throw new ArgumentException("A destination directory is required.", nameof(destinationPath));
        }

        // The local user explicitly selected this destination through SaveFileDialog. Writing to
        // that exact unprivileged path is the purpose of the export operation.
        // codeql[cs/path-injection]
        Directory.CreateDirectory(destinationDirectory);
        GifEngineOptions attemptOptions = engineOptions;
        while (true)
        {
            string temporary = Path.Combine(
                destinationDirectory,
                $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            try
            {
                statistics = WriteAnimation(
                    temporary,
                    plan,
                    cache,
                    attemptOptions,
                    log,
                    progress,
                    cancellationToken,
                    decodeSuperset);

                cancellationToken.ThrowIfCancellationRequested();
                // The destination is the exact path confirmed by the local SaveFileDialog.
                // codeql[cs/path-injection]
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

                return statistics.Frames;
            }
            catch (GifHardwareDecodeException failure) when (!cancellationToken.IsCancellationRequested)
            {
                log.LogWarning(
                    failure.InnerException,
                    "Hardware video decode failed during GIF export; retrying with the software decoder.");
                attemptOptions = attemptOptions with { PreferHardwareDecode = false };
            }
            finally
            {
                try
                {
                    // Only the exporter-created GUID temporary sibling can reach this cleanup path.
                    // codeql[cs/path-injection]
                    if (File.Exists(temporary))
                    {
                        // codeql[cs/path-injection]
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
    }

    private static GifRenderStatistics WriteAnimation(
        string temporary,
        GifRenderPlan plan,
        GifFrameCache? cache,
        GifEngineOptions engineOptions,
        ILogger log,
        IProgress<VideoFrameRenderProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<double>? decodeSuperset)
    {
        using var output = new FileStream(
            // The temporary name is a new GUID plus Path.GetFileName(destination), and
            // therefore remains beside the explicitly selected output file.
            // codeql[cs/path-injection]
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        using var writer = new AnimatedGifWriter(
            output,
            plan.Request.Width,
            plan.Request.Height,
            frameDelayCentiseconds: 10,
            loopCount: 0,
            leaveOpen: true);
        try
        {
            GifRenderStatistics statistics = GifExportEngine.Render(
                plan,
                writer,
                cache,
                engineOptions,
                log,
                progress,
                cancellationToken,
                decodeSuperset);
            writer.Complete();
            output.Flush(flushToDisk: true);
            return statistics;
        }
        catch
        {
            // Preserve cancellation/render failures as the primary exception; disposal
            // must not try to complete a deliberately abandoned partial animation.
            writer.Abort();
            throw;
        }
    }

    internal static (int Width, int Height) FitWithin(int width, int height, int longEdge)
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
    internal static GifFrameSchedule BuildFrameSchedule(
        VideoEditDocument document,
        double playbackSpeed = 1.0,
        int framesPerSecond = FramesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (framesPerSecond is not (5 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }
        if (!double.IsFinite(playbackSpeed) || playbackSpeed is < 0.25 or > 4.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playbackSpeed),
                "GIF playback speed must be between 0.25x and 4x.");
        }

        double durationMs = document.TrimOutMs - document.TrimInMs;
        int totalCentiseconds = Math.Max(
            1,
            checked((int)Math.Round(
                durationMs / 10.0,
                MidpointRounding.AwayFromZero)));
        var boundaries = new SortedSet<int> { 0, totalCentiseconds };
        int cadenceCentiseconds = 100 / framesPerSecond;
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
                    UiText.Get("Text_6D9F1A20DDCC"));
            }

            _ = boundaries.Add(startCs);
            _ = boundaries.Add(endCs);
            TimedTextOverlay adjusted = overlay.Clone();
            adjusted.StartMs = document.TrimInMs + (startCs * 10.0);
            adjusted.EndMs = document.TrimInMs + (endCs * 10.0);
            quantizedOverlays.Add(adjusted);
        }

        var quantizedFrameLayers = new List<FrameEditLayer>();
        foreach (FrameEditLayer layer in document.FrameEditLayers)
        {
            double start = Math.Max(document.TrimInMs, layer.StartMs);
            double end = Math.Min(document.TrimOutMs, layer.EndMs);
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
                    UiText.Get("Text_F2F36C2D52F6"));
            }

            _ = boundaries.Add(startCs);
            _ = boundaries.Add(endCs);
            FrameEditLayer adjusted = layer.Clone();
            adjusted.StartMs = document.TrimInMs + (startCs * 10.0);
            adjusted.EndMs = document.TrimInMs + (endCs * 10.0);
            quantizedFrameLayers.Add(adjusted);
        }

        int frameCount = boundaries.Count - 1;

        int[] ordered = boundaries.ToArray();
        var sourceTimes = new double[frameCount];
        var delays = new int[frameCount];
        int previousOutputBoundary = 0;
        for (int index = 0; index < frameCount; index++)
        {
            sourceTimes[index] = document.TrimInMs + (ordered[index] * 10.0);
            int outputBoundary = Math.Max(
                previousOutputBoundary + 1,
                checked((int)Math.Round(
                    ordered[index + 1] / playbackSpeed,
                    MidpointRounding.AwayFromZero)));
            delays[index] = outputBoundary - previousOutputBoundary;
            previousOutputBoundary = outputBoundary;
            if (delays[index] <= 0)
            {
                throw new InvalidOperationException("GIF frame schedule contains a non-positive delay.");
            }
        }

        return new GifFrameSchedule(sourceTimes, delays, quantizedOverlays, quantizedFrameLayers);
    }
}
