using System.Globalization;

namespace MyCapture.Core.Primitives;

/// <summary>
/// An axis-aligned rectangle in capture-image pixel space.
/// </summary>
/// <remarks>
/// Width and height are permitted to be negative during construction because
/// dragging a selection upward or leftward naturally produces them; call
/// <see cref="Normalized"/> before using the value for anything other than
/// tracking an in-progress drag.
/// </remarks>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static RectD Empty => new(0, 0, 0, 0);

    public double Left => Math.Min(X, X + Width);
    public double Top => Math.Min(Y, Y + Height);
    public double Right => Math.Max(X, X + Width);
    public double Bottom => Math.Max(Y, Y + Height);

    public PointD TopLeft => new(Left, Top);
    public PointD BottomRight => new(Right, Bottom);
    public PointD Center => new((Left + Right) / 2.0, (Top + Bottom) / 2.0);

    public bool IsEmpty => Math.Abs(Width) < double.Epsilon || Math.Abs(Height) < double.Epsilon;

    /// <summary>
    /// Returns an equivalent rectangle with non-negative width and height.
    /// </summary>
    public RectD Normalized() => new(Left, Top, Right - Left, Bottom - Top);

    public static RectD FromCorners(PointD a, PointD b) =>
        new RectD(a.X, a.Y, b.X - a.X, b.Y - a.Y).Normalized();

    public RectD Inflate(double amount) =>
        new RectD(Left - amount, Top - amount, (Right - Left) + (amount * 2), (Bottom - Top) + (amount * 2))
            .Normalized();

    public bool Contains(PointD p) =>
        p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    /// <summary>
    /// Clamps this rectangle so it lies entirely inside <paramref name="bounds"/>.
    /// </summary>
    /// <remarks>
    /// Used when the user nudges a selection with the arrow keys: the selection
    /// must stop at the monitor edge rather than silently producing coordinates
    /// outside the frozen frame, which would crop to garbage.
    /// </remarks>
    public RectD ClampTo(RectD bounds)
    {
        RectD n = Normalized();
        RectD b = bounds.Normalized();

        double width = Math.Min(n.Width, b.Width);
        double height = Math.Min(n.Height, b.Height);

        double left = Math.Clamp(n.Left, b.Left, b.Right - width);
        double top = Math.Clamp(n.Top, b.Top, b.Bottom - height);

        return new RectD(left, top, width, height);
    }

    /// <summary>
    /// Rounds outward to whole pixels.
    /// </summary>
    /// <remarks>
    /// Selection edges must land on pixel boundaries or the captured bitmap picks
    /// up a half-blended row from the neighbouring content. Rounding outward
    /// rather than to nearest guarantees the user never loses a pixel they framed.
    /// </remarks>
    public RectD ToPixelBounds()
    {
        RectD n = Normalized();
        double left = Math.Floor(n.Left);
        double top = Math.Floor(n.Top);
        double right = Math.Ceiling(n.Right);
        double bottom = Math.Ceiling(n.Bottom);
        return new RectD(left, top, right - left, bottom - top);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{X:0.##},{Y:0.##} {Width:0.##}x{Height:0.##}]");
}
