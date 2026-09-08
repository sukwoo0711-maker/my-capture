using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

public enum AnnotationStrokeStyle
{
    Solid = 0,
    Dashed = 1,
    Dotted = 2,
    ThickDashed = 3,
}

/// <summary>
/// Common state for annotations defined by a bounding box with a stroke and fill.
/// </summary>
public abstract class ShapeAnnotation : AnnotationItem
{
    private RectD _rect;
    private ColorRgba _stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
    private ColorRgba _fill = ColorRgba.Transparent;
    private double _strokeThickness = 3;
    private AnnotationStrokeStyle _strokeStyle;
    private bool _fillMatchesStroke;

    public RectD Rect
    {
        get => _rect;
        set => SetProperty(ref _rect, value.Normalized());
    }

    public ColorRgba Stroke
    {
        get => _stroke;
        set => SetProperty(ref _stroke, value);
    }

    /// <summary>
    /// Interior fill. Fully transparent by default: an opaque box would hide the
    /// screenshot content the annotation is pointing at.
    /// </summary>
    public ColorRgba Fill
    {
        get => _fill;
        set => SetProperty(ref _fill, value);
    }

    public double StrokeThickness
    {
        get => _strokeThickness;
        set => SetProperty(ref _strokeThickness, Math.Clamp(value, 0, 256));
    }

    public AnnotationStrokeStyle StrokeStyle
    {
        get => _strokeStyle;
        set => SetProperty(ref _strokeStyle, Enum.IsDefined(value) ? value : AnnotationStrokeStyle.Solid);
    }

    /// <summary>
    /// New user-created fills follow the outline color. Legacy and privacy-redaction fills
    /// remain independent unless the user explicitly changes their transparency.
    /// </summary>
    public bool FillMatchesStroke
    {
        get => _fillMatchesStroke;
        set => SetProperty(ref _fillMatchesStroke, value);
    }

    [JsonIgnore]
    public double FillTransparency => Math.Round(100 - Fill.A * 100.0 / 255);

    [JsonIgnore]
    public double EffectiveStrokeThickness => StrokeStyle == AnnotationStrokeStyle.ThickDashed
        ? Math.Max(6, StrokeThickness) : StrokeThickness;

    public static ColorRgba FillForTransparency(ColorRgba stroke, double transparency)
    {
        double safe = double.IsFinite(transparency) ? Math.Clamp(transparency, 0, 100) : 100;
        return stroke.WithAlpha((byte)Math.Round(255 * (100 - safe) / 100, MidpointRounding.AwayFromZero));
    }

    [JsonIgnore]
    public override RectD Bounds => _rect;

    [JsonIgnore]
    public bool HasFill => Fill.A > 0;

    public override void Translate(double dx, double dy) =>
        Rect = new RectD(_rect.X + dx, _rect.Y + dy, _rect.Width, _rect.Height);

    public override void SetBounds(RectD bounds) => Rect = bounds.Normalized();

    protected void CopyShapeTo(ShapeAnnotation target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CopyBaseTo(target);
        target.Rect = Rect;
        target.Stroke = Stroke;
        target.Fill = Fill;
        target.StrokeThickness = StrokeThickness;
        target.StrokeStyle = StrokeStyle;
        target.FillMatchesStroke = FillMatchesStroke;
    }

    /// <summary>
    /// Half the stroke width, floored so a hairline stroke still has a grabbable
    /// band.
    /// </summary>
    /// <remarks>
    /// Without the floor, a 1px stroke would require pixel-perfect aiming to
    /// select, which reads as the annotation being unselectable.
    /// </remarks>
    protected double PickBand => Math.Max(EffectiveStrokeThickness / 2.0, 3.0);
}

/// <summary>
/// A rectangle, optionally with rounded corners.
/// </summary>
public sealed class RectangleAnnotation : ShapeAnnotation
{
    private double _cornerRadius;

    public double CornerRadius
    {
        get => _cornerRadius;
        set => SetProperty(ref _cornerRadius, Math.Max(0, value));
    }

    [JsonIgnore]
    public override string DisplayName => UiText.Get("Text_DC0760235344");

    public override double DistanceTo(PointD point)
    {
        // A filled shape is pickable anywhere inside it; an outline-only shape is
        // pickable only near the outline, so that a rectangle drawn around content
        // does not swallow clicks meant for annotations underneath it.
        if (HasFill && Rect.Contains(point))
        {
            return 0;
        }

        double d = GeometryMath.DistanceToRectOutline(point, Rect);
        return Math.Max(0, d - PickBand);
    }

    public override AnnotationItem Clone()
    {
        var clone = new RectangleAnnotation { CornerRadius = CornerRadius };
        CopyShapeTo(clone);
        return clone;
    }
}

/// <summary>
/// An ellipse inscribed in its bounding box.
/// </summary>
public sealed class EllipseAnnotation : ShapeAnnotation
{
    [JsonIgnore]
    public override string DisplayName => UiText.Get("Text_59ADEC0FB6E4");

    public override double DistanceTo(PointD point)
    {
        if (HasFill)
        {
            RectD r = Rect;
            double rx = r.Width / 2.0;
            double ry = r.Height / 2.0;

            if (rx > double.Epsilon && ry > double.Epsilon)
            {
                PointD c = r.Center;
                double nx = (point.X - c.X) / rx;
                double ny = (point.Y - c.Y) / ry;
                if ((nx * nx) + (ny * ny) <= 1.0)
                {
                    return 0;
                }
            }
        }

        double d = GeometryMath.DistanceToEllipseOutline(point, Rect);
        return Math.Max(0, d - PickBand);
    }

    public override AnnotationItem Clone()
    {
        var clone = new EllipseAnnotation();
        CopyShapeTo(clone);
        return clone;
    }
}
