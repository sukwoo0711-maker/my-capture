namespace MyCapture.App.Updates;

/// <summary>One operation at a time; cancellation remains active until the operation settles.</summary>
internal sealed class UpdateSession(IUpdateService service) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    internal bool IsBusy { get { lock (_gate) return _cancellation is not null; } }
    internal void Cancel() { lock (_gate) _cancellation?.Cancel(); }
    internal async Task<StagedUpdateResult?> DownloadAsync(UpdateVersion current, string root, IProgress<UpdateProgress> progress)
    {
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cancellation is not null) return null;
            _cancellation = cancellation = new CancellationTokenSource();
        }
        try
        {
            var result = await service.CheckAndStageAsync(current, root, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                result.VerifiedPackage?.Cleanup();
                return StagedUpdateResult.Failed(UpdateErrorKind.Cancelled, "Cancelled.");
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return StagedUpdateResult.Failed(UpdateErrorKind.Cancelled, "Cancelled.");
        }
        finally
        {
            lock (_gate) { _cancellation = null; cancellation.Dispose(); }
        }
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _cancellation?.Cancel(); }
        service.Dispose();
    }
}
