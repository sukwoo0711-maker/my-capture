namespace MyCapture.App.Capture;

/// <summary>Tracks whether any surface that can enter a grab dismissed in the settle window.</summary>
internal sealed class PresentationDismissals
{
    private long _lastUnexcludedTick = long.MinValue;
    internal long LastDismissedTick { get; private set; } = long.MinValue;

    internal void Record(long tick, bool captureExcluded)
    {
        LastDismissedTick = tick;
        // An excluded dismissal must never erase an earlier unexcluded dismissal. Only
        // the latest unexcluded tick is needed: all older ones expire no later than it.
        if (!captureExcluded) _lastUnexcludedTick = tick;
    }

    internal bool NeedsSettle(long tick) => IsRecent(tick, _lastUnexcludedTick);

    internal static bool IsRecent(long tick, long dismissedTick)
    {
        if (dismissedTick == long.MinValue) return false;
        long elapsed = unchecked(tick - dismissedTick);
        return elapsed >= 0 && elapsed < CaptureOverlayCoordinator.PresentationSettleMs;
    }
}
