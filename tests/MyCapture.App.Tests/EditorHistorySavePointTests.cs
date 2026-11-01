using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorHistorySavePointTests
{
    [Fact]
    public void ClosingAfterUndoingFiveHundredRetainedStepsStillAsksAboutRemainingEdits() => StaTestHost.Run(() =>
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(900, 520);
        var shape = new RectangleAnnotation { Rect = new RectD(100, 100, 120, 80) };
        document.Add(shape);
        using EditorWindowHost host = EditorWindowHost.Create(Solid(900, 520, 0x30), document);
        host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
        AnnotationEditorController controller = Controller(host.Editor);
        controller.SetSelected(shape);
        for (int i = 0; i < 510; i++) controller.NudgeSelected(1, 0);
        Assert.Equal(500, controller.Undo.UndoCount);
        for (int i = 0; i < 500; i++) Assert.True(controller.PerformUndo());

        Assert.Equal(110, shape.Rect.X);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.True(host.Editor.HasUnsavedChanges);
        int asked = 0;
        host.Window.ConfirmClose = () => { asked++; return EditorCloseChoice.Cancel; };
        host.Window.RequestClose();

        Assert.Equal(1, asked);
        Assert.True(host.Window.IsVisible);
        host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
    });

    [Fact]
    public void UndoAndRedoAroundASavePointDriveEditorDirtyStateWithoutUsingDepth() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.ConfirmClose = () => EditorCloseChoice.DontSave;
        AnnotationEditorController controller = Controller(host.Editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(100, 100));
        controller.PointerMove(new PointD(200, 180));
        controller.PointerUp(new PointD(200, 180));
        Assert.True(host.Editor.HasUnsavedChanges);
        controller.Undo.MarkSaved();
        Assert.False(host.Editor.HasUnsavedChanges);

        Assert.True(controller.PerformUndo());
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.True(host.Editor.HasUnsavedChanges);
        Assert.True(controller.PerformRedo());
        Assert.False(host.Editor.HasUnsavedChanges);
    });
}
