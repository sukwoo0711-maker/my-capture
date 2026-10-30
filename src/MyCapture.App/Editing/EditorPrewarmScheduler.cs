namespace MyCapture.App.Editing;

/// <summary>Dispatcher-owned, one-shot startup work that survives a busy first idle turn.</summary>
internal sealed class EditorPrewarmScheduler(
    Action<Action> postIdle,
    Func<bool> isBusy,
    Func<bool> shouldStop,
    Action retryLater,
    Action prewarm)
{
    private bool _queued;
    private bool _finished;

    internal void Request()
    {
        if (_finished || _queued) return;
        _queued = true;
        postIdle(TryRun);
    }

    internal void EditorOpened() => _finished = true;

    private void TryRun()
    {
        _queued = false;
        if (_finished) return;
        if (shouldStop())
        {
            _finished = true;
            return;
        }
        if (isBusy())
        {
            // A flow-end event can retry immediately. The host's delayed fallback covers
            // asynchronous capture completion without spinning the idle queue while busy.
            retryLater();
            return;
        }
        // Consume before calling out: an exception or reentrant request must not repeat it.
        _finished = true;
        prewarm();
    }
}
