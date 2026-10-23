using System.ComponentModel;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A pen stroke appends each pointer position to the draft instead of copying the whole stroke,
/// and a pointer position that changes nothing does not repaint. Both must leave the strokes,
/// shapes, notifications and history exactly as copying and repainting every move did.
/// </summary>
public sealed class AnnotationEditorDraftInkTests
{
    private static AnnotationEditorController NewController(out AnnotationDocument document, out UndoStack undo)
    {
        document = AnnotationDocument.CreateFor(800, 600);
        undo = new UndoStack();
        return new AnnotationEditorController(document, undo);
    }

    private static PointD[] Wave(int count) =>
    [
        .. Enumerable.Range(0, count).Select(static i => new PointD(20 + (i * 1.5), 300 + (Math.Sin(i * 0.11) * 120))),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrokeFollowsEveryMoveAndCommitsTheSimplifiedPointsOfCopyingEachMove(bool highlighter)
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out UndoStack undo);
        controller.Tool = highlighter ? EditorTool.Highlighter : EditorTool.Pen;
        // Duplicates included: a repeated position is drawn and kept, as before.
        PointD[] path = [.. Wave(400), new PointD(620, 300), new PointD(620, 300)];

        controller.PointerDown(path[0]);
        PenAnnotation pen = Assert.IsType<PenAnnotation>(Assert.Single(document.Items));
        var notifications = new List<string>();
        pen.PropertyChanged += (_, e) => notifications.Add(e.PropertyName ?? string.Empty);
        for (int i = 1; i < path.Length; i++)
        {
            controller.PointerMove(path[i]);
            Assert.Equal(path.Take(i + 1), pen.Points);
        }

        // Every move still raises the same points/bounds notifications.
        Assert.Equal(
            Enumerable.Repeat(new[] { nameof(PenAnnotation.Points), nameof(PenAnnotation.Bounds) }, path.Length - 1).SelectMany(static pair => pair),
            notifications);

        controller.PointerUp(path[^1]);
        var expected = new PenAnnotation { Points = [.. path], StrokeThickness = pen.StrokeThickness };
        expected.SimplifyInPlace();
        Assert.Equal(expected.Points, pen.Points);
        Assert.Same(pen, Assert.Single(document.Items));

        PenPointCollection committed = pen.Points;
        PointD[] snapshot = [.. committed];
        Assert.True(undo.Undo());
        Assert.Empty(document.Items);
        Assert.True(undo.Redo());
        Assert.Same(pen, Assert.Single(document.Items));
        Assert.Equal(snapshot, pen.Points);

