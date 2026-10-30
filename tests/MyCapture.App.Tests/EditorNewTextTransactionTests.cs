using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorNewTextTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardedDraftPreservesBothUndoAndRedo(bool cancel)
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var undo = new UndoStack();
        var controller = new AnnotationEditorController(document, undo) { TextFitter = Measure };
        var first = new RectangleAnnotation { Rect = new RectD(400, 30, 100, 100) };
        var second = new EllipseAnnotation { Rect = new RectD(600, 30, 100, 100) };
        undo.Execute(new AddAnnotationCommand(document, first));
        undo.Execute(new AddAnnotationCommand(document, second));
        Assert.True(controller.PerformUndo());
        string before = document.ToJson();
        string? nextUndo = undo.NextUndoDescription;
        string? nextRedo = undo.NextRedoDescription;

        TextAnnotation draft = controller.BeginTextAnnotation(new PointD(30, 30), 100, 40);
        controller.ApplyFontSize(36);
        controller.ApplyTextBold(true);
        controller.ApplyTextBackground(true);
        controller.ApplyTextFontFamily("Consolas");
        controller.ApplyStrokeColor(ColorRgba.White);
        Assert.Equal(1, undo.UndoCount);
        Assert.Equal(1, undo.RedoCount);
        Assert.Equal(nextUndo, undo.NextUndoDescription);
        Assert.Equal(nextRedo, undo.NextRedoDescription);

        if (cancel) controller.CancelTextEdit(draft);
        else controller.CommitTextEdit(draft, string.Empty);

        Assert.Equal(before, document.ToJson());
        Assert.Equal(1, undo.UndoCount);
        Assert.Equal(1, undo.RedoCount);
        Assert.Null(controller.EditingText);
        Assert.False(controller.IsCreatingText);
        Assert.Null(controller.Selected);
        Assert.True(controller.PerformRedo());
        Assert.Same(second, document.Items[1]);
        Assert.True(controller.PerformUndo());
        Assert.True(controller.PerformUndo());
        Assert.Empty(document.Items);
    }

    [Fact]
    public void FirstCommitAddsFinalContentStyleAndLayoutInOneUndoStep()
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var undo = new UndoStack();
        var controller = new AnnotationEditorController(document, undo) { TextFitter = Measure };
        TextAnnotation draft = controller.BeginTextAnnotation(new PointD(30, 30), 100, 40);
        controller.ApplyFontSize(36);
        controller.ApplyTextBold(true);
        controller.ApplyTextBackground(true);
        controller.ApplyTextFontFamily("Consolas");

        Assert.Equal(0, undo.UndoCount);
        controller.CommitTextEdit(draft, "검토할 항목");

        Assert.Equal(1, undo.UndoCount);
        Assert.Same(draft, Assert.Single(document.Items));
        Assert.Equal("검토할 항목", draft.Text);
        Assert.Equal(36, draft.FontSize);
        Assert.True(draft.Bold);
        Assert.Equal(ColorRgba.Black, draft.Background);
        Assert.Equal("Consolas", draft.FontFamily);
        RectD measured = Measure(draft);
        Assert.Equal(measured.X, draft.Rect.X, 8);
        Assert.Equal(measured.Y, draft.Rect.Y, 8);
        Assert.Equal(measured.Width, draft.Rect.Width, 8);
        Assert.Equal(measured.Height, draft.Rect.Height, 8);
        string committed = document.ToJson();
        Assert.True(controller.PerformUndo());
        Assert.Empty(document.Items);
        Assert.Null(controller.Selected);
        Assert.False(controller.PerformUndo());
        Assert.True(controller.PerformRedo());
        Assert.Equal(committed, document.ToJson());
        Assert.False(controller.PerformRedo());
    }

    [Fact]
    public void OnlyAContentCommitInvalidatesTheOldRedoBranch()
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var undo = new UndoStack();
        var controller = new AnnotationEditorController(document, undo) { TextFitter = Measure };
        undo.Execute(new AddAnnotationCommand(document, new RectangleAnnotation { Rect = new RectD(600, 20, 80, 60) }));
        Assert.True(controller.PerformUndo());
        TextAnnotation text = controller.BeginTextAnnotation(new PointD(30, 30), 100, 40);
        Assert.Equal(1, undo.RedoCount);

        controller.CommitTextEdit(text, "새 글");

        Assert.Equal(0, undo.RedoCount);
        Assert.Equal(1, undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Empty(document.Items);
        Assert.True(controller.PerformRedo());
        Assert.Same(text, Assert.Single(document.Items));
    }

    [Fact]
    public void ReeditingCommittedTextRemainsASeparateStep()
    {
        var document = AnnotationDocument.CreateFor(900, 520);
        var controller = new AnnotationEditorController(document, new UndoStack()) { TextFitter = Measure };
        TextAnnotation text = controller.BeginTextAnnotation(new PointD(30, 30), 100, 40);
        controller.CommitTextEdit(text, "Original");
        RectD originalBounds = text.Rect;
        Assert.True(controller.BeginTextEdit(text));
        controller.CommitTextEdit(text, "Revised content");

        Assert.Equal(2, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Same(text, Assert.Single(document.Items));
        Assert.Equal("Original", text.Text);
        Assert.Equal(originalBounds, text.Rect);
        Assert.True(controller.PerformUndo());
        Assert.Empty(document.Items);
    }

    private static RectD Measure(TextAnnotation text) => new(text.Rect.X, text.Rect.Y,
        text.Text.Length * text.FontSize * (text.Bold ? 0.7 : 0.5), text.FontSize * 1.4);
}
