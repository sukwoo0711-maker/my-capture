namespace MyCapture.App;

/// <summary>Real editor actions observed by optional practice guidance.</summary>
internal static class PracticeSignals
{
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
