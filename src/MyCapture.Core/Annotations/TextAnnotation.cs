using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// A text label.
/// </summary>
/// <remarks>
/// Carries its own <see cref="Rect"/> rather than measuring on demand so that the
/// stored layer file renders identically on a machine that lacks the original font.
/// The box is authoritative; the renderer fits text into it.
/// </remarks>
public sealed class TextAnnotation : AnnotationItem
{
    private RectD _rect;
    private string _text = string.Empty;
    private ColorRgba _foreground = ColorRgba.FromRgb(0xEF, 0x44, 0x44);
    private ColorRgba _background = ColorRgba.Transparent;
    private double _fontSize = 18;
    private string _fontFamily = "Malgun Gothic";
    private bool _bold;
    private bool _italic;
    private double _rotationDegrees;
    private bool _hasOutline = true;

    public RectD Rect
    {
        get => _rect;
        set => SetProperty(ref _rect, value.Normalized());
    }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }

    public ColorRgba Foreground
    {
        get => _foreground;
        set => SetProperty(ref _foreground, value);
    }

    /// <summary>
    /// Optional plate behind the text.
    /// </summary>
    public ColorRgba Background
    {
        get => _background;
        set => SetProperty(ref _background, value);
    }

    /// <summary>
    /// Draw a contrasting outline around the glyphs.
    /// </summary>
    /// <remarks>
    /// On by default. Annotation text lands on arbitrary screenshot content, and a
    /// red caption over a red button is unreadable without either a plate or an
    /// outline. An outline is chosen as the default because it does not hide the
    /// pixels the caption refers to.
    /// </remarks>
    public bool HasOutline
    {
        get => _hasOutline;
        set => SetProperty(ref _hasOutline, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, 4, 800));
    }

    public string FontFamily
    {
        get => _fontFamily;
        set => SetProperty(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Malgun Gothic" : value);
    }

    public bool Bold
    {
        get => _bold;
        set => SetProperty(ref _bold, value);
    }

    public bool Italic
    {
        get => _italic;
        set => SetProperty(ref _italic, value);
    }

    /// <summary>
    /// Rotation about the box centre, in degrees clockwise.
    /// </summary>
    public double RotationDegrees
    {
        get => _rotationDegrees;
        set => SetProperty(ref _rotationDegrees, NormalizeAngle(value));
    }

    [JsonIgnore]
    public override string DisplayName =>
        string.IsNullOrWhiteSpace(Text)
            ? "텍스트"
            : Text.Length <= 12 ? Text : Text[..12] + "…";

    [JsonIgnore]
    public override RectD Bounds => _rect;

    public override void Translate(double dx, double dy) =>
        Rect = new RectD(_rect.X + dx, _rect.Y + dy, _rect.Width, _rect.Height);

    public override void SetBounds(RectD bounds)
    {
        RectD from = _rect;
        RectD to = bounds.Normalized();

        // Scale the font with the box so dragging a corner behaves like resizing
        // text rather than reflowing it, matching how Snipaste's text box behaves.
        if (from.Height > double.Epsilon && to.Height > double.Epsilon)
        {
            FontSize = _fontSize * (to.Height / from.Height);
        }

        Rect = to;
    }

    public override double DistanceTo(PointD point)
    {
        // Rotate the probe into the box's own frame instead of rotating the box.
        PointD local = Math.Abs(_rotationDegrees) < 1e-9
            ? point
            : GeometryMath.Rotate(point, _rect.Center, -_rotationDegrees);

        if (_rect.Contains(local))
        {
            return 0;
        }

        return GeometryMath.DistanceToRectOutline(local, _rect);
    }

    public override AnnotationItem Clone()
    {
        var clone = new TextAnnotation
        {
            Rect = Rect,
            Text = Text,
            Foreground = Foreground,
            Background = Background,
            HasOutline = HasOutline,
            FontSize = FontSize,
            FontFamily = FontFamily,
            Bold = Bold,
            Italic = Italic,
            RotationDegrees = RotationDegrees,
        };

        CopyBaseTo(clone);
        return clone;
    }

    private static double NormalizeAngle(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
        {
            return 0;
        }

        double a = degrees % 360.0;
        return a < 0 ? a + 360.0 : a;
    }
}
