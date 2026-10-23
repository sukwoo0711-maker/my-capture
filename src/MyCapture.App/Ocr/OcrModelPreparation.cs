using MyCapture.Core.Settings;

namespace MyCapture.App.Ocr;

/// <summary>
/// Owns the cancellation of PP-OCR model preparation. Preparations started together share one
/// token, so a second start never discards the first one's partly downloaded file. Leaving the
/// neural quality or exiting cancels them; the store's own cleanup removes the partial temp
/// file while completed, verified files stay. Each token stays valid until every worker that
/// received it has finished.
/// </summary>
internal sealed class OcrModelPreparation
{
    private readonly object _sync = new();
    private Lease? _current;
    private bool _closed;

    internal Task StartAsync(Func<CancellationToken, Task> prepare)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        Lease lease;
        lock (_sync)
        {
            if (_closed) return Task.CompletedTask;
            lease = _current ??= new Lease();
            lease.Users++;
        }

        // Capture before scheduling: cancellation can arrive before the worker starts.
        CancellationToken token = lease.Source.Token;
        return Task.Run(async () =>
        {
            try { await prepare(token).ConfigureAwait(false); }
            finally { Release(lease); }
        });
    }

    /// <summary>Cancels running preparations; a later start receives a new token.</summary>
    internal void Cancel()
    {
        lock (_sync)
        {
            _current?.Source.Cancel();
            _current = null;
        }
    }

    internal void CancelForQuality(OcrQuality quality)
    {
        if (!OcrQualityProfile.UseNeuralModel(quality)) Cancel();
    }

    /// <summary>Exit: cancels running preparations and ignores any later start.</summary>
    internal void Close()
    {
        lock (_sync)
        {
            _closed = true;
            _current?.Source.Cancel();
            _current = null;
        }
    }

    private void Release(Lease lease)
    {
        lock (_sync)
        {
            if (--lease.Users > 0) return;
            if (ReferenceEquals(_current, lease)) _current = null;
            lease.Source.Dispose();
        }
    }

    private sealed class Lease
    {
        internal CancellationTokenSource Source { get; } = new();
        internal int Users { get; set; }
    }
}
