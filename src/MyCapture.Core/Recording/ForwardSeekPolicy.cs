namespace MyCapture.Core.Recording;

/// <summary>Where a decoder resumed after a forward seek, relative to the request that caused it.</summary>
public enum ForwardSeekLanding
{
    /// <summary>The first sample after the seek lies after the previously decoded position and at or before the target.</summary>
    Advanced,

    /// <summary>The key frame before the target was not newer than samples already decoded; the seek saved nothing.</summary>
    NoProgress,

    /// <summary>The first sample after the seek lies after the target, so frames the request needs were skipped.</summary>
    Overshot,
}

/// <summary>
/// Decides when a forward frame request should seek rather than decode every intermediate sample.
/// </summary>
/// <remarks>
/// <para>
/// Media Foundation's <c>IMFSourceReader::SetCurrentPosition</c> resumes at the key frame at or
/// before the requested time, so the decoder still walks forward to the exact target. Continuing
/// sequentially costs one decode per sample between the newest decoded sample and the target;
/// seeking costs at most one key-frame interval of decodes plus a flush. Seeking is therefore
/// never slower once the gap reaches the key-frame interval, because such a gap must contain a
/// key frame newer than the current position.
/// </para>
/// <para>
/// The default threshold is at least the key-frame interval the platform H.264 encoders wrote
/// for MyCapture recordings on the reference machine (one second at 1080p, two seconds at
/// 640x360, independent of frame rate) and more than twice the largest step of any sequential
/// schedule: playback and MP4 export advance one frame per request, and GIF export at most 800 ms
/// of source time (5 fps at 4x speed). Those schedules therefore never seek. Very small regions
/// were written with three-second intervals; they decode cheaply and fall under the adaptive rule.
/// </para>
/// <para>
/// Sources with sparser key frames are handled adaptively: when a seek lands no further than the
/// position already decoded, the threshold grows beyond the observed key-frame distance, so the
/// waste is bounded by one interval. A source that lands after the requested time cannot serve
/// exact forward seeks at all, and forward seeking is disabled for that reader.
/// </para>
/// </remarks>
public sealed class ForwardSeekPolicy
{
    /// <summary>Default minimum forward gap that seeks instead of decoding sequentially.</summary>
    public const double DefaultThresholdMs = 2000;

    private const double TimestampEpsilonMs = 0.01;

    /// <param name="thresholdMs">
    /// Minimum forward gap that seeks. Positive infinity keeps every forward request sequential.
    /// </param>
    public ForwardSeekPolicy(double thresholdMs = DefaultThresholdMs)
    {
        if (double.IsNaN(thresholdMs) || thresholdMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdMs), "The forward-seek threshold must be positive.");
        }

        ThresholdMs = thresholdMs;
    }

    /// <summary>Current minimum forward gap; positive infinity once forward seeking is disabled.</summary>
    public double ThresholdMs { get; private set; }

    /// <summary>
    /// True when <paramref name="targetMs"/> lies at least <see cref="ThresholdMs"/> beyond the
    /// newest decoded sample. Unknown positions and an exhausted stream never seek: there is
    /// nothing to skip, and the sequential path keeps holding the final frame.
    /// </summary>
    public bool ShouldSeek(double? newestDecodedMs, double targetMs, bool endOfStream)
    {
        if (endOfStream
            || newestDecodedMs is not double newest
            || !double.IsFinite(newest)
            || !double.IsFinite(targetMs))
        {
            return false;
        }

        return targetMs - newest >= ThresholdMs;
    }

    /// <summary>
    /// Classifies the first sample decoded after a forward seek and adapts the threshold.
    /// </summary>
    /// <param name="originMs">Newest sample decoded before the seek.</param>
    /// <param name="targetMs">Requested presentation time.</param>
    /// <param name="landingMs">Presentation time of the first sample decoded after the seek.</param>
    public ForwardSeekLanding Evaluate(double originMs, double targetMs, double landingMs)
    {
        if (!double.IsFinite(landingMs) || landingMs > targetMs + TimestampEpsilonMs)
        {
            Disable();
            return ForwardSeekLanding.Overshot;
        }

        if (landingMs <= originMs + TimestampEpsilonMs)
        {
            // No key frame exists in (landing, target], so the interval exceeds target - landing.
            // Doubling past that distance bounds the cost of any later misjudged seek.
            ThresholdMs = Math.Max(ThresholdMs, targetMs - landingMs) * 2;
            return ForwardSeekLanding.NoProgress;
        }

        return ForwardSeekLanding.Advanced;
    }

    /// <summary>Keeps every later forward request sequential, e.g. after the source rejected a seek.</summary>
    public void Disable() => ThresholdMs = double.PositiveInfinity;
}
