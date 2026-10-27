using System.Globalization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Capture;

/// <summary>
/// The aspect-ratio lock of the region selector, in the order the R key cycles through them and the
/// order the selector's menu lists them.
/// </summary>
public enum SelectionRatio
{
    /// <summary>No lock: the selection is whatever the drag draws.</summary>
    Free = 0,
    Ratio16x9,
    Ratio4x3,
    Ratio1x1,
    Ratio9x16,
}

/// <summary>A selection size the selector can place at once, in physical pixels.</summary>
public readonly record struct FixedSelectionSize(int Width, int Height)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width} × {Height}");
}

/// <summary>
/// The selector's aspect-ratio lock and fixed sizes as pure pixel arithmetic: no WPF, no display
/// and no input, so every rule is the same whether a drag, a key or a test drives it.
/// </summary>
/// <remarks>
/// <para>
/// Everything is in physical pixels of the frozen frame; the display scale only decides which
/// pixels a pointer is over, never the result. A locked selection has whole-pixel sides whose
/// ratio is the nearest the pixel grid allows: the side the user is changing is exact, the other
/// is rounded half up, so the error is at most half a pixel on that side. A ratio therefore
/// reads as "16:9" within <see cref="Matches"/>, not as an exact fraction.
/// </para>
/// <para>
/// A recording needs even sides for H.264 (the recorder otherwise trims an odd last row or
/// column and the ratio drifts). With <c>evenSize</c> every side is a multiple of two and the
/// same rounding runs on pairs of pixels, which keeps the ratio as close as an even grid allows.
/// </para>
/// <para>
/// None of the members allocates; they run on every pointer move while a ratio is locked.
/// </para>
/// </remarks>
public static class SelectionRatios
{
    /// <summary>The locks in cycle and menu order.</summary>
    public static IReadOnlyList<SelectionRatio> All { get; } =
        [SelectionRatio.Free, SelectionRatio.Ratio16x9, SelectionRatio.Ratio4x3, SelectionRatio.Ratio1x1, SelectionRatio.Ratio9x16];

    /// <summary>The sizes the selector's menu can place.</summary>
    public static IReadOnlyList<FixedSelectionSize> FixedSizes { get; } =
        [new(1280, 720), new(1920, 1080), new(1080, 1080)];

    /// <summary>Free, 16:9, 4:3, 1:1, 9:16 and back to Free.</summary>
    public static SelectionRatio Next(SelectionRatio current) => current switch
    {
        SelectionRatio.Free => SelectionRatio.Ratio16x9,
        SelectionRatio.Ratio16x9 => SelectionRatio.Ratio4x3,
        SelectionRatio.Ratio4x3 => SelectionRatio.Ratio1x1,
        SelectionRatio.Ratio1x1 => SelectionRatio.Ratio9x16,
        _ => SelectionRatio.Free,
    };

    /// <summary>Width and height of the lock, or null for <see cref="SelectionRatio.Free"/>.</summary>
    public static (int Width, int Height)? Terms(SelectionRatio ratio) => ratio switch
    {
        SelectionRatio.Ratio16x9 => (16, 9),
        SelectionRatio.Ratio4x3 => (4, 3),
        SelectionRatio.Ratio1x1 => (1, 1),
        SelectionRatio.Ratio9x16 => (9, 16),
        _ => null,
    };

    /// <summary>"16:9" and the like; empty for <see cref="SelectionRatio.Free"/>. Digits only, so it needs no translation.</summary>
    public static string Label(SelectionRatio ratio) => ratio switch
    {
        SelectionRatio.Ratio16x9 => "16:9",
        SelectionRatio.Ratio4x3 => "4:3",
        SelectionRatio.Ratio1x1 => "1:1",
        SelectionRatio.Ratio9x16 => "9:16",
        _ => string.Empty,
    };

