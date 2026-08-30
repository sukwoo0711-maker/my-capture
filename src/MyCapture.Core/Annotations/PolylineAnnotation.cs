using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// A straight line, an arrow, or a multi-segment strip.
/// </summary>
/// <remarks>
/// <para>
/// One type covers all three because they differ only in point count and which ends
/// carry an arrowhead. Snipaste exposes the same behaviour as a line/arrow toggle
/// plus click-to-extend for strips; modelling them separately would triple the
/// selection, resize and serialisation code for no user-visible benefit.
/// </para>
/// <para>
/// Arrowheads are described by <see cref="HeadAtStart"/> and <see cref="HeadAtEnd"/>
/// rather than by a single "is arrow" flag so a double-headed arrow — which is what
/// people reach for to annotate a measurement — needs no extra type.
/// </para>
/// </remarks>
public sealed class PolylineAnnotation : AnnotationItem
{
    private List<PointD> _points = [];
    private ColorRgba _stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
    private double _strokeThickness = 3;
    private bool _headAtStart;
    private bool _headAtEnd = true;

    /// <summary>
    /// Vertices in order. Two points describe a straight line.
    /// </summary>
    public List<PointD> Points
    {
        get => _points;
        set
        {
            _points = value ?? [];
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(Bounds));
        }
    }

    public ColorRgba Stroke
    {
        get => _stroke;
        set => SetProperty(ref _stroke, value);
    }

    public double StrokeThickness
    {
        get => _strokeThickness;
        set => SetProperty(ref _strokeThickness, Math.Clamp(value, 0.5, 256));
    }

    public bool HeadAtStart
    {
        get => _headAtStart;
        set => SetProperty(ref _headAtStart, value);
    }

    public bool HeadAtEnd
    {
        get => _headAtEnd;
        set => SetProperty(ref _headAtEnd, value);
    }

    /// <summary>
    /// Arrowhead length as a multiple of <see cref="StrokeThickness"/>.
    /// </summary>
    /// <remarks>
    /// Tying the head to the stroke width rather than to an absolute size keeps the
    /// arrow legible when the user thickens the stroke, which is the normal reaction
    /// to an arrow being hard to see.
    /// </remarks>
    public double HeadSizeFactor { get; set; } = 4.0;

    [JsonIgnore]
    public override string DisplayName =>
        (HeadAtStart, HeadAtEnd) switch
        {
            (false, false) => "선",
            (true, true) => "양방향 화살표",
            _ => "화살표",
        };

    [JsonIgnore]
    public override RectD Bounds => GeometryMath.BoundsOf(_points);

    [JsonIgnore]
    public bool IsArrow => HeadAtStart || HeadAtEnd;

    public override void Translate(double dx, double dy)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            _points[i] = _points[i].Offset(dx, dy);
        }

        RaisePropertyChanged(nameof(Points));
        RaisePropertyChanged(nameof(Bounds));
    }

    public override void SetBounds(RectD bounds)
    {
        RectD from = Bounds;
        RectD to = bounds.Normalized();

        for (int i = 0; i < _points.Count; i++)
        {
            PointD p = _points[i];
            _points[i] = new PointD(
                MapRange(p.X, from.Left, from.Width, to.Left, to.Width),
                MapRange(p.Y, from.Top, from.Height, to.Top, to.Height));
        }

        RaisePropertyChanged(nameof(Points));
        RaisePropertyChanged(nameof(Bounds));
    }

    public override double DistanceTo(PointD point)
    {
        double d = GeometryMath.DistanceToPolyline(point, _points);
        double band = Math.Max(StrokeThickness / 2.0, 4.0);
        return Math.Max(0, d - band);
    }

    public override AnnotationItem Clone()
    {
        var clone = new PolylineAnnotation
        {
            Points = [.. _points],
            Stroke = Stroke,
            StrokeThickness = StrokeThickness,
            HeadAtStart = HeadAtStart,
            HeadAtEnd = HeadAtEnd,
            HeadSizeFactor = HeadSizeFactor,
        };

        CopyBaseTo(clone);
        return clone;
    }

    /// <summary>
    /// Creates a two-point arrow.
    /// </summary>
    public static PolylineAnnotation CreateArrow(PointD from, PointD to, ColorRgba stroke, double thickness) =>
        new()
        {
            Points = [from, to],
            Stroke = stroke,
            StrokeThickness = thickness,
            HeadAtStart = false,
            HeadAtEnd = true,
        };

    /// <summary>
    /// Creates a two-point plain line.
    /// </summary>
    public static PolylineAnnotation CreateLine(PointD from, PointD to, ColorRgba stroke, double thickness) =>
        new()
        {
            Points = [from, to],
            Stroke = stroke,
            StrokeThickness = thickness,
            HeadAtStart = false,
            HeadAtEnd = false,
        };
}
