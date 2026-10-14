using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>Modifier-key constraints in image pixels, independent of the input device.</summary>
public static class AnnotationGestureConstraints
{
    public static PointD Square(PointD origin, PointD point)
    {
        double dx = point.X - origin.X;
        double dy = point.Y - origin.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return origin.Offset(dx < 0 ? -side : side, dy < 0 ? -side : side);
    }

    public static PointD Line(PointD origin, PointD point)
    {
        double dx = point.X - origin.X;
        double dy = point.Y - origin.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        double length = Math.Sqrt(dx * dx + dy * dy);
        return origin.Offset(Math.Cos(angle) * length, Math.Sin(angle) * length);
    }

    public static PointD Axis(PointD origin, PointD point) =>
        Math.Abs(point.X - origin.X) >= Math.Abs(point.Y - origin.Y)
            ? new PointD(point.X, origin.Y)
            : new PointD(origin.X, point.Y);
}
