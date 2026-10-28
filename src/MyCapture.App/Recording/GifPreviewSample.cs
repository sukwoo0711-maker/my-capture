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
    private readonly GifFrameSchedule _schedule;

    private GifPreviewSample(
        GifSampleWindow window,
        GifExportQuality quality,
        double speed,
        GifSegmentSize size,
        double elapsedMs,
        GifSampleFrames frames,
        double motionFraction,
        VideoExportCalculation? wholeRange,
        GifFrameSchedule schedule)
    {
        Window = window;
        Quality = quality;
        Speed = speed;
        Size = size;
        ElapsedMs = elapsedMs;
        Frames = frames;
        MotionFraction = motionFraction;
        _wholeRange = wholeRange;
        _schedule = schedule;
    }

    internal GifSampleWindow Window { get; }

    internal GifExportQuality Quality { get; }

    internal double Speed { get; }

    internal long Bytes => Size.Bytes;

    /// <summary>Whole file, head and first-image bytes; see <see cref="GifSegmentSize"/>.</summary>
    internal GifSegmentSize Size { get; }

    /// <summary>Wall-clock export time of the sample, excluding preview decoding.</summary>
    internal double ElapsedMs { get; }

    internal GifSampleFrames Frames { get; }

    /// <summary>Mean changed-block share between frames; see <see cref="GifMotionAnalysis"/>.</summary>
    internal double MotionFraction { get; }

    /// <summary>The sample as a measurement of the range that starts at <paramref name="rangeStartMs"/>.</summary>
    internal GifPresetMeasurement MeasurementFrom(double rangeStartMs) =>
        GifPresetMeasurement.Of(Quality, Size, Window.DurationMs, ElapsedMs / Window.DurationMs, Math.Abs(Window.StartMs - rangeStartMs) < 0.5);

    /// <summary>Source time of a sample frame, including quantized cut seams and speed changes.</summary>
    internal double SourceTimeOf(int frameIndex) =>
        _schedule.SourceTimeAtOutput(Frames.Timeline.StartOf(frameIndex));

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
            GifSampleFrames frames = GifSampleDecoder.Decode(calculation.ResultPath, cancellationToken, fitToMemoryBudget: true);
            double motion = GifMotionAnalysis.ChangedFraction(
                frames,
                GifMotionAnalysis.LumaThreshold,
                cancellationToken,
                NominalDelayCentiseconds(quality, speed, recording.Fps));
            cancellationToken.ThrowIfCancellationRequested();
            GifSegmentSize size = GifSegmentSize.Measure(calculation.ResultPath);
            if (!window.CoversWholeRange)
            {
                calculation.Dispose();
                calculation = null;
            }

            GifFrameSchedule schedule = AnimatedGifExporter.BuildFrameSchedule(sampleDocument, speed, quality.FramesPerSecond, recording.Fps);
            var sample = new GifPreviewSample(window, quality, speed, size, elapsed, frames, motion, calculation, schedule);
            calculation = null;
            return sample;
        }
        finally
        {
            calculation?.Dispose();
        }
    }

    /// <summary>
    /// Renders an estimate-only segment and returns its GIF sizes; the file is discarded.
    /// Must run on an STA worker thread.
    /// </summary>
    internal static GifSegmentSize MeasureSegment(
        RecordingResult recording,
        VideoEditDocument segmentDocument,
        GifExportQuality quality,
        double speed,
        CancellationToken cancellationToken)
    {
        using VideoExportCalculation calculation = VideoExportCalculation.CalculateGif(
            path => AnimatedGifExporter.Export(recording, segmentDocument, path, null, cancellationToken, speed, quality),
            cancellationToken);
        return GifSegmentSize.Measure(calculation.ResultPath);
    }

    /// <summary>The exporter's frame delay before identical frames merge, in centiseconds.</summary>
    internal static double NominalDelayCentiseconds(GifExportQuality quality, double speed, double sourceFramesPerSecond = double.PositiveInfinity) =>
        100.0 / GifExportQuality.EffectiveFrameRate(quality.FramesPerSecond, sourceFramesPerSecond, speed) / speed;

    public void Dispose()
    {
        _wholeRange?.Dispose();
        _wholeRange = null;
    }
}
