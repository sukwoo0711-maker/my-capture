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
/// Frames are due on a fixed grid (one interval after the previous due time), not one
/// interval after the moment the previous frame was actually taken. A loop that wakes a
/// little late therefore catches up on the next frame instead of pushing every later frame
/// back: with a sub-millisecond wake-up latency per frame the old "interval after the last
/// frame" rule lost about 2% of the frames at 30 fps, and with a 15.6 ms system timer it
/// turned 30 fps into 21.4 fps. Only a stall of a whole interval or more re-anchors the grid,
/// one interval after the late frame, so missed slots are dropped rather than burst out.
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
    private double _nextDueMs;

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
    /// A frame is due once the elapsed time reaches the next slot of the frame grid; a frame
    /// taken slightly late does not delay the slot after it. If the loop was blocked for a
    /// whole interval or more, only a single frame is emitted at the current elapsed time —
    /// the intervening frames are dropped — which is exactly the adaptive behaviour that
    /// protects a slow machine.
    /// </remarks>
    public bool TryClaimFrame(double elapsedMs, out double timestampMs)
    {
        if (_lastEmittedTimestampMs < 0)
        {
            // First frame anchors the timeline at zero regardless of any warm-up jitter.
            timestampMs = 0;
            _lastEmittedTimestampMs = 0;
            _nextDueMs = _frameIntervalMs;
            _emittedFrames++;
            return true;
        }

        if (elapsedMs + 1e-6 < _nextDueMs)
        {
            timestampMs = 0;
            return false;
        }

        timestampMs = elapsedMs;
        _lastEmittedTimestampMs = elapsedMs;
        _emittedFrames++;

        // Stay on the grid so wake-up latency never accumulates. If this frame is late by a
        // whole interval or more, the slots in between are dropped and the grid restarts one
        // interval after this frame, so the loop never bursts through the backlog.
        _nextDueMs += _frameIntervalMs;
        if (elapsedMs + 1e-6 >= _nextDueMs)
        {
            _nextDueMs = elapsedMs + _frameIntervalMs;
        }

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

        return Math.Max(0, _nextDueMs - elapsedMs);
    }
}
