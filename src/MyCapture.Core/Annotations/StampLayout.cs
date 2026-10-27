using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// Where a logo or signature stamp sits on the canvas, as a position on a three-by-three grid.
/// </summary>
public enum StampAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Center,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

/// <summary>
/// The placement math for the reusable stamp: it turns a remembered nine-grid anchor, margins and
/// width fraction into a rectangle on one canvas, and a rectangle back into those three values.
/// </summary>
/// <remarks>
/// <para>
/// The placement is stored relative to the canvas rather than as pixels so that a stamp placed
/// at the bottom-right of a 1280-pixel capture also lands at the bottom-right of a 2560-pixel
/// one. The margins stay absolute (canvas pixels): the gap to the edge is the part a person
/// perceives as a fixed distance, while the logo itself scales with the capture.
/// </para>
/// <para>
/// The canvas is whatever the editor shows now, so a rotated or cropped capture is laid out
/// against its rotated or cropped size. The result always lies inside the canvas.
/// </para>
/// </remarks>
public static class StampLayout
{
    public const double DefaultMargin = 16;

    public const double DefaultWidthFraction = 0.15;

    public const double MaxMargin = 4096;

    public const double MinWidthFraction = 0.02;

    public const double MaxWidthFraction = 1.0;

    /// <summary>The smallest edge a stamp is laid out with, so a tiny canvas never produces nothing.</summary>
    private const double MinEdge = 1;

    /// <summary>Within this many pixels a rectangle counts as the one the layout produced.</summary>
    public const double SameRectTolerance = 0.5;

    /// <summary>
    /// The rectangle for a stamp whose picture is <paramref name="sourceWidth"/> by
    /// <paramref name="sourceHeight"/> pixels, on a canvas of <paramref name="canvasWidth"/> by
    /// <paramref name="canvasHeight"/>.
    /// </summary>
    /// <remarks>
    /// The width is <paramref name="widthFraction"/> of the canvas width but never more than the
    /// picture's own width (a small logo is not blown up) and never so large that the stamp,
    /// with its margins, no longer fits the canvas. A margin on an axis the anchor centres is
    /// not used. Margins larger than a quarter of the canvas are reduced so the stamp keeps room.
    /// </remarks>
    public static RectD Place(
        StampAnchor anchor,
        double marginX,
        double marginY,
        double widthFraction,
        double canvasWidth,
        double canvasHeight,
        double sourceWidth,
        double sourceHeight)
    {
        if (!(canvasWidth > 0) || !(canvasHeight > 0) || !(sourceWidth > 0) || !(sourceHeight > 0))
        {
            return RectD.Empty;
        }

        double aspect = sourceWidth / sourceHeight;
        (double horizontal, double vertical) = Axes(anchor);
        double mx = horizontal == 0 ? 0 : Math.Clamp(Finite(marginX, DefaultMargin), 0, canvasWidth / 4);
        double my = vertical == 0 ? 0 : Math.Clamp(Finite(marginY, DefaultMargin), 0, canvasHeight / 4);
        double fraction = Math.Clamp(Finite(widthFraction, DefaultWidthFraction), MinWidthFraction, MaxWidthFraction);

        double availableWidth = canvasWidth - (2 * mx);
        double availableHeight = canvasHeight - (2 * my);

        double width = Math.Min(sourceWidth, canvasWidth * fraction);
        double height = width / aspect;
        if (width > availableWidth)
        {
            width = availableWidth;
            height = width / aspect;
        }

        if (height > availableHeight)
        {
            height = availableHeight;
            width = height * aspect;
        }

        width = Math.Max(MinEdge, width);
        height = Math.Max(MinEdge, height);

        double x = horizontal switch
        {
            < 0 => mx,
            > 0 => canvasWidth - mx - width,
            _ => (canvasWidth - width) / 2,
        };
        double y = vertical switch
        {
            < 0 => my,
            > 0 => canvasHeight - my - height,
            _ => (canvasHeight - height) / 2,
        };

        return new RectD(x, y, width, height);
    }

