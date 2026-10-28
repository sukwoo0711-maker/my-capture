using System.Windows;

namespace MyCapture.App.Capture;

/// <summary>
/// Already-open editors contribute their managed state to the capture guard. Reading these
/// callbacks does not enumerate desktop windows, acquire handles or wait for rendering.
/// </summary>
internal static class CaptureEditorRegistry
{
    [ThreadStatic]
    private static List<Func<CaptureGuardState?>>? _readers;

    internal static void Track<T>(T editor, Func<T, CaptureGuardState> readState) where T : Window
    {
        var reference = new WeakReference<T>(editor);
        CaptureGuardState? Read() => reference.TryGetTarget(out T? window) && window.IsLoaded
            ? readState(window) : null;
        List<Func<CaptureGuardState?>> readers = _readers ??= [];
        readers.Add(Read);
        editor.Closed += (_, _) => readers.Remove(Read);
    }

    internal static CaptureGuardState WithEditors(CaptureGuardState state)
    {
        if (_readers is null) return state;
        foreach (Func<CaptureGuardState?> read in _readers)
        {
            if (read() is not { } editor) continue;
            state = state with
            {
                EditorOpen = state.EditorOpen || editor.EditorOpen,
                EditorAsking = state.EditorAsking || editor.EditorAsking,
                EditorSaving = state.EditorSaving || editor.EditorSaving,
                EditorBusy = state.EditorBusy || editor.EditorBusy,
            };
        }
        return state;
    }
}
