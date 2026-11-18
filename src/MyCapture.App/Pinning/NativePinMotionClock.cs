using System.Windows.Threading;

namespace MyCapture.App.Pinning;

/// <summary>What a <see cref="NativePinMotionClock"/> ticks: a native pin, or a WPF pin's zoom motion.</summary>
internal interface INativeMotionTarget
{
    /// <summary>One DWM-paced tick on the owner's dispatcher; <paramref name="result"/> is DwmFlush's HRESULT.</summary>
    void OnNativeMotionTick(NativePinMotionClock clock, int result);
}

/// <summary>
/// DWM-paced motion without a WPF rendering target. One dedicated background thread posts
/// one weak STA callback and waits for its acknowledgement before the next DWM flush.
/// It exits on stop, releasing the thread's USER state instead of retaining it in the pool.
/// </summary>
internal sealed class NativePinMotionClock
{
    private static int _liveCount;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly Dispatcher _dispatcher;
    private readonly WeakReference<INativeMotionTarget> _owner;
    private bool _disposed;
    internal static int LiveCount => Volatile.Read(ref _liveCount);
    internal Task Completion { get; }

    internal NativePinMotionClock(Dispatcher dispatcher, INativeMotionTarget owner)
    {
        _dispatcher = dispatcher;
        _owner = new WeakReference<INativeMotionTarget>(owner);
        Interlocked.Increment(ref _liveCount);
        try
        {
            Completion = Task.Factory.StartNew(Run, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { _cancel.Dispose(); Interlocked.Decrement(ref _liveCount); throw; }
    }

    internal void Stop()
    {
        lock (_gate) { if (!_disposed) _cancel.Cancel(); }
    }

    private void Run()
    {
        CancellationToken token = _cancel.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int result = NativePinWindowInterop.DwmFlush();
                token.ThrowIfCancellationRequested();
                if (!DispatchTick(result, token) || result < 0) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_gate) { _disposed = true; _cancel.Dispose(); }
            Interlocked.Decrement(ref _liveCount);
        }
    }

    private bool DispatchTick(int result, CancellationToken token)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return false;
        // This promise has only the dedicated thread's blocking wait; no user
        // continuation or pool work is needed to signal acknowledgement.
        var acknowledged = new TaskCompletionSource();
        DispatcherOperation operation;
        try
        {
            operation = _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        if (_owner.TryGetTarget(out var owner)) owner.OnNativeMotionTick(this, result);
                        else Stop();
                    }
                }
                finally { acknowledged.TrySetResult(); }
            }));
        }
        catch (InvalidOperationException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return false;
        }
        // Only this dedicated clock thread waits. The STA never joins it and the
        // managed promise remains safe to signal if cancellation wins mid-callback.
        try { acknowledged.Task.Wait(token); return true; }
        finally
        {
            // Aborting a queued callback requires no STA wait. The callback itself
            // owns only this clock and a weak pin reference if it has already begun.
            if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort();
        }
    }
}