    /// <summary>
    /// Whether a <paramref name="width"/> × <paramref name="height"/> selection carries the lock, to
    /// the rounding every operation here applies (a free selection that happens to have the ratio
    /// counts, one that is a few pixels off does not). Always false for <see cref="SelectionRatio.Free"/>.
    /// </summary>
    public static bool Matches(int width, int height, SelectionRatio ratio)
    {
        if (Terms(ratio) is not { } terms || width < 1 || height < 1)
        {
            return false;
        }

        // Rounding the derived side moves it by at most half a pixel, or a pixel pair when sides are
        // even; in cross-multiplied form that is at most the larger term.
        long error = Math.Abs(((long)height * terms.Width) - ((long)width * terms.Height));
        return error <= Math.Max(terms.Width, terms.Height);
    }

    /// <summary>
    /// The selection while a new region is being dragged: it starts at <paramref name="anchor"/>, grows
    /// towards <paramref name="pointer"/> with the lock's ratio, and stays inside
    /// <paramref name="bounds"/>.
    /// </summary>
    /// <remarks>
    /// The longer side of the drag leads: the selection reaches the pointer along it and the other
    /// side follows, so dragging straight along an axis still draws a region. Near an edge the
    /// selection shrinks to the largest one that fits instead of leaving the ratio. Free returns the
    /// plain rectangle between the corners, clamped.
    /// </remarks>
    public static RectD FitDrag(PointD anchor, PointD pointer, RectD bounds, SelectionRatio ratio, bool evenSize = false)
    {
        if (Terms(ratio) is not { } terms)
        {
            return RectD.FromCorners(anchor, pointer).ClampTo(bounds);
        }

        RectD frame = bounds.Normalized();
        double ax = Math.Clamp(Math.Round(anchor.X, MidpointRounding.AwayFromZero), frame.Left, frame.Right);
        double ay = Math.Clamp(Math.Round(anchor.Y, MidpointRounding.AwayFromZero), frame.Top, frame.Bottom);
        double dx = pointer.X - ax;
        double dy = pointer.Y - ay;
        int directionX = Direction(dx, ax, frame.Left, frame.Right);
        int directionY = Direction(dy, ay, frame.Top, frame.Bottom);
        double roomX = directionX > 0 ? frame.Right - ax : ax - frame.Left;
        double roomY = directionY > 0 ? frame.Bottom - ay : ay - frame.Top;
        int unit = evenSize ? 2 : 1;
        double wantedWidth = Math.Max(Math.Abs(dx), Math.Abs(dy) * terms.Width / terms.Height);
        (int width, int height) = wantedWidth < unit / 2.0
            ? (0, 0)
            : FitFromWidth(terms, wantedWidth, roomX, roomY, unit);
        if (width == 0)
        {
            return new RectD(ax, ay, 0, 0);
        }

        return new RectD(directionX > 0 ? ax : ax - width, directionY > 0 ? ay : ay - height, width, height);
    }

