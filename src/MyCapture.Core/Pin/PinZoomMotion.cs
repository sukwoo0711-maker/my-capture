namespace MyCapture.Core.Pin;

/// <summary>
/// The zoom a pin shows on the screen while it moves to the zoom its state already has. Only the
/// shown zoom moves: the state, the readout and the saved session take the new zoom at once.
/// </summary>
/// <remarks>
/// <para>
/// The motion runs on elapsed time, not on frames, with a cubic ease-out (the standard motion curve)
/// over the logarithm of the zoom, so every 10% notch looks equally fast at any size. It never
/// passes the target and never comes back (no spring on key or wheel input).
/// </para>
/// <para>
/// A new target replaces the running motion: it starts from the zoom shown now and takes the full
/// duration from there, so quick notches never queue and the last one is reached
/// <see cref="DurationMs"/> minus <see cref="LeadMs"/> after it. The clock starts
/// <see cref="LeadMs"/> in the past, so the frame drawn in the turn of the input already moves.
/// </para>
/// </remarks>
public sealed class PinZoomMotion
{
    private double _fromLog;
    private double _toLog;
    private double _startMs;

    /// <param name="durationMs">How long a motion takes from the zoom shown when it starts.</param>
    /// <param name="leadMs">How far into the motion the first frame (in the input's turn) is drawn.</param>
    public PinZoomMotion(double durationMs, double leadMs)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        if (!double.IsFinite(leadMs) || leadMs < 0 || leadMs >= durationMs) throw new ArgumentOutOfRangeException(nameof(leadMs));
        DurationMs = durationMs;
        LeadMs = leadMs;
    }

    public double DurationMs { get; }

    public double LeadMs { get; }

    /// <summary>A motion is running: the shown zoom has not reached <see cref="Target"/> yet.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The zoom the motion ends at (the last target given).</summary>
    public double Target => Math.Exp(_toLog);

    /// <summary>
    /// Moves to <paramref name="target"/> from <paramref name="shown"/> (the zoom on the screen now, which
    /// may be part of the way through a previous motion) and returns the zoom to show at <paramref name="nowMs"/>.
    /// </summary>
    public double Retarget(double shown, double target, double nowMs)
    {
        Validate(shown, nameof(shown));
        Validate(target, nameof(target));
        if (!double.IsFinite(nowMs)) throw new ArgumentOutOfRangeException(nameof(nowMs));
        _fromLog = Math.Log(shown);
        _toLog = Math.Log(target);
        if (Math.Abs(_toLog - _fromLog) < 1e-12)
        {
            IsActive = false;
            return target;
        }
        _startMs = nowMs - LeadMs;
        IsActive = true;
        return Sample(nowMs);
    }

    /// <summary>The zoom to show at <paramref name="nowMs"/>; the motion ends (and returns the target) at its duration.</summary>
    public double Sample(double nowMs)
    {
        if (!IsActive) return Target;
        double progress = (nowMs - _startMs) / DurationMs;
        if (double.IsNaN(progress) || progress >= 1)
        {
            IsActive = false;
            return Target;
        }
        return Math.Exp(_fromLog + ((_toLog - _fromLog) * Ease(Math.Max(0, progress))));
    }

    /// <summary>Ends the motion at its target and returns it.</summary>
    public double Finish()
    {
        IsActive = false;
        return Target;
    }

    /// <summary>Cubic ease-out: fast at first, settling into the target without passing it.</summary>
    public static double Ease(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        double rest = 1 - p;
        return 1 - (rest * rest * rest);
    }

    private static void Validate(double zoom, string name)
    {
        if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(name);
    }
}
