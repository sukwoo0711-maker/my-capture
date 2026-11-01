using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

internal enum PointerAction
{
    None, Create, Move, Resize, Marquee, ToggleSelection, PenDraw, AddStepNumber, Text, Image,
}

/// <summary>The same decision drives hover feedback and the next pointer press.</summary>
internal readonly record struct PointerIntent(
    PointerAction Action,
    AnnotationItem? Target = null,
    ResizeHandle Handle = ResizeHandle.None,
    EditorCursorKind Cursor = EditorCursorKind.Arrow);

internal readonly record struct SelectionHandle(ResizeHandle Handle, PointD Position);

internal static class PointerIntentGeometry
{
    internal static IReadOnlyList<SelectionHandle> SelectionHandles(AnnotationItem item, double dipPerPixel)
    {
        double scale = double.IsFinite(dipPerPixel) && dipPerPixel > 0 ? dipPerPixel : 1;
        RectD bounds = item.Bounds;
        // Leave an eight-DIP move area between the eight-DIP handle hit areas.
        double width = Math.Max(bounds.Width, 24 / scale);
        double height = Math.Max(bounds.Height, 24 / scale);
        RectD display = new(bounds.Center.X - width / 2, bounds.Center.Y - height / 2, width, height);
        var result = new List<SelectionHandle>(8);
        foreach (ResizeHandle handle in Enum.GetValues<ResizeHandle>())
        {
            if (handle == ResizeHandle.None || AnnotationEditorController.UsableHandle(item, handle) == ResizeHandle.None) continue;
            PointD point = HandlePoint(display, handle);
            if (item is TextAnnotation { RotationDegrees: not 0 } text)
                point = GeometryMath.Rotate(point, bounds.Center, text.RotationDegrees);
            result.Add(new SelectionHandle(handle, point));
        }
        return result;
    }

    internal static PointD HandlePoint(RectD bounds, ResizeHandle handle) => new(
        handle is ResizeHandle.TopLeft or ResizeHandle.MiddleLeft or ResizeHandle.BottomLeft ? bounds.Left
            : handle is ResizeHandle.TopRight or ResizeHandle.MiddleRight or ResizeHandle.BottomRight ? bounds.Right : bounds.Center.X,
        handle is ResizeHandle.TopLeft or ResizeHandle.TopCenter or ResizeHandle.TopRight ? bounds.Top
            : handle is ResizeHandle.BottomLeft or ResizeHandle.BottomCenter or ResizeHandle.BottomRight ? bounds.Bottom : bounds.Center.Y);

    internal static ResizeHandle NearestHandle(IEnumerable<SelectionHandle> handles, PointD point, double tolerance)
    {
        ResizeHandle result = ResizeHandle.None;
        double closest = double.PositiveInfinity;
        foreach (SelectionHandle handle in handles)
        {
            double dx = point.X - handle.Position.X, dy = point.Y - handle.Position.Y;
            double distance = dx * dx + dy * dy;
            if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance && distance < closest)
            {
                closest = distance;
                result = handle.Handle;
            }
        }
        return result;
    }

    internal static EditorCursorKind ResizeCursor(AnnotationItem item, ResizeHandle handle)
    {
        if (item is not TextAnnotation { RotationDegrees: not 0 } text)
            return EditorCursorPolicy.Resolve(EditorTool.Select, new EditorPointerTarget(Handle: handle));
        PointD direction = HandlePoint(new RectD(-1, -1, 2, 2), handle);
        double degrees = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI + text.RotationDegrees;
        int quadrant = ((int)Math.Round(degrees / 45) % 4 + 4) % 4;
        return quadrant switch
        {
            0 => EditorCursorKind.ResizeWestEast,
            1 => EditorCursorKind.ResizeNorthwestSoutheast,
            2 => EditorCursorKind.ResizeNorthSouth,
            _ => EditorCursorKind.ResizeNortheastSouthwest,
        };
    }
}
