namespace MyCapture.Core.Recording;

/// <summary>
/// The visible time window of the detail (zoomed) timeline over a clip, plus the pixel↔time
/// mapping the detail strip uses. Pure and UI-free so the zoom/pan/clamp behaviour of the
/// two-line "overview + detail" timeline is unit-tested without any WPF control.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the pro-editor convention (Premiere's zoom scroll bar, Camtasia's zoom slider +
/// fit): the overview strip shows the whole clip and carries a draggable/resizable "viewport
/// brush"; this type is that brush's model — <see cref="ViewStartMs"/>..<see cref="ViewEndMs"/>
/// is exactly the sub-range the detail strip expands to full width.
/// </para>
/// <para>
/// Invariants held at all times: <c>0 &lt;= ViewStartMs &lt; ViewEndMs &lt;= DurationMs</c> and
/// the visible span is never smaller than <see cref="MinSpanMs"/> (a few frames), so the detail
/// strip can never collapse to zero or run past the clip.
/// </para>
/// </remarks>
public sealed class TimelineViewport
{
    private readonly double _minSpanMs;

    public TimelineViewport(double durationMs, int fps)
    {
        DurationMs = Math.Max(1.0, durationMs);
        // Minimum visible span = 3 frames (or 100ms if fps is unknown), so the deepest zoom
        // still shows a handful of frames rather than a single pixel-thin sliver.
        double frameMs = fps > 0 ? 1000.0 / fps : 33.0;
        _minSpanMs = Math.Min(DurationMs, Math.Max(100.0, frameMs * 3));

        ViewStartMs = 0;
        ViewEndMs = DurationMs;
    }

    public double DurationMs { get; }

    public double ViewStartMs { get; private set; }

    public double ViewEndMs { get; private set; }

    public double MinSpanMs => _minSpanMs;

    public double VisibleSpanMs => ViewEndMs - ViewStartMs;

    /// <summary>True when the detail view currently spans the whole clip.</summary>
    public bool IsFitAll => ViewStartMs <= 0 && ViewEndMs >= DurationMs;

    /// <summary>Detail-strip pixel X for a clip time, given the strip's pixel width.</summary>
    public double MsToPx(double ms, double widthPx)
    {
        if (widthPx <= 0 || VisibleSpanMs <= 0)
        {
            return 0;
        }

        double clamped = Math.Clamp(ms, ViewStartMs, ViewEndMs);
        return (clamped - ViewStartMs) / VisibleSpanMs * widthPx;
    }

    /// <summary>Clip time for a detail-strip pixel X, given the strip's pixel width.</summary>
    public double PxToMs(double px, double widthPx)
    {
        if (widthPx <= 0)
        {
            return ViewStartMs;
        }

        double frac = Math.Clamp(px / widthPx, 0.0, 1.0);
        return ViewStartMs + (frac * VisibleSpanMs);
    }

    /// <summary>
    /// Zooms around <paramref name="centerMs"/> by <paramref name="factor"/> (&lt;1 zooms in,
    /// &gt;1 zooms out), keeping the centre time under the cursor fixed, then clamps.
    /// </summary>
    public void Zoom(double centerMs, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor))
        {
            return;
        }

        double center = Math.Clamp(centerMs, 0, DurationMs);
        double newSpan = Math.Clamp(VisibleSpanMs * factor, _minSpanMs, DurationMs);

        // Keep the centre's fractional position within the view constant so zoom feels anchored.
        double frac = VisibleSpanMs > 0 ? (center - ViewStartMs) / VisibleSpanMs : 0.5;
        double newStart = center - (frac * newSpan);
        SetView(newStart, newStart + newSpan);
    }

    /// <summary>Pans the view by <paramref name="deltaMs"/>, preserving span and clamping.</summary>
    public void Pan(double deltaMs)
    {
        double span = VisibleSpanMs;
        double newStart = Math.Clamp(ViewStartMs + deltaMs, 0, DurationMs - span);
        SetView(newStart, newStart + span);
    }

    /// <summary>Sets the view explicitly (e.g. from dragging the overview brush edges).</summary>
    public void SetView(double startMs, double endMs)
    {
        double start = startMs;
        double end = endMs;

        if (end < start)
        {
            (start, end) = (end, start);
        }

        // Enforce the minimum span before clamping into range.
        if (end - start < _minSpanMs)
        {
            double mid = (start + end) / 2.0;
            start = mid - (_minSpanMs / 2.0);
            end = mid + (_minSpanMs / 2.0);
        }

        // Clamp the whole window into [0, Duration] without shrinking below the min span.
        double span = Math.Min(end - start, DurationMs);
        start = Math.Clamp(start, 0, DurationMs - span);
        ViewStartMs = start;
        ViewEndMs = start + span;
    }

    /// <summary>Resets to show the entire clip (Camtasia-style fit-to-timeline).</summary>
    public void FitAll()
    {
        ViewStartMs = 0;
        ViewEndMs = DurationMs;
    }

    /// <summary>
    /// Ensures <paramref name="ms"/> is visible, panning the minimum amount if it is outside
    /// the current view. Used to keep the playhead on-screen in the detail strip.
    /// </summary>
    public void EnsureVisible(double ms)
    {
        if (ms < ViewStartMs)
        {
            Pan(ms - ViewStartMs);
        }
        else if (ms > ViewEndMs)
        {
            Pan(ms - ViewEndMs);
        }
    }
}
