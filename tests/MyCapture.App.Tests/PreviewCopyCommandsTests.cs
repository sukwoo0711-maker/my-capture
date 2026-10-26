using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Preview;
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
    public void EveryPreviewHostTextBoxHasTheMemoryCommandBoundary() => StaTestHost.Run(() =>
    {
        using var session = new PinPreviewSession(new PreviewLaunchOptions(@"C:\SyntheticPreview\profile", "synthetic"),
            Dispatcher.CurrentDispatcher);
        // Build the host controls only. Start() would create a profile and named handles.
        var window = Assert.IsType<Window>(typeof(PinPreviewSession)
            .GetMethod("CreateWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null));
        try
        {
            var layout = Assert.IsType<Grid>(window.Content);
            var tabs = Assert.Single(layout.Children.OfType<TabControl>());
            TextBox[] inputs = tabs.Items.OfType<TabItem>().Select(tab => Assert.IsType<TextBox>(tab.Content)).ToArray();
            TextBox result = Assert.Single(layout.Children.OfType<TextBox>());
            Assert.Equal(2, inputs.Length);
            inputs[0].SelectAll();
            Execute(inputs[0], ApplicationCommands.Copy);
            Assert.Equal(inputs[0].Text, result.Text);
            inputs[1].SelectAll();
            Execute(inputs[1], ApplicationCommands.Paste);
            Assert.Equal(inputs[0].Text, inputs[1].Text);
            result.SelectAll();
            Assert.True(CanExecute(result, ApplicationCommands.Copy));
            Assert.False(CanExecute(result, ApplicationCommands.Cut));
            Assert.False(CanExecute(result, ApplicationCommands.Paste));
            foreach (TextBox editor in inputs.Append(result)) Assert.Equal(3, editor.ContextMenu.Items.Count);
        }
        finally { window.Close(); }
    });

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
