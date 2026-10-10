using Microsoft.Extensions.Logging;
using MyCapture.Core.Diagnostics;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Re-renders a non-destructive source interval into MP4, optionally burning timed text into
/// each frame. The source file is never modified.
/// </summary>
internal static class TrimReencoder
{
    public static int Reencode(
        string sourcePath,
        string outputPath,
        double inMs,
        double outMs,
        RecordingResult recording,
        Func<VideoEncoderOptions, IVideoEncoder> encoderFactory,
        ILogger log,
        IReadOnlyList<TimedTextOverlay>? textOverlays = null,
        IReadOnlyList<FrameEditLayer>? frameEditLayers = null,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? bitrateBitsPerSecond = null,
        Mp4RenderOptions? renderOptions = null) =>
        ReencodeWithStatistics(
            sourcePath,
            outputPath,
            inMs,
            outMs,
            recording,
            encoderFactory,
            log,
            textOverlays,
            frameEditLayers,
            progress,
            cancellationToken,
            bitrateBitsPerSecond,
            renderOptions).Frames;

    /// <summary>
    /// <see cref="Reencode"/> that also reports which decode path produced the frames. A hardware
    /// decoder that fails after opening is retried once in software into a fresh encoder, which
    /// overwrites the partial output.
    /// </summary>
    internal static Mp4RenderStatistics ReencodeWithStatistics(
        string sourcePath,
        string outputPath,
        double inMs,
        double outMs,
        RecordingResult recording,
        Func<VideoEncoderOptions, IVideoEncoder> encoderFactory,
        ILogger log,
        IReadOnlyList<TimedTextOverlay>? textOverlays = null,
        IReadOnlyList<FrameEditLayer>? frameEditLayers = null,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? bitrateBitsPerSecond = null,
        Mp4RenderOptions? renderOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(encoderFactory);
        ArgumentNullException.ThrowIfNull(log);
        VideoLayerResourceBudget.Validate(frameEditLayers);

        int fps = Math.Max(1, recording.Fps);
        int width = recording.Width;
        int height = recording.Height;
        int bitrate = bitrateBitsPerSecond ?? VideoEncoderOptions.DeriveBitrate(width, height, fps);
        if (bitrate is < 64_000 or > 24_000_000)
            throw new ArgumentOutOfRangeException(nameof(bitrateBitsPerSecond));
        var options = new VideoEncoderOptions(outputPath, width, height, fps, bitrate);
        var request = new VideoFrameRenderRequest(
            sourcePath,
            inMs,
            outMs,
            fps,
            width,
            height,
            recording.Width,
            recording.Height,
            textOverlays ?? [],
            frameEditLayers ?? []);
        Mp4RenderOptions attempt = renderOptions ?? Mp4RenderOptions.Default;
        while (true)
        {
            IVideoEncoder? encoder = null;
            try
            {
                Mp4RenderStatistics statistics = VideoFrameRenderPipeline.RenderToEncoder(
                    request,
                    format => encoder = encoderFactory(options with { InputFormat = format }),
                    attempt,
                    log,
                    progress,
                    cancellationToken);
                (encoder ?? throw new InvalidOperationException("No video encoder was opened.")).Complete();
                log.LogInformation(
                    "Video re-rendered {Frames} frame(s) [{In:0}..{Out:0})ms from {Source}, {Composed} composed -> {Path}",
                    statistics.Frames,
                    inMs,
                    outMs,
                    statistics.FrameSource,
                    statistics.ComposedFrames,
                    LogText.SingleLine(outputPath));
                return statistics;
            }
            catch (Mp4HardwareDecodeException failure) when (!cancellationToken.IsCancellationRequested)
            {
                log.LogWarning(
                    failure.InnerException,
                    "Hardware video decode failed during MP4 export; retrying with the software decoder.");
                attempt = attempt with { PreferHardwareDecode = false };
            }
            finally
            {
                encoder?.Dispose();
            }
        }
    }
}
