using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPointerIntentModelTests
{
    private static AnnotationEditorController Controller(double scale, out AnnotationDocument doc, out UndoStack undo)
    {
        doc = AnnotationDocument.CreateFor(900, 520);
        undo = new UndoStack(mergeWindow: TimeSpan.Zero);
        return new(doc, undo) { Tool = EditorTool.Select, DipPerPixel = scale, HitTolerance = 6 / scale };
    }

    [Theory]
    [InlineData(.77, 16)] [InlineData(1, 16)] [InlineData(3.52, 16)]
    [InlineData(.77, 120)] [InlineData(1, 120)] [InlineData(3.52, 120)]
    public void BodyAndEveryHandleHaveOneHoverPressDragDecision(double scale, double size)
    {
        var c = Controller(scale, out var doc, out _);
        var item = new RectangleAnnotation { Rect = new(200, 200, size, size), Fill = ColorRgba.Black };
        doc.Add(item); c.SetSelected(item);
        RectD initial = item.Rect;
        PointerIntent body = c.ResolvePointerIntent(item.Rect.Center);
        Assert.Equal(PointerAction.Move, body.Action);
        Assert.Equal(EditorCursorKind.Move, body.Cursor);
        c.PointerDown(item.Rect.Center);
        Assert.Equal(body, c.ActivePointerIntent);
        c.PointerMove(item.Rect.Center.Offset(20 / scale, 10 / scale));
        Assert.Equal(body, c.ResolvePointerIntent(new(1, 1)));
        Assert.Equal(initial.Width, item.Rect.Width);
        Assert.Equal(initial.Height, item.Rect.Height);
        c.CancelGesture();
        Assert.Equal(initial, item.Rect);

        foreach (SelectionHandle handle in c.GetSelectionHandles(scale))
        {
            PointerIntent hover = c.ResolvePointerIntent(handle.Position);
            Assert.Equal(PointerAction.Resize, hover.Action);
            Assert.Equal(handle.Handle, hover.Handle);
            c.PointerDown(handle.Position);
            Assert.Equal(hover, c.ActivePointerIntent);
            c.PointerMove(handle.Position.Offset(3 / scale, 2 / scale));
            Assert.Equal(hover, c.ResolvePointerIntent(new(850, 500)));
            c.CancelGesture();
            Assert.Equal(initial, item.Rect);
        }
    }

    [Theory]
    [InlineData(.77)] [InlineData(1)] [InlineData(3.52)]
    public void PressOffsetPreventsFirstMoveJumpAndOutsideToleranceIsNotResize(double scale)
    {
        var c = Controller(scale, out var doc, out _);
        var item = new RectangleAnnotation { Rect = new(100, 100, 120, 50), Fill = ColorRgba.Black };
        doc.Add(item); c.SetSelected(item);
        var press = new PointD(100 - 7 / scale, 125);
        Assert.Equal(PointerAction.Resize, c.ResolvePointerIntent(press).Action);
        Assert.NotEqual(PointerAction.Resize, c.ResolvePointerIntent(new(100 - 8.5 / scale, 125)).Action);
        c.PointerDown(press);
        c.PointerMove(press.Offset(3 / scale, 0));
        Assert.Equal(100 + 3 / scale, item.Rect.X, 8);
        c.PointerUp(press.Offset(3 / scale, 0));
        Assert.Null(c.ActivePointerIntent);
    }

    [Theory]
    [InlineData(.77)] [InlineData(1)] [InlineData(3.52)]
    public void ArrowSweepCrossesBothAxesWithoutLosingPointIdentityAndUndoRestoresPoints(double scale)
    {
        var c = Controller(scale, out var doc, out var undo);
        var line = PolylineAnnotation.CreateArrow(new(100, 100), new(300, 250), ColorRgba.Black, 2);
        doc.Add(line); c.SetSelected(line);
        PointD[] before = [.. line.Points];
        c.PointerDown(new(300, 250));
        foreach (PointD end in new PointD[] { new(100, 250), new(50, 250), new(50, 100), new(50, 40), new(330, 280) })
        {
            c.PointerMove(end);
            Assert.Equal(before[0], line.Points[0]);
            Assert.Equal(end, line.Points[1]);
        }
        c.PointerUp(new(50, 40));
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(before, line.Points);
        Assert.True(c.PerformRedo());
        Assert.Equal(new PointD(50, 40), line.Points[1]);
    }

    [Theory]
    [InlineData(.77)] [InlineData(1)] [InlineData(3.52)]
    public void ThinVisibleInkWinsOverLaterNearbyLine(double scale)
    {
        var c = Controller(scale, out var doc, out _);
        var first = PolylineAnnotation.CreateLine(new(60, 40.5), new(180, 40.5), ColorRgba.Black, 1);
        var later = PolylineAnnotation.CreateLine(new(60, 45.5), new(180, 45.5), ColorRgba.White, 1);
        doc.Add(first); doc.Add(later);
        Assert.Same(first, c.ResolvePointerIntent(new(120, 40.5)).Target);
        c.PointerDown(new(120, 40.5));
        Assert.Same(first, c.Selected);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ActualLineInkWinsOverRectangleOrEllipsePickBandInEitherOrder(bool ellipse, bool lineOnTop)
    {
        foreach (double scale in new[] { .77, 1, 3.52 })
        {
            var c = Controller(scale, out var doc, out _);
            var line = PolylineAnnotation.CreateLine(new(100, 100), new(200, 100), ColorRgba.Black, 1);
            ShapeAnnotation shape = ellipse ? new EllipseAnnotation() : new RectangleAnnotation();
            shape.Rect = new RectD(100, 103, 100, 40);
            shape.StrokeThickness = 1;
            if (lineOnTop) { doc.Add(shape); doc.Add(line); }
            else { doc.Add(line); doc.Add(shape); }
            Assert.Equal(0, shape.DistanceTo(new PointD(150, 100)));
            Assert.InRange(shape.PaintedDistanceTo(new PointD(150, 100)), 2.49, 2.51);
            Assert.Same(line, c.ResolvePointerIntent(new(150, 100)).Target);
            Assert.Same(shape, doc.HitTest(new(150, 103), 6 / scale));
            c.PointerDown(new(150, 100));
            Assert.Same(line, c.Selected);
        }
    }

    [Fact]
    public void RoundedCornerFillDoesNotSwallowInkOutsideItsPaintedCorner()
    {
        var doc = AnnotationDocument.CreateFor(400, 300);
        var line = PolylineAnnotation.CreateLine(new(90, 102), new(110, 102), ColorRgba.Black, 1);
        var rounded = new RectangleAnnotation
        {
            Rect = new(100, 100, 100, 40), CornerRadius = 20, Fill = ColorRgba.White, StrokeThickness = 1,
        };
        doc.Add(line); doc.Add(rounded);
        Assert.True(rounded.PaintedDistanceTo(new(102, 102)) > 4);
        Assert.Same(line, doc.HitTest(new(102, 102), 6));
        Assert.Same(rounded, doc.HitTest(new(150, 110), 6));
    }

    [Fact]
    public void EccentricEllipseUsesNormalDistanceAtAnOffAxisStroke()
    {
        var ellipse = new EllipseAnnotation { Rect = new(100, 100, 200, 20), StrokeThickness = 1 };
        double angle = Math.PI / 4;
        var onBoundary = new PointD(200 + 100 * Math.Cos(angle), 110 + 10 * Math.Sin(angle));
        double nx = Math.Cos(angle) / 100, ny = Math.Sin(angle) / 10;
        double norm = Math.Sqrt(nx * nx + ny * ny);
        PointD onInk = onBoundary.Offset(.4 * nx / norm, .4 * ny / norm);
        Assert.Equal(0, ellipse.PaintedDistanceTo(onInk));
        PointD outside = onBoundary.Offset(.8 * nx / norm, .8 * ny / norm);
        Assert.Equal(.3, ellipse.PaintedDistanceTo(outside), 8);
    }

    [Theory]
    [InlineData(200, 20, 0, 0, 10)]
    [InlineData(200, 20, 100, 0, 0)]
    [InlineData(200, 20, 103, 0, 3)]
    [InlineData(200, 20, 0, 14, 4)]
    [InlineData(20, 200, 14, 0, 4)]
    [InlineData(20, 20, 0, 0, 10)]
    [InlineData(0, 20, 3, 0, 3)]
    public void ExactEllipseDistanceHandlesAxesCirclesAndCollapsedBounds(double width, double height, double x, double y, double expected)
    {
        Assert.Equal(expected, GeometryMath.DistanceToEllipseOutlineExact(new(x, y),
            new RectD(-width / 2, -height / 2, width, height)), 8);
    }

    [Fact]
    public void TolerancePassStillUsesTheExistingShapeAndPenPickBands()
    {
        var doc = AnnotationDocument.CreateFor(400, 300);
        var shape = new RectangleAnnotation { Rect = new(100, 103, 100, 40), StrokeThickness = 1 };
        doc.Add(shape);
        Assert.Same(shape, doc.HitTest(new(150, 100), 0));
        doc.Items.Clear();
        var pen = new PenAnnotation { Points = [new(100, 100), new(200, 100)], StrokeThickness = 1 };
        doc.Add(pen);
        Assert.Same(pen, doc.HitTest(new(150, 104), 0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArrowHeadInkWinsButTheUnpaintedTipCapDoesNot(bool atStart)
    {
        var doc = AnnotationDocument.CreateFor(400, 300);
        var arrow = PolylineAnnotation.CreateArrow(new(100, 100), new(200, 100), ColorRgba.Black, 10);
        arrow.HeadAtStart = atStart;
        arrow.HeadAtEnd = !atStart;
        PointD wing = atStart ? new(135, 108) : new(165, 108);
        var nearby = PolylineAnnotation.CreateLine(wing.Offset(-5, 3), wing.Offset(5, 3), ColorRgba.White, 1);
        doc.Add(arrow); doc.Add(nearby);
        Assert.Equal(0, arrow.PaintedDistanceTo(wing));
        Assert.Same(arrow, doc.HitTest(wing, 6));
        PointD beyondTip = atStart ? new(97, 100) : new(203, 100);
        var ink = PolylineAnnotation.CreateLine(beyondTip.Offset(0, -5), beyondTip.Offset(0, 5), ColorRgba.White, 1);
        doc.Items.Clear(); doc.Add(ink); doc.Add(arrow);
        Assert.Equal(3, arrow.PaintedDistanceTo(beyondTip), 8);
        Assert.Same(ink, doc.HitTest(beyondTip, 6));
    }

    [Fact]
    public void CollapsedArrowReleaseUndoAndCancelRestoreOriginalPointArray()
    {
        var c = Controller(1, out var doc, out _);
        var line = PolylineAnnotation.CreateArrow(new(100, 100), new(300, 250), ColorRgba.Black, 2);
        doc.Add(line); c.SetSelected(line);
        PointD[] before = [.. line.Points];
        c.PointerDown(new(300, 250)); c.PointerUp(new(100, 250));
        Assert.Equal(0, line.Bounds.Width);
        Assert.True(c.PerformUndo()); Assert.Equal(before, line.Points);
        c.PointerDown(new(300, 250)); c.PointerMove(new(100, 250)); c.CancelGesture();
        Assert.Equal(before, line.Points);
    }

    [Theory]
    [InlineData((int)EditorTool.Pen, false)] [InlineData((int)EditorTool.Highlighter, false)]
    [InlineData((int)EditorTool.Pen, true)] [InlineData((int)EditorTool.Highlighter, true)]
    public void FreehandStrokeIncludesReleaseWithoutRequiringMoveEvent(int tool, bool move)
    {
        var c = Controller(1, out var doc, out _); c.Tool = (EditorTool)tool;
        c.PointerDown(new(30, 25));
        if (move) c.PointerMove(new(75, 25));
        c.PointerUp(new(80, 25));
        var pen = Assert.IsType<PenAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(new PointD(80, 25), pen.Points[^1]);
    }

    [Fact]
    public void ModifiersGroupAndDrawingToolsDoNotOfferUnavailableHandles()
    {
        var c = Controller(1, out var doc, out _);
        var first = new RectangleAnnotation { Rect = new(100, 100, 100, 100), Fill = ColorRgba.Black };
        var second = new RectangleAnnotation { Rect = new(300, 100, 100, 100), Fill = ColorRgba.Black };
        doc.Add(first); doc.Add(second); c.SetSelected(first);
        Assert.Equal(PointerAction.ToggleSelection, c.ResolvePointerIntent(first.Rect.TopLeft, control: true).Action);
        c.SelectAll(); Assert.Empty(c.GetSelectionHandles(1));
        c.Tool = EditorTool.Rectangle; c.SetSelected(first);
        Assert.Empty(c.GetSelectionHandles(1));
        Assert.Equal(PointerAction.Create, c.ResolvePointerIntent(first.Rect.TopLeft).Action);
    }

    [Fact]
    public void RotatedTextHandlesUseRenderedPositionsAndKeepOppositeWorldCornerFixed()
    {
        var c = Controller(1, out var doc, out _);
        var text = new TextAnnotation { Rect = new(100, 100, 80, 40), Text = "hello", FontSize = 20, RotationDegrees = 90 };
        doc.Add(text); c.SetSelected(text);
        PointD anchor = GeometryMath.Rotate(text.Rect.TopLeft, text.Rect.Center, 90);
        SelectionHandle handle = c.GetSelectionHandles(1).Single(h => h.Handle == ResizeHandle.BottomRight);
        Assert.Equal(PointerAction.Resize, c.ResolvePointerIntent(handle.Position).Action);
        Assert.Equal(EditorCursorKind.ResizeNortheastSouthwest, c.ResolvePointerIntent(handle.Position).Cursor);
        c.PointerDown(handle.Position);
        c.PointerMove(handle.Position.Offset(-20, 40));
        Assert.Equal(anchor.X, GeometryMath.Rotate(text.Rect.TopLeft, text.Rect.Center, 90).X, 8);
        Assert.Equal(anchor.Y, GeometryMath.Rotate(text.Rect.TopLeft, text.Rect.Center, 90).Y, 8);
        c.CancelGesture(); Assert.Equal(new RectD(100, 100, 80, 40), text.Rect);
    }
}
