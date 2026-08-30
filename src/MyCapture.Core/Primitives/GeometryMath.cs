namespace MyCapture.Core.Primitives;

/// <summary>
/// Geometry used by annotation hit testing.
/// </summary>
/// <remarks>
/// Hit testing lives in the domain layer rather than relying on WPF's visual hit
/// testing because selection has to work identically whether an annotation is
/// currently rendered on screen, being replayed from a saved layer file, or being
/// evaluated in a unit test.
/// </remarks>
public static class GeometryMath
{
    /// <summary>
    /// Shortest distance from <paramref name="p"/> to the segment
    /// <paramref name="a"/>-<paramref name="b"/>.
    /// </summary>
    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);

        if (lengthSquared <= double.Epsilon)
        {
            // Degenerate segment: both endpoints coincide.
            return p.DistanceTo(a);
        }

        // Project p onto the infinite line, then clamp to the segment.
        double t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);

        var closest = new PointD(a.X + (t * dx), a.Y + (t * dy));
        return p.DistanceTo(closest);
    }

    /// <summary>
    /// Shortest distance from <paramref name="p"/> to a polyline.
    /// </summary>
    /// <returns>
    /// <see cref="double.PositiveInfinity"/> when fewer than two points are given.
    /// </returns>
    public static double DistanceToPolyline(PointD p, IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return double.PositiveInfinity;
        }

        if (points.Count == 1)
        {
            return p.DistanceTo(points[0]);
        }

        double best = double.PositiveInfinity;
        for (int i = 0; i < points.Count - 1; i++)
        {
            double d = DistanceToSegment(p, points[i], points[i + 1]);
            if (d < best)
            {
                best = d;
            }
        }

        return best;
    }

    /// <summary>
    /// Distance from <paramref name="p"/> to the outline of <paramref name="rect"/>.
    /// Returns 0 when the point lies on or inside the outline band.
    /// </summary>
    public static double DistanceToRectOutline(PointD p, RectD rect)
    {
        RectD r = rect.Normalized();

        var tl = new PointD(r.Left, r.Top);
        var tr = new PointD(r.Right, r.Top);
        var br = new PointD(r.Right, r.Bottom);
        var bl = new PointD(r.Left, r.Bottom);

        double d = DistanceToSegment(p, tl, tr);
        d = Math.Min(d, DistanceToSegment(p, tr, br));
        d = Math.Min(d, DistanceToSegment(p, br, bl));
        d = Math.Min(d, DistanceToSegment(p, bl, tl));
        return d;
    }

    /// <summary>
    /// Distance from <paramref name="p"/> to the outline of the ellipse inscribed in
    /// <paramref name="rect"/>.
    /// </summary>
    /// <remarks>
    /// Uses the scaled-radial approximation rather than an exact closest-point
    /// solve. The exact solution requires iterating a quartic; for picking an
    /// annotation with a mouse the approximation is indistinguishable and cheap.
    /// </remarks>
    public static double DistanceToEllipseOutline(PointD p, RectD rect)
    {
        RectD r = rect.Normalized();

        double rx = r.Width / 2.0;
        double ry = r.Height / 2.0;

        if (rx <= double.Epsilon || ry <= double.Epsilon)
        {
            return DistanceToRectOutline(p, r);
        }

        PointD c = r.Center;
        double nx = (p.X - c.X) / rx;
        double ny = (p.Y - c.Y) / ry;
        double norm = Math.Sqrt((nx * nx) + (ny * ny));

        if (norm <= double.Epsilon)
        {
            // Dead centre: the nearest outline point is one semi-axis away.
            return Math.Min(rx, ry);
        }

        // Radial projection onto the ellipse, then measure in real coordinates.
        var onEllipse = new PointD(c.X + (nx / norm * rx), c.Y + (ny / norm * ry));
        return p.DistanceTo(onEllipse);
    }

    /// <summary>
    /// Rotates <paramref name="p"/> around <paramref name="center"/>.
    /// </summary>
    public static PointD Rotate(PointD p, PointD center, double degrees)
    {
        if (Math.Abs(degrees) < 1e-9)
        {
            return p;
        }

        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        double dx = p.X - center.X;
        double dy = p.Y - center.Y;

        return new PointD(
            center.X + (dx * cos) - (dy * sin),
            center.Y + (dx * sin) + (dy * cos));
    }

    /// <summary>
    /// Axis-aligned bounds enclosing <paramref name="points"/>.
    /// </summary>
    public static RectD BoundsOf(IReadOnlyList<PointD> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return RectD.Empty;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (PointD p in points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Reduces a dense freehand stroke while keeping its shape.
    /// </summary>
    /// <remarks>
    /// A pen stroke sampled from mouse moves can reach several thousand points,
    /// which bloats the saved layer file and slows re-rendering in the gallery.
    /// Ramer-Douglas-Peucker removes the points that carry no shape information.
    /// </remarks>
    public static List<PointD> Simplify(IReadOnlyList<PointD> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count < 3 || tolerance <= 0)
        {
            return [.. points];
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        SimplifySegment(points, 0, points.Count - 1, tolerance, keep);

        var result = new List<PointD>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    private static void SimplifySegment(
        IReadOnlyList<PointD> points, int first, int last, double tolerance, bool[] keep)
    {
        if (last <= first + 1)
        {
            return;
        }

        double worst = -1;
        int worstIndex = -1;

        for (int i = first + 1; i < last; i++)
        {
            double d = DistanceToSegment(points[i], points[first], points[last]);
            if (d > worst)
            {
                worst = d;
                worstIndex = i;
            }
        }

        if (worst <= tolerance || worstIndex < 0)
        {
            return;
        }

        keep[worstIndex] = true;
        SimplifySegment(points, first, worstIndex, tolerance, keep);
        SimplifySegment(points, worstIndex, last, tolerance, keep);
    }
}
