using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// Rotates a whole annotation document and its canvas in 90-degree steps.
/// </summary>
/// <remarks>
/// <para>
/// The editor's canvas is the captured image itself, so a rotation must move both the
/// base image (done by the view swapping its bitmap) and every annotation into the new
/// pixel space. This type only owns the annotation and canvas-dimension part: each item's
/// geometry is remapped clockwise about the canvas centre and the width/height are
/// swapped, so a document rotated here aligns exactly with the same image rotated by a
/// 90-degree bitmap transform.
/// </para>
/// <para>
/// Mapping rule for a clockwise quarter turn on a W×H canvas: (x, y) → (H-1-y, x) in the
/// discrete pixel grid; expressed with continuous coordinates the transform is
/// (x, y) → (H − y, x). Text and image annotations keep their upright glyph orientation
/// (their <c>RotationDegrees</c> is unchanged) so captions stay readable after the turn.
/// An image's bounding box is rotated with the canvas. A text box keeps its own width and
/// height and only its centre turns: its width is where the upright text wraps, so swapping
/// the sides would squeeze the same caption into a narrow, tall column.
/// </para>
/// </remarks>
public static class AnnotationDocumentRotator
{
    /// <summary>Applies <paramref name="quarterTurns"/> clockwise rotations to <paramref name="document"/> in place.</summary>
    public static void Rotate(AnnotationDocument document, int quarterTurns)
    {
        ArgumentNullException.ThrowIfNull(document);
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0)
        {
            return;
        }

        for (int pass = 0; pass < turns; pass++)
        {
            RotateQuarterClockwise(document);
        }
    }

    private static void RotateQuarterClockwise(AnnotationDocument document)
    {
        double oldWidth = document.CanvasWidth;
        double oldHeight = document.CanvasHeight;

        foreach (AnnotationItem item in document.Items)
        {
            switch (item)
            {
                case ShapeAnnotation shape:
                    shape.Rect = MapRect(shape.Rect, oldHeight);
                    break;
                case TextAnnotation text:
                    text.Rect = MapUprightRect(text.Rect, oldHeight);
                    break;
                case ImageAnnotation image:
                    image.Rect = MapRect(image.Rect, oldHeight);
                    break;
                case BlurAnnotation blur:
                    blur.Rect = MapRect(blur.Rect, oldHeight);
                    break;
                case PolylineAnnotation polyline:
                    polyline.Points = [.. polyline.Points.Select(p => MapPoint(p, oldHeight))];
                    break;
                case PenAnnotation pen:
                    pen.Points = [.. pen.Points.Select(p => MapPoint(p, oldHeight))];
                    break;
            }
        }

        (document.CanvasWidth, document.CanvasHeight) = ((int)oldHeight, (int)oldWidth);
    }

    private static PointD MapPoint(PointD p, double oldHeight) => new(oldHeight - p.Y, p.X);

    private static RectD MapRect(RectD rect, double oldHeight)
    {
        // Rotating a rectangle 90° clockwise maps its top-left corner to the new
        // top-right corner: the new left edge sits at oldHeight − old bottom.
        RectD n = rect.Normalized();
        double newLeft = oldHeight - n.Bottom;
        return new RectD(newLeft, n.Left, n.Height, n.Width).Normalized();
    }

    private static RectD MapUprightRect(RectD rect, double oldHeight)
    {
        // Same centre mapping as every other point; the upright box keeps its size. A square
        // (a step badge) lands exactly where MapRect would put it.
        RectD n = rect.Normalized();
        PointD center = MapPoint(n.Center, oldHeight);
        return new RectD(center.X - (n.Width / 2), center.Y - (n.Height / 2), n.Width, n.Height);
    }
}
