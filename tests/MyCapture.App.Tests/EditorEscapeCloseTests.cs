using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorEscapeCloseTests
{
    [Fact]
    public void TopLevelEscapeClosesCleanEditorWithoutConfirmation() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "SelectTool", EditorTool.Select);
        int asks = 0;
        host.Window.ConfirmClose = () => { asks++; return EditorCloseChoice.Cancel; };
        Escape(host.Editor);
        Assert.False(host.Window.IsVisible);
        Assert.Equal(0, asks);
    });

    [Theory]
    [InlineData("Cancel", false)]
    [InlineData("DontSave", false)]
    [InlineData("SaveAndClose", false)]
    [InlineData("SaveAndClose", true)]
    public void DirtyEscapeUsesExistingCloseChoiceAndSaveResult(string choiceName, bool saveSucceeds) => StaTestHost.Run(() =>
    {
        var choice = Enum.Parse<EditorCloseChoice>(choiceName);
        using EditorWindowHost host = EditorWindowHost.Create();
        AddShape(host.Editor);
        Invoke(host.Editor, "SelectTool", EditorTool.Select);
        Controller(host.Editor).SetSelected(null);
        int asks = 0, commits = 0;
        host.Window.ConfirmClose = () => { asks++; return choice; };
        host.Window.CommitRequested = _ => { commits++; return Task.FromResult(saveSucceeds); };
        Escape(host.Editor);
        host.Editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, asks);
        Assert.Equal(choice == EditorCloseChoice.SaveAndClose ? 1 : 0, commits);
        bool closes = choice == EditorCloseChoice.DontSave || choice == EditorCloseChoice.SaveAndClose && saveSucceeds;
        Assert.Equal(!closes, host.Window.IsVisible);
        Assert.Equal(choice == EditorCloseChoice.SaveAndClose && saveSucceeds, host.Window.WasCommitted);
    });

    [Theory]
    [InlineData("text")]
    [InlineData("color")]
    [InlineData("crop")]
    [InlineData("selection")]
    [InlineData("tool")]
    [InlineData("mosaic-drag")]
    public void EscapeDismissesOneInnerLayerAndHeldKeyCannotClose(string layer) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        int closes = 0;
        editor.CloseRequested += (_, _) => closes++;
        host.Window.ConfirmClose = () => EditorCloseChoice.Cancel;
        switch (layer)
        {
            case "text": Invoke(editor, "PlaceTextBox", new PointD(40, 40)); Field<TextBox>(editor, "_activeTextBox").Text = "memo"; break;
            case "color": ColorInput(editor); break;
            case "crop": editor.HandleShortcut(Key.X, ModifierKeys.None); editor.PendingCrop!.SetWidth(101); break;
            case "selection": AddShape(editor); break;
            case "tool": Invoke(editor, "SelectTool", EditorTool.Rectangle); break;
            case "mosaic-drag":
                editor.HandleShortcut(Key.M, ModifierKeys.None);
                editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(100, 80)));
                editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(200, 180)), leftPressed: true);
                break;
        }
        Escape(editor);
        Assert.True(host.Window.IsVisible);
        Assert.Equal(0, closes);
        switch (layer)
        {
            case "text": Assert.Null(Field<TextBox?>(editor, "_activeTextBox")); break;
            case "color": Assert.False(editor.CustomColorPopupForTest.IsOpen); break;
            case "crop": Assert.Null(editor.PendingCrop); break;
            case "selection": Assert.Null(Controller(editor).Selected); break;
            case "tool": Assert.Equal(EditorTool.Select, Controller(editor).Tool); break;
            case "mosaic-drag":
                Assert.False(Field<bool>(editor, "_mosaicDragging"));
                editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(200, 180)));
                Assert.Empty(Controller(editor).Document.Items);
                break;
        }
        // Moving focus onto the editor must not turn repeat events into a second press.
        editor.Focus();
        for (int repeat = 0; repeat < 3; repeat++) Escape(editor, repeat: true);
        Assert.Equal(0, closes);
        Assert.True(host.Window.IsVisible);
    });

    [Fact]
    public void RepeatAtIdleNeverClosesButFreshPressDoes() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "SelectTool", EditorTool.Select);
        Escape(host.Editor, repeat: true);
        Assert.True(host.Window.IsVisible);
        Escape(host.Editor);
        Assert.False(host.Window.IsVisible);
    });

    private static void Escape(AnnotationEditorControl editor, bool repeat = false)
    {
        KeyEventArgs args = KeyArgs(editor, Key.Escape, false, Keyboard.PreviewKeyDownEvent);
        if (repeat)
        {
            typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
            Assert.True(args.IsRepeat);
        }
        Assert.True(editor.HandleKeyDown(args));
    }

    private static void AddShape(AnnotationEditorControl editor)
    {
        var controller = Controller(editor);
        controller.Tool = EditorTool.Rectangle;
        controller.PointerDown(new PointD(20, 20));
        controller.PointerMove(new PointD(120, 80));
        controller.PointerUp(new PointD(120, 80));
    }
}
