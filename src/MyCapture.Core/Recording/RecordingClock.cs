namespace MyCapture.Core.Recording;

/// <summary>
/// Decides, from elapsed time alone, which presentation timestamps a recording should
/// emit so playback runs at real speed with an even cadence — and lets the caller skip
/// (drop) frames when the encoder cannot keep up, without the output speeding up.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that keeps a weak PC responsive. The grab/encode loop asks the
/// clock, on each wake-up, whether a new frame is due and what timestamp it should
/// carry. Because timestamps come from wall-clock elapsed time rather than a frame
/// counter, a dropped frame leaves a longer gap between the surviving frames instead
/// of compressing real time — so a laggy encoder yields a lower effective frame rate,
/// never a sped-up clip.
/// </para>
/// <para>
/// Pure and deterministic: elapsed milliseconds in, decisions out. No timers, no
/// threads, no clocks of its own, so the drop behaviour is unit-tested exactly.
/// </para>
/// </remarks>
public sealed class RecordingClock
{
    private readonly double _frameIntervalMs;
    private long _emittedFrames;
    private double _lastEmittedTimestampMs = -1;

    public RecordingClock(int targetFps)
    {
        if (targetFps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps), "Target FPS must be positive.");
        }

        TargetFps = targetFps;
        _frameIntervalMs = 1000.0 / targetFps;
    }

    public int TargetFps { get; }

    public double FrameIntervalMs => _frameIntervalMs;

    /// <summary>Number of frames actually emitted so far.</summary>
    public long EmittedFrames => _emittedFrames;

    /// <summary>
    /// Given the elapsed time since recording started, decides whether a frame is due.
    /// </summary>
    /// <remarks>
    /// A frame is due when at least one whole frame interval has passed since the last
    /// emitted timestamp. If the loop was blocked and several intervals passed, only a
    /// single frame is emitted at the current elapsed time — the intervening frames are
    /// dropped — which is exactly the adaptive behaviour that protects a slow machine.
    /// </remarks>
    public bool TryClaimFrame(double elapsedMs, out double timestampMs)
    {
        if (_lastEmittedTimestampMs < 0)
        {
            // First frame anchors the timeline at zero regardless of any warm-up jitter.
            timestampMs = 0;
            _lastEmittedTimestampMs = 0;
            _emittedFrames++;
            return true;
        }

        if (elapsedMs - _lastEmittedTimestampMs + 1e-6 < _frameIntervalMs)
        {
            timestampMs = 0;
            return false;
        }

        timestampMs = elapsedMs;
        _lastEmittedTimestampMs = elapsedMs;
        _emittedFrames++;
        return true;
    }

    /// <summary>
    /// Milliseconds until the next frame is due, so the loop can sleep instead of busy-waiting.
    /// </summary>
    public double MillisecondsUntilNextFrame(double elapsedMs)
    {
        if (_lastEmittedTimestampMs < 0)
        {
            return 0;
        }

        double due = _lastEmittedTimestampMs + _frameIntervalMs;
        return Math.Max(0, due - elapsedMs);
    }
}
