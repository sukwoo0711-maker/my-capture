using MyCapture.Core.Primitives;

namespace MyCapture.Core.Pin;

/// <summary>Drag-only snapping in physical virtual-desktop pixels, including mixed-DPI displays.</summary>
public static class PinSnap
{
    public const double DistanceDip = 8;
    public readonly record struct Target(RectD Bounds, bool IsHidden = false);

    public static RectD Calculate(RectD moving, IReadOnlyList<PinDisplayArea> displays,
        IReadOnlyList<Target> pins, bool shiftHeld = false)
    {
        if (shiftHeld) return moving;
        int monitor = -1;
        double bestArea = -1, bestDistance = double.PositiveInfinity;
        for (int i = 0; i < displays.Count; i++)
        {
            RectD bounds = displays[i].Bounds;
            double area = Math.Max(0, Math.Min(moving.Right, bounds.Right) - Math.Max(moving.Left, bounds.Left))
                * Math.Max(0, Math.Min(moving.Bottom, bounds.Bottom) - Math.Max(moving.Top, bounds.Top));
            double x = Gap(moving.Left, moving.Right, bounds.Left, bounds.Right);
            double y = Gap(moving.Top, moving.Bottom, bounds.Top, bounds.Bottom);
            double distance = x * x + y * y;
            if (area > bestArea || area == bestArea && distance < bestDistance)
            {
                monitor = i;
                bestArea = area;
                bestDistance = distance;
            }
        }
        double scale = monitor >= 0 ? displays[monitor].ScaleFactor : 1;
        double threshold = DistanceDip * (double.IsFinite(scale) && scale > 0 ? scale : 1);
        double dx = 0, dy = 0, nearestX = threshold, nearestY = threshold;
        if (monitor >= 0)
        {
            RectD work = displays[monitor].WorkArea;
            Consider(work.Left - moving.Left, ref dx, ref nearestX);
            Consider(work.Right - moving.Right, ref dx, ref nearestX);
            Consider(work.Top - moving.Top, ref dy, ref nearestY);
            Consider(work.Bottom - moving.Bottom, ref dy, ref nearestY);
        }
        foreach (Target pin in pins)
        {
            if (pin.IsHidden || pin.Bounds.IsEmpty) continue;
            RectD other = pin.Bounds;
            if (Gap(moving.Top, moving.Bottom, other.Top, other.Bottom) <= threshold)
            {
                Consider(other.Left - moving.Left, ref dx, ref nearestX);
                Consider(other.Right - moving.Left, ref dx, ref nearestX);
                Consider(other.Left - moving.Right, ref dx, ref nearestX);
                Consider(other.Right - moving.Right, ref dx, ref nearestX);
            }
            if (Gap(moving.Left, moving.Right, other.Left, other.Right) <= threshold)
            {
                Consider(other.Top - moving.Top, ref dy, ref nearestY);
                Consider(other.Bottom - moving.Top, ref dy, ref nearestY);
                Consider(other.Top - moving.Bottom, ref dy, ref nearestY);
                Consider(other.Bottom - moving.Bottom, ref dy, ref nearestY);
            }
        }
        return new RectD(moving.Left + dx, moving.Top + dy, moving.Width, moving.Height);
    }

    private static double Gap(double a, double b, double c, double d) => Math.Max(0, Math.Max(c - b, a - d));
    private static void Consider(double delta, ref double chosen, ref double nearest)
    {
        double distance = Math.Abs(delta);
        if (distance > nearest) return;
        chosen = delta;
        nearest = distance;
    }
}
