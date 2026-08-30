using System.Diagnostics;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Capacity-one latest-wins coordinator between immediate timeline intent and decoder seeking.
/// It permits at most one engine call in flight and one pending request, samples drag preview,
/// gives release Exact requests priority, and never publishes a stale generation.
/// </summary>
internal sealed class PreviewSeekCoordinator : IDisposable, IAsyncDisposable
{
    private sealed record QueuedSeek(
        PreviewSeekRequest Request,
        TaskCompletionSource<PresentedPreviewFrame>? Completion);

    internal static readonly TimeSpan DefaultPreviewInterval = TimeSpan.FromMilliseconds(45);

    private readonly object _gate = new();
    private readonly IVideoPreviewEngine _engine;
    private readonly int _fps;
    private readonly TimeSpan _previewInterval;
    private readonly CancellationTokenSource _shutdown = new();

    private QueuedSeek? _pending;
    private QueuedSeek? _inFlightSeek;
    private CancellationTokenSource? _previewDelay;
    private Task _workerTask = Task.CompletedTask;
    private bool _workerRunning;
    private bool _disposed;
    private bool _shutdownDisposed;
    private long _nextGeneration;
    private long _latestGeneration;
    private long _latestExactGeneration;
    private long _lastPreviewDispatchTimestamp;
    private bool _hasPreviewDispatch;
    private int? _lastDispatchedPreviewFrame;
    private double _intentPositionMs;
    private double _requestedPositionMs;
    private double _presentedPositionMs;
    private long _presentedGeneration;
    private PreviewSeekMode _presentedMode;
    private Exception? _lastError;

