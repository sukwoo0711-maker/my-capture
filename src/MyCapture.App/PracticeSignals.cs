namespace MyCapture.App;

/// <summary>Real editor actions observed by optional practice guidance.</summary>
internal static class PracticeSignals
{
    internal static event Action<PracticeEvent>? Result;
    internal static event Action<Guid, Editing.AnnotationEditorControl>? EditorReady;
    internal static event Action<Guid>? PictureOpened;
    internal static event Action<Guid>? LibraryEditorOpened;
    internal static void Publish(PracticeEvent result)
    {
        if (Result is not { } observers) return;
        foreach (Action<PracticeEvent> observer in observers.GetInvocationList())
            try { observer(result); } catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(error); }
    }
    internal static void Ready(Guid recordId, Editing.AnnotationEditorControl editor)
    {
        if (EditorReady is not { } observers) return;
        foreach (Action<Guid, Editing.AnnotationEditorControl> observer in observers.GetInvocationList())
            try { observer(recordId, editor); } catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(error); }
    }
    internal static void PreviewOpened(Guid recordId)
    {
        if (PictureOpened is not { } observers) return;
        foreach (Action<Guid> observer in observers.GetInvocationList())
            try { observer(recordId); } catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(error); }
    }
    internal static void EditorOpenedFromLibrary(Guid recordId)
    {
        if (LibraryEditorOpened is not { } observers) return;
        foreach (Action<Guid> observer in observers.GetInvocationList())
            try { observer(recordId); } catch (Exception error) when (error is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(error); }
    }
    internal static event Action? ArrowAdded;
    internal static event Action? ArrowShortcutFired;

    internal static void NotifyArrowAdded() => Notify(ArrowAdded);
    internal static void NotifyArrowShortcutFired() => Notify(ArrowShortcutFired);

    private static void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Guidance must not interrupt an edit that has already succeeded.
                System.Diagnostics.Debug.WriteLine(error);
            }
        }
    }
}
