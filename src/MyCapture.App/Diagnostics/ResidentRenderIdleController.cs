namespace MyCapture.App.Diagnostics;

internal enum ResidentIdleReleaseVariant { None, ProcessMode, KeepAlive }

/// <summary>Dispatcher-owned idle policy. Synthetic probe callbacks do not count as user input.</summary>
internal sealed class ResidentRenderIdleController
{
    private readonly TimeSpan _threshold;
    private readonly Func<TimeSpan> _clock;
    private readonly Action _release;
    private readonly Action _restore;
    private TimeSpan _lastUse;
    private TimeSpan _lastVisible;
    private bool _transitioning;

    internal ResidentRenderIdleController(TimeSpan threshold, Func<TimeSpan> clock, Action release, Action restore)
    {
        if (threshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
        _clock = clock;
        _release = release;
        _restore = restore;
        _lastUse = _lastVisible = clock();
    }

    internal bool IsReleased { get; private set; }
    internal int ReleaseCount { get; private set; }
    internal int RestoreCount { get; private set; }

    internal bool Poll(bool hasVisibleWindow, TimeSpan inputIdle)
    {
        TimeSpan now = _clock();
        if (hasVisibleWindow) _lastVisible = now;
        bool idle = !hasVisibleWindow && inputIdle >= _threshold
            && now - _lastUse >= _threshold && now - _lastVisible >= _threshold;
        if (!idle || IsReleased || _transitioning) return idle;
        _transitioning = true;
        try
        {
            _release();
            IsReleased = true;
            ReleaseCount++;
        }
        finally { _transitioning = false; }
        return true;
    }

    /// <summary>Synchronous: the caller cannot continue to its command/window until restoration finishes.</summary>
    internal void BeforeUse()
    {
        _lastUse = _clock();
        // Native channel/window teardown may reenter managed code. Returning here would let
        // a nested command show while release/restore is unfinished. Reject that measurement
        // instead; the native presentation observer latches this exception for the probe.
        if (_transitioning) throw new InvalidOperationException("Resident rendering was used during an idle-release transition.");
        if (!IsReleased) return;
        _transitioning = true;
        try
        {
            _restore();
            IsReleased = false;
            RestoreCount++;
        }
        finally { _transitioning = false; }
    }
}
