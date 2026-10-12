using System.Windows;
using System.Windows.Media;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// Product UI rendered without a window: a popup surface (the tray menu) or a card's content.
/// Opening a popup would put it on the user's screen, so the element is laid out detached
/// (implicit styles and dynamic resources still come from the Application) and drawn through a
/// VisualBrush, which keeps its rounded corners and shadow transparent over the scene.
/// </summary>
internal static class Detached
{
    internal static Size Layout(FrameworkElement element, double width = double.PositiveInfinity)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(new Size(double.IsInfinity(width) ? element.DesiredSize.Width : width, element.DesiredSize.Height)));
        element.UpdateLayout();
        return new Size(element.ActualWidth, element.ActualHeight);
    }

    /// <summary>Draws <paramref name="element"/> with its top-left at <paramref name="origin"/>; <paramref name="pad"/> leaves room for its shadow.</summary>
    internal static void Draw(DrawingContext dc, FrameworkElement element, Point origin, double pad = 24)
    {
        double width = element.ActualWidth, height = element.ActualHeight;
        var area = new Rect(origin.X - pad, origin.Y - pad, width + 2 * pad, height + 2 * pad);
        var brush = new VisualBrush(element)
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(-pad, -pad, width + 2 * pad, height + 2 * pad),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = area,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        dc.DrawRectangle(brush, null, area);
    }
}
