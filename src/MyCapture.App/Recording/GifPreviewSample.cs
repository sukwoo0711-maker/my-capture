using System.Diagnostics;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// One preview sample rendered by the real GIF exporter. When the sample is the whole kept
/// range it also owns that render as an exact, saveable export result.
/// </summary>
internal sealed class GifPreviewSample : IDisposable
{
    private VideoExportCalculation? _wholeRange;

    private GifPreviewSample(
        GifSampleWindow window,
        GifExportQuality quality,
        double speed,
        long bytes,
        double elapsedMs,
        GifSampleFrames frames,
        double motionFraction,
        VideoExportCalculation? wholeRange)
    {
        Window = window;
        Quality = quality;
        Speed = speed;
        Bytes = bytes;
        ElapsedMs = elapsedMs;
        Frames = frames;
        MotionFraction = motionFraction;
        _wholeRange = wholeRange;
    }

    internal GifSampleWindow Window { get; }

    internal GifExportQuality Quality { get; }

    internal double Speed { get; }

    internal long Bytes { get; }

    /// <summary>Wall-clock export time of the sample, excluding preview decoding.</summary>
    internal double ElapsedMs { get; }

    internal GifSampleFrames Frames { get; }

    /// <summary>Mean changed-block share between frames; see <see cref="GifMotionAnalysis"/>.</summary>
    internal double MotionFraction { get; }

    internal GifPresetMeasurement Measurement => new(Quality, Bytes, Window.DurationMs, ElapsedMs / Window.DurationMs);

    /// <summary>Source time of a sample frame, from its output start time and the speed.</summary>
    internal double SourceTimeOf(int frameIndex) =>
        Math.Min(Window.EndMs - 1, Window.StartMs + (Frames.Timeline.StartOf(frameIndex) * Speed));

    /// <summary>Transfers the whole-range result to the caller, or returns null.</summary>
    internal VideoExportCalculation? TakeWholeRangeResult()
    {
        VideoExportCalculation? result = _wholeRange;
        _wholeRange = null;
        return result;
    }

    /// <summary>Renders and decodes the sample; must run on an STA worker thread.</summary>
    internal static GifPreviewSample Render(
        RecordingResult recording,
        VideoEditDocument sampleDocument,
        GifSampleWindow window,
        GifExportQuality quality,
        double speed,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        VideoExportCalculation? calculation = VideoExportCalculation.CalculateGif(
            path => AnimatedGifExporter.Export(recording, sampleDocument, path, null, cancellationToken, speed, quality),
            cancellationToken);
        try
        {
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            GifSampleFrames frames = GifSampleDecoder.Decode(calculation.ResultPath, cancellationToken);
            double motion = GifMotionAnalysis.ChangedFraction(frames, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            long bytes = calculation.ResultBytes;
            if (!window.CoversWholeRange)
            {
                calculation.Dispose();
                calculation = null;
            }

            var sample = new GifPreviewSample(window, quality, speed, bytes, elapsed, frames, motion, calculation);
            calculation = null;
            return sample;
        }
        finally
        {
            calculation?.Dispose();
        }
    }

    /// <summary>
    /// Renders an estimate-only segment and returns its GIF bytes; the file is discarded.
    /// Must run on an STA worker thread.
    /// </summary>
    internal static long MeasureSegment(
        RecordingResult recording,
        VideoEditDocument segmentDocument,
        GifExportQuality quality,
        double speed,
        CancellationToken cancellationToken)
    {
        using VideoExportCalculation calculation = VideoExportCalculation.CalculateGif(
            path => AnimatedGifExporter.Export(recording, segmentDocument, path, null, cancellationToken, speed, quality),
            cancellationToken);
        return calculation.ResultBytes;
    }

    public void Dispose()
    {
        _wholeRange?.Dispose();
        _wholeRange = null;
    }
}
