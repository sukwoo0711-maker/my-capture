namespace MyCapture.App.Capture;

/// <summary>
/// Coalesces guide bookkeeping on the UI thread. Neither a hidden selector preparing its frame nor
/// a visible selector may be interrupted by the settings write. The idle callback checks again in
/// case another screenshot or recording selector started after the previous one closed.
/// </summary>
internal sealed class CaptureGuideWriteScheduler(
    Func<bool> isSelecting,
    Action<Action> scheduleIdle,
    bool legacyGuideWrite = false)
{
    private Action? _pendingWrite;
    private bool _scheduled;

    internal void Schedule(Action write)
    {
        if (legacyGuideWrite)
        {
            scheduleIdle(write);
            return;
        }

        _pendingWrite = write;
        NotifySelectorClosed();
    }

    internal void NotifySelectorClosed()
    {
        if (_pendingWrite is null || _scheduled || isSelecting()) return;
        _scheduled = true;
        scheduleIdle(WriteWhenIdle);
    }

    private void WriteWhenIdle()
    {
        _scheduled = false;
        if (isSelecting()) return;
        Action? write = _pendingWrite;
        _pendingWrite = null;
        write?.Invoke();
    }
}
