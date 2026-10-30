using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorTypingSaveShortcutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SaveCommitsTypingBeforeInvokingNormalSave(bool saveAs, bool succeeds) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        Field<TextBox>(host.Editor, "_activeTextBox").Text = "저장할 내용\nsecond line";
        int calls = 0;
        host.Window.CommitRequested = result =>
        {
            calls++;
            Assert.Null(Field<TextBox?>(host.Editor, "_activeTextBox"));
            Assert.Equal(saveAs ? EditorCommitAction.SaveAs : EditorCommitAction.QuickSave, result.Action);
            Assert.Equal("저장할 내용\nsecond line", Assert.IsType<TextAnnotation>(Assert.Single(result.Document.Items)).Text);
            Assert.Equal(1, Controller(host.Editor).Undo.UndoCount);
            return Task.FromResult(succeeds);
        };

        Assert.True(host.Editor.HandleShortcut(Key.S, ModifierKeys.Control | (saveAs ? ModifierKeys.Shift : ModifierKeys.None)));
        host.Editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, calls);
        Assert.Equal(succeeds, host.Window.WasCommitted);
        Assert.Equal(!succeeds, host.Window.IsVisible);
        if (!succeeds)
        {
            Assert.True(host.Editor.HasUnsavedChanges);
            Assert.True(Controller(host.Editor).PerformUndo());
            Assert.Empty(Controller(host.Editor).Document.Items);
        }
    });

    [Fact]
    public void CopyEnterAndImeRemainTextInputAndEscapeCancelsOnlyTyping() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        TextBox box = Field<TextBox>(host.Editor, "_activeTextBox");
        box.Text = "입력 중";
        int calls = 0;
        host.Window.CommitRequested = _ => { calls++; return Task.FromResult(false); };

        Assert.False(host.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
        Assert.False(host.Editor.HandleShortcut(Key.Enter, ModifierKeys.None));
        Assert.False(Press(box, Key.Escape, ime: true));
        Assert.Same(box, Field<TextBox>(host.Editor, "_activeTextBox"));
        Assert.Equal("입력 중", box.Text);
        Assert.Equal(0, calls);
        Assert.True(host.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Null(Field<TextBox?>(host.Editor, "_activeTextBox"));
        Assert.Empty(Controller(host.Editor).Document.Items);
        Assert.True(host.Window.IsVisible);
    });
}
