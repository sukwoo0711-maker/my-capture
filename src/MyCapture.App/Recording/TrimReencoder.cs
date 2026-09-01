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
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(encoderFactory);
        ArgumentNullException.ThrowIfNull(log);

        int fps = Math.Max(1, recording.Fps);
        int width = recording.Width;
        int height = recording.Height;
        int bitrate = VideoEncoderOptions.DeriveBitrate(width, height, fps);
        var options = new VideoEncoderOptions(outputPath, width, height, fps, bitrate);
        using IVideoEncoder encoder = encoderFactory(options);

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
                textOverlays ?? []),
            (frame, outputTimestamp) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] pixels = VideoFrameRenderPipeline.ToBgra32(frame, width, height, out int stride);
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, outputTimestamp));
            },
            progress,
            cancellationToken);

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