        // The controller keeps nothing of the committed stroke: the next one leaves it alone.
        controller.PointerDown(new PointD(100, 100));
        controller.PointerMove(new PointD(140, 120));
        controller.PointerMove(new PointD(180, 90));
        controller.PointerUp(new PointD(180, 90));
        Assert.Equal(2, document.Items.Count);
        Assert.Equal(snapshot, committed);
        Assert.Same(committed, pen.Points);
    }

    [Fact]
    public void AChangeToTheDraftMidStrokeIsReplacedByTheDrawnPointsAsBefore()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);
        controller.Tool = EditorTool.Pen;
        PointD[] path = Wave(12);

        controller.PointerDown(path[0]);
        PenAnnotation pen = Assert.IsType<PenAnnotation>(Assert.Single(document.Items));
        for (int i = 1; i < 5; i++) controller.PointerMove(path[i]);

        // A crop moves the stroke in place; the next move restores the drawn points.
        pen.Translate(40, -10);
        controller.PointerMove(path[5]);
        Assert.Equal(path.Take(6), pen.Points);
        controller.PointerMove(path[6]);
        Assert.Equal(path.Take(7), pen.Points);

        // A rotation replaces the list; the next move restores the drawn points again.
        List<PointD> replaced = [.. pen.Points.Select(static p => new PointD(p.Y, p.X))];
        pen.Points = replaced;
        controller.PointerMove(path[7]);
        Assert.Equal(path.Take(8), pen.Points);
        Assert.Equal(path.Take(7).Select(static p => new PointD(p.Y, p.X)), replaced);
        controller.PointerMove(path[8]);
        Assert.Equal(path.Take(9), pen.Points);

        controller.CancelGesture();
        Assert.Empty(document.Items);
        // No longer the draft: changing it reaches no controller state.
        pen.Translate(5, 5);
        controller.PointerMove(path[9]);
        Assert.Equal(path.Take(9).Select(static p => new PointD(p.X + 5, p.Y + 5)), pen.Points);
    }

    [Fact]
    public void ARepeatedPositionMovesNothingAndDoesNotRepaint()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out UndoStack undo);
        int repaints = 0;
        controller.VisualInvalidated += (_, _) => repaints++;

        // Creating a rectangle.
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(100, 100));
        repaints = 0;
        controller.PointerMove(new PointD(180, 160));
        Assert.Equal(1, repaints);
        controller.PointerMove(new PointD(180, 160));
        Assert.Equal(1, repaints);
        controller.PointerUp(new PointD(180, 160));
        var rectangle = Assert.IsType<RectangleAnnotation>(Assert.Single(document.Items));
        Assert.Equal(new RectD(100, 100, 80, 60), rectangle.Rect);

        // Creating an arrow.
        controller.Tool = EditorTool.Arrow;
        controller.PointerDown(new PointD(300, 300));
        repaints = 0;
        controller.PointerMove(new PointD(360, 330));
        controller.PointerMove(new PointD(360, 330));
        Assert.Equal(1, repaints);
        controller.PointerUp(new PointD(360, 330));
        var arrow = Assert.IsType<PolylineAnnotation>(document.Items[^1]);
        Assert.Equal(new[] { new PointD(300, 300), new PointD(360, 330) }, arrow.Points);

        // Moving the rectangle, including an axis-locked move along the locked axis.
        controller.Tool = EditorTool.Select;
        controller.PointerDown(new PointD(100, 130));
        repaints = 0;
        controller.PointerMove(new PointD(130, 140));
        Assert.Equal(1, repaints);
        controller.PointerMove(new PointD(130, 140));
        Assert.Equal(1, repaints);
        controller.PointerMove(new PointD(160, 132), shift: true);
        Assert.Equal(2, repaints);
        controller.PointerMove(new PointD(160, 128), shift: true);
        Assert.Equal(2, repaints);
        controller.PointerUp(new PointD(160, 128), shift: true);
        Assert.Equal(new RectD(160, 100, 80, 60), rectangle.Rect);
        Assert.True(controller.PerformUndo());
        Assert.Equal(new RectD(100, 100, 80, 60), rectangle.Rect);

        // A marquee over empty canvas.
        controller.PointerDown(new PointD(500, 500));
        repaints = 0;
        controller.PointerMove(new PointD(550, 540));
        int afterMarquee = repaints;
        Assert.True(afterMarquee >= 1);
        controller.PointerMove(new PointD(550, 540));
        Assert.Equal(afterMarquee, repaints);
        controller.PointerUp(new PointD(550, 540));

        // A pen stroke still repaints and keeps a repeated point, exactly as before.
        controller.Tool = EditorTool.Pen;
        controller.PointerDown(new PointD(10, 10));
        repaints = 0;
        controller.PointerMove(new PointD(20, 20));
        controller.PointerMove(new PointD(20, 20));
        Assert.Equal(2, repaints);
        var ink = Assert.IsType<PenAnnotation>(document.Items[^1]);
        Assert.Equal(new[] { new PointD(10, 10), new PointD(20, 20), new PointD(20, 20) }, ink.Points);
        controller.PointerUp(new PointD(20, 20));
        Assert.Equal(3, undo.UndoCount);
    }

    [Fact]
    public void AnInPlaceScaleOfTheDraftIsReplacedByTheDrawnPoints()
    {
        AnnotationEditorController controller = NewController(out AnnotationDocument document, out _);
        controller.Tool = EditorTool.Pen;
        controller.PointerDown(new PointD(10, 10));
        controller.PointerMove(new PointD(30, 10));
        var pen = Assert.IsType<PenAnnotation>(Assert.Single(document.Items));

        // A group transform scales the stroke in place; the drawn points win on the next move.
        pen.SetBounds(new RectD(0, 0, 200, 0));
        Assert.Equal(new[] { new PointD(0, 0), new PointD(200, 0) }, pen.Points);
        int notified = 0;
        PropertyChangedEventHandler handler = (_, _) => notified++;
        pen.PropertyChanged += handler;
        controller.PointerMove(new PointD(50, 10));
        Assert.Equal(new[] { new PointD(10, 10), new PointD(30, 10), new PointD(50, 10) }, pen.Points);
        Assert.Equal(2, notified);
        pen.PropertyChanged -= handler;
    }
}
