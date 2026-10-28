namespace MyCapture.App;

/// <summary>A dispatcher-owned, cancellable, single collection per completed flow.</summary>
internal sealed class IdleMemoryReclamation
{
    internal const long DelayMilliseconds = 3000;
    private long? _due;

    internal bool IsPending => _due.HasValue;

    internal void FlowEnded(long now) => _due = now + DelayMilliseconds;

    internal void FlowStarted() => _due = null;

    internal bool TryCollect(long now, bool isIdle, Action collect)
    {
        if (_due is not { } due || now < due) return false;
        // Consume even if the idle check fails. A later flow end can schedule a new attempt;
        // an old end must never cause a collection during the next flow.
        _due = null;
        if (!isIdle) return false;
        collect();
        return true;
    }
}
