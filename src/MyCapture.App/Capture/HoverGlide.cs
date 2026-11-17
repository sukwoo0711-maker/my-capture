using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>
/// Where a selector draws its hover frame while it moves to a newly detected window or element. The
/// native and the WPF selector both keep one and feed it the hover target and their clock.
/// </summary>
/// <remarks>
/// Only the picture moves: what a click or Enter captures, and the size the tab shows, is always the
/// new target. A new target glides there from the rectangle on screen, also in the middle of
/// another glide, so a pointer sweeping across windows never makes the frame jump. Windows of any
/// size glide; a move of a few pixels and every move with Windows animation effects off snap.
/// Rectangles are frame pixels, and every frame lands on whole pixels.
/// </remarks>
internal sealed class HoverGlide
{
    /// <summary>Glide length for a nearby target of a similar size: <c>Motion.Fast</c>.</summary>
    internal static readonly TimeSpan FastLength = TimeSpan.FromMilliseconds(83);

    /// <summary>Glide length for a distant target or one of a very different size: <c>Motion.Normal</c>.</summary>
    internal static readonly TimeSpan NormalLength = TimeSpan.FromMilliseconds(167);

    /// <summary>
    /// Pixels the centre may move, and the width and height may change together, for the frame to
    /// snap instead of gliding.
    /// </summary>
    internal const double SnapDistance = 6;

    /// <summary>Centre distance in pixels from which a glide takes <see cref="NormalLength"/>.</summary>
    internal const double FastDistance = 480;

    /// <summary>Larger-to-smaller area ratio from which a glide takes <see cref="NormalLength"/>.</summary>
    internal const double FastAreaRatio = 6;

    // cubic-bezier(0.20, 0.90, 0.30, 1.00): leaves at once and settles softly, so the frame keeps
    // up with the pointer without a visible landing bump.
    private const double X1 = 0.20, Y1 = 0.90, X2 = 0.30, Y2 = 1.00;

    private RectD _from;
    private TimeSpan _start, _length;

    /// <summary>The rectangle the frame is moving to: the current hover.</summary>
    internal RectD? Target { get; private set; }

    /// <summary>The rectangle drawn now.</summary>
    internal RectD? Shown { get; private set; }

    /// <summary>Whether the frame has not landed yet, so the selector must draw another frame.</summary>
    internal bool IsGliding { get; private set; }

    /// <summary>
    /// Moves the frame towards <paramref name="target"/> and returns where to draw it at
    /// <paramref name="now"/>. Without <paramref name="animate"/> every change snaps.
    /// </summary>
    internal RectD? Update(RectD? target, TimeSpan now, bool animate)
    {
        if (target is not { } to)
        {
            Reset();
            return null;
        }
        if (Target != to)
        {
            // A new target starts from what is on screen, mid-glide included, with a fresh clock.
            RectD? from = Shown;
            Target = to;
            TimeSpan? length = animate && from is { } start ? Length(start, to) : null;
            IsGliding = length.HasValue;
            if (IsGliding)
            {
                _from = from!.Value;
                _start = now;
                _length = length!.Value;
            }
        }
        else if (!animate) IsGliding = false;

        if (!IsGliding) return Shown = to;
        double progress = (now - _start) / _length;
        RectD shown = progress >= 1 ? to : At(_from, to, Ease(Math.Max(0, progress)));
        // The curve's tail moves less than a pixel: once the whole-pixel frame is on the target it
        // has landed, and the selector stops asking for frames.
        if (shown == to) IsGliding = false;
        return Shown = shown;
    }

    /// <summary>Forgets the frame: the next target snaps.</summary>
    internal void Reset() => Snap(null);

    /// <summary>
    /// Draws <paramref name="target"/> at once and ends any glide, for a step that must show exactly
    /// what a click takes (an element level).
    /// </summary>
    internal void Snap(RectD? target)
    {
        Target = Shown = target;
        IsGliding = false;
    }

    /// <summary>
    /// How long the frame glides from <paramref name="from"/> to <paramref name="to"/>, or
    /// <see langword="null"/> when it snaps.
    /// </summary>
    internal static TimeSpan? Length(RectD from, RectD to)
    {
        PointD a = from.Center, b = to.Center;
        double distance = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
        double resize = Math.Abs((to.Right - to.Left) - (from.Right - from.Left))
            + Math.Abs((to.Bottom - to.Top) - (from.Bottom - from.Top));
        if (distance <= SnapDistance && resize <= SnapDistance) return null;
        double fromArea = (from.Right - from.Left) * (from.Bottom - from.Top);
        double toArea = (to.Right - to.Left) * (to.Bottom - to.Top);
        double smaller = Math.Min(fromArea, toArea), larger = Math.Max(fromArea, toArea);
        bool similar = smaller > 0 ? larger / smaller < FastAreaRatio : larger <= 0;
        return distance < FastDistance && similar ? FastLength : NormalLength;
    }

    /// <summary>The glide's easing: cubic-bezier(0.20, 0.90, 0.30, 1.00) at <paramref name="progress"/>.</summary>
    internal static double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        // x(t) rises monotonically from 0 to 1 (both x control points lie in [0, 1]): find the t
        // whose x is the progress, Newton first and bisection if the slope gives out.
        double t = progress;
        for (int step = 0; step < 8; step++)
        {
            double error = Bezier(t, X1, X2) - progress;
            if (Math.Abs(error) < 1e-10) return Bezier(t, Y1, Y2);
            double slope = Slope(t, X1, X2);
            if (Math.Abs(slope) < 1e-6) break;
            t = Math.Clamp(t - (error / slope), 0, 1);
        }
        double low = 0, high = 1;
        t = progress;
        for (int step = 0; step < 48 && high - low > 1e-9; step++)
        {
            if (Bezier(t, X1, X2) < progress) low = t; else high = t;
            t = (low + high) / 2;
        }
        return Bezier(t, Y1, Y2);
    }

    /// <summary>
    /// The rectangle <paramref name="eased"/> of the way from <paramref name="from"/> to
    /// <paramref name="to"/>, on whole pixels. Each edge moves on its own, so the rectangle never
    /// turns inside out.
    /// </summary>
    internal static RectD At(RectD from, RectD to, double eased)
    {
        double left = Math.Round(from.Left + ((to.Left - from.Left) * eased));
        double top = Math.Round(from.Top + ((to.Top - from.Top) * eased));
        double right = Math.Round(from.Right + ((to.Right - from.Right) * eased));
        double bottom = Math.Round(from.Bottom + ((to.Bottom - from.Bottom) * eased));
        return new RectD(left, top, right - left, bottom - top);
    }

    // One coordinate of the curve through 0, p1, p2 and 1: ((a t + b) t + c) t.
    private static double Bezier(double t, double p1, double p2)
    {
        double c = 3 * p1, b = (3 * (p2 - p1)) - c, a = 1 - c - b;
        return ((((a * t) + b) * t) + c) * t;
    }

    private static double Slope(double t, double p1, double p2)
    {
        double c = 3 * p1, b = (3 * (p2 - p1)) - c, a = 1 - c - b;
        return (((3 * a * t) + (2 * b)) * t) + c;
    }
}
