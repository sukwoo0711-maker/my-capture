using System.Windows;

namespace MyCapture.App.Capture;

/// <summary>
/// Already-open editors contribute their managed state to the capture guard. Reading these
/// callbacks does not enumerate desktop windows, acquire handles or wait for rendering.
/// </summary>
internal static class CaptureEditorRegistry
{
    [ThreadStatic]
    private static List<Registration>? _readers;

    private sealed record Registration(Func<CaptureGuardState?> ReadState, Func<bool> IsVisible);

    internal static void Track<T>(T editor, Func<T, CaptureGuardState> readState) where T : Window
    {
        var reference = new WeakReference<T>(editor);
        CaptureGuardState? Read() => reference.TryGetTarget(out T? window) && window.IsLoaded
            ? readState(window) : null;
        bool IsVisible() => reference.TryGetTarget(out T? window) && window.IsVisible
            && window.WindowState != WindowState.Minimized;
        var registration = new Registration(Read, IsVisible);
        List<Registration> readers = _readers ??= [];
        readers.Add(registration);
        editor.Closed += (_, _) => readers.Remove(registration);
    }

    // Delivery policy only: hidden/minimized editors still contribute their busy state to the guard.
    internal static bool HasVisibleEditor => _readers?.Any(reader => reader.IsVisible()) == true;

    internal static CaptureGuardState WithEditors(CaptureGuardState state)
    {
        if (_readers is null) return state;
        foreach (Registration reader in _readers)
        {
            if (reader.ReadState() is not { } editor) continue;
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
