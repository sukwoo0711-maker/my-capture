using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Preview;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

// Raise preview routed events only. Native command execution never runs, even on a failed
// assertion, and these tests contain no Windows clipboard API, profile, or visible window.
public sealed class PreviewCopyCommandsTests
{
    [Fact]
    public void CopyAndCutTransferExactSelectionThroughTheMemoryBuffer() => StaTestHost.Run(() =>
    {
        var buffer = new PreviewCopyBuffer();
        var editor = Editor(buffer, "# 제목\r\n\r\n**Save your work**");
        string selected = "# 제목\r\n\r\n";
        editor.Select(0, selected.Length);
        Assert.True(CanExecute(editor, ApplicationCommands.Copy));
        Execute(editor, ApplicationCommands.Copy);
        Assert.Equal(selected, buffer.Text);
        Assert.Equal("# 제목\r\n\r\n**Save your work**", editor.Text);
        Assert.True(CanExecute(editor, ApplicationCommands.Cut));
        Execute(editor, ApplicationCommands.Cut);
        Assert.Equal(selected, buffer.Text);
        Assert.Equal("**Save your work**", editor.Text);
        Assert.False(CanExecute(editor, ApplicationCommands.Copy));
    });

    [Fact]
    public void PasteUsesOnlyTheMemoryBufferAndReplacesTheSelection() => StaTestHost.Run(() =>
    {
        var buffer = new PreviewCopyBuffer();
        var editor = Editor(buffer, "before value after");
        editor.Select(7, 5);
        Assert.False(CanExecute(editor, ApplicationCommands.Paste));
        Execute(editor, ApplicationCommands.Paste);
        Assert.Equal("before value after", editor.Text);
        _ = buffer.CopyTextAsync("**한글**");
        Assert.True(CanExecute(editor, ApplicationCommands.Paste));
        Execute(editor, ApplicationCommands.Paste);
        Assert.Equal("before **한글** after", editor.Text);
        Assert.Equal(13, editor.CaretIndex);
        Assert.Equal(0, editor.SelectionLength);
        Assert.Equal("**한글**", buffer.Text);
    });

    [Fact]
    public void ReadOnlyResultsCanCopyButCannotCutOrPaste() => StaTestHost.Run(() =>
    {
        var buffer = new PreviewCopyBuffer();
        var editor = Editor(buffer, "copied result");
        editor.IsReadOnly = true;
        editor.SelectAll();
        Execute(editor, ApplicationCommands.Copy);
        Assert.Equal("copied result", buffer.Text);
        foreach (RoutedUICommand command in new[] { ApplicationCommands.Cut, ApplicationCommands.Paste })
        {
            Assert.False(CanExecute(editor, command));
            Execute(editor, command);
            Assert.Equal("copied result", editor.Text);
        }
    });

    [Fact]
    public void ContextMenuTargetsTheSameInterceptedCommandsAcrossHostEditors() => StaTestHost.Run(() =>
    {
        var buffer = new PreviewCopyBuffer();
        var source = Editor(buffer, "# Markdown");
        var destination = Editor(buffer, "English source");
        source.SelectAll();
        MenuItem copy = Menu(source, ApplicationCommands.Copy);
        Assert.Same(source, copy.CommandTarget);
        Execute(source, Assert.IsType<RoutedUICommand>(copy.Command));
        destination.SelectAll();
        MenuItem paste = Menu(destination, ApplicationCommands.Paste);
        Assert.Same(destination, paste.CommandTarget);
        Assert.True(CanExecute(destination, Assert.IsType<RoutedUICommand>(paste.Command)));
        Execute(destination, Assert.IsType<RoutedUICommand>(paste.Command));
        Assert.Equal("# Markdown", destination.Text);
        Assert.Equal(3, destination.ContextMenu.Items.Count);
    });

    [Fact]
    public void HumanPreviewHostHasNoNativeEditingCommandsAndUsesOnlyInjectedPinInput() => StaTestHost.Run(() =>
    {
        int reads = 0;
        int writes = 0;
        var clipboard = new PreviewClipboardAccess(
            () => { reads++; return Task.FromResult(ClipboardImageReader.PinReadAttempt.NoContent()); },
            (_, _, _, _, _) => { writes++; throw new InvalidOperationException("No clipboard image write expected."); },
            _ => { writes++; throw new InvalidOperationException("No clipboard text write expected."); });
        var registrar = new FakeRegistrar();
        using var input = new PreviewPinInput(registrar, async () =>
        {
            await clipboard.ReadWhileActiveAsync(() => true);
            return PasteResult.NoSupportedContent;
        });
        // Construct only controls. No session Start(), native registration or user profile exists.
        var window = new PinPreviewWindow(input);
        try
        {
            Assert.Equal(0, reads);
            Assert.Empty(registrar.Attempts);
            input.RegisterShortcut();
            Assert.Equal(PreviewPinInput.Primary, Assert.Single(registrar.Attempts));
            DependencyObject[] controls = Descendants(window).ToArray();
            Assert.Empty(controls.OfType<TextBox>());
            Assert.Empty(controls.OfType<TabControl>());
            Assert.Contains(controls.OfType<TextBlock>(), text => text.Text == "활성 단축키 · F3");
            Button pin = Assert.Single(controls.OfType<Button>(), button => Equals(button.Content, "클립보드 핀"));
            Button close = Assert.Single(controls.OfType<Button>(), button => Equals(button.Content, "종료"));
            Assert.True(close.IsEnabled);
            Assert.True(pin.IsEnabled);
            pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, reads);
            Assert.Equal(0, writes);
            Assert.Contains(controls.OfType<TextBlock>(), text => text.Text == "텍스트나 이미지를 복사한 후 다시 시도하십시오.");
            Assert.True(input.HandleMessage(PreviewPinInput.WmHotkey, PreviewPinInput.HotkeyId));
            Assert.True(input.LastPinTask.IsCompletedSuccessfully);
            Assert.Equal(2, reads);
            Assert.Equal(0, writes);
            Assert.True(pin.IsEnabled);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject control) continue;
            yield return control;
            foreach (DependencyObject descendant in Descendants(control)) yield return descendant;
        }
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        internal List<Hotkey> Attempts { get; } = [];
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            Assert.Equal(PreviewPinInput.HotkeyId, id);
            Attempts.Add(hotkey);
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }
        public void Unregister(int id) => Assert.Equal(PreviewPinInput.HotkeyId, id);
    }
    private static TextBox Editor(PreviewCopyBuffer buffer, string text)
    {
        var editor = new TextBox { Text = text, AcceptsReturn = true };
        PreviewCopyCommands.Attach(editor, buffer);
        return editor;
    }

    private static MenuItem Menu(TextBox editor, RoutedUICommand command) =>
        Assert.Single(editor.ContextMenu.Items.OfType<MenuItem>(), item => item.Command == command);

    private static bool CanExecute(TextBox editor, RoutedUICommand command)
    {
        var args = CreateArgs<CanExecuteRoutedEventArgs>(command);
        args.RoutedEvent = CommandManager.PreviewCanExecuteEvent;
        editor.RaiseEvent(args);
        Assert.True(args.Handled);
        Assert.False(args.ContinueRouting);
        return args.CanExecute;
    }

    private static void Execute(TextBox editor, RoutedUICommand command)
    {
        var args = CreateArgs<ExecutedRoutedEventArgs>(command);
        args.RoutedEvent = CommandManager.PreviewExecutedEvent;
        editor.RaiseEvent(args);
        Assert.True(args.Handled);
    }

    private static T CreateArgs<T>(RoutedUICommand command) where T : RoutedEventArgs =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [command, null], culture: null)!;
}
