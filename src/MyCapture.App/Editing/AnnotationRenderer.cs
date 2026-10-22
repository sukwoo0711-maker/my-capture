using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

/// <summary>
/// Draws annotation items into a <see cref="DrawingContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The renderer draws in the selected image's pixel coordinates. The caller pushes a
/// single transform that maps those pixels to device-independent units before calling
/// in, so nothing here needs to know the monitor's DPI: the same code renders correctly
/// at 100% and 150%, and would render identically when flattening the layer later.
/// </para>
/// <para>
/// Holds the image store it reads inserted bitmaps from, on the live editor surface the
/// cache of blurred frames, and the drawing resources it built for earlier paints.
/// </para>
/// <para>
/// The editor repaints every item on every pointer move, so whatever a paint derives from an
/// item (pens, brushes, arrowheads, ink and outline geometry, a caption's laid-out glyphs,
/// rotations) is kept frozen and keyed by every value it was built from. An unchanged item
/// then costs no allocation and hands the composition thread the resources it already has.
/// When any input differs the resource is built exactly as before, so a paint issues the
/// same drawing commands with equal resources, and therefore the same pixels, whether or not
/// it was cached. Items that stop being drawn (deleted, undone, edited live) drop out.
/// </para>
/// <para>
/// Blur layers sample the base capture passed as <c>blurSource</c>. Exports blur exactly the
/// covered pixels synchronously; the live surface clips a background-computed blurred frame
/// instead. Both come from the same integer kernel, so the preview shows the bytes the
/// export writes.
/// </para>
/// </remarks>
internal sealed class AnnotationRenderer
{
    // Distinct pens and brushes in one document; a bound, not a working-set size.
    private const int ResourceCacheLimit = 256;

    // Paints after which an item that was not drawn (deleted, undone, edited live, or left out
    // of a partial render) loses its cached resources.
    private const int UndrawnPassesKept = 32;

    private static readonly Brush BlurCover = CreateBlurCover();
    private static readonly Pen MissingImagePen = CreateMissingImagePen();

    private readonly AnnotationImageStore _images;
    private readonly AnnotationBlurCache? _liveBlur;
    private readonly Dictionary<ColorRgba, SolidColorBrush> _brushes = [];
    private readonly Dictionary<StrokeKey, Pen> _strokePens = [];
    private readonly Dictionary<(ColorRgba Color, double Thickness), Pen> _outlinePens = [];
    private readonly Dictionary<AnnotationItem, ItemCache> _itemCaches = new(ReferenceEqualityComparer.Instance);
    private int _pass;