    /// <summary>
    /// A selection brought onto the lock around its centre. It only shrinks: the result is the largest
    /// ratio rectangle inside <paramref name="selection"/>, so it never reaches past the selection or
    /// past <paramref name="bounds"/>. Free (or a selection too small to hold the ratio) returns it unchanged.
    /// </summary>
    public static RectD Refit(RectD selection, RectD bounds, SelectionRatio ratio, bool evenSize = false)
    {
        if (Terms(ratio) is not { } terms)
        {
            return selection;
        }

        RectD area = selection.ToPixelBounds().ClampTo(bounds.Normalized());
        if (area.Width < 1 || area.Height < 1)
        {
            return selection;
        }

        (int width, int height) = FitContain(terms, area.Width, area.Height, evenSize ? 2 : 1);
        if (width == 0)
        {
            return selection;
        }

        // An odd leftover goes to the right and bottom.
        return new RectD(
            area.Left + Math.Floor((area.Width - width) / 2),
            area.Top + Math.Floor((area.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// One Shift+arrow press with the lock on: the top-left corner stays, the side the key changes moves by
    /// <paramref name="step"/> pixels and the other side follows the ratio. A selection that does not carry
    /// the lock yet is first brought onto it (shrunk, never grown). Free returns the selection unchanged.
    /// </summary>
    /// <param name="horizontal">True for Left and Right, false for Up and Down.</param>
    /// <param name="direction">Positive grows the selection, negative shrinks it.</param>
    public static RectD Resize(RectD selection, bool horizontal, int direction, double step, RectD bounds,
        SelectionRatio ratio, bool evenSize = false)
    {
        if (Terms(ratio) is not { } terms)
        {
            return selection;
        }

        RectD frame = bounds.Normalized();
        RectD area = selection.ToPixelBounds().ClampTo(frame);
        if (area.Width < 1 || area.Height < 1)
        {
            return selection;
        }

        int unit = evenSize ? 2 : 1;
        int width = (int)area.Width;
        int height = (int)area.Height;
        if (!Matches(width, height, ratio))
        {
            (width, height) = FitContain(terms, area.Width, area.Height, unit);
            if (width == 0)
            {
                return selection;
            }
        }

        double roomWidth = frame.Right - area.Left;
        double roomHeight = frame.Bottom - area.Top;
        int stepUnits = Math.Max(1, (int)Math.Ceiling(Math.Abs(step) / unit));
        int signedUnits = direction < 0 ? -stepUnits : stepUnits;
        (int newWidth, int newHeight) = horizontal
            ? FitFromWidth(terms, (Units(width, unit) + signedUnits) * (double)unit, roomWidth, roomHeight, unit)
            : FitFromHeight(terms, (Units(height, unit) + signedUnits) * (double)unit, roomWidth, roomHeight, unit);
        // A key never works against itself: where the room stops the pressed side from moving, the other
        // side's rounding must not make the selection smaller on a grow key (or larger on a shrink key).
        bool moved = direction < 0
            ? newWidth <= width && newHeight <= height
            : newWidth >= width && newHeight >= height;
        return newWidth == 0 || !moved ? new RectD(area.Left, area.Top, width, height) : new RectD(area.Left, area.Top, newWidth, newHeight);
    }

    /// <summary>
    /// A rectangle of exactly <paramref name="size"/> centred on <paramref name="monitor"/>. A monitor
    /// that cannot hold it gets the largest rectangle of the same ratio that fits: whole multiples of
    /// the size's reduced ratio (1920 × 1080 becomes 16 × 9 steps), so a 16:9 size stays exactly 16:9.
    /// </summary>
    /// <param name="monitor">The monitor, in the same pixel space as <paramref name="bounds"/>.</param>
    /// <param name="bounds">The frame; the result never leaves it. It also stands in for a monitor outside it.</param>
    /// <returns>The placement, or null for a size below one pixel or a frame without area.</returns>
    public static RectD? PlaceFixed(FixedSelectionSize size, RectD monitor, RectD bounds, bool evenSize = false)
    {
        if (size.Width < 1 || size.Height < 1)
        {
            return null;
        }

        RectD frame = bounds.Normalized();
        RectD area = Intersection(monitor.Normalized(), frame);
        if (area.Width < 1 || area.Height < 1)
        {
            area = Intersection(frame, frame);
        }

        if (area.Width < 1 || area.Height < 1)
        {
            return null;
        }

        int width = size.Width;
        int height = size.Height;
        if (width > area.Width || height > area.Height)
        {
            (width, height) = LargestSameRatio(size, area.Width, area.Height, evenSize);
            if (width == 0)
            {
                return null;
            }
        }

        return new RectD(
            area.Left + Math.Floor((area.Width - width) / 2),
            area.Top + Math.Floor((area.Height - height) / 2),
            width,
            height);
    }

    private static (int Width, int Height) LargestSameRatio(FixedSelectionSize size, double roomWidth, double roomHeight,
        bool evenSize)
    {
        int divisor = Gcd(size.Width, size.Height);
        (int Width, int Height) terms = (size.Width / divisor, size.Height / divisor);
        // The smallest exact step: the reduced ratio, doubled when an even size needs it.
        int multiple = !evenSize || (terms.Width % 2 == 0 && terms.Height % 2 == 0) ? 1 : 2;
        long stepWidth = (long)terms.Width * multiple;
        long stepHeight = (long)terms.Height * multiple;
        long steps = (long)Math.Floor(Math.Min(roomWidth / stepWidth, roomHeight / stepHeight));
        return steps >= 1
            ? ((int)(stepWidth * steps), (int)(stepHeight * steps))
            : FitContain(terms, roomWidth, roomHeight, evenSize ? 2 : 1);
    }

    /// <summary>The largest ratio rectangle inside a room, rounded like every other fit.</summary>
    private static (int Width, int Height) FitContain((int Width, int Height) terms, double roomWidth, double roomHeight,
        int unit) =>
        FitFromWidth(terms, Math.Min(roomWidth, roomHeight * terms.Width / terms.Height), roomWidth, roomHeight, unit);

    /// <summary>
    /// Width taken as given (rounded to <paramref name="unit"/> pixels), height derived from it, both held
    /// inside the room. Returns (0, 0) when the room is smaller than one unit.
    /// </summary>
    private static (int Width, int Height) FitFromWidth((int Width, int Height) terms, double wantedWidth,
        double roomWidth, double roomHeight, int unit)
    {
        long maxWidth = (long)Math.Floor(roomWidth / unit);
        long maxHeight = (long)Math.Floor(roomHeight / unit);
        if (maxWidth < 1 || maxHeight < 1)
        {
            return (0, 0);
        }

        // The widest width whose rounded height still fits: 2 * height * w < (2 * maxHeight + 1) * width.
        long limit = Math.Max(1, ((((2 * maxHeight) + 1) * terms.Width) - 1) / (2L * terms.Height));
        long width = Math.Clamp((long)Math.Round(wantedWidth / unit, MidpointRounding.AwayFromZero), 1, Math.Min(maxWidth, limit));
        long height = Math.Min(maxHeight, Scale(width, terms.Height, terms.Width));
        return ((int)(width * unit), (int)(height * unit));
    }

    /// <summary>The same with the height taken as given and the width derived.</summary>
    private static (int Width, int Height) FitFromHeight((int Width, int Height) terms, double wantedHeight,
        double roomWidth, double roomHeight, int unit)
    {
        long maxWidth = (long)Math.Floor(roomWidth / unit);
        long maxHeight = (long)Math.Floor(roomHeight / unit);
        if (maxWidth < 1 || maxHeight < 1)
        {
            return (0, 0);
        }

        long limit = Math.Max(1, ((((2 * maxWidth) + 1) * terms.Height) - 1) / (2L * terms.Width));
        long height = Math.Clamp((long)Math.Round(wantedHeight / unit, MidpointRounding.AwayFromZero), 1, Math.Min(maxHeight, limit));
        long width = Math.Min(maxWidth, Scale(height, terms.Width, terms.Height));
        return ((int)(width * unit), (int)(height * unit));
    }

    /// <summary><paramref name="side"/> × <paramref name="numerator"/> / <paramref name="denominator"/>, rounded half up, at least 1.</summary>
    private static long Scale(long side, int numerator, int denominator) =>
        Math.Max(1, ((2 * side * numerator) + denominator) / (2L * denominator));

    private static int Units(int pixels, int unit) => (int)Math.Round(pixels / (double)unit, MidpointRounding.AwayFromZero);

    /// <summary>+1 or -1 along an axis; a drag that has not left the anchor yet heads where there is more room.</summary>
    private static int Direction(double delta, double anchor, double low, double high) =>
        delta > 0 ? 1 : delta < 0 ? -1 : (high - anchor) >= (anchor - low) ? 1 : -1;

    /// <summary>The overlap of two rectangles, rounded inwards to whole pixels; empty when they do not meet.</summary>
    private static RectD Intersection(RectD first, RectD second)
    {
        double left = Math.Ceiling(Math.Max(first.Left, second.Left));
        double top = Math.Ceiling(Math.Max(first.Top, second.Top));
        double right = Math.Floor(Math.Min(first.Right, second.Right));
        double bottom = Math.Floor(Math.Min(first.Bottom, second.Bottom));
        return right > left && bottom > top ? new RectD(left, top, right - left, bottom - top) : RectD.Empty;
    }

    private static int Gcd(int first, int second)
    {
        while (second != 0)
        {
            (first, second) = (second, first % second);
        }

        return first;
    }
}
