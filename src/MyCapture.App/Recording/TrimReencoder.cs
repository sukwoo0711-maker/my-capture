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
        int? bitrateBitsPerSecond = null)
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
        using IVideoEncoder encoder = encoderFactory(options);
        double frameMs = 1000.0 / fps;
        byte[]? pixelBuffer = null;

        int emitted = VideoFrameRenderPipeline.Render(
            new VideoFrameRenderRequest(
                sourcePath,
                inMs,
                outMs,
                fps,
                width,
                height,
                recording.Width,
                recording.Height,
                textOverlays ?? [],
                frameEditLayers ?? []),
            (frame, outputTimestamp) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                pixelBuffer = VideoFrameRenderPipeline.ToBgra32(frame, width, height, out int stride, pixelBuffer);
                encoder.WriteFrame(new EncoderFrame(pixelBuffer, width, height, stride, outputTimestamp));
            },
            progress,
            cancellationToken,
            (decoded, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Plain trim without overlays: decoded BGRA pixels go straight to the
                // encoder, skipping WPF composition entirely.
                encoder.WriteFrame(new EncoderFrame(
                    decoded.Pixels,
                    decoded.Width,
                    decoded.Height,
                    decoded.Stride,
                    index * frameMs));
            });

        encoder.Complete();
        log.LogInformation(
            "Video re-rendered {Frames} frame(s) [{In:0}..{Out:0})ms -> {Path}",
            emitted,
            inMs,
            outMs,
            LogText.SingleLine(outputPath));
        return emitted;
    }
}
