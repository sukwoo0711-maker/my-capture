using MyCapture.Core.Primitives;

namespace MyCapture.Core.Capture;

/// <summary>A clipped, non-zero-winding selection in physical bitmap pixels.</summary>
public sealed class FreeformMask
{
    // Integrate horizontal coverage exactly and sample vertically. Sixteen subrows give smooth
    // diagonals without allocating a supersized bitmap or revisiting every interior pixel 16 times.
    private const int Subrows = 16;

    private FreeformMask(RectD bounds, byte[] coverage)
    {
        Bounds = bounds;
        Width = (int)bounds.Width;
        Height = (int)bounds.Height;
        Coverage = coverage;
    }

    public RectD Bounds { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, straight alpha coverage, from 0 (outside) to 255 (inside).</summary>
    public byte[] Coverage { get; }

    /// <summary>
    /// Closes the stroke, clips its edges to the desktop and creates its mask. A clipped bounding
    /// box smaller than 8 by 8 physical pixels, or an empty filled area, is not a selection.
    /// Crossing and repeated edges use the non-zero winding rule, not even/odd or signed area.
    /// This runs only on confirmation; pointer movement only needs to append to the stroke.
    /// </summary>
    public static FreeformMask? Create(IReadOnlyList<PointD> points, RectD clipBounds)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3 || !Finite(clipBounds) || clipBounds.IsEmpty) return null;
        var polygon = new List<PointD>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            PointD point = points[i];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
            polygon.Add(point);
        }

        RectD clip = clipBounds.Normalized();
        // Intersect edges, rather than clamp vertices: clamping a segment's endpoints would change
        // the diagonal and accidentally select pixels outside the original outline.
        polygon = Clip(polygon, axisX: true, clip.Left, keepGreater: true);
        polygon = Clip(polygon, axisX: true, clip.Right, keepGreater: false);
        polygon = Clip(polygon, axisX: false, clip.Top, keepGreater: true);
        polygon = Clip(polygon, axisX: false, clip.Bottom, keepGreater: false);
        if (polygon.Count < 3) return null;

        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (PointD point in polygon)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        if (right - left < 8 || bottom - top < 8) return null;
        RectD bounds = new RectD(left, top, right - left, bottom - top).ToPixelBounds();
        if (bounds.Width > int.MaxValue || bounds.Height > int.MaxValue) return null;
        int width = (int)bounds.Width, height = (int)bounds.Height;
        var coverage = new byte[checked(width * height)];
        var crossings = new Crossing[polygon.Count];
        var whole = new int[width + 1];
        var fractional = new double[width];
        bool hasCoverage = false;

        for (int row = 0; row < height; row++)
        {
            Array.Clear(whole);
            Array.Clear(fractional);
            for (int sample = 0; sample < Subrows; sample++)
            {
                double y = bounds.Top + row + ((sample + 0.5) / Subrows);
                int count = 0;
                PointD a = polygon[^1];
                foreach (PointD b in polygon)
                {
                    // Half-open vertical intervals count shared vertices once. Horizontal and
                    // zero-length edges never change winding.
                    if ((a.Y <= y && y < b.Y) || (b.Y <= y && y < a.Y))
                    {
                        double x = a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                        crossings[count++] = new Crossing(x - bounds.Left, b.Y > a.Y ? 1 : -1);
                    }
                    a = b;
                }
                Array.Sort(crossings, 0, count, CrossingComparer.Instance);
                int winding = 0;
                double start = 0;
                for (int i = 0; i < count; i++)
                {
                    Crossing crossing = crossings[i];
                    int previous = winding;
                    winding += crossing.Winding;
                    if (previous == 0 && winding != 0) start = crossing.X;
                    else if (previous != 0 && winding == 0)
                        AddSpan(start, crossing.X, width, whole, fractional);
                }
            }

            int full = 0;
            for (int column = 0; column < width; column++)
            {
                full += whole[column];
                byte alpha = (byte)Math.Clamp((int)Math.Round(
                    (full + fractional[column]) * 255 / Subrows), 0, 255);
                coverage[(row * width) + column] = alpha;
                hasCoverage |= alpha != 0;
            }
        }
        return hasCoverage ? new FreeformMask(bounds, coverage) : null;
    }

    /// <summary>
    /// Applies coverage to a cropped straight-BGRA buffer. Interior pixels stay byte-identical;
    /// edge colours remain straight (not darkened). Pixels outside are cleared, so the saved file
    /// cannot reveal unselected desktop content by removing its alpha channel.
    /// </summary>
    public void ApplyToBgra32(Span<byte> pixels, int stride)
    {
        if (stride < checked(Width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        if (pixels.Length < checked(((Height - 1) * stride) + (Width * 4)))
            throw new ArgumentException("The pixel buffer is smaller than the mask.", nameof(pixels));
        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                int pixel = (row * stride) + (column * 4);
                byte alpha = Coverage[(row * Width) + column];
                if (alpha == 0)
                {
                    pixels.Slice(pixel, 4).Clear();
                }
                else if (alpha != 255)
                {
                    pixels[pixel + 3] = (byte)(((pixels[pixel + 3] * alpha) + 127) / 255);
                }
            }
        }
    }

    private static void AddSpan(double left, double right, int width, int[] whole, double[] fractional)
    {
        left = Math.Clamp(left, 0, width);
        right = Math.Clamp(right, 0, width);
        if (right <= left) return;
        int first = (int)Math.Floor(left), last = (int)Math.Ceiling(right) - 1;
        if (first == last)
        {
            fractional[first] += right - left;
            return;
        }
        fractional[first] += first + 1 - left;
        fractional[last] += right - last;
        whole[first + 1]++;
        whole[last]--;
    }

    private static List<PointD> Clip(List<PointD> input, bool axisX, double edge, bool keepGreater)
    {
        var output = new List<PointD>(input.Count);
        if (input.Count == 0) return output;
        PointD previous = input[^1];
        double previousValue = axisX ? previous.X : previous.Y;
        bool previousInside = keepGreater ? previousValue >= edge : previousValue <= edge;
        foreach (PointD current in input)
        {
            double value = axisX ? current.X : current.Y;
            bool inside = keepGreater ? value >= edge : value <= edge;
            if (inside != previousInside)
            {
                double t = (edge - previousValue) / (value - previousValue);
                output.Add(axisX
                    ? new PointD(edge, previous.Y + (t * (current.Y - previous.Y)))
                    : new PointD(previous.X + (t * (current.X - previous.X)), edge));
            }
            if (inside) output.Add(current);
            previous = current;
            previousValue = value;
            previousInside = inside;
        }
        return output;
    }

    private static bool Finite(RectD rectangle) =>
        double.IsFinite(rectangle.Left) && double.IsFinite(rectangle.Top)
        && double.IsFinite(rectangle.Right) && double.IsFinite(rectangle.Bottom);

    private readonly record struct Crossing(double X, int Winding);

    private sealed class CrossingComparer : IComparer<Crossing>
    {
        internal static readonly CrossingComparer Instance = new();
        public int Compare(Crossing x, Crossing y) => x.X.CompareTo(y.X);
    }
}
