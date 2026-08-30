using System.Globalization;

namespace MyCapture.Core.Primitives;

/// <summary>
/// A point in capture-image pixel space.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>System.Windows.Point</c>. Annotation geometry is persisted
/// to JSON and must survive across app versions, so the serialised shape is part
/// of the file format. Owning the type keeps that shape small (<c>{"x":..,"y":..}</c>)
/// and prevents a WPF dependency from leaking into the domain layer, which in turn
/// keeps the queue and annotation model unit-testable without a UI thread.
/// </para>
/// <para>
/// All annotation coordinates are stored in the captured image's own pixel space,
/// never in device-independent units. A capture taken on a 150% monitor and later
/// re-edited on a 100% monitor must render identically, and that is only true if
/// geometry is anchored to the pixels of the image rather than to the DPI of
/// whichever display happened to be involved.
/// </para>
/// </remarks>
public readonly record struct PointD(double X, double Y)
{
    public static PointD Origin => new(0, 0);

    public PointD Offset(double dx, double dy) => new(X + dx, Y + dy);

    public double DistanceTo(PointD other)
    {
        double dx = other.X - X;
        double dy = other.Y - Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.##}, {Y:0.##})");
}
