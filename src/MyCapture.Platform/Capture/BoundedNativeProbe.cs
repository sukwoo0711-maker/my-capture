namespace MyCapture.Platform.Capture;

/// <summary>
/// Runs one optional native metadata read on its own thread under a deadline. Only one read per gate
/// runs at a time, including after the caller has stopped waiting, so a hung provider (a busy Explorer,
/// a slow network share) can neither block capture nor pile up native calls. A busy gate, a failure or
/// a missed deadline all return the fallback, because the metadata is never required for a capture.
/// </summary>
internal sealed class BoundedNativeProbe
{
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _budget;
    private readonly ApartmentState _apartment;
    private readonly string _threadName;
    private readonly bool _waitForBusyGate;

    /// <param name="waitForBusyGate">
    /// Wait for a probe that still owns the gate (for example the previous capture's) instead of
    /// returning the fallback at once. The wait counts against the same deadline, so a read still ends
    /// within <paramref name="budget"/>. Only callers that nothing on screen waits for should wait.
    /// </param>
    internal BoundedNativeProbe(SemaphoreSlim gate, TimeSpan budget, ApartmentState apartment, string threadName,
        bool waitForBusyGate = false)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
        _apartment = apartment;
        _threadName = threadName ?? throw new ArgumentNullException(nameof(threadName));
        _waitForBusyGate = waitForBusyGate;
    }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, T> probe, T fallback)
    {
        ArgumentNullException.ThrowIfNull(probe);
        TimeSpan remaining = await EnterAsync(_gate, _budget, _waitForBusyGate).ConfigureAwait(false);
        if (remaining <= TimeSpan.Zero) return fallback;

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        var worker = new Thread(() =>
        {
            T result = fallback;
            try
            {
                result = probe(cancellation.Token);
                if (cancellation.IsCancellationRequested) result = fallback;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Optional metadata. Never log what the provider returned: it can hold paths.
                result = fallback;
            }
            finally
            {
                cancellation.Dispose();
                _gate.Release();
                completion.TrySetResult(result);
            }
        }) { IsBackground = true, Name = _threadName };
        worker.SetApartmentState(_apartment);
        try
        {
            worker.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellation.Dispose();
            _gate.Release();
            return fallback;
        }

        Task winner = await Task.WhenAny(completion.Task, Task.Delay(remaining)).ConfigureAwait(false);
        if (winner == completion.Task) return await completion.Task.ConfigureAwait(false);
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The probe finished concurrently with the deadline. */ }
        return fallback;
    }

    /// <summary>
    /// Takes <paramref name="gate"/> and returns how much of <paramref name="budget"/> is left for the
    /// probe, or zero (gate not taken) when it is busy and waiting is off or did not get it in time.
    /// </summary>
    internal static async Task<TimeSpan> EnterAsync(SemaphoreSlim gate, TimeSpan budget, bool waitForBusyGate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        if (gate.Wait(0)) return budget;
        if (!waitForBusyGate) return TimeSpan.Zero;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!await gate.WaitAsync(budget).ConfigureAwait(false)) return TimeSpan.Zero;
        TimeSpan remaining = budget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) return remaining;
        gate.Release();
        return TimeSpan.Zero;
    }
}
