using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// Base type for everything the user can draw on a capture.
/// </summary>
/// <remarks>
/// <para>
/// This type existing at all is the product's main differentiator. Snipaste's own
/// documentation states that re-editing a finished shape is not implemented and
/// that the workaround is to undo repeatedly until the shape disappears and then
/// redo. That limitation follows from annotations being rasterised into the bitmap
/// as they are drawn. Here every annotation stays a live object with identity,
/// geometry and style, so it can be selected, moved, restyled or deleted at any
/// time — including days later, after the capture has been reloaded from the queue.
/// </para>
/// <para>
/// The type discriminator strings are part of the persisted file format. They must
/// never be renamed; adding new ones is safe.
/// </para>
/// <para>
/// Coordinates are in the captured image's pixel space. See <see cref="PointD"/>.
/// </para>
/// </remarks>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RectangleAnnotation), "rect")]
[JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
[JsonDerivedType(typeof(PolylineAnnotation), "polyline")]
[JsonDerivedType(typeof(PenAnnotation), "pen")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(ImageAnnotation), "image")]
public abstract class AnnotationItem : ObservableObject
{
    private double _opacity = 1.0;

    /// <summary>
    /// Stable identity, used by undo/redo and selection to refer to this item
    /// across removal and reinsertion.
    /// </summary>
    /// <remarks>
    /// Undo of a delete must restore the same item, not an equal-looking copy, or a
    /// subsequent redo of a style change would target a stale object.
    /// </remarks>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Paint order. Larger values paint on top.
    /// </summary>
    public int ZIndex { get; set; }

    /// <summary>
    /// Per-item opacity (0..1), multiplied with any colour alpha.
    /// </summary>
    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, Math.Clamp(value, 0.0, 1.0));
    }

    /// <summary>
    /// Human-readable name shown in the layer list.
    /// </summary>
    [JsonIgnore]
    public abstract string DisplayName { get; }

    /// <summary>
    /// Axis-aligned bounds enclosing the drawn result, excluding stroke width.
    /// </summary>
    [JsonIgnore]
    public abstract RectD Bounds { get; }

    /// <summary>
    /// Whether this item can be resized by dragging its selection handles.
    /// </summary>
    /// <remarks>
    /// Freehand pen strokes report <see langword="false"/>: scaling a dense stroke
    /// distorts line weight in a way users read as corruption rather than as a
    /// transform.
    /// </remarks>
    [JsonIgnore]
    public virtual bool SupportsResize => true;

    /// <summary>
    /// Moves the item by the given offset, in image pixels.
    /// </summary>
    public abstract void Translate(double dx, double dy);

    /// <summary>
    /// Replaces the item's bounds, scaling its geometry to fit.
    /// </summary>
    /// <remarks>
    /// Implementations must tolerate a degenerate <paramref name="bounds"/>: the
    /// user can drag a resize handle past the opposite edge, and collapsing to zero
    /// must not produce NaN geometry that then fails to serialise.
    /// </remarks>
    public abstract void SetBounds(RectD bounds);

    /// <summary>
    /// Distance from <paramref name="point"/> to the item, in image pixels.
    /// </summary>
    /// <remarks>
    /// Returns 0 for a point inside a filled region. The caller compares against a
    /// tolerance derived from the current zoom so picking stays predictable when the
    /// user is zoomed out.
    /// </remarks>
    public abstract double DistanceTo(PointD point);

    /// <summary>
    /// Deep copy with a new <see cref="Id"/>. Used by duplicate and by undo
    /// snapshots of style changes.
    /// </summary>
    public abstract AnnotationItem Clone();

    protected void CopyBaseTo(AnnotationItem target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ZIndex = ZIndex;
        target.Opacity = Opacity;
    }

    /// <summary>
    /// Scales a value from one range to another, tolerating a zero-length source.
    /// </summary>
    protected static double MapRange(double value, double fromStart, double fromLength, double toStart, double toLength)
    {
        if (Math.Abs(fromLength) <= double.Epsilon)
        {
            // Source collapsed: pin to the destination start rather than dividing by
            // zero and poisoning the geometry with NaN.
            return toStart;
        }

        return toStart + ((value - fromStart) / fromLength * toLength);
    }
}
