namespace MyCapture.Core.Recording;

/// <summary>
/// A non-destructive in/out trim over a clip of a known duration.
/// </summary>
/// <remarks>
/// <para>
/// Trimming never touches the recorded file until the user commits. The editor keeps
/// one of these, moves the In and Out handles, and only when "완료" is pressed does the
/// selected span get re-encoded. That mirrors the still-image editor's layer-preserving
/// philosophy: the source is sacred until an explicit commit.
/// </para>
/// <para>
/// Positions are milliseconds. The invariants — <c>0 &lt;= In &lt; Out &lt;= Duration</c>
/// and a minimum span — are enforced here so the UI cannot drag the handles into a
/// state that would produce an empty or reversed clip.
/// </para>
/// </remarks>
public sealed class TrimSelection
{
    /// <summary>Smallest span the user can trim to, so an accidental drag cannot empty the clip.</summary>
    public const double MinimumSpanMs = 100.0;

    public TrimSelection(double durationMs)
    {
        DurationMs = Math.Max(0, durationMs);
        InMs = 0;
        OutMs = DurationMs;
    }

    public double DurationMs { get; }

    public double InMs { get; private set; }

    public double OutMs { get; private set; }

    public double SelectedDurationMs => Math.Max(0, OutMs - InMs);

    /// <summary>True when the selection still covers the whole clip.</summary>
    public bool IsFullClip => InMs <= 0 && OutMs >= DurationMs;

    /// <summary>
    /// Sets the In point, keeping it at least <see cref="MinimumSpanMs"/> before Out.
    /// </summary>
    public void SetIn(double positionMs)
    {
        double max = Math.Max(0, OutMs - MinimumSpanMs);
        InMs = Math.Clamp(positionMs, 0, max);
    }

    /// <summary>
    /// Sets the Out point, keeping it at least <see cref="MinimumSpanMs"/> after In.
    /// </summary>
    public void SetOut(double positionMs)
    {
        double min = Math.Min(DurationMs, InMs + MinimumSpanMs);
        OutMs = Math.Clamp(positionMs, min, DurationMs);
    }

    /// <summary>Resets to cover the entire clip.</summary>
    public void Reset()
    {
        InMs = 0;
        OutMs = DurationMs;
    }

    /// <summary>Keeps <paramref name="positionMs"/> inside the current selection.</summary>
    public double ClampToSelection(double positionMs) => Math.Clamp(positionMs, InMs, OutMs);
}
