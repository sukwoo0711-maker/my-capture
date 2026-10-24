using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// Draws a <see cref="SymbolIcon"/> outline from its first point to its last, like a pen
/// stroke: a confirmation check that redraws each time an action succeeds again.
/// </summary>
/// <remarks>
/// Only the icon's <see cref="SymbolIcon.Data"/> changes while it draws, so its size, stroke
/// and theme brush stay with the icon. <see cref="Stop"/> must run before anything else sets
/// the icon's geometry; with Windows animation effects off the outline appears at once.
/// </remarks>
internal static class StrokeDrawMotion
{
    private static readonly DependencyProperty FinalProperty = DependencyProperty.RegisterAttached(
        "Final", typeof(Geometry), typeof(StrokeDrawMotion));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached(
        "Progress", typeof(double), typeof(StrokeDrawMotion), new PropertyMetadata(1d, OnProgressChanged));

    /// <summary>How much of the outline is drawn, from 0 to 1; 1 when no draw is running.</summary>
    internal static double GetProgress(SymbolIcon icon) => (double)icon.GetValue(ProgressProperty);

    /// <summary>Whether a draw is running on <paramref name="icon"/>.</summary>
    internal static bool IsDrawing(SymbolIcon icon) => icon.GetValue(FinalProperty) is Geometry;

    /// <summary>Draws <paramref name="geometry"/> on <paramref name="icon"/> over <paramref name="duration"/>.</summary>
    internal static void Play(SymbolIcon icon, Geometry geometry, Duration duration)
    {
        Stop(icon);
        icon.Data = geometry;
        if (!FluidMotion.AnimationsEnabled) return;

        icon.SetValue(FinalProperty, geometry);
        var draw = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        draw.Completed += (_, _) =>
        {
            if (ReferenceEquals(icon.GetValue(FinalProperty), geometry)) Stop(icon);
        };
        icon.BeginAnimation(ProgressProperty, draw, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Ends a running draw and leaves the whole outline, if one was drawing.</summary>
    internal static void Stop(SymbolIcon icon)
    {
        if (icon.GetValue(FinalProperty) is not Geometry final) return;
        icon.ClearValue(FinalProperty);
        icon.BeginAnimation(ProgressProperty, null);
        icon.Data = final;
    }

    private static void OnProgressChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is SymbolIcon icon && icon.GetValue(FinalProperty) is Geometry final)
            icon.Data = Partial(final, (double)e.NewValue);
    }

    /// <summary>The first <paramref name="progress"/> of <paramref name="geometry"/>'s outline, by length.</summary>
    internal static Geometry Partial(Geometry geometry, double progress)
    {
        if (progress >= 1) return geometry;
        if (!(progress > 0)) return Geometry.Empty;

        PathGeometry flat = geometry.GetFlattenedPathGeometry();
        var figures = new List<List<Point>>();
        double total = 0;
        foreach (PathFigure figure in flat.Figures)
        {
            var points = new List<Point> { figure.StartPoint };
            foreach (PathSegment segment in figure.Segments)
            {
                if (segment is LineSegment line) points.Add(line.Point);
                else if (segment is PolyLineSegment polyline) points.AddRange(polyline.Points);
            }
            if (figure.IsClosed) points.Add(figure.StartPoint);
            for (int i = 1; i < points.Count; i++) total += (points[i] - points[i - 1]).Length;
            figures.Add(points);
        }

        double remaining = total * progress;
        var partial = new StreamGeometry();
        using (StreamGeometryContext context = partial.Open())
        {
            foreach (List<Point> points in figures)
            {
                if (remaining <= 0) break;
                context.BeginFigure(points[0], isFilled: false, isClosed: false);
                for (int i = 1; i < points.Count && remaining > 0; i++)
                {
                    Vector step = points[i] - points[i - 1];
                    double length = step.Length;
                    Point end = length <= remaining ? points[i] : points[i - 1] + (step * (remaining / length));
                    context.LineTo(end, isStroked: true, isSmoothJoin: false);
                    remaining -= Math.Min(length, remaining);
                }
            }
        }
        partial.Freeze();
        return partial;
    }
}
