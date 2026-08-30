using System.Globalization;
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
/// Stateless except for the image store it reads inserted bitmaps from.
/// </para>
/// </remarks>
internal sealed class AnnotationRenderer
{
    private readonly AnnotationImageStore _images;

    internal AnnotationRenderer(AnnotationImageStore images)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
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
    internal void Render(
        DrawingContext dc,
        AnnotationDocument document,
        double pixelsPerDip,
        AnnotationItem? suppress = null)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(document);

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

    private static Pen StrokePen(ColorRgba color, double thickness)
    {
        var pen = new Pen(color.ToBrush(), Math.Max(0.01, thickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawRectangle(DrawingContext dc, RectangleAnnotation rect)
    {
        Brush? fill = rect.Fill.ToBrushOrNull();
        Pen? pen = rect.StrokeThickness > 0 ? StrokePen(rect.Stroke, rect.StrokeThickness) : null;
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

    private static void DrawEllipse(DrawingContext dc, EllipseAnnotation ellipse)
    {
        Brush? fill = ellipse.Fill.ToBrushOrNull();
        Pen? pen = ellipse.StrokeThickness > 0 ? StrokePen(ellipse.Stroke, ellipse.StrokeThickness) : null;
        Rect b = ToRect(ellipse.Rect);
        dc.DrawEllipse(fill, pen, new Point(b.Left + (b.Width / 2), b.Top + (b.Height / 2)), b.Width / 2, b.Height / 2);
    }

    private static void DrawPolyline(DrawingContext dc, PolylineAnnotation polyline)
    {
        IReadOnlyList<PointD> points = polyline.Points;
        if (points.Count < 2)
        {
            return;
        }

        Pen pen = StrokePen(polyline.Stroke, polyline.StrokeThickness);
        for (int i = 0; i < points.Count - 1; i++)
        {
            dc.DrawLine(pen, ToPoint(points[i]), ToPoint(points[i + 1]));
        }

        if (polyline.HeadAtEnd)
        {
            DrawArrowHead(dc, polyline, points[^2], points[^1]);
        }

        if (polyline.HeadAtStart)
        {
            DrawArrowHead(dc, polyline, points[1], points[0]);
        }
    }

    private static void DrawArrowHead(DrawingContext dc, PolylineAnnotation polyline, PointD from, PointD to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= double.Epsilon)
        {
            return;
        }

        double ux = dx / length;
        double uy = dy / length;
        double headLength = polyline.StrokeThickness * polyline.HeadSizeFactor;
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

        dc.DrawGeometry(polyline.Stroke.ToBrush(), null, geometry);
    }

    private static void DrawPen(DrawingContext dc, PenAnnotation pen)
    {
        IReadOnlyList<PointD> points = pen.Points;
        if (points.Count == 0)
        {
            return;
        }

        ColorRgba color = pen.IsHighlighter ? pen.Stroke.WithAlpha((byte)Math.Min((int)pen.Stroke.A, 110)) : pen.Stroke;

        if (points.Count == 1)
        {
            double r = Math.Max(0.5, pen.StrokeThickness / 2);
            dc.DrawEllipse(color.ToBrush(), null, ToPoint(points[0]), r, r);
            return;
        }

        Pen strokePen = StrokePen(color, pen.StrokeThickness);
        var figure = new PathFigure { StartPoint = ToPoint(points[0]), IsClosed = false, IsFilled = false };
        for (int i = 1; i < points.Count; i++)
        {
            figure.Segments.Add(new LineSegment(ToPoint(points[i]), isStroked: true));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        dc.DrawGeometry(null, strokePen, geometry);
    }

    private static void DrawText(DrawingContext dc, TextAnnotation text, double pixelsPerDip)
    {
        Rect box = ToRect(text.Rect);

        bool rotated = Math.Abs(text.RotationDegrees) > 1e-6;
        if (rotated)
        {
            dc.PushTransform(new RotateTransform(
                text.RotationDegrees,
                box.Left + (box.Width / 2),
                box.Top + (box.Height / 2)));
        }

        Brush? plate = text.Background.ToBrushOrNull();
        if (plate is not null)
        {
            dc.DrawRectangle(plate, null, box);
        }

        var typeface = new Typeface(
            new FontFamily(text.FontFamily),
            text.Italic ? FontStyles.Italic : FontStyles.Normal,
            text.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text.Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1, text.FontSize),
            text.Foreground.ToBrush(),
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, box.Width),
            MaxTextHeight = Math.Max(1, box.Height),
        };

        var origin = new Point(box.Left, box.Top);
        if (text.HasOutline)
        {
            Geometry geometry = formatted.BuildGeometry(origin);
            ColorRgba outline = Contrasting(text.Foreground);
            var outlinePen = new Pen(outline.ToBrush(), Math.Max(1, text.FontSize / 12))
            {
                LineJoin = PenLineJoin.Round,
            };
            outlinePen.Freeze();
            dc.DrawGeometry(null, outlinePen, geometry);
        }

        dc.DrawText(formatted, origin);

        if (rotated)
        {
            dc.Pop();
        }
    }

    private void DrawImage(DrawingContext dc, ImageAnnotation image)
    {
        BitmapSource? bitmap = _images.Get(image.AssetFileName);
        Rect box = ToRect(image.Rect);

        bool rotated = Math.Abs(image.RotationDegrees) > 1e-6;
        if (rotated)
        {
            dc.PushTransform(new RotateTransform(
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
            var pen = new Pen(ColorRgba.FromRgb(0x88, 0x88, 0x88).ToBrush(), 1);
            pen.Freeze();
            dc.DrawRectangle(null, pen, box);
        }

        if (rotated)
        {
            dc.Pop();
        }
    }

    private static Point ToPoint(PointD p) => new(p.X, p.Y);

    /// <summary>
    /// A near-black or near-white outline that contrasts with the fill colour.
    /// </summary>
    private static ColorRgba Contrasting(ColorRgba c)
    {
        // Rec. 601 luma; the same rule the eye uses to judge whether text needs a dark or
        // light halo against its own colour.
        double luma = (0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B);
        return luma > 140
            ? new ColorRgba(c.A, 0, 0, 0)
            : new ColorRgba(c.A, 255, 255, 255);
    }
}
