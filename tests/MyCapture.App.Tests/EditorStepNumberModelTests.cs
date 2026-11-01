using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorStepNumberModelTests
{
    private static AnnotationEditorController Controller(out AnnotationDocument doc, out UndoStack undo, int width = 900, int height = 520)
    {
        doc = AnnotationDocument.CreateFor(width, height);
        undo = new UndoStack(mergeWindow: TimeSpan.Zero);
        return new(doc, undo) { Tool = EditorTool.StepNumber };
    }

    [Theory]
    [InlineData(.77)] [InlineData(1)] [InlineData(3.52)]
    public void BadgeCornersResizeProportionallyBodyMovesAndOtherLocationsAdd(double scale)
    {
        var c = Controller(out var doc, out var undo);
        c.DipPerPixel = scale; c.HitTolerance = 6 / scale;
        var badge = c.AddStepNumber(new(200, 150));
        RectD initial = badge.Rect;
        Assert.Equal(new PointD(200, 150), initial.Center);
        Assert.Equal(4, c.GetSelectionHandles(scale).Count);
        SelectionHandle corner = c.GetSelectionHandles(scale).Single(h => h.Handle == ResizeHandle.BottomRight);
        c.PointerDown(corner.Position); c.PointerUp(corner.Position.Offset(24, 12));
        Assert.Single(doc.Items);
        Assert.Equal(54, badge.Rect.Width, 8); Assert.Equal(badge.Rect.Width, badge.Rect.Height);
        Assert.True(badge.FontSize > 18);
        Assert.True(c.PerformUndo()); Assert.Equal(initial, badge.Rect);
        c.PointerDown(badge.Rect.Center); c.PointerUp(badge.Rect.Center.Offset(10, 15));
        Assert.Equal(initial.Width, badge.Rect.Width); Assert.Equal(initial.Center.Offset(10, 15), badge.Rect.Center);
        c.PointerDown(new(500, 300)); c.PointerUp(new(500, 300));
        Assert.Equal(2, doc.Items.Count);
        // An unselected older badge is a new placement in Step mode.
        c.PointerDown(badge.Rect.Center); c.PointerUp(badge.Rect.Center);
        Assert.Equal(3, doc.Items.Count);
        Assert.True(undo.CanUndo);
    }

    [Theory]
    [InlineData(.77)] [InlineData(1)] [InlineData(3.52)]
    public void PlacementAtAllFourImageEdgesFitsAndSharesPreviewGeometry(double scale)
    {
        var c = Controller(out var doc, out _); c.DipPerPixel = scale;
        foreach (var point in new PointD[] { new(0, 0), new(900, 0), new(0, 520), new(900, 520) })
        {
            RectD? preview = c.StepNumberPreviewBounds(point);
            var badge = c.AddStepNumber(point);
            Assert.Equal(preview, badge.Rect);
            Assert.InRange(badge.Rect.Left, 0, 900); Assert.InRange(badge.Rect.Right, 0, 900);
            Assert.InRange(badge.Rect.Top, 0, 520); Assert.InRange(badge.Rect.Bottom, 0, 520);
            Assert.Equal(36, badge.Rect.Width);
        }
        Assert.Equal(4, doc.Items.Count);
    }

    [Fact]
    public void TinyCanvasCannotAddAndSmallCanvasConstrainsSize()
    {
        var tiny = Controller(out var smallDoc, out var smallUndo, 23, 100);
        Assert.False(tiny.CanAddStepNumber); Assert.Null(tiny.TryAddStepNumber(new(10, 10)));
        tiny.PointerDown(new(10, 10)); tiny.PointerUp(new(10, 10));
        Assert.Empty(smallDoc.Items); Assert.False(smallUndo.CanUndo);
        var c = Controller(out _, out _, 30, 100);
        Assert.Equal(30, c.AddStepNumber(new(15, 15)).Rect.Width);
        Assert.False(c.ApplyStepSize(31)); Assert.True(c.ApplyStepSize(24));
    }

    [Fact]
    public void ManualNextNumberAndSelectedNumberValidateAndAddUndoRestoresSequence()
    {
        var c = Controller(out var doc, out _);
        Assert.False(c.TrySetNextStepNumber(0)); Assert.False(c.TrySetNextStepNumber(1000));
        Assert.True(c.TrySetNextStepNumber(998));
        var first = c.AddStepNumber(new(100, 100));
        Assert.Equal("998", first.Text); Assert.Equal(999, c.NextStepNumber);
        c.AddStepNumber(new(200, 100));
        Assert.Equal(1000, c.NextStepNumber); Assert.False(c.CanAddStepNumber);
        Assert.Null(c.TryAddStepNumber(new(300, 100))); Assert.Equal(2, doc.Items.Count);
        Assert.True(c.PerformUndo()); Assert.Equal(999, c.NextStepNumber);
        Assert.True(c.PerformRedo()); Assert.Equal(1000, c.NextStepNumber);
        c.SetSelected(first);
        Assert.False(c.ApplySelectedStepNumber(1000)); Assert.Equal("998", first.Text);
        Assert.True(c.ApplySelectedStepNumber(7)); Assert.Equal("7", first.Text);
        Assert.True(c.PerformUndo()); Assert.Equal("998", first.Text);
    }

    [Fact]
    public void DuplicateAssignsConsecutiveNumbersAtomicallyAndUndoRestoresManualNext()
    {
        var c = Controller(out var doc, out var undo);
        c.AddStepNumber(new(100, 100)); c.AddStepNumber(new(200, 100));
        c.SelectAll(); c.TrySetNextStepNumber(998);
        c.DuplicateSelected();
        Assert.Equal(4, doc.Items.Count); Assert.Equal(new[] { "998", "999" }, c.SelectedItems.Cast<TextAnnotation>().Select(t => t.Text));
        Assert.Equal(1000, c.NextStepNumber);
        Assert.True(c.PerformUndo()); Assert.Equal(2, doc.Items.Count); Assert.Equal(998, c.NextStepNumber);
        c.SelectAll(); c.TrySetNextStepNumber(999);
        int count = undo.UndoCount; c.DuplicateSelected();
        Assert.Equal(2, doc.Items.Count); Assert.Equal(count, undo.UndoCount); Assert.Equal(999, c.NextStepNumber);
    }

    [Fact]
    public void RenumberUsesNumericThenPersistentIdentityOrderingAndOneUndoPreservesLegacy()
    {
        var c = Controller(out var doc, out var undo);
        var a = new TextAnnotation { IsStepNumber = true, Text = "9", Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Rect = new(100, 100, 36, 36) };
        var b = new TextAnnotation { IsStepNumber = true, Text = "9", Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Rect = new(200, 100, 36, 36) };
        var legacy = new TextAnnotation { IsStepNumber = true, Text = "A", Rect = new(300, 100, 36, 36) };
        var large = new TextAnnotation { IsStepNumber = true, Text = "1005", Rect = new(400, 100, 36, 36) };
        doc.Add(a); doc.Add(large); doc.Add(legacy); doc.Add(b);
        c.TrySetNextStepNumber(50);
        Assert.True(c.RenumberStepNumbers()); Assert.Equal(1, undo.UndoCount);
        Assert.Equal("1", b.Text); Assert.Equal("2", a.Text); Assert.Equal("3", large.Text); Assert.Equal("A", legacy.Text);
        Assert.Equal(50, c.NextStepNumber);
        Assert.True(c.PerformUndo()); Assert.Equal("9", a.Text); Assert.Equal("9", b.Text); Assert.Equal("1005", large.Text);
        Assert.Equal("A", legacy.Text); Assert.Equal(50, c.NextStepNumber);
        Assert.True(c.PerformRedo()); Assert.Equal("1", b.Text);
    }

    [Fact]
    public void MoreThan999IntegerBadgesCannotBeRenumbered()
    {
        var c = Controller(out var doc, out var undo);
        for (int n = 0; n < 1000; n++) doc.Add(new TextAnnotation { IsStepNumber = true, Text = "7" });
        Assert.False(c.CanRenumberStepNumbers); Assert.False(c.RenumberStepNumbers()); Assert.False(undo.CanUndo);
    }

    [Fact]
    public void StepSizeIsIndependentOfCaptionFontAndRetainedForNextBadge()
    {
        var c = Controller(out _, out _); c.DefaultFontSize = 80;
        var first = c.AddStepNumber(new(100, 100)); Assert.Equal(36, first.Rect.Width);
        Assert.True(c.ApplyStepSize(72)); Assert.Equal(72, first.Rect.Width); Assert.Equal(80, c.DefaultFontSize);
        var second = c.AddStepNumber(new(250, 100)); Assert.Equal(72, second.Rect.Width);
        Assert.True(c.ApplySelectedStepNumber(999)); Assert.InRange(second.FontSize, 10, 36);
    }

    [Fact]
    public void EnteringStepToolKeepsThePreviouslySelectedBadgeEditable()
    {
        var c = Controller(out _, out _);
        var badge = c.AddStepNumber(new(100, 100));
        c.Tool = EditorTool.Select; c.SetSelected(badge); c.Tool = EditorTool.StepNumber;
        Assert.Same(badge, c.Selected);
        Assert.Equal(PointerAction.Move, c.ResolvePointerIntent(badge.Rect.Center).Action);
    }

    [Fact]
    public void GroupSelectionCannotSilentlyEditOnlyItsPrimaryStepBadge()
    {
        var c = Controller(out _, out var undo);
        var first = c.AddStepNumber(new(100, 100));
        var second = c.AddStepNumber(new(200, 100));
        c.SelectAll(); int commands = undo.UndoCount;
        Assert.False(c.ApplySelectedStepNumber(7)); Assert.False(c.ApplyStepSize(72));
        Assert.Equal("1", first.Text); Assert.Equal("2", second.Text);
        Assert.Equal(36, first.Rect.Width); Assert.Equal(36, second.Rect.Width);
        Assert.Equal(commands, undo.UndoCount); Assert.Equal(36, c.StepSize);
    }
}