    internal PreviewSeekCoordinator(
        IVideoPreviewEngine engine,
        int fps,
        TimeSpan? previewInterval = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _fps = Math.Max(1, fps);
        _previewInterval = previewInterval ?? DefaultPreviewInterval;
        if (_previewInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(previewInterval));
        }
    }

    internal event Action<PresentedPreviewFrame>? PreviewPresented;

    internal event Action<Exception>? SeekFailed;

    internal double IntentPositionMs { get { lock (_gate) { return _intentPositionMs; } } }

    internal double RequestedPreviewPositionMs { get { lock (_gate) { return _requestedPositionMs; } } }

    internal double PresentedPositionMs { get { lock (_gate) { return _presentedPositionMs; } } }

    internal long PresentedGeneration { get { lock (_gate) { return _presentedGeneration; } } }

    internal PreviewSeekMode PresentedMode { get { lock (_gate) { return _presentedMode; } } }

    internal Exception? LastError { get { lock (_gate) { return _lastError; } } }

    internal bool IsInFlightForTest { get { lock (_gate) { return _inFlightSeek is not null; } } }

    internal bool HasPendingForTest { get { lock (_gate) { return _pending is not null; } } }

    internal int PendingCountForTest { get { lock (_gate) { return _pending is null ? 0 : 1; } } }

    internal long IssuedSeekCountForTest { get; private set; }

    internal long DroppedRequestCountForTest { get; private set; }

    internal long DeduplicatedRequestCountForTest { get; private set; }

    internal long StaleResultCountForTest { get; private set; }

    internal int MaxObservedInFlightForTest { get; private set; }

    internal long RequestPreview(double targetPositionMs)
    {
        double target = Math.Max(0, targetPositionMs);
        int frame = FrameIndex(target);
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            _intentPositionMs = target;

            // Once release Exact is queued/in-flight, late MouseMove messages cannot replace it.
            if (_pending?.Request.Mode == PreviewSeekMode.Exact
                || _inFlightSeek?.Request.Mode == PreviewSeekMode.Exact)
            {
                DeduplicatedRequestCountForTest++;
                return _latestExactGeneration;
            }

            if ((_pending is not null
                    && _pending.Request.Mode == PreviewSeekMode.Preview
                    && _pending.Request.TargetFrameIndex == frame)
                || (_pending is null
                    && _inFlightSeek is not null
                    && _inFlightSeek.Request.Mode == PreviewSeekMode.Preview
                    && _inFlightSeek.Request.TargetFrameIndex == frame)
                || (_pending is null
                    && _inFlightSeek is null
                    && _lastDispatchedPreviewFrame == frame))
            {
                DeduplicatedRequestCountForTest++;
                return _latestGeneration;
            }

            long generation = ++_nextGeneration;
            _latestGeneration = generation;
            ReplacePendingLocked(new QueuedSeek(
                new PreviewSeekRequest(generation, target, frame, PreviewSeekMode.Preview),
                null));
            EnsureWorkerLocked();
            return generation;
        }
    }

    internal long RequestExact(double targetPositionMs)
    {
        lock (_gate)
        {
            return QueueExactLocked(Math.Max(0, targetPositionMs), completion: null);
        }
    }

    internal Task<PresentedPreviewFrame> RequestExactAsync(double targetPositionMs)
    {
        var completion = new TaskCompletionSource<PresentedPreviewFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            long generation = QueueExactLocked(Math.Max(0, targetPositionMs), completion);
            if (generation == 0)
            {
                completion.TrySetCanceled();
            }
        }

        return completion.Task;
    }

    internal async Task WaitForIdleForTestAsync()
    {
        while (true)
        {
            Task worker;
            lock (_gate)
            {
                if (!_workerRunning)
                {
                    return;
                }

                worker = _workerTask;
            }

            await worker.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            BeginDisposeLocked();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_gate)
        {
            BeginDisposeLocked();
            worker = _workerTask;
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            if (!_shutdownDisposed)
            {
                _shutdown.Dispose();
                _shutdownDisposed = true;
            }
        }
    }

    private long QueueExactLocked(
        double targetPositionMs,
        TaskCompletionSource<PresentedPreviewFrame>? completion)
    {
        if (_disposed)
        {
            return 0;
        }

        _intentPositionMs = targetPositionMs;
        long generation = ++_nextGeneration;
        _latestGeneration = generation;
        _latestExactGeneration = generation;
        int frame = FrameIndex(targetPositionMs);
        ReplacePendingLocked(new QueuedSeek(
            new PreviewSeekRequest(generation, targetPositionMs, frame, PreviewSeekMode.Exact),
            completion));

        // An Exact request bypasses a preview sampling wait. A decoder call already in flight
        // remains bounded to one, but its stale result will not be published.
        try
        {
            _previewDelay?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        EnsureWorkerLocked();
        return generation;
    }

    private void ReplacePendingLocked(QueuedSeek next)
    {
        if (_pending is not null)
        {
            DroppedRequestCountForTest++;
            _pending.Completion?.TrySetCanceled();
        }

        _pending = next;
    }

    private void EnsureWorkerLocked()
    {
        if (_workerRunning)
        {
            return;
        }

        _workerRunning = true;
        _workerTask = Task.Run(RunWorkerAsync);
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            QueuedSeek current;
            lock (_gate)
            {
                if (_disposed || _pending is null)
                {
                    _workerRunning = false;
                    return;
                }

                current = _pending;
                _pending = null;
            }

            if (current.Request.Mode == PreviewSeekMode.Preview)
            {
                QueuedSeek? sampled = await AwaitPreviewWindowAsync(current).ConfigureAwait(false);
                if (sampled is null)
                {
                    lock (_gate)
                    {
                        _workerRunning = false;
                    }
                    return;
                }

                current = sampled;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    current.Completion?.TrySetCanceled();
                    _workerRunning = false;
                    return;
                }

                // Close the small race between ending the sample wait and acquiring this lock.
                if (current.Request.Mode == PreviewSeekMode.Preview
                    && _pending?.Request.Mode == PreviewSeekMode.Exact)
                {
                    DroppedRequestCountForTest++;
                    current = _pending;
                    _pending = null;
                }

                _inFlightSeek = current;
                _requestedPositionMs = current.Request.TargetPositionMs;
                IssuedSeekCountForTest++;
                MaxObservedInFlightForTest = Math.Max(MaxObservedInFlightForTest, 1);
                if (current.Request.Mode == PreviewSeekMode.Preview)
                {
                    _hasPreviewDispatch = true;
                    _lastPreviewDispatchTimestamp = Stopwatch.GetTimestamp();
                    _lastDispatchedPreviewFrame = current.Request.TargetFrameIndex;
                }
                else
                {
                    _lastDispatchedPreviewFrame = null;
                }
            }

            PresentedPreviewFrame result = default;
            Exception? failure = null;
            try
            {
                result = await _engine.SeekAsync(current.Request, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                current.Completion?.TrySetCanceled();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            PresentedPreviewFrame? publish = null;
            lock (_gate)
            {
                _inFlightSeek = null;
                if (failure is not null)
                {
                    _lastError = failure;
                    current.Completion?.TrySetException(failure);
                }
                else if (!_disposed && result.Generation != 0)
                {
                    bool stale = result.Generation != _latestGeneration
                        || result.Generation < _latestExactGeneration;
                    if (stale)
                    {
                        StaleResultCountForTest++;
                        current.Completion?.TrySetCanceled();
                    }
                    else
                    {
                        _presentedGeneration = result.Generation;
                        _presentedPositionMs = result.PresentedPositionMs;
                        _presentedMode = result.Mode;
                        current.Completion?.TrySetResult(result);
                        publish = result;
                    }
                }
            }

            if (failure is not null)
            {
                SeekFailed?.Invoke(failure);
            }
            else if (publish is PresentedPreviewFrame presented)
            {
                PreviewPresented?.Invoke(presented);
            }
        }
    }

    private async Task<QueuedSeek?> AwaitPreviewWindowAsync(QueuedSeek current)
    {
        TimeSpan remaining;
        CancellationTokenSource? delay = null;
        lock (_gate)
        {
            remaining = RemainingPreviewDelayLocked();
            if (remaining > TimeSpan.Zero)
            {
                delay = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _previewDelay = delay;

                // Register the delay while holding the same lock used by QueueExactLocked.
                // This leaves no gap where Exact can miss cancellation and wait a preview interval.
                if (_pending?.Request.Mode == PreviewSeekMode.Exact)
                {
                    delay.Cancel();
                }
            }
        }

        if (delay is not null)
        {
            using (delay)
            {
                try
                {
                    await Task.Delay(remaining, delay.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_shutdown.IsCancellationRequested)
                    {
                        return null;
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_previewDelay, delay))
                        {
                            _previewDelay = null;
                        }
                    }
                }
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_pending is not null)
            {
                DroppedRequestCountForTest++;
                current = _pending;
                _pending = null;
            }

            return current;
        }
    }

    private TimeSpan RemainingPreviewDelayLocked()
    {
        if (!_hasPreviewDispatch || _previewInterval == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastPreviewDispatchTimestamp);
        return elapsed >= _previewInterval ? TimeSpan.Zero : _previewInterval - elapsed;
    }

    private int FrameIndex(double positionMs)
    {
        double frameMs = FrameStepCalculator.FrameDurationMs(_fps);
        if (frameMs <= 0)
        {
            return 0;
        }

        double raw = Math.Floor(positionMs / frameMs + 1e-6);
        return raw >= int.MaxValue ? int.MaxValue : (int)raw;
    }

    private void BeginDisposeLocked()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending?.Completion?.TrySetCanceled();
        _pending = null;
        try
        {
            _previewDelay?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _shutdown.Cancel();
    }
}
