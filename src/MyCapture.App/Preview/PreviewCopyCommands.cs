using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App.Preview;

/// <summary>All host editing commands use the preview's own buffer, including its context menu.</summary>
internal static class PreviewCopyCommands
{
    internal static void Attach(TextBox editor, PreviewCopyBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(buffer);
        CommandManager.AddPreviewCanExecuteHandler(editor, (_, args) =>
        {
            if (!IsCopyCommand(args.Command)) return;
            // Handle disabled commands too: otherwise TextBox's native handler can inspect
            // the Windows clipboard to decide whether Paste is available.
            args.CanExecute = CanExecute(editor, buffer, args.Command);
            args.ContinueRouting = false;
            args.Handled = true;
        });
        CommandManager.AddPreviewExecutedHandler(editor, (_, args) =>
        {
            if (!IsCopyCommand(args.Command)) return;
            args.Handled = true;
            Execute(editor, buffer, args.Command);
        });

        var menu = new ContextMenu();
        foreach (RoutedUICommand command in new[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste })
            menu.Items.Add(new MenuItem { Header = command.Text, Command = command, CommandTarget = editor });
        editor.ContextMenu = menu;
    }

    private static bool IsCopyCommand(ICommand command) => command == ApplicationCommands.Copy
        || command == ApplicationCommands.Cut || command == ApplicationCommands.Paste;

    private static bool CanExecute(TextBox editor, PreviewCopyBuffer buffer, ICommand command)
    {
        if (!editor.IsEnabled) return false;
        if (command == ApplicationCommands.Copy) return editor.SelectionLength > 0;
        if (editor.IsReadOnly) return false;
        return command == ApplicationCommands.Cut ? editor.SelectionLength > 0 : buffer.Text.Length > 0;
    }

    private static void Execute(TextBox editor, PreviewCopyBuffer buffer, ICommand command)
    {
        if (!CanExecute(editor, buffer, command)) return;
        if (command == ApplicationCommands.Copy)
        {
            _ = buffer.CopyTextAsync(editor.SelectedText);
            return;
        }
        if (command == ApplicationCommands.Cut)
        {
            string selected = editor.SelectedText;
            editor.SelectedText = string.Empty;
            _ = buffer.CopyTextAsync(selected);
            return;
        }
        int start = editor.SelectionStart;
        string text = buffer.Text;
        editor.BeginChange();
        try
        {
            editor.SelectedText = text;
            editor.Select(Math.Min(start + text.Length, editor.Text.Length), 0);
        }
        finally { editor.EndChange(); }
    }
}
