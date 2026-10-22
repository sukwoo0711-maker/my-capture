using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// A freehand stroke.
/// </summary>
/// <remarks>
/// Distinct from <see cref="PolylineAnnotation"/> despite both being point lists.
/// A pen stroke is dense and sampled from pointer input, so it needs simplification
/// on commit and it is rendered smoothed rather than as hard segments. It also opts
/// out of resize: scaling a dense stroke changes the apparent line weight against
/// its own curvature, which users read as the annotation having been damaged.
/// </remarks>
public sealed class PenAnnotation : AnnotationItem
{
    private PenPointCollection _points = [];
    private ColorRgba _stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
    private double _strokeThickness = 3;

    public PenAnnotation() => _points.Changed += OnPointsChanged;

    [JsonIgnore]
    public long PointsVersion { get; private set; }

    public PenPointCollection Points
    {
        get => _points;
        set
        {
            _points.Changed -= OnPointsChanged;
            _points = value ?? [];
            _points.Changed += OnPointsChanged;
            OnPointsChanged();
        }
    }

    private void OnPointsChanged()
    {
        PointsVersion++;
        RaisePropertyChanged(nameof(Points));
        RaisePropertyChanged(nameof(Bounds));
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

    /// <summary>
    /// Draw as a translucent highlighter rather than an opaque pen.
    /// </summary>
    /// <remarks>
    /// A flag instead of a separate type: the geometry, editing gesture and
    /// serialisation are identical, and only the compositing differs.
    /// </remarks>
    public bool IsHighlighter { get; set; }

    [JsonIgnore]
    public override string DisplayName => IsHighlighter ? UiText.Get("Text_425C77DFA926") : UiText.Get("Text_E414AD855B8D");

    [JsonIgnore]
    public override RectD Bounds => GeometryMath.BoundsOfSpan(_points.AsSpan());

    [JsonIgnore]
    public override bool SupportsResize => false;

    public override void Translate(double dx, double dy)
    {
        _points.Transform(p => p.Offset(dx, dy));
    }

    public override void SetBounds(RectD bounds)
    {
        // Honoured even though SupportsResize is false, so that a group transform
        // (for example scaling every annotation when a capture is resized) still
        // moves pen strokes correctly instead of leaving them behind.
        RectD from = Bounds;
        RectD to = bounds.Normalized();

        _points.Transform(p => new PointD(
                MapRange(p.X, from.Left, from.Width, to.Left, to.Width),
                MapRange(p.Y, from.Top, from.Height, to.Top, to.Height)));
    }

    public override double DistanceTo(PointD point)
    {
        double d = GeometryMath.DistanceToPolylineSpan(point, _points.AsSpan());
        double band = Math.Max(StrokeThickness / 2.0, 4.0);
        return Math.Max(0, d - band);
    }

    /// <summary>
    /// Drops points that carry no shape information.
    /// </summary>
    /// <remarks>
    /// Called once when the stroke is committed. The tolerance is derived from the
    /// stroke width because deviations far below the line weight are invisible: a
    /// 12px brush cannot express a 1px wiggle.
    /// </remarks>
    public void SimplifyInPlace()
    {
        if (_points.Count < 3)
        {
            return;
        }

        double tolerance = Math.Max(0.6, StrokeThickness * 0.2);
        List<PointD> simplified = GeometryMath.Simplify(_points, tolerance);

        if (simplified.Count < _points.Count)
        {
            Points = simplified;
        }
    }

    public override AnnotationItem Clone()
    {
        var clone = new PenAnnotation
        {
            Points = [.. _points],
            Stroke = Stroke,
            StrokeThickness = StrokeThickness,
            IsHighlighter = IsHighlighter,
        };

        CopyBaseTo(clone);
        return clone;
    }
}
