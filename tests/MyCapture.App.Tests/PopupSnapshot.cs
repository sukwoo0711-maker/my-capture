using System.Windows;

namespace MyCapture.App.Tests;

/// <summary>Geometry for detached popup roots, whose rendered origin includes their margin.</summary>
internal static class PopupSnapshot
{
    internal static Size ExtentOf(FrameworkElement body) => new(
        body.Margin.Left + body.ActualWidth + body.Margin.Right,
        body.Margin.Top + body.ActualHeight + body.Margin.Bottom);

    internal static Vector BodyOffsetOf(FrameworkElement body) => new(body.Margin.Left, body.Margin.Top);

    /// <summary>
    /// Keeps the body at its intended origin while drawing the complete bitmap, including its band.
    /// Image dimensions and scale are in the caller's drawing units, preserving its pixel rounding.
    /// </summary>
    internal static Rect DrawBounds(FrameworkElement body, Point bodyOrigin,
        double imageWidth, double imageHeight, double scale = 1) =>
        new(bodyOrigin - BodyOffsetOf(body) * scale, new Size(imageWidth, imageHeight));
}