    /// <param name="images">Resolves inserted image assets.</param>
    /// <param name="liveBlur">
    /// Background blur frames for the interactive surface; <see langword="null"/> for exports,
    /// which blur synchronously.
    /// </param>
    internal AnnotationRenderer(AnnotationImageStore images, AnnotationBlurCache? liveBlur = null)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _liveBlur = liveBlur;
    }

    /// <summary>
    /// Draws every item in paint order.
    /// </summary>
    /// <param name="pixelsPerDip">
    /// Device pixels per DIP for the target surface, used only to render text crisply.
    /// </param>
    /// <param name="suppress">
    /// An item currently being edited by a live TextBox, so it is not double-drawn.
    /// </param>
    /// <param name="blurSource">
    /// The unannotated base capture in the document's pixel space, sampled by blur layers.
    /// Without it a blur layer draws an opaque cover rather than reveal anything.
    /// </param>
    internal void Render(
        DrawingContext dc,
        AnnotationDocument document,
        double pixelsPerDip,
        AnnotationItem? suppress = null,
        BitmapSource? blurSource = null)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(document);

        bool livePass = _liveBlur is not null && blurSource is not null;
        if (livePass)
        {
            _liveBlur!.BeginPass(blurSource!);
        }

        _pass++;
        try
        {
            RenderItems(dc, document, pixelsPerDip, suppress, blurSource);
        }
        finally
        {
            if (livePass)
            {
                _liveBlur!.EndPass();
            }
        }

        DropUndrawnItemCaches();
    }

    private void RenderItems(
        DrawingContext dc,
        AnnotationDocument document,
        double pixelsPerDip,
        AnnotationItem? suppress,
        BitmapSource? blurSource)
    {
        foreach (AnnotationItem item in document.Items)
        {
            if (ReferenceEquals(item, suppress))
            {
                continue;
            }

            double previousOpacity = item.Opacity;
            bool needsLayer = previousOpacity < 1.0;
            if (needsLayer)
            {
                dc.PushOpacity(previousOpacity);
            }

            switch (item)
            {
                case RectangleAnnotation rect:
                    DrawRectangle(dc, rect);
                    break;
                case EllipseAnnotation ellipse:
                    DrawEllipse(dc, ellipse);
                    break;
                case PolylineAnnotation polyline:
                    DrawPolyline(dc, polyline);
                    break;
                case PenAnnotation pen:
                    DrawPen(dc, pen);
                    break;
                case TextAnnotation text:
                    DrawText(dc, text, pixelsPerDip);
                    break;
                case ImageAnnotation image:
                    DrawImage(dc, image);
                    break;
                case BlurAnnotation blur:
                    DrawBlur(dc, blur, blurSource);
                    break;
            }

            if (needsLayer)
            {
                dc.Pop();
            }
        }
    }

    private static Rect ToRect(RectD r)
    {
        RectD n = r.Normalized();
        return new Rect(n.Left, n.Top, n.Width, n.Height);
    }

    /// <summary>A frozen brush for <paramref name="color"/>, shared by every item that uses it.</summary>
    private SolidColorBrush BrushOf(ColorRgba color)
    {
        if (!_brushes.TryGetValue(color, out SolidColorBrush? brush))
        {
            if (_brushes.Count >= ResourceCacheLimit)
            {
                _brushes.Clear();
            }

            brush = color.ToBrush();
            _brushes.Add(color, brush);
        }

        return brush;
    }

    /// <summary>As <see cref="WpfConversions.ToBrushOrNull"/>: no brush for a fully transparent colour.</summary>
    private SolidColorBrush? BrushOrNull(ColorRgba color) => color.A == 0 ? null : BrushOf(color);

    private Pen StrokePen(ColorRgba color, double thickness, AnnotationStrokeStyle style = AnnotationStrokeStyle.Solid)
    {
        var key = new StrokeKey(color, thickness, style);
        if (!_strokePens.TryGetValue(key, out Pen? pen))
        {
            if (_strokePens.Count >= ResourceCacheLimit)
            {
                _strokePens.Clear();
            }

            pen = new Pen(BrushOf(color), Math.Max(0.01, thickness))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
                DashCap = style == AnnotationStrokeStyle.Dotted ? PenLineCap.Round : PenLineCap.Flat,
                DashStyle = style switch
                {
                    AnnotationStrokeStyle.Dashed or AnnotationStrokeStyle.ThickDashed => DashStyles.Dash,
                    AnnotationStrokeStyle.Dotted => DashStyles.Dot,
                    _ => DashStyles.Solid,
                },
            };
            pen.Freeze();
            _strokePens.Add(key, pen);
        }

        return pen;
    }

    private Pen OutlinePen(ColorRgba color, double thickness)
    {
        if (!_outlinePens.TryGetValue((color, thickness), out Pen? pen))
        {
            if (_outlinePens.Count >= ResourceCacheLimit)
            {
                _outlinePens.Clear();
            }

            pen = new Pen(BrushOf(color), thickness)
            {
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            _outlinePens.Add((color, thickness), pen);
        }

        return pen;
    }

    private void DrawRectangle(DrawingContext dc, RectangleAnnotation rect)
    {
        Brush? fill = BrushOrNull(rect.Fill);
        Pen? pen = rect.StrokeThickness > 0 ? StrokePen(rect.Stroke, rect.EffectiveStrokeThickness, rect.StrokeStyle) : null;
        Rect bounds = ToRect(rect.Rect);
        if (rect.CornerRadius > 0)
        {
            dc.DrawRoundedRectangle(fill, pen, bounds, rect.CornerRadius, rect.CornerRadius);
        }
        else
        {
            dc.DrawRectangle(fill, pen, bounds);
        }
    }

    private void DrawEllipse(DrawingContext dc, EllipseAnnotation ellipse)
    {
        Brush? fill = BrushOrNull(ellipse.Fill);
        Pen? pen = ellipse.StrokeThickness > 0 ? StrokePen(ellipse.Stroke, ellipse.EffectiveStrokeThickness, ellipse.StrokeStyle) : null;
        Rect b = ToRect(ellipse.Rect);
        dc.DrawEllipse(fill, pen, new Point(b.Left + (b.Width / 2), b.Top + (b.Height / 2)), b.Width / 2, b.Height / 2);
    }

    private void DrawPolyline(DrawingContext dc, PolylineAnnotation polyline)
    {
        IReadOnlyList<PointD> points = polyline.Points;
        if (points.Count < 2)
        {
            return;
        }

        Pen pen = StrokePen(polyline.Stroke, polyline.StrokeThickness);
        // An arrow's shaft stops where its head begins. Drawn to the tip, the shaft's round cap
        // blunted the point by half the stroke width: 32 px at the 64 px maximum.
        double headLength = polyline.StrokeThickness * polyline.HeadSizeFactor;
        int last = points.Count - 2;
        for (int i = 0; i <= last; i++)
        {
            Point from = ToPoint(points[i]);
            Point to = ToPoint(points[i + 1]);
            Vector along = to - from;
            double length = along.Length;
            double startInset = i == 0 && polyline.HeadAtStart ? headLength : 0;
            double endInset = i == last && polyline.HeadAtEnd ? headLength : 0;
            if (startInset + endInset == 0)
            {
                dc.DrawLine(pen, from, to);
            }
            else if (length > startInset + endInset)
            {
                // A segment shorter than its heads is all head; the head covers it.
                along /= length;
                dc.DrawLine(pen, from + (along * startInset), to - (along * endInset));
            }
        }

        if (!polyline.HeadAtEnd && !polyline.HeadAtStart)
        {
            return;
        }

        ArrowCache heads = CacheFor<ArrowCache>(polyline);
        if (polyline.HeadAtEnd)
        {
            DrawArrowHead(dc, polyline, points[^2], points[^1], ref heads.End);
        }

        if (polyline.HeadAtStart)
        {
            DrawArrowHead(dc, polyline, points[1], points[0], ref heads.Start);
        }
    }

    private void DrawArrowHead(DrawingContext dc, PolylineAnnotation polyline, PointD from, PointD to, ref ArrowHead head)
    {
        var key = new ArrowHeadKey(from, to, polyline.StrokeThickness * polyline.HeadSizeFactor);
        if (!head.Built || head.Key != key)
        {
            head = new ArrowHead { Built = true, Key = key, Geometry = BuildArrowHead(key) };
        }

        if (head.Geometry is { } geometry)
        {
            dc.DrawGeometry(BrushOf(polyline.Stroke), null, geometry);
        }
    }

    /// <summary>The filled head at <c>To</c>, or <see langword="null"/> for a zero-length segment.</summary>
    private static PathGeometry? BuildArrowHead(ArrowHeadKey key)
    {
        PointD from = key.From;
        PointD to = key.To;
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= double.Epsilon)
        {
            return null;
        }

        double ux = dx / length;
        double uy = dy / length;
        double headLength = key.HeadLength;
        double headWidth = headLength * 0.6;

        var baseCenter = new Point(to.X - (ux * headLength), to.Y - (uy * headLength));
        double px = -uy;
        double py = ux;

        var left = new Point(baseCenter.X + (px * headWidth / 2), baseCenter.Y + (py * headWidth / 2));
        var right = new Point(baseCenter.X - (px * headWidth / 2), baseCenter.Y - (py * headWidth / 2));

        var figure = new PathFigure { StartPoint = ToPoint(to), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(left, isStroked: false));
        figure.Segments.Add(new LineSegment(right, isStroked: false));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void DrawPen(DrawingContext dc, PenAnnotation pen)
    {
        PenPointCollection points = pen.Points;
        if (points.Count == 0)
        {
            return;
        }

        ColorRgba color = pen.IsHighlighter ? pen.Stroke.WithAlpha((byte)Math.Min((int)pen.Stroke.A, 110)) : pen.Stroke;

        if (points.Count == 1)
        {
            double r = Math.Max(0.5, pen.StrokeThickness / 2);
            dc.DrawEllipse(BrushOf(color), null, ToPoint(points[0]), r, r);
            return;
        }

        Pen strokePen = StrokePen(color, pen.StrokeThickness);
        dc.DrawGeometry(null, strokePen, InkGeometry(pen, points));
    }

    /// <summary>
    /// The stroke's segments as one frozen geometry, rebuilt only after a point mutation.
    /// </summary>
    /// <remarks>
    /// The collection reports in-place edits as well as replacement, keeping the unchanged
    /// stroke check constant-time even for very long strokes.
    /// </remarks>
    private StreamGeometry InkGeometry(PenAnnotation pen, PenPointCollection points)
    {
        InkCache cache = CacheFor<InkCache>(pen);
        if (cache.Geometry is { } cached
            && cache.PointsVersion == pen.PointsVersion)
        {
            return cached;
        }

        // Ink can contain hundreds of thousands of points. Stream the same stroked
        // segments without allocating a mutable Freezable for every point on every pass.
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            ReadOnlySpan<PointD> span = points.AsSpan();
            context.BeginFigure(ToPoint(span[0]), isFilled: false, isClosed: false);
            for (int i = 1; i < span.Length; i++)
            {
                context.LineTo(ToPoint(span[i]), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        cache.PointsVersion = pen.PointsVersion;
        cache.Geometry = geometry;
        return geometry;
    }

    private void DrawText(DrawingContext dc, TextAnnotation text, double pixelsPerDip)
    {
        Rect box = ToRect(text.Rect);
        TextCache cache = CacheFor<TextCache>(text);

        bool rotated = Math.Abs(text.RotationDegrees) > 1e-6;
        if (rotated)
        {
            dc.PushTransform(cache.Rotation.Get(
                text.RotationDegrees,
                box.Left + (box.Width / 2),
                box.Top + (box.Height / 2)));
        }

        // Never trimmed or cut at the box: see TextAnnotationLayout. Text that fits its box lays
        // out exactly as before, so existing captions keep their pixels.
        TextAnnotationLayout.FormatKey key = TextAnnotationLayout.KeyOf(text, pixelsPerDip);
        if (cache.Formatted is null || !cache.Key.Equals(key))
        {
            cache.Key = key;
            cache.Formatted = TextAnnotationLayout.Format(key);
            cache.Glyphs = null;
            cache.Outline = null;
        }

        FormattedText formatted = cache.Formatted;

        Brush? plate = BrushOrNull(text.Background);
        if (plate is not null)
        {
            dc.DrawRectangle(plate, null, text.IsStepNumber ? box : TextAnnotationLayout.PlateBounds(box, formatted));
        }

        var origin = text.IsStepNumber
            ? new Point(box.Left + Math.Max(0, (box.Width - formatted.Width) / 2), box.Top + Math.Max(0, (box.Height - formatted.Height) / 2))
            : new Point(box.Left, box.Top);
        if (cache.Glyphs is null || !cache.Origin.Equals(origin))
        {
            // The layout bakes its origin into every glyph and outline coordinate, so a moved
            // caption builds both again exactly where the uncached paint would have.
            cache.Origin = origin;
            cache.Glyphs = RecordGlyphs(formatted, origin);
            cache.Outline = null;
        }

        if (text.HasOutline)
        {
            cache.Outline ??= Frozen(formatted.BuildGeometry(origin));
            bool consistent = text.OutlineStyle == TextOutlineStyle.Consistent;
            ColorRgba outline = Contrasting(text.Foreground, consistent);
            double thickness = consistent ? text.FontSize / 18 : Math.Max(1, text.FontSize / 12);
            dc.DrawGeometry(null, OutlinePen(outline, thickness), cache.Outline);
        }

        dc.DrawDrawing(cache.Glyphs);

        if (rotated)
        {
            dc.Pop();
        }
    }

    /// <summary>The glyph runs <c>DrawText(formatted, origin)</c> issues, recorded once and frozen.</summary>
    private static DrawingGroup RecordGlyphs(FormattedText formatted, Point origin)
    {
        var glyphs = new DrawingGroup();
        using (DrawingContext context = glyphs.Open())
        {
            context.DrawText(formatted, origin);
        }

        return Frozen(glyphs);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return freezable;
    }

    private void DrawImage(DrawingContext dc, ImageAnnotation image)
    {
        BitmapSource? bitmap = _images.Get(image.AssetFileName);
        Rect box = ToRect(image.Rect);

        bool rotated = Math.Abs(image.RotationDegrees) > 1e-6;
        if (rotated)
        {
            dc.PushTransform(CacheFor<ImageCache>(image).Rotation.Get(
                image.RotationDegrees,
                box.Left + (box.Width / 2),
                box.Top + (box.Height / 2)));
        }

        if (bitmap is not null)
        {
            dc.DrawImage(bitmap, box);
        }
        else
        {
            // Asset not decodable this session (for example a document reloaded without its
            // sidecar): draw a placeholder outline so the item is still visible and pickable
            // rather than silently vanishing.
            dc.DrawRectangle(null, MissingImagePen, box);
        }

        if (rotated)
        {
            dc.Pop();
        }
    }

    private void DrawBlur(DrawingContext dc, BlurAnnotation blur, BitmapSource? source)
    {
        if (source is null)
        {
            dc.DrawRectangle(BlurCover, null, ToRect(blur.Rect));
            return;
        }

        RectD region = blur.PixelRegionWithin(source.PixelWidth, source.PixelHeight);
        if (region.IsEmpty)
        {
            return;
        }

        var pixels = new Rect(region.X, region.Y, region.Width, region.Height);
        if (_liveBlur is null)
        {
            dc.DrawImage(AnnotationBlurPixels.BlurRegion(source, region, blur.Radius), pixels);
            return;
        }

        BitmapSource? frame = _liveBlur.Frame(blur.Radius);
        if (frame is null)
        {
            dc.DrawRectangle(BlurCover, null, pixels);
            return;
        }

        dc.PushClip(CacheFor<BlurCache>(blur).Clip(pixels));
        dc.DrawImage(frame, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        dc.Pop();
    }

    /// <summary>
    /// The cache entry of <paramref name="item"/>, created on first use and marked as drawn in
    /// this pass.
    /// </summary>
    private T CacheFor<T>(AnnotationItem item)
        where T : ItemCache, new()
    {
        ref ItemCache? slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_itemCaches, item, out _);
        if (slot is not T cache)
        {
            cache = new T();
            slot = cache;
        }

        cache.Pass = _pass;
        return cache;
    }

    /// <summary>
    /// Every <see cref="UndrawnPassesKept"/> paints, forgets the items none of them drew, so a
    /// deleted or undone item does not keep its geometry alive, while an item a partial render
    /// (a subset document) left out keeps it.
    /// </summary>
    private void DropUndrawnItemCaches()
    {
        if (_pass % UndrawnPassesKept != 0)
        {
            return;
        }

        foreach ((AnnotationItem item, ItemCache cache) in _itemCaches)
        {
            if (_pass - cache.Pass >= UndrawnPassesKept)
            {
                _itemCaches.Remove(item);
            }
        }
    }

    /// <summary>
    /// Neutral, opaque fill shown while a blur is unavailable, so sensitive pixels are never
    /// exposed even for one frame.
    /// </summary>
    private static Brush CreateBlurCover()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateMissingImagePen()
    {
        var pen = new Pen(ColorRgba.FromRgb(0x88, 0x88, 0x88).ToBrush(), 1);
        pen.Freeze();
        return pen;
    }

    private static Point ToPoint(PointD p) => new(p.X, p.Y);

    /// <summary>
    /// A near-black or near-white outline that contrasts with the fill colour.
    /// </summary>
    private static ColorRgba Contrasting(ColorRgba c, bool consistent)
    {
        // Red and other mid-tone captions share the dark edge of light captions. Only
        // very dark text needs a light edge; old layers retain their original threshold.
        double luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
        return luma > (consistent ? 45 : 140)
            ? new ColorRgba(c.A, 0, 0, 0)
            : new ColorRgba(c.A, 255, 255, 255);
    }

    private readonly record struct StrokeKey(ColorRgba Color, double Thickness, AnnotationStrokeStyle Style);

    private readonly record struct ArrowHeadKey(PointD From, PointD To, double HeadLength);

    /// <summary>One arrowhead and the key it was built for; <see cref="Built"/> is false until first use.</summary>
    private struct ArrowHead
    {
        internal bool Built;
        internal ArrowHeadKey Key;
        internal PathGeometry? Geometry;
    }

    /// <summary>A frozen rotation about a box centre, rebuilt when the angle or centre changes.</summary>
    private struct CachedRotation
    {
        private double _degrees;
        private double _centerX;
        private double _centerY;
        private RotateTransform? _transform;

        internal RotateTransform Get(double degrees, double centerX, double centerY)
        {
            if (_transform is null || !_degrees.Equals(degrees) || !_centerX.Equals(centerX) || !_centerY.Equals(centerY))
            {
                var transform = new RotateTransform(degrees, centerX, centerY);
                transform.Freeze();
                (_degrees, _centerX, _centerY, _transform) = (degrees, centerX, centerY, transform);
            }

            return _transform;
        }
    }

    /// <summary>What one item's paints share; <see cref="Pass"/> is the last paint that drew it.</summary>
    private abstract class ItemCache
    {
        internal int Pass;
    }

    private sealed class TextCache : ItemCache
    {
        internal CachedRotation Rotation;
        internal TextAnnotationLayout.FormatKey Key;
        internal FormattedText? Formatted;
        internal Point Origin;
        internal DrawingGroup? Glyphs;
        internal Geometry? Outline;
    }

    private sealed class InkCache : ItemCache
    {
        internal long PointsVersion;
        internal StreamGeometry? Geometry;
    }

    private sealed class ArrowCache : ItemCache
    {
        internal ArrowHead Start;
        internal ArrowHead End;
    }

    private sealed class ImageCache : ItemCache
    {
        internal CachedRotation Rotation;
    }

    private sealed class BlurCache : ItemCache
    {
        private Rect _bounds;
        private RectangleGeometry? _clip;

        internal RectangleGeometry Clip(Rect bounds)
        {
            if (_clip is null || !_bounds.Equals(bounds))
            {
                var clip = new RectangleGeometry(bounds);
                clip.Freeze();
                (_bounds, _clip) = (bounds, clip);
            }

            return _clip;
        }
    }
}
