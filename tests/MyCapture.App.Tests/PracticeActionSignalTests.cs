using System.Windows;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class PracticeActionSignalTests
{
    [Theory]
    [InlineData(ArrowHeadStyle.End)]
    [InlineData(ArrowHeadStyle.Both)]
    [InlineData(ArrowHeadStyle.None)]
    public void ArrowSignalRequiresACompletedNewGesture(ArrowHeadStyle heads)
    {
        var document = AnnotationDocument.CreateFor(800, 600);
        document.Add(new PolylineAnnotation { Points = [new(10, 10), new(50, 40)] });
        var controller = new AnnotationEditorController(document, new UndoStack());
        int arrows = 0;
        controller.UserArrowAdded += () => arrows++;
        controller.ApplyArrowHeads(heads);
        Assert.Equal(0, arrows);

        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new(100, 100));
        controller.PointerUp(new(180, 160));
        Assert.Equal(0, arrows);

        controller.Tool = EditorTool.Arrow;
        controller.PointerDown(new(200, 200));
        controller.PointerMove(new(300, 250));
        Assert.Equal(0, arrows);
        controller.CancelGesture();
        Assert.Equal(0, arrows);
        controller.PointerDown(new(200, 200));
        controller.PointerUp(new(200, 200));
        Assert.Equal(0, arrows);

        controller.PointerDown(new(200, 200));
        controller.PointerUp(new(300, 250));
        Assert.Equal(1, arrows);
        Assert.Equal(3, document.Items.Count);
        Assert.True(controller.PerformUndo());
        Assert.True(controller.PerformRedo());
        controller.DuplicateSelected();
        Assert.Equal(1, arrows);
    }

    [Fact]
    public void EditorPublishesTheArrowKeyBeforeTheActualArrowIsDrawn() => StaTestHost.Run(() =>
    {
        int shortcuts = 0, arrows = 0;
        void Shortcut() => shortcuts++;
        void Arrow() => arrows++;
        PracticeSignals.ArrowShortcutFired += Shortcut;
        PracticeSignals.ArrowAdded += Arrow;
        try
        {
            using var host = new InspectorContractHost();
            Assert.Equal(0, arrows);
            Assert.Equal(0, shortcuts);
            Keyboard.ClearFocus();
            Assert.True(host.Editor.HandleShortcut(Key.A, ModifierKeys.None));
            Assert.Equal(1, shortcuts);
            Assert.Equal(0, arrows);

            Point start = host.Editor.ViewportPointOf(new PointD(120, 120));
            Point end = host.Editor.ViewportPointOf(new PointD(280, 220));
            host.Editor.SimulatePointerDown(start);
            host.Editor.SimulatePointerMove(end, leftPressed: true);
            Assert.Equal(0, arrows);
            host.Editor.SimulatePointerUp(end);
            Assert.Equal(1, arrows);
            Assert.IsType<PolylineAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));

            Assert.True(host.Editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            Assert.True(host.Editor.HandleShortcut(Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Equal(1, arrows);
            Assert.Equal(1, shortcuts);
        }
        finally
        {
            PracticeSignals.ArrowShortcutFired -= Shortcut;
            PracticeSignals.ArrowAdded -= Arrow;
        }
    });
}
