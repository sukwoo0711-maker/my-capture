using MyCapture.Core.Primitives;

namespace MyCapture.Core.Pin;

[Flags]
public enum PinResizeEdge
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,
}

/// <summary>Proportional resizing with a fixed opposite corner or opposite edge midpoint.</summary>
public static class PinResize
{
    public const double HitZoneDip = 8;
    public const double MaxHitZoneFraction = 0.2;

    public static PinResizeEdge HitTest(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width)
            || !double.IsFinite(height) || width <= 0 || height <= 0
            || x < 0 || y < 0 || x > width || y > height) return PinResizeEdge.None;

        double horizontal = Math.Min(HitZoneDip, width * MaxHitZoneFraction);
        double vertical = Math.Min(HitZoneDip, height * MaxHitZoneFraction);
        PinResizeEdge edge = x <= horizontal ? PinResizeEdge.Left
            : x >= width - horizontal ? PinResizeEdge.Right : PinResizeEdge.None;
        if (y <= vertical) edge |= PinResizeEdge.Top;
        else if (y >= height - vertical) edge |= PinResizeEdge.Bottom;
        return edge;
    }

    /// <summary>
    /// Bounds and pointer deltas share one coordinate plane. DPI ratios allow that plane to
    /// remain physical desktop pixels when the window moves to a differently scaled monitor.
    /// Corner deltas are projected onto the original diagonal, so either pointer axis can resize.
    /// </summary>
    public static PinGeometry.Placement Calculate(RectD start, double startZoom, PinResizeEdge edge,
        double deltaX, double deltaY, double dpiRatioX = 1, double dpiRatioY = 1,
        double? imageWidth = null, double? imageHeight = null, double minimumWidth = 1, double minimumHeight = 1)
    {
        int horizontal = edge.HasFlag(PinResizeEdge.Left) ? -1 : edge.HasFlag(PinResizeEdge.Right) ? 1 : 0;
        int vertical = edge.HasFlag(PinResizeEdge.Top) ? -1 : edge.HasFlag(PinResizeEdge.Bottom) ? 1 : 0;
        if (edge == PinResizeEdge.None) return new(start.Left, start.Top, start.Width, start.Height, startZoom);

        double naturalWidth = imageWidth ?? start.Width / startZoom * dpiRatioX;
        double naturalHeight = imageHeight ?? start.Height / startZoom * dpiRatioY;
        // Start from the unrounded image extent. A tiny axis can be held at the window's
        // one-DIP minimum; treating that floor as image size would jump on the first sample.
        double targetWidth = naturalWidth / dpiRatioX * startZoom + horizontal * deltaX;
        double targetHeight = naturalHeight / dpiRatioY * startZoom + vertical * deltaY;
        double zoom = horizontal == 0 ? targetHeight / naturalHeight
            : vertical == 0 ? targetWidth / naturalWidth
            : (targetWidth * naturalWidth + targetHeight * naturalHeight)
                / (naturalWidth * naturalWidth + naturalHeight * naturalHeight);
        zoom = PinGeometry.ClampZoom(zoom);
        double width = Math.Max(minimumWidth, naturalWidth * zoom);
        double height = Math.Max(minimumHeight, naturalHeight * zoom);
        double anchorX = horizontal < 0 ? 1 : horizontal > 0 ? 0 : 0.5;
        double anchorY = vertical < 0 ? 1 : vertical > 0 ? 0 : 0.5;
        return new(start.Left + (start.Width - width) * anchorX,
            start.Top + (start.Height - height) * anchorY, width, height, zoom);
    }
}
