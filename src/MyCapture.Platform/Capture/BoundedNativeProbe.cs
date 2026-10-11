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

    internal BoundedNativeProbe(SemaphoreSlim gate, TimeSpan budget, ApartmentState apartment, string threadName)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
        _apartment = apartment;
        _threadName = threadName ?? throw new ArgumentNullException(nameof(threadName));
    }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, T> probe, T fallback)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (!_gate.Wait(0)) return fallback;

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

        Task winner = await Task.WhenAny(completion.Task, Task.Delay(_budget)).ConfigureAwait(false);
        if (winner == completion.Task) return await completion.Task.ConfigureAwait(false);
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The probe finished concurrently with the deadline. */ }
        return fallback;
    }
}
