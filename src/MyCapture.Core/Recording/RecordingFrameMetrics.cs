namespace MyCapture.Core.Recording;

/// <summary>
/// Frame-rate health of a finished recording, measured over the recorded time only.
/// </summary>
/// <remarks>
/// <paramref name="RecordedMs"/> must already exclude paused spans (see
/// <see cref="RecordingPauseTimeline"/>). A pause is not a drop: no frame was due while the
/// user had paused, so counting that wall-clock time would report phantom dropped frames
/// and an artificially low effective frame rate.
/// </remarks>
/// <param name="RecordedMs">Recorded duration with every paused span excluded.</param>
/// <param name="Fps">Target frame rate.</param>
/// <param name="EmittedFrames">Frames actually written.</param>
public readonly record struct RecordingFrameMetrics(double RecordedMs, int Fps, long EmittedFrames)
{
    /// <summary>Frames that should have been produced over the recorded time.</summary>
    public long ExpectedFrames => RecordedMs > 0 && Fps > 0
        ? Math.Max(EmittedFrames, (long)Math.Ceiling(RecordedMs * Fps / 1000.0))
        : Math.Max(0, EmittedFrames);

    /// <summary>Whole frame intervals skipped to keep playback aligned to real time.</summary>
    public long DroppedFrames => Math.Max(0, ExpectedFrames - EmittedFrames);

    /// <summary>Frames actually encoded per second of recorded time.</summary>
    public double EffectiveFps => RecordedMs > 0
        ? EmittedFrames * 1000.0 / RecordedMs
        : 0;

    /// <summary>Ratio of skipped intervals to expected intervals, from 0 to 1.</summary>
    public double DropRate => ExpectedFrames > 0
        ? DroppedFrames / (double)ExpectedFrames
        : 0;
}
