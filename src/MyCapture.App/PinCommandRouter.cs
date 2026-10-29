using System.Windows;
using System.Windows.Interop;
using MyCapture.App.Editing;

namespace MyCapture.App;

/// <summary>Resolves the global pin command before any clipboard operation starts.</summary>
internal static class PinCommandRouter
{
    internal static AnnotationEditorWindow? FindForegroundEditor(IEnumerable<Window> windows, nint foreground) =>
        foreground == 0 ? null : windows.OfType<AnnotationEditorWindow>().FirstOrDefault(window =>
            new WindowInteropHelper(window).Handle == foreground && window.FrameStillImage is null);

    internal static void Execute(Func<Action?> foregroundEditor, Func<bool> selectorOpen,
        Action confirmSelector, Action pinClipboard)
    {
        if (foregroundEditor() is { } pinEditor) pinEditor();
        else if (selectorOpen()) confirmSelector();
        else pinClipboard();
    }
}
