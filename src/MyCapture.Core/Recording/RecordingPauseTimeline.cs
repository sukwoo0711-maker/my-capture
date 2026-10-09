namespace MyCapture.Core.Recording;

/// <summary>Where a recording session is on its pause timeline.</summary>
public enum RecordingPauseState
{
    Recording = 0,
    Paused = 1,
    Ended = 2,
}

/// <summary>
/// Maps elapsed session time onto the recorded timeline by excluding every paused span, so
/// frames captured after a resume continue exactly where the clip left off.
/// </summary>
/// <remarks>
/// <para>
/// The recorder feeds one monotonic time base (milliseconds since capture started) into the
/// pause commands and into <see cref="ActiveElapsedMs"/>. A paused span therefore never
/// reaches the encoder: the active time freezes at the moment of the pause and continues
/// from the same value after the resume. MP4 timestamps stay continuous and the clip length
/// is the sum of the recorded spans, which is also the base for the frame-rate metrics.
/// </para>
/// <para>
/// Time never runs backwards here: every call clamps its input to the latest time already
/// observed, so a late reading from another thread can never move an emitted timestamp
/// backwards. Pure and deterministic like <see cref="RecordingClock"/> — no timers, no
/// clocks of its own and no locking; the recorder serialises access.
/// </para>
/// </remarks>
public sealed class RecordingPauseTimeline
{
    private double _latestMs;
    private double _closedPausesMs;
    private double _openPauseStartedMs;

    public RecordingPauseState State { get; private set; } = RecordingPauseState.Recording;

    public bool IsPaused => State == RecordingPauseState.Paused;

    /// <summary>Number of pauses requested over the session, including one still open.</summary>
    public int PauseCount { get; private set; }

    /// <summary>Starts a paused span. Rejected while already paused or after the session ended.</summary>
    public bool TryPause(double elapsedMs)
    {
        double now = Observe(elapsedMs);
        if (State != RecordingPauseState.Recording)
        {
            return false;
        }

        _openPauseStartedMs = now;
        State = RecordingPauseState.Paused;
        PauseCount++;
        return true;
    }

    /// <summary>Closes the open paused span. Rejected unless the session is paused.</summary>
    public bool TryResume(double elapsedMs)
    {
        double now = Observe(elapsedMs);
        if (State != RecordingPauseState.Paused)
        {
            return false;
        }

        _closedPausesMs += now - _openPauseStartedMs;
        State = RecordingPauseState.Recording;
        return true;
    }

    /// <summary>
    /// Ends the session. Stopping while paused closes the open span at the same instant, so
    /// the recorded duration ends where the last recorded span ended.
    /// </summary>
    public void End(double elapsedMs)
    {
        if (State == RecordingPauseState.Ended)
        {
            return;
        }

        double now = Observe(elapsedMs);
        if (State == RecordingPauseState.Paused)
        {
            _closedPausesMs += now - _openPauseStartedMs;
        }

        State = RecordingPauseState.Ended;
    }

    /// <summary>Total paused time up to <paramref name="elapsedMs"/>, including an open pause.</summary>
    public double PausedMs(double elapsedMs)
    {
        double now = Observe(elapsedMs);
        return State == RecordingPauseState.Paused
            ? _closedPausesMs + (now - _openPauseStartedMs)
            : _closedPausesMs;
    }

    /// <summary>
    /// Recorded time at <paramref name="elapsedMs"/>: elapsed time minus every paused span.
    /// Frozen while paused and after <see cref="End"/>.
    /// </summary>
    public double ActiveElapsedMs(double elapsedMs)
    {
        double now = Observe(elapsedMs);
        // While paused the value is computed from the pause start alone, so it is bit-for-bit
        // constant however much time passes.
        double activeEnd = State == RecordingPauseState.Paused ? _openPauseStartedMs : now;
        return Math.Max(0, activeEnd - _closedPausesMs);
    }

    private double Observe(double elapsedMs)
    {
        // After End the timeline is frozen; later readings must not extend it.
        if (State != RecordingPauseState.Ended && double.IsFinite(elapsedMs) && elapsedMs > _latestMs)
        {
            _latestMs = elapsedMs;
        }

        return _latestMs;
    }
}
