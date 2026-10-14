using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AnnotationEditorSelectionTests
{
    private static AnnotationEditorController NewController(
        out AnnotationDocument document, out UndoStack undo, TimeSpan? mergeWindow = null)
    {
        document = AnnotationDocument.CreateFor(800, 600);
        undo = new UndoStack(mergeWindow: mergeWindow);
        return new AnnotationEditorController(document, undo) { Tool = EditorTool.Select };
    }

    private static RectangleAnnotation Rectangle(double x, double y, double width = 40, double height = 40) =>
        new() { Rect = new RectD(x, y, width, height), Fill = ColorRgba.Black };

    private static void Add(AnnotationDocument document, params AnnotationItem[] items)
    {
        foreach (AnnotationItem item in items)
        {
            document.Add(item);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Marquee_SelectsIntersectingAndTouchingBoundsInEitherDirection(bool reverse)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation inside = Rectangle(20, 20, 20, 20);
        RectangleAnnotation crossing = Rectangle(80, 60, 40, 20);
        RectangleAnnotation touching = Rectangle(100, 40, 20, 20);
        RectangleAnnotation outside = Rectangle(120, 120);
        Add(doc, inside, crossing, touching, outside);
        PointD start = reverse ? new PointD(100, 100) : new PointD(0, 0);
        PointD end = reverse ? new PointD(0, 0) : new PointD(100, 100);

        c.PointerDown(start);
        c.PointerMove(end);

        Assert.Equal(new RectD(0, 0, 100, 100), c.MarqueeBounds);
        c.PointerUp(end);

        Assert.Equal<AnnotationItem>([inside, crossing, touching], c.SelectedItems);
        Assert.DoesNotContain(outside, c.SelectedItems);
        Assert.Null(c.MarqueeBounds);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void Marquee_SelectsHorizontalAndVerticalLineBounds()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        PolylineAnnotation horizontal = PolylineAnnotation.CreateLine(new PointD(20, 30), new PointD(80, 30), ColorRgba.Black, 2);
        PolylineAnnotation vertical = PolylineAnnotation.CreateLine(new PointD(70, 20), new PointD(70, 80), ColorRgba.Black, 2);
        Add(doc, horizontal, vertical);

        c.PointerDown(new PointD(0, 0));
        c.PointerUp(new PointD(100, 100));

        Assert.Equal<AnnotationItem>([horizontal, vertical], c.SelectedItems);
        Assert.Equal(new RectD(20, 20, 60, 60), c.SelectionBounds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0)]
    [InlineData(0, 100)]
    public void Marquee_WithZeroAreaSelectsNothing(double x, double y)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation rectangle = Rectangle(50, 0);
        doc.Add(rectangle);

        c.PointerDown(new PointD(0, 0));
        c.PointerUp(new PointD(x, y));

        Assert.Empty(c.SelectedItems);
        Assert.Null(c.SelectionBounds);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void Marquee_ReplacesPreviousSelection()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SetSelected(first);

        c.PointerDown(new PointD(180, 180));
        c.PointerUp(new PointD(250, 250));

        Assert.Same(second, Assert.Single(c.SelectedItems));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ModifiedMarquee_AddsToInitialSelection(bool shift, bool control)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        RectangleAnnotation outside = Rectangle(400, 400);
        Add(doc, first, second, outside);
        c.SetSelected(first);

        c.PointerDown(new PointD(180, 180), shift, control);
        c.PointerMove(new PointD(450, 450), shift);
        c.PointerMove(new PointD(250, 250), shift);
        c.PointerUp(new PointD(250, 250), shift);

        Assert.Equal<AnnotationItem>([first, second], c.SelectedItems);
        Assert.DoesNotContain(outside, c.SelectedItems);
        Assert.Equal(0, undo.UndoCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ModifiedClick_TogglesMembershipAndPrimaryWithoutMovingItems(bool shift, bool control)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SetSelected(first);

        c.PointerDown(second.Rect.Center, shift, control);
        c.PointerUp(second.Rect.Center, shift);

        Assert.Equal<AnnotationItem>([first, second], c.SelectedItems);
        Assert.Same(second, c.Selected);

        c.PointerDown(second.Rect.Center, shift, control);
        c.PointerUp(second.Rect.Center, shift);

        Assert.Same(first, Assert.Single(c.SelectedItems));
        Assert.Same(first, c.Selected);
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void ToggleSelected_AddsAndRemovesWithoutDuplicates()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SetSelected(first);
        c.ToggleSelected(second);

        Assert.Equal(new RectD(100, 100, 140, 140), c.SelectionBounds);
        Assert.Same(second, c.Selected);

        c.ToggleSelected(first);
        Assert.Same(second, Assert.Single(c.SelectedItems));
        c.ToggleSelected(second);

        Assert.Empty(c.SelectedItems);
        Assert.Null(c.Selected);
        Assert.Null(c.SelectionBounds);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void SelectAll_ThenClearSelectionLeavesDocumentAndHistoryIntact()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);

        c.SelectAll();
        c.SelectAll();

        Assert.Equal<AnnotationItem>([first, second], c.SelectedItems);
        Assert.Same(second, c.Selected);

        c.SetSelected(null);

        Assert.Empty(c.SelectedItems);
        Assert.Null(c.SelectionBounds);
        Assert.Equal<AnnotationItem>([first, second], doc.Items);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void UnmodifiedClickOnUnselectedItem_ReplacesSelection()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        RectangleAnnotation third = Rectangle(300, 300);
        Add(doc, first, second, third);
        c.SetSelected(first);
        c.ToggleSelected(second);

        c.PointerDown(third.Rect.Center);
        c.PointerUp(third.Rect.Center);

        Assert.Same(third, Assert.Single(c.SelectedItems));
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void GroupMove_PreservesMembershipAndUsesOneUndoStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SelectAll();

        c.PointerDown(new PointD(120, 120));
        c.PointerMove(new PointD(125, 123));
        c.PointerMove(new PointD(145, 135));
        c.PointerUp(new PointD(150, 140));

        Assert.Equal<AnnotationItem>([first, second], c.SelectedItems);
        Assert.Equal(new RectD(130, 120, 40, 40), first.Rect);
        Assert.Equal(new RectD(230, 220, 40, 40), second.Rect);
        Assert.Equal(new RectD(130, 120, 140, 140), c.SelectionBounds);
        Assert.Equal(1, undo.UndoCount);

        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
        Assert.Equal(0, undo.UndoCount);
        Assert.True(c.PerformRedo());
        Assert.Equal(new RectD(130, 120, 40, 40), first.Rect);
        Assert.Equal(new RectD(230, 220, 40, 40), second.Rect);
    }

    [Theory]
    [InlineData(30, 12, 30, 0)]
    [InlineData(12, 30, 0, 30)]
    [InlineData(-30, 12, -30, 0)]
    [InlineData(12, -30, 0, -30)]
    public void ShiftMove_LocksDominantAxisFromGestureOrigin(double dx, double dy, double expectedX, double expectedY)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SelectAll();

        c.PointerDown(new PointD(120, 120));
        c.PointerMove(new PointD(125, 125));
        c.PointerMove(new PointD(120 + dx, 120 + dy), shift: true);
        c.PointerUp(new PointD(120 + dx, 120 + dy), shift: true);

        Assert.Equal(new RectD(100 + expectedX, 100 + expectedY, 40, 40), first.Rect);
        Assert.Equal(new RectD(200 + expectedX, 200 + expectedY, 40, 40), second.Rect);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
    }

    [Fact]
    public void Move_ReleasingShiftRestoresUnconstrainedDelta()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        RectangleAnnotation rectangle = Rectangle(100, 100);
        doc.Add(rectangle);

        c.PointerDown(new PointD(120, 120));
        c.PointerMove(new PointD(150, 135), shift: true);
        Assert.Equal(new RectD(130, 100, 40, 40), rectangle.Rect);

        c.PointerUp(new PointD(150, 135));

        Assert.Equal(new RectD(130, 115, 40, 40), rectangle.Rect);
    }

    [Fact]
    public void RepeatedGroupDrags_AreSeparateUndoSteps()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SelectAll();

        c.PointerDown(new PointD(120, 120));
        c.PointerUp(new PointD(130, 120));
        c.PointerDown(new PointD(130, 120));
        c.PointerUp(new PointD(145, 125));

        Assert.Equal(2, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(110, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(210, 200, 40, 40), second.Rect);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
    }

    [Theory]
    [InlineData(30, 12, 30, 0)]
    [InlineData(12, 30, 0, 30)]
    [InlineData(-30, 12, -30, 0)]
    [InlineData(12, -30, 0, -30)]
    public void SingleItemShiftMove_LocksDominantAxisAndUndoesAsOneStep(double dx, double dy, double expectedX, double expectedY)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation rectangle = Rectangle(100, 100);
        doc.Add(rectangle);
        c.SetSelected(rectangle);

        c.PointerDown(new PointD(120, 120));
        c.PointerMove(new PointD(125, 125));
        c.PointerMove(new PointD(120 + dx, 120 + dy), shift: true);
        c.PointerUp(new PointD(120 + dx, 120 + dy), shift: true);

        Assert.Same(rectangle, Assert.Single(c.SelectedItems));
        Assert.Equal(new RectD(100 + expectedX, 100 + expectedY, 40, 40), rectangle.Rect);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), rectangle.Rect);
        Assert.True(c.PerformRedo());
        Assert.Equal(new RectD(100 + expectedX, 100 + expectedY, 40, 40), rectangle.Rect);
    }

    [Fact]
    public void ShiftHeldFromPointerDown_MovesOriginalGroupAlongDominantAxis()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SelectAll();
        c.HitTolerance = 6;

        c.PointerDown(new PointD(120, 120), shift: true);
        c.PointerMove(new PointD(160, 135), shift: true);
        c.PointerUp(new PointD(160, 135), shift: true);

        Assert.Equal<AnnotationItem>([first, second], c.SelectedItems);
        Assert.Equal(new RectD(140, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(240, 200, 40, 40), second.Rect);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ModifiedPointerMovementBelowHitTolerance_RemainsSelectionToggle(bool shift, bool control)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SelectAll();
        c.HitTolerance = 6;

        c.PointerDown(new PointD(120, 120), shift, control);
        c.PointerMove(new PointD(123, 122), shift);
        c.PointerUp(new PointD(123, 122), shift);

        Assert.Same(second, Assert.Single(c.SelectedItems));
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void RapidSingleItemDrags_RemainSeparateWithinMergeWindow()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        RectangleAnnotation rectangle = Rectangle(100, 100);
        doc.Add(rectangle);
        c.SetSelected(rectangle);

        c.PointerDown(new PointD(120, 120));
        c.PointerUp(new PointD(130, 120));
        c.PointerDown(new PointD(130, 120));
        c.PointerUp(new PointD(145, 125));

        Assert.Equal(2, undo.UndoCount);
        Assert.Equal(new RectD(125, 105, 40, 40), rectangle.Rect);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(110, 100, 40, 40), rectangle.Rect);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), rectangle.Rect);
        Assert.True(c.PerformRedo());
        Assert.True(c.PerformRedo());
        Assert.Equal(new RectD(125, 105, 40, 40), rectangle.Rect);
    }

    [Fact]
    public void DragBackToOrigin_DoesNotCreateHistory()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        Add(doc, first, second);
        c.SelectAll();

        c.PointerDown(new PointD(120, 120));
        c.PointerMove(new PointD(150, 140));
        c.PointerUp(new PointD(120, 120));

        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new RectD(200, 200, 40, 40), second.Rect);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void CancelGroupMove_RestoresEveryItemWithoutHistory()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        PolylineAnnotation line = PolylineAnnotation.CreateLine(new PointD(200, 200), new PointD(260, 200), ColorRgba.Black, 2);
        Add(doc, first, line);
        c.SelectAll();

        c.PointerDown(new PointD(120, 120));
        c.PointerMove(new PointD(160, 150));
        c.CancelGesture();
        c.PointerUp(new PointD(160, 150));

        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new[] { new PointD(200, 200), new PointD(260, 200) }, line.Points);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void MultiSelection_DoesNotExposeResizeHandles()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out _);
        Add(doc, Rectangle(100, 100), Rectangle(200, 200));
        c.SelectAll();

        Assert.Equal(ResizeHandle.None, c.HandleAt(new PointD(100, 100), 6));
        Assert.Equal(ResizeHandle.None, c.HandleAt(new PointD(240, 240), 6));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingDegenerateLine_PreservesEveryPointThroughUndoRedo(bool vertical)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        PointD[] original = vertical
            ? [new PointD(100, 100), new PointD(100, 140), new PointD(100, 200)]
            : [new PointD(100, 100), new PointD(140, 100), new PointD(200, 100)];
        var line = new PolylineAnnotation { Points = [.. original], HeadAtEnd = false };
        doc.Add(line);
        PointD start = vertical ? new PointD(100, 140) : new PointD(140, 100);

        c.PointerDown(start);
        c.PointerMove(start.Offset(25, 35));
        c.PointerUp(start.Offset(25, 35));

        Assert.Equal(original.Select(p => p.Offset(25, 35)), line.Points);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(original, line.Points);
        Assert.True(c.PerformRedo());
        Assert.Equal(original.Select(p => p.Offset(25, 35)), line.Points);
    }

    [Fact]
    public void GroupDelete_IsOneStepAndRestoresOriginalPaintOrder()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        RectangleAnnotation second = Rectangle(200, 200);
        RectangleAnnotation third = Rectangle(300, 300);
        RectangleAnnotation fourth = Rectangle(400, 400);
        Add(doc, first, second, third, fourth);
        c.SetSelected(fourth);
        c.ToggleSelected(second);

        c.DeleteSelected();

        Assert.Equal<AnnotationItem>([first, third], doc.Items);
        Assert.Empty(c.SelectedItems);
        Assert.Null(c.Selected);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal<AnnotationItem>([first, second, third, fourth], doc.Items);
        Assert.True(c.PerformRedo());
        Assert.Equal<AnnotationItem>([first, third], doc.Items);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(10, 0)]
    [InlineData(0, 10)]
    public void GroupNudge_IsOneUndoStep(double dx, double dy)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation first = Rectangle(100, 100);
        PolylineAnnotation line = PolylineAnnotation.CreateLine(new PointD(200, 200), new PointD(260, 200), ColorRgba.Black, 2);
        Add(doc, first, line);
        c.SelectAll();

        c.NudgeSelected(dx, dy);

        Assert.Equal(new RectD(100 + dx, 100 + dy, 40, 40), first.Rect);
        Assert.Equal(new[] { new PointD(200 + dx, 200 + dy), new PointD(260 + dx, 200 + dy) }, line.Points);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(new RectD(100, 100, 40, 40), first.Rect);
        Assert.Equal(new[] { new PointD(200, 200), new PointD(260, 200) }, line.Points);
        Assert.True(c.PerformRedo());
        Assert.Equal(new RectD(100 + dx, 100 + dy, 40, 40), first.Rect);
    }

    [Fact]
    public void GroupDuplicate_CreatesIndependentOffsetCopiesAsOneUndoStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation rectangle = Rectangle(100, 100);
        PolylineAnnotation line = PolylineAnnotation.CreateLine(new PointD(200, 200), new PointD(260, 200), ColorRgba.Black, 2);
        RectangleAnnotation unselected = Rectangle(300, 300);
        Add(doc, rectangle, line, unselected);
        c.SetSelected(line);
        c.ToggleSelected(rectangle);

        c.DuplicateSelected();

        Assert.Equal(5, doc.Items.Count);
        var rectangleCopy = Assert.IsType<RectangleAnnotation>(doc.Items[3]);
        var lineCopy = Assert.IsType<PolylineAnnotation>(doc.Items[4]);
        Assert.NotEqual(rectangle.Id, rectangleCopy.Id);
        Assert.NotEqual(line.Id, lineCopy.Id);
        Assert.NotSame(line.Points, lineCopy.Points);
        Assert.Equal(new RectD(110, 110, 40, 40), rectangleCopy.Rect);
        Assert.Equal(new[] { new PointD(210, 210), new PointD(270, 210) }, lineCopy.Points);
        Assert.Equal(rectangle.Fill, rectangleCopy.Fill);
        Assert.Equal(line.Stroke, lineCopy.Stroke);
        Assert.Equal<AnnotationItem>([rectangleCopy, lineCopy], c.SelectedItems);
        Assert.Equal(new RectD(100, 100, 40, 40), rectangle.Rect);
        Assert.Equal(new[] { new PointD(200, 200), new PointD(260, 200) }, line.Points);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal<AnnotationItem>([rectangle, line, unselected], doc.Items);
        Assert.True(c.PerformRedo());
        Assert.Equal<AnnotationItem>([rectangle, line, unselected, rectangleCopy, lineCopy], doc.Items);
    }

    [Fact]
    public void GroupDuplicate_ImageKeepsAssetReferenceReachableThroughUndoRedo()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var image = new ImageAnnotation
        {
            Rect = new RectD(100, 100, 80, 60), AssetFileName = "selection-fixture.png",
            SourceWidth = 160, SourceHeight = 120,
        };
        RectangleAnnotation rectangle = Rectangle(250, 250);
        Add(doc, image, rectangle);
        c.SelectAll();
        Assert.Empty(undo.ReferencedImageAssets);

        c.DuplicateSelected();

        var copy = Assert.IsType<ImageAnnotation>(doc.Items[2]);
        Assert.NotEqual(image.Id, copy.Id);
        Assert.Equal(image.AssetFileName, copy.AssetFileName);
        Assert.Equal(image.SourceWidth, copy.SourceWidth);
        Assert.Equal(image.SourceHeight, copy.SourceHeight);
        Assert.Equal(new RectD(110, 110, 80, 60), copy.Rect);
        Assert.Equal(image.AssetFileName, Assert.Single(undo.ReferencedImageAssets));
        Assert.Equal(1, undo.UndoCount);

        Assert.True(c.PerformUndo());
        Assert.Equal<AnnotationItem>([image, rectangle], doc.Items);
        Assert.Equal(image.AssetFileName, Assert.Single(undo.ReferencedImageAssets));
        Assert.True(c.PerformRedo());
        Assert.Same(copy, doc.Items[2]);
        Assert.Equal(image.AssetFileName, Assert.Single(undo.ReferencedImageAssets));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroupReorder_AdjacentSelectionMovesOneLayerWithoutChangingRelativeOrder(bool forward)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation a = Rectangle(100, 100);
        RectangleAnnotation b = Rectangle(200, 200);
        RectangleAnnotation cc = Rectangle(300, 300);
        RectangleAnnotation d = Rectangle(400, 400);
        Add(doc, a, b, cc, d);
        c.SetSelected(cc);
        c.ToggleSelected(b);

        c.ReorderSelected(forward);

        AnnotationItem[] expected = forward ? [a, d, b, cc] : [b, cc, a, d];
        Assert.Equal(expected, doc.Items);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal<AnnotationItem>([a, b, cc, d], doc.Items);
        Assert.True(c.PerformRedo());
        Assert.Equal(expected, doc.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroupReorder_DisjointSelectionMovesEachItemOneLayer(bool forward)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation a = Rectangle(100, 100);
        RectangleAnnotation b = Rectangle(200, 200);
        RectangleAnnotation cc = Rectangle(300, 300);
        RectangleAnnotation d = Rectangle(400, 400);
        Add(doc, a, b, cc, d);
        c.SetSelected(forward ? a : b);
        c.ToggleSelected(forward ? cc : d);

        c.ReorderSelected(forward);

        Assert.Equal<AnnotationItem>([b, a, d, cc], doc.Items);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal<AnnotationItem>([a, b, cc, d], doc.Items);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroupReorder_AtBoundaryPreservesOrderAndDoesNotCreateHistory(bool forward)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation a = Rectangle(100, 100);
        RectangleAnnotation b = Rectangle(200, 200);
        RectangleAnnotation cc = Rectangle(300, 300);
        Add(doc, a, b, cc);
        c.SetSelected(forward ? cc : a);
        c.ToggleSelected(b);

        c.ReorderSelected(forward);

        Assert.Equal<AnnotationItem>([a, b, cc], doc.Items);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void NoSelectionOperations_AndZeroNudgeDoNotCreateHistory()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        RectangleAnnotation rectangle = Rectangle(100, 100);
        doc.Add(rectangle);

        c.DeleteSelected();
        c.NudgeSelected(1, 0);
        c.DuplicateSelected();
        c.ReorderSelected(true);
        c.SetSelected(rectangle);
        c.NudgeSelected(0, 0);

        Assert.Same(rectangle, Assert.Single(doc.Items));
        Assert.Equal(new RectD(100, 100, 40, 40), rectangle.Rect);
        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void GroupColor_UpdatesCompatibleItemsAndTheirLinkedFillsAsOneStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var shape = new RectangleAnnotation
        {
            Rect = new RectD(10, 10, 40, 40), Stroke = ColorRgba.Black,
            Fill = ColorRgba.Black.WithAlpha(128), FillMatchesStroke = true,
        };
        var line = new PolylineAnnotation { Points = [new PointD(100, 100), new PointD(140, 100)], Stroke = ColorRgba.Black };
        var pen = new PenAnnotation { Points = [new PointD(200, 200), new PointD(240, 240)], Stroke = ColorRgba.Black };
        var text = new TextAnnotation { Rect = new RectD(300, 300, 40, 40), Foreground = ColorRgba.Black };
        var badge = new TextAnnotation { Rect = new RectD(350, 350, 40, 40), IsStepNumber = true, Background = ColorRgba.Black };
        var image = new ImageAnnotation { Rect = new RectD(400, 400, 40, 40), AssetFileName = "fixture.png" };
        Add(doc, shape, line, pen, text, badge, image);
        c.SelectAll();
        ColorRgba color = ColorRgba.FromRgb(20, 40, 60);

        c.ApplyStrokeColor(color);

        Assert.Equal(color, shape.Stroke);
        Assert.Equal(color.WithAlpha(128), shape.Fill);
        Assert.Equal(color, line.Stroke);
        Assert.Equal(color, pen.Stroke);
        Assert.Equal(color, text.Foreground);
        Assert.Equal(color, badge.Background);
        Assert.Equal("fixture.png", image.AssetFileName);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(ColorRgba.Black, shape.Stroke);
        Assert.Equal(ColorRgba.Black.WithAlpha(128), shape.Fill);
        Assert.Equal(ColorRgba.Black, line.Stroke);
        Assert.Equal(ColorRgba.Black, pen.Stroke);
        Assert.Equal(ColorRgba.Black, text.Foreground);
        Assert.Equal(ColorRgba.Black, badge.Background);
        Assert.True(c.PerformRedo());
        Assert.Equal(color, shape.Stroke);
        Assert.Equal(color, text.Foreground);
    }

    [Fact]
    public void GroupThickness_UpdatesCompatibleItemsAsOneStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var shape = new RectangleAnnotation { Rect = new RectD(10, 10, 40, 40), StrokeThickness = 2 };
        var line = new PolylineAnnotation { Points = [new PointD(100, 100), new PointD(140, 100)], StrokeThickness = 3 };
        var pen = new PenAnnotation { Points = [new PointD(200, 200), new PointD(240, 240)], StrokeThickness = 4 };
        var text = new TextAnnotation { Rect = new RectD(300, 300, 40, 40), FontSize = 18 };
        Add(doc, shape, line, pen, text);
        c.SelectAll();

        c.ApplyStrokeThickness(8);

        Assert.Equal(8, shape.StrokeThickness);
        Assert.Equal(8, line.StrokeThickness);
        Assert.Equal(8, pen.StrokeThickness);
        Assert.Equal(18, text.FontSize);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(2, shape.StrokeThickness);
        Assert.Equal(3, line.StrokeThickness);
        Assert.Equal(4, pen.StrokeThickness);
        Assert.True(c.PerformRedo());
        Assert.Equal(8, shape.StrokeThickness);
        Assert.Equal(8, line.StrokeThickness);
        Assert.Equal(8, pen.StrokeThickness);
    }

    [Fact]
    public void GroupStrokeStyle_UpdatesShapesAndRestoresThicknessAsOneStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var rectangle = new RectangleAnnotation { Rect = new RectD(10, 10, 40, 40), StrokeThickness = 2 };
        var ellipse = new EllipseAnnotation { Rect = new RectD(100, 100, 40, 40), StrokeThickness = 8, StrokeStyle = AnnotationStrokeStyle.Dotted };
        var line = new PolylineAnnotation { Points = [new PointD(200, 200), new PointD(240, 200)], StrokeThickness = 3 };
        Add(doc, rectangle, ellipse, line);
        c.SelectAll();

        c.ApplyStrokeStyle(AnnotationStrokeStyle.ThickDashed);

        Assert.Equal(AnnotationStrokeStyle.ThickDashed, rectangle.StrokeStyle);
        Assert.Equal(AnnotationStrokeStyle.ThickDashed, ellipse.StrokeStyle);
        Assert.Equal(6, rectangle.StrokeThickness);
        Assert.Equal(8, ellipse.StrokeThickness);
        Assert.Equal(3, line.StrokeThickness);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(AnnotationStrokeStyle.Solid, rectangle.StrokeStyle);
        Assert.Equal(AnnotationStrokeStyle.Dotted, ellipse.StrokeStyle);
        Assert.Equal(2, rectangle.StrokeThickness);
        Assert.Equal(8, ellipse.StrokeThickness);
    }

    [Fact]
    public void GroupFillTransparency_UsesEachShapesOwnStrokeAndUndoesLegacyFillsAsOneStep()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        ColorRgba legacyFill = ColorRgba.FromRgb(10, 20, 30);
        var rectangle = new RectangleAnnotation { Rect = new RectD(10, 10, 40, 40), Stroke = ColorRgba.Black, Fill = legacyFill };
        var ellipse = new EllipseAnnotation { Rect = new RectD(100, 100, 40, 40), Stroke = ColorRgba.White };
        var text = new TextAnnotation { Rect = new RectD(200, 200, 40, 40), Background = legacyFill };
        Add(doc, rectangle, ellipse, text);
        c.SelectAll();

        c.ApplyFillTransparency(50);

        Assert.Equal(ColorRgba.Black.WithAlpha(128), rectangle.Fill);
        Assert.Equal(ColorRgba.White.WithAlpha(128), ellipse.Fill);
        Assert.True(rectangle.FillMatchesStroke);
        Assert.True(ellipse.FillMatchesStroke);
        Assert.Equal(legacyFill, text.Background);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(legacyFill, rectangle.Fill);
        Assert.Equal(ColorRgba.Transparent, ellipse.Fill);
        Assert.False(rectangle.FillMatchesStroke);
        Assert.False(ellipse.FillMatchesStroke);
    }

    [Fact]
    public void ApplyingUnchangedGroupStyles_DoesNotCreateHistory()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        var first = new RectangleAnnotation
        {
            Rect = new RectD(100, 100, 40, 40), Stroke = ColorRgba.Black,
            Fill = ColorRgba.Black.WithAlpha(128), FillMatchesStroke = true, StrokeThickness = 3,
        };
        var second = new EllipseAnnotation
        {
            Rect = new RectD(200, 200, 40, 40), Stroke = ColorRgba.Black,
            Fill = ColorRgba.Black.WithAlpha(128), FillMatchesStroke = true, StrokeThickness = 3,
        };
        Add(doc, first, second);
        c.SelectAll();

        c.ApplyStrokeColor(ColorRgba.Black);
        c.ApplyStrokeThickness(3);
        c.ApplyStrokeStyle(AnnotationStrokeStyle.Solid);
        c.ApplyFillTransparency(50);

        Assert.Equal(0, undo.UndoCount);
    }

    [Fact]
    public void ContinuousGroupThickness_CoalescesChangingCommandCountsAndRestoresEachOriginalValue()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        var rectangle = new RectangleAnnotation { Rect = new RectD(100, 100, 40, 40), StrokeThickness = 2 };
        var line = new PolylineAnnotation { Points = [new PointD(200, 200), new PointD(240, 200)], StrokeThickness = 4 };
        var pen = new PenAnnotation { Points = [new PointD(300, 300), new PointD(340, 340)], StrokeThickness = 6 };
        Add(doc, rectangle, line, pen);
        c.SelectAll();

        // The first edit changes two items; the later edits change all three.
        c.ApplyStrokeThickness(4);
        c.ApplyStrokeThickness(8);
        c.ApplyStrokeThickness(10);

        Assert.Equal(10, rectangle.StrokeThickness);
        Assert.Equal(10, line.StrokeThickness);
        Assert.Equal(10, pen.StrokeThickness);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(2, rectangle.StrokeThickness);
        Assert.Equal(4, line.StrokeThickness);
        Assert.Equal(6, pen.StrokeThickness);
        Assert.Equal(0, undo.UndoCount);
        Assert.True(c.PerformRedo());
        Assert.Equal(10, rectangle.StrokeThickness);
        Assert.Equal(10, line.StrokeThickness);
        Assert.Equal(10, pen.StrokeThickness);
    }

    [Fact]
    public void ContinuousGroupColor_CoalescesAndRestoresLinkedFills()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        var rectangle = new RectangleAnnotation
        {
            Rect = new RectD(100, 100, 40, 40), Stroke = ColorRgba.Black,
            Fill = ColorRgba.Black.WithAlpha(128), FillMatchesStroke = true,
        };
        var ellipse = new EllipseAnnotation
        {
            Rect = new RectD(200, 200, 40, 40), Stroke = ColorRgba.White,
            Fill = ColorRgba.White.WithAlpha(64), FillMatchesStroke = true,
        };
        Add(doc, rectangle, ellipse);
        c.SelectAll();
        ColorRgba final = ColorRgba.FromRgb(20, 40, 60);

        c.ApplyStrokeColor(ColorRgba.White);
        c.ApplyStrokeColor(final);

        Assert.Equal(final, rectangle.Stroke);
        Assert.Equal(final, ellipse.Stroke);
        Assert.Equal(final.WithAlpha(128), rectangle.Fill);
        Assert.Equal(final.WithAlpha(64), ellipse.Fill);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(ColorRgba.Black, rectangle.Stroke);
        Assert.Equal(ColorRgba.White, ellipse.Stroke);
        Assert.Equal(ColorRgba.Black.WithAlpha(128), rectangle.Fill);
        Assert.Equal(ColorRgba.White.WithAlpha(64), ellipse.Fill);
        Assert.True(c.PerformRedo());
        Assert.Equal(final.WithAlpha(128), rectangle.Fill);
        Assert.Equal(final.WithAlpha(64), ellipse.Fill);
    }

    [Fact]
    public void ContinuousGroupStrokeStyle_CoalescesAndRestoresOriginalThickness()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        var rectangle = new RectangleAnnotation { Rect = new RectD(100, 100, 40, 40), StrokeThickness = 2 };
        var ellipse = new EllipseAnnotation
        {
            Rect = new RectD(200, 200, 40, 40), StrokeThickness = 8, StrokeStyle = AnnotationStrokeStyle.Dashed,
        };
        Add(doc, rectangle, ellipse);
        c.SelectAll();

        c.ApplyStrokeStyle(AnnotationStrokeStyle.Dashed);
        c.ApplyStrokeStyle(AnnotationStrokeStyle.Dotted);
        c.ApplyStrokeStyle(AnnotationStrokeStyle.ThickDashed);

        Assert.Equal(AnnotationStrokeStyle.ThickDashed, rectangle.StrokeStyle);
        Assert.Equal(AnnotationStrokeStyle.ThickDashed, ellipse.StrokeStyle);
        Assert.Equal(6, rectangle.StrokeThickness);
        Assert.Equal(8, ellipse.StrokeThickness);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(AnnotationStrokeStyle.Solid, rectangle.StrokeStyle);
        Assert.Equal(AnnotationStrokeStyle.Dashed, ellipse.StrokeStyle);
        Assert.Equal(2, rectangle.StrokeThickness);
        Assert.Equal(8, ellipse.StrokeThickness);
        Assert.True(c.PerformRedo());
        Assert.Equal(AnnotationStrokeStyle.ThickDashed, rectangle.StrokeStyle);
        Assert.Equal(AnnotationStrokeStyle.ThickDashed, ellipse.StrokeStyle);
        Assert.Equal(6, rectangle.StrokeThickness);
    }

    [Fact]
    public void ContinuousGroupTransparency_CoalescesAndRestoresLegacyFillState()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        ColorRgba legacyFill = ColorRgba.FromRgb(10, 20, 30);
        var rectangle = new RectangleAnnotation { Rect = new RectD(100, 100, 40, 40), Stroke = ColorRgba.Black, Fill = legacyFill };
        var ellipse = new EllipseAnnotation
        {
            Rect = new RectD(200, 200, 40, 40), Stroke = ColorRgba.White,
            Fill = ColorRgba.White.WithAlpha(128), FillMatchesStroke = true,
        };
        Add(doc, rectangle, ellipse);
        c.SelectAll();

        c.ApplyFillTransparency(50);
        c.ApplyFillTransparency(25);
        c.ApplyFillTransparency(0);

        Assert.Equal(ColorRgba.Black, rectangle.Fill);
        Assert.Equal(ColorRgba.White, ellipse.Fill);
        Assert.True(rectangle.FillMatchesStroke);
        Assert.True(ellipse.FillMatchesStroke);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Equal(legacyFill, rectangle.Fill);
        Assert.Equal(ColorRgba.White.WithAlpha(128), ellipse.Fill);
        Assert.False(rectangle.FillMatchesStroke);
        Assert.True(ellipse.FillMatchesStroke);
        Assert.True(c.PerformRedo());
        Assert.Equal(ColorRgba.Black, rectangle.Fill);
        Assert.Equal(ColorRgba.White, ellipse.Fill);
        Assert.True(rectangle.FillMatchesStroke);
    }

    [Fact]
    public void GroupStyleEdits_WithDifferentSelectionSetsDoNotMerge()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        var first = new RectangleAnnotation { Rect = new RectD(100, 100, 40, 40), Stroke = ColorRgba.Black };
        var second = new RectangleAnnotation { Rect = new RectD(200, 200, 40, 40), Stroke = ColorRgba.Black };
        var third = new RectangleAnnotation { Rect = new RectD(300, 300, 40, 40), Stroke = ColorRgba.Black };
        Add(doc, first, second, third);
        c.SetSelected(first);
        c.ToggleSelected(second);
        ColorRgba middle = ColorRgba.FromRgb(20, 40, 60);

        c.ApplyStrokeColor(middle);
        c.SetSelected(second);
        c.ToggleSelected(third);
        c.ApplyStrokeColor(ColorRgba.White);

        Assert.Equal(2, undo.UndoCount);
        Assert.Equal(middle, first.Stroke);
        Assert.Equal(ColorRgba.White, second.Stroke);
        Assert.Equal(ColorRgba.White, third.Stroke);
        Assert.True(c.PerformUndo());
        Assert.Equal(middle, first.Stroke);
        Assert.Equal(middle, second.Stroke);
        Assert.Equal(ColorRgba.Black, third.Stroke);
        Assert.True(c.PerformUndo());
        Assert.Equal(ColorRgba.Black, first.Stroke);
        Assert.Equal(ColorRgba.Black, second.Stroke);
        Assert.Equal(ColorRgba.Black, third.Stroke);
    }

    [Fact]
    public void GroupStyleEdits_WithDifferentPropertyFamiliesDoNotMerge()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo, TimeSpan.FromMinutes(1));
        var first = new RectangleAnnotation { Rect = new RectD(100, 100, 40, 40), Stroke = ColorRgba.Black, StrokeThickness = 2 };
        var second = new EllipseAnnotation { Rect = new RectD(200, 200, 40, 40), Stroke = ColorRgba.Black, StrokeThickness = 4 };
        Add(doc, first, second);
        c.SelectAll();

        c.ApplyStrokeColor(ColorRgba.White);
        c.ApplyStrokeThickness(8);

        Assert.Equal(2, undo.UndoCount);
        Assert.Equal(ColorRgba.White, first.Stroke);
        Assert.Equal(ColorRgba.White, second.Stroke);
        Assert.Equal(8, first.StrokeThickness);
        Assert.Equal(8, second.StrokeThickness);
        Assert.True(c.PerformUndo());
        Assert.Equal(ColorRgba.White, first.Stroke);
        Assert.Equal(ColorRgba.White, second.Stroke);
        Assert.Equal(2, first.StrokeThickness);
        Assert.Equal(4, second.StrokeThickness);
        Assert.True(c.PerformUndo());
        Assert.Equal(ColorRgba.Black, first.Stroke);
        Assert.Equal(ColorRgba.Black, second.Stroke);
    }

    [Theory]
    [InlineData(false, 30, 10, 100, 100)]
    [InlineData(false, -30, 10, 70, 100)]
    [InlineData(true, 10, 30, 100, 100)]
    [InlineData(true, 10, -30, 100, 70)]
    public void ShiftDrawing_MakesSquareOrCircleInEveryDirection(bool ellipse, double dx, double dy, double x, double y)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        c.Tool = ellipse ? EditorTool.Ellipse : EditorTool.Rectangle;

        c.PointerDown(new PointD(100, 100), shift: true);
        c.PointerMove(new PointD(100 + dx, 100 + dy), shift: true);
        c.PointerUp(new PointD(100 + dx, 100 + dy), shift: true);

        var shape = Assert.IsAssignableFrom<ShapeAnnotation>(Assert.Single(doc.Items));
        Assert.Equal(new RectD(x, y, 30, 30), shape.Rect);
        Assert.Equal(ellipse, shape is EllipseAnnotation);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(c.PerformUndo());
        Assert.Empty(doc.Items);
    }

    [Theory]
    [InlineData(30, 4, 0)]
    [InlineData(30, 20, 45)]
    [InlineData(4, 30, 90)]
    [InlineData(-30, 20, 135)]
    [InlineData(-30, 4, 180)]
    [InlineData(30, -20, -45)]
    public void ShiftArrow_SnapsToNearest45DegreesAndPreservesLength(double dx, double dy, double degrees)
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        c.Tool = EditorTool.Arrow;

        c.PointerDown(new PointD(100, 100));
        c.PointerMove(new PointD(100 + dx, 100 + dy), shift: true);
        c.PointerUp(new PointD(100 + dx, 100 + dy), shift: true);

        var line = Assert.IsType<PolylineAnnotation>(Assert.Single(doc.Items));
        double length = Math.Sqrt(dx * dx + dy * dy);
        double angle = degrees * Math.PI / 180;
        Assert.Equal(new PointD(100, 100), line.Points[0]);
        Assert.Equal(100 + length * Math.Cos(angle), line.Points[^1].X, 6);
        Assert.Equal(100 + length * Math.Sin(angle), line.Points[^1].Y, 6);
        Assert.Equal(1, undo.UndoCount);
    }

    [Fact]
    public void Drawing_ReleasingShiftRestoresUnconstrainedShape()
    {
        AnnotationEditorController c = NewController(out AnnotationDocument doc, out UndoStack undo);
        c.Tool = EditorTool.Rectangle;

        c.PointerDown(new PointD(100, 100));
        c.PointerMove(new PointD(130, 110), shift: true);
        Assert.Equal(new RectD(100, 100, 30, 30), Assert.IsType<RectangleAnnotation>(Assert.Single(doc.Items)).Rect);

        c.PointerUp(new PointD(130, 110));

        Assert.Equal(new RectD(100, 100, 30, 10), Assert.IsType<RectangleAnnotation>(Assert.Single(doc.Items)).Rect);
        Assert.Equal(1, undo.UndoCount);
    }
}
