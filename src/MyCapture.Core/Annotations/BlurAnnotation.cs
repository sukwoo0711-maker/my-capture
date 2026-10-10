using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// A rectangle whose capture pixels are shown Gaussian-blurred.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the mosaic tool, which bakes a pixelated patch into an image asset, a blur stays a
/// live effect: only the rectangle and the strength are persisted, and every renderer blurs
/// the base capture underneath at draw time. Moving or resizing the layer therefore blurs
/// whatever now sits under it instead of dragging stale pixels along, and the strength can
/// be changed days later when the capture is re-edited from the library.
/// </para>
/// <para>
/// The blur samples only the base capture, never annotations painted below it, so it hides
/// the same pixels in the editor preview and in every export.
/// </para>
/// </remarks>
public sealed class BlurAnnotation : AnnotationItem
{
    /// <summary>Weakest accepted strength, in image pixels.</summary>
    public const int MinimumRadius = 2;

    /// <summary>Strongest accepted strength, in image pixels.</summary>
    public const int MaximumRadius = 64;

    /// <summary>
    /// Default strength. A Gaussian standard deviation of 16 image pixels smears body text
    /// at 100-200% display scaling into an unreadable band.
    /// </summary>
    public const int DefaultRadius = 16;

    private RectD _rect;
    private int _radius = DefaultRadius;

    public RectD Rect
    {
        get => _rect;
        set => SetProperty(ref _rect, value.Normalized());
    }

    /// <summary>
    /// Gaussian standard deviation in image pixels. Values outside the supported range
    /// (for example from a hand-edited layer file) are clamped rather than rejected.
    /// </summary>
    public int Radius
    {
        get => _radius;
        set => SetProperty(ref _radius, ClampRadius(value));
    }

    [JsonIgnore]
    public override string DisplayName => UiText.Get("EditorBlur_Name");

    [JsonIgnore]
    public override RectD Bounds => _rect;

    /// <summary>Clamps a requested strength to the supported range.</summary>
    public static int ClampRadius(double value) =>
        double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), MinimumRadius, MaximumRadius)
            : DefaultRadius;

    /// <summary>
    /// The whole pixels this blur covers on a <paramref name="canvasWidth"/> by
    /// <paramref name="canvasHeight"/> canvas.
    /// </summary>
    /// <remarks>
    /// Rounded outward so a fractional edge never leaves a partly covered pixel sharp, then
    /// intersected (not shifted) with the canvas. Returns an empty rectangle when the blur
    /// lies entirely outside the canvas.
    /// </remarks>
    public RectD PixelRegionWithin(int canvasWidth, int canvasHeight)
    {
        RectD pixels = _rect.ToPixelBounds();
        double left = Math.Max(0, pixels.Left);
        double top = Math.Max(0, pixels.Top);
        double right = Math.Min(Math.Max(0, canvasWidth), pixels.Right);
        double bottom = Math.Min(Math.Max(0, canvasHeight), pixels.Bottom);
        return right > left && bottom > top
            ? new RectD(left, top, right - left, bottom - top)
            : RectD.Empty;
    }

    public override void Translate(double dx, double dy) =>
        Rect = new RectD(_rect.X + dx, _rect.Y + dy, _rect.Width, _rect.Height);

    public override void SetBounds(RectD bounds) => Rect = bounds.Normalized();

    public override double DistanceTo(PointD point) =>
        // The blurred area reads as solid content, so the whole area is pickable.
        _rect.Contains(point) ? 0 : GeometryMath.DistanceToRectOutline(point, _rect);

    public override AnnotationItem Clone()
    {
        var clone = new BlurAnnotation
        {
            Rect = Rect,
            Radius = Radius,
        };

        CopyBaseTo(clone);
        return clone;
    }
}
