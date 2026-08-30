using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Behaviour of the annotation editor's interaction model.
/// </summary>
/// <remarks>
/// The controller takes pointer positions in selected-image pixels and drives the domain
/// document and undo stack with no WPF dependency, so every gesture is exercised here on a
/// plain thread with no message pump.
/// </remarks>
public sealed class AnnotationEditorControllerTests
{
    private static AnnotationEditorController NewController(out AnnotationDocument document, out UndoStack undo)
    {
        document = AnnotationDocument.CreateFor(800, 600);
        undo = new UndoStack();
        return new AnnotationEditorController(document, undo);
    }

    [Fact]
    public void RectangleTool_DragCreatesOneUndoableRectangle()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        c.Tool = EditorTool.Rectangle;

        c.PointerDown(new PointD(10, 10));
        c.PointerMove(new PointD(110, 60));
        c.PointerUp(new PointD(110, 60));

        AnnotationItem item = Assert.Single(doc.Items);
        var rect = Assert.IsType<RectangleAnnotation>(item);
        Assert.Equal(100, rect.Rect.Width, 3);
        Assert.Equal(50, rect.Rect.Height, 3);
        Assert.True(undo.CanUndo);

        // One drag is one undo step.
        Assert.True(c.PerformUndo());
        Assert.Empty(doc.Items);
        Assert.True(c.PerformRedo());
        Assert.Single(doc.Items);
    }

    [Fact]
    public void ArrowTool_DragCreatesArrowWithHead()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        c.Tool = EditorTool.Arrow;

        c.PointerDown(new PointD(20, 20));
        c.PointerMove(new PointD(200, 120));
        c.PointerUp(new PointD(200, 120));

        var arrow = Assert.IsType<PolylineAnnotation>(Assert.Single(doc.Items));
        Assert.True(arrow.HeadAtEnd);
        Assert.Equal(new PointD(20, 20), arrow.Points[0]);
        Assert.Equal(new PointD(200, 120), arrow.Points[^1]);
    }

    [Fact]
    public void PenTool_DragCreatesStrokeAndSimplifies()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        c.Tool = EditorTool.Pen;

        c.PointerDown(new PointD(0, 0));
        for (int i = 1; i <= 20; i++)
        {
            c.PointerMove(new PointD(i * 5, 0)); // collinear points collapse on simplify
        }

        c.PointerUp(new PointD(100, 0));

        var pen = Assert.IsType<PenAnnotation>(Assert.Single(doc.Items));
        Assert.True(pen.Points.Count >= 2);
        Assert.True(pen.Points.Count < 21, "Collinear stroke should have been simplified.");
    }

    [Fact]
    public void ClickWithoutDrag_LeavesNoDegenerateShape()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        c.Tool = EditorTool.Rectangle;

        c.PointerDown(new PointD(50, 50));
        c.PointerUp(new PointD(50, 50));

        Assert.Empty(doc.Items);
    }

    [Fact]
    public void SelectTool_MoveIsUndoableAsSingleStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        // Filled so an interior click selects it; an outline-only shape deliberately does
        // not swallow interior clicks (see AnnotationItem.DistanceTo).
        var rect = new RectangleAnnotation
        {
            Rect = new RectD(10, 10, 100, 100),
            Fill = new ColorRgba(0x40, 0x3B, 0x82, 0xF6),
        };
        doc.Add(rect);

        c.Tool = EditorTool.Select;
        c.HitTolerance = 6;
        c.PointerDown(new PointD(50, 50)); // inside -> selects and starts move
        Assert.Same(rect, c.Selected);

        c.PointerMove(new PointD(70, 60));
        c.PointerMove(new PointD(90, 80));
        c.PointerUp(new PointD(90, 80));

        // Total drag delta from the press point (50,50) to (90,80) is (+40,+30).
        Assert.Equal(50, rect.Rect.X, 3);
        Assert.Equal(40, rect.Rect.Y, 3);

        Assert.True(undo.Undo());
        Assert.Equal(10, rect.Rect.X, 3);
        Assert.Equal(10, rect.Rect.Y, 3);
    }

    [Fact]
    public void SelectTool_ResizeViaCornerHandle()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        var rect = new RectangleAnnotation { Rect = new RectD(100, 100, 100, 100) };
        doc.Add(rect);
        c.Tool = EditorTool.Select;
        c.SetSelected(rect);
        c.HitTolerance = 6;

        // Grab the bottom-right handle at (200,200) and drag it out.
        c.PointerDown(new PointD(200, 200));
        c.PointerMove(new PointD(260, 240));
        c.PointerUp(new PointD(260, 240));

        Assert.Equal(160, rect.Rect.Width, 3);
        Assert.Equal(140, rect.Rect.Height, 3);
    }

    [Fact]
    public void PenStroke_DoesNotSupportResize()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        c.Tool = EditorTool.Pen;
        c.PointerDown(new PointD(0, 0));
        c.PointerMove(new PointD(10, 10));
        c.PointerMove(new PointD(20, 5));
        c.PointerUp(new PointD(20, 5));

        var pen = (PenAnnotation)doc.Items[0];
        Assert.False(pen.SupportsResize);
        Assert.Equal(ResizeHandle.None, c.HandleAt(pen.Bounds.TopLeft, 6));
    }

    [Fact]
    public void DeleteSelected_IsUndoableAndRestoresPaintOrder()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        var a = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };
        var b = new EllipseAnnotation { Rect = new RectD(20, 20, 10, 10) };
        var cc = new RectangleAnnotation { Rect = new RectD(40, 40, 10, 10) };
        doc.Add(a);
        doc.Add(b);
        doc.Add(cc);

        c.SetSelected(b);
        c.DeleteSelected();
        Assert.Equal(2, doc.Items.Count);
        Assert.Null(c.Selected);

        Assert.True(c.PerformUndo());
        Assert.Equal(3, doc.Items.Count);
        Assert.Same(b, doc.Items[1]); // restored to original middle position
    }

    [Fact]
    public void ApplyStrokeColor_ChangesSelectedAndIsUndoable()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        var rect = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10), Stroke = ColorRgba.FromRgb(1, 2, 3) };
        doc.Add(rect);
        c.SetSelected(rect);

        ColorRgba blue = ColorRgba.FromRgb(0x3B, 0x82, 0xF6);
        c.ApplyStrokeColor(blue);
        Assert.Equal(blue, rect.Stroke);

        Assert.True(c.PerformUndo());
        Assert.Equal(ColorRgba.FromRgb(1, 2, 3), rect.Stroke);
    }

    [Fact]
    public void BeginTextAnnotation_EmptyCommitRemovesIt()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        TextAnnotation text = c.BeginTextAnnotation(new PointD(30, 30), 180, 40);
        Assert.Single(doc.Items);
        Assert.True(c.IsCreatingText);

        c.CommitTextEdit(text, string.Empty);
        Assert.Empty(doc.Items);
        Assert.False(c.IsCreatingText);
    }

    [Fact]
    public void BeginTextAnnotation_NonEmptyCommitKeepsItAndIsUndoable()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        TextAnnotation text = c.BeginTextAnnotation(new PointD(30, 30), 180, 40);

        c.CommitTextEdit(text, "검토 필요");
        Assert.Single(doc.Items);
        Assert.Equal("검토 필요", ((TextAnnotation)doc.Items[0]).Text);

        // Undo the text change, then the add.
        Assert.True(c.PerformUndo());
        Assert.Equal(string.Empty, ((TextAnnotation)doc.Items[0]).Text);
        Assert.True(c.PerformUndo());
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void AddImageAnnotation_IsUndoableAndSelected()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        ImageAnnotation image = c.AddImageAnnotation("image-01.png", 320, 180, new RectD(10, 10, 160, 90));

        Assert.Single(doc.Items);
        Assert.Same(image, c.Selected);
        Assert.Equal("image-01.png", image.AssetFileName);

        Assert.True(c.PerformUndo());
        Assert.Empty(doc.Items);
    }

    [Fact]
    public void SwitchingAwayFromSelectTool_ClearsSelection()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        var rect = new RectangleAnnotation { Rect = new RectD(0, 0, 10, 10) };
        doc.Add(rect);
        c.SetSelected(rect);
        Assert.NotNull(c.Selected);

        c.Tool = EditorTool.Rectangle;
        Assert.Null(c.Selected);
    }
}