    /// <summary>
    /// The anchor, margins and width fraction that describe <paramref name="rect"/> on a canvas.
    /// </summary>
    /// <remarks>
    /// The anchor is the cell of the three-by-three grid that holds the rectangle's centre. The
    /// margins are its distances to the edges that anchor refers to, never negative (a stamp
    /// dragged partly off the canvas is remembered as touching the edge). On an axis the anchor
    /// centres, the margin keeps its previous value because the position carries none.
    /// </remarks>
    public static (StampAnchor Anchor, double MarginX, double MarginY, double WidthFraction) Describe(
        RectD rect,
        double canvasWidth,
        double canvasHeight,
        double previousMarginX = DefaultMargin,
        double previousMarginY = DefaultMargin)
    {
        RectD box = rect.Normalized();
        if (!(canvasWidth > 0) || !(canvasHeight > 0) || box.IsEmpty)
        {
            return (StampAnchor.BottomRight, previousMarginX, previousMarginY, DefaultWidthFraction);
        }

        PointD center = box.Center;
        int column = center.X < canvasWidth / 3 ? -1 : center.X > canvasWidth * 2 / 3 ? 1 : 0;
        int row = center.Y < canvasHeight / 3 ? -1 : center.Y > canvasHeight * 2 / 3 ? 1 : 0;

        double marginX = column switch
        {
            < 0 => box.Left,
            > 0 => canvasWidth - box.Right,
            _ => previousMarginX,
        };
        double marginY = row switch
        {
            < 0 => box.Top,
            > 0 => canvasHeight - box.Bottom,
            _ => previousMarginY,
        };

        return (
            From(column, row),
            Math.Clamp(Math.Round(marginX), 0, MaxMargin),
            Math.Clamp(Math.Round(marginY), 0, MaxMargin),
            Math.Clamp(box.Width / canvasWidth, MinWidthFraction, MaxWidthFraction));
    }

    /// <summary>Whether two rectangles are the same to within <see cref="SameRectTolerance"/> pixels.</summary>
    public static bool AreSame(RectD a, RectD b)
    {
        RectD x = a.Normalized();
        RectD y = b.Normalized();
        return Math.Abs(x.X - y.X) <= SameRectTolerance
            && Math.Abs(x.Y - y.Y) <= SameRectTolerance
            && Math.Abs(x.Width - y.Width) <= SameRectTolerance
            && Math.Abs(x.Height - y.Height) <= SameRectTolerance;
    }

    /// <summary>-1, 0 or 1 along each axis: left/centre/right and top/middle/bottom.</summary>
    private static (int Horizontal, int Vertical) Axes(StampAnchor anchor) => anchor switch
    {
        StampAnchor.TopLeft => (-1, -1),
        StampAnchor.TopCenter => (0, -1),
        StampAnchor.TopRight => (1, -1),
        StampAnchor.MiddleLeft => (-1, 0),
        StampAnchor.Center => (0, 0),
        StampAnchor.MiddleRight => (1, 0),
        StampAnchor.BottomLeft => (-1, 1),
        StampAnchor.BottomCenter => (0, 1),
        _ => (1, 1),
    };

    private static StampAnchor From(int column, int row) => (column, row) switch
    {
        (< 0, < 0) => StampAnchor.TopLeft,
        (0, < 0) => StampAnchor.TopCenter,
        (> 0, < 0) => StampAnchor.TopRight,
        (< 0, 0) => StampAnchor.MiddleLeft,
        (0, 0) => StampAnchor.Center,
        (> 0, 0) => StampAnchor.MiddleRight,
        (< 0, > 0) => StampAnchor.BottomLeft,
        (0, > 0) => StampAnchor.BottomCenter,
        _ => StampAnchor.BottomRight,
    };

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
