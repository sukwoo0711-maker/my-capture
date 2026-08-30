using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// An image pasted onto the capture.
/// </summary>
/// <remarks>
/// <para>
/// Neither Snipaste nor AlCapture offers this. It exists because the common real
/// task — dropping a logo, a signature, or a second screenshot onto the first —
/// otherwise forces the user out to an image editor.
/// </para>
/// <para>
/// The pixels are stored as a sidecar file inside the capture's own directory and
/// referenced by <see cref="AssetFileName"/>, not embedded as base64 in the layer
/// file. Embedding would inflate the layer JSON by roughly 33% of the image size and
/// make the file unreadable during support; a sidecar keeps the layer file diffable
/// and lets the queue account for the bytes accurately.
/// </para>
/// </remarks>
public sealed class ImageAnnotation : AnnotationItem
{
    private RectD _rect;
    private double _rotationDegrees;

    /// <summary>
    /// File name, relative to the capture's own directory. No path separators.
    /// </summary>
    /// <remarks>
    /// Relative on purpose: the capture directory can be relocated by the user, and
    /// an absolute path would break every pasted image the moment they do.
    /// </remarks>
    public string AssetFileName { get; set; } = string.Empty;

    /// <summary>
    /// Pixel width of the source asset, used to preserve aspect ratio on resize.
    /// </summary>
    public int SourceWidth { get; set; }

    public int SourceHeight { get; set; }

    public RectD Rect
    {
        get => _rect;
        set => SetProperty(ref _rect, value.Normalized());
    }

    public double RotationDegrees
    {
        get => _rotationDegrees;
        set => SetProperty(ref _rotationDegrees, double.IsFinite(value) ? value % 360.0 : 0);
    }

    [JsonIgnore]
    public override string DisplayName => "이미지";

    [JsonIgnore]
    public override RectD Bounds => _rect;

    [JsonIgnore]
    public double SourceAspectRatio =>
        SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 1.0;

    public override void Translate(double dx, double dy) =>
        Rect = new RectD(_rect.X + dx, _rect.Y + dy, _rect.Width, _rect.Height);

    public override void SetBounds(RectD bounds) => Rect = bounds.Normalized();

    public override double DistanceTo(PointD point)
    {
        PointD local = Math.Abs(_rotationDegrees) < 1e-9
            ? point
            : GeometryMath.Rotate(point, _rect.Center, -_rotationDegrees);

        // An image is opaque content, so the whole area is pickable.
        return _rect.Contains(local) ? 0 : GeometryMath.DistanceToRectOutline(local, _rect);
    }

    public override AnnotationItem Clone()
    {
        var clone = new ImageAnnotation
        {
            AssetFileName = AssetFileName,
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            Rect = Rect,
            RotationDegrees = RotationDegrees,
        };

        CopyBaseTo(clone);
        return clone;
    }
}
