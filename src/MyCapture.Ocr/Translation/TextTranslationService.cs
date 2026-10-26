using Microsoft.Extensions.Logging;

namespace MyCapture.Ocr.Translation;

/// <summary>One translated block and the protected source values it could not keep.</summary>
internal sealed record TranslatedText(string Source, string Text, IReadOnlyList<string> Missing, IReadOnlyList<string> Invented)
{
    internal bool NeedsReview => Missing.Count > 0 || Invented.Count > 0;
}

/// <summary>Blocks finished so far, in reading order.</summary>
internal readonly record struct TranslationProgress(int Completed, int Total);

/// <summary>What the OCR result window uses: the model on disk, then translation.</summary>
internal interface ITextTranslator
{
    long ModelDownloadBytes { get; }

    Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken);

    Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
        IProgress<TranslationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Translation between English and Korean using the selected store, entirely on this PC. The model loads on
/// first use and is released after a quiet minute, because its sessions hold about half a
/// gigabyte. Translations are cached per segment, so switching views does not translate again.
/// Every segment is repaired against its source with <see cref="ProtectedTokens.Repair"/>, and
/// the values that could not be put back are reported for the reader to check.
/// </summary>
internal sealed class TextTranslationService : ITextTranslator, IDisposable
{
    /// <summary>A block up to this many source tokens is translated whole.</summary>
    internal const int MaxSegmentTokens = 64;

    /// <summary>
    /// Segments per model call: a whole screen at once, which beat three calls of 16 for the same
    /// 40 lines. The int8 model quantizes activations per call, so a batch can word a sentence a
    /// little differently than translating it alone.
    /// </summary>
    internal const int BatchSize = 40;

    private const int CacheLimit = 4096;

    private readonly TranslationModelStore _store;
    private readonly Func<ISentenceTranslator> _loadEngine;
    private readonly ILogger _log;
    private readonly TimeSpan _idleRelease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private ISentenceTranslator? _engine;
    private volatile bool _disposed;

    internal TextTranslationService(TranslationModelStore store, ILogger<TextTranslationService> log)
        : this(store, () => MarianTranslator.Load(store.ModelDirectory, store.ModelFiles,
            MarianTranslator.DefaultThreads), log, TimeSpan.FromMinutes(1))
    {
    }

    internal TextTranslationService(TranslationModelStore store, Func<ISentenceTranslator> loadEngine, ILogger log,
        TimeSpan idleRelease)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loadEngine = loadEngine ?? throw new ArgumentNullException(nameof(loadEngine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _idleRelease = idleRelease;
        _idleTimer = new Timer(_ => ReleaseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public long ModelDownloadBytes => _store.TotalBytes;

    internal bool IsEngineLoaded => Volatile.Read(ref _engine) is not null;

    public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) =>
        _store.IsInstalledAsync(cancellationToken);

    public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
        _store.DownloadAsync(progress, cancellationToken);

    /// <summary>Releases the model and deletes its files; translation then needs a new download.</summary>
    internal async Task DeleteModelAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReleaseEngine();
            _cache.Clear();
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
        IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SetIdleTimer(Timeout.InfiniteTimeSpan);
            return await Task.Run(() => TranslateCore(sources, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (_disposed || !SetIdleTimer(_idleRelease)) ReleaseEngine();
            _gate.Release();
        }
    }

    private bool SetIdleTimer(TimeSpan dueTime)
    {
        try
        {
            return _idleTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private List<TranslatedText> TranslateCore(IReadOnlyList<string> sources, IProgress<TranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(0, sources.Count));
        if (sources.All(string.IsNullOrWhiteSpace))
            return [.. sources.Select(source => new TranslatedText(source, "", [], []))];

        ISentenceTranslator engine = _engine ??= LoadEngine();
        var segments = new List<string>[sources.Count];
        for (int index = 0; index < sources.Count; index++)
            segments[index] = [.. TranslationSegmenter.Split(sources[index] ?? "", engine.CountTokens,
                MaxSegmentTokens, MarianTranslator.MaxSourceTokens)];

        if (_cache.Count > CacheLimit) _cache.Clear();
        var pending = segments.SelectMany(block => block).Where(segment => !_cache.ContainsKey(segment))
            .Distinct(StringComparer.Ordinal).ToList();
        int completed = CompletedBlocks(segments, 0);
        if (completed > 0) progress?.Report(new(completed, sources.Count));
        for (int start = 0; start < pending.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> batch = pending.GetRange(start, Math.Min(BatchSize, pending.Count - start));
            IReadOnlyList<string> translated = engine.Translate(batch, cancellationToken);
            for (int index = 0; index < batch.Count; index++) _cache[batch[index]] = translated[index];
            int now = CompletedBlocks(segments, completed);
            if (now > completed) progress?.Report(new(completed = now, sources.Count));
        }

        var results = new List<TranslatedText>(sources.Count);
        for (int index = 0; index < sources.Count; index++)
        {
            var texts = new List<string>();
            var missing = new List<string>();
            var invented = new List<string>();
            foreach (string segment in segments[index])
            {
                (string repaired, _) = ProtectedTokens.Repair(segment, _cache[segment]);
                ProtectedTokenCheck check = ProtectedTokens.Check(segment, repaired);
                texts.Add(repaired);
                missing.AddRange(check.Altered.Select(issue => issue.Token));
                invented.AddRange(check.Invented.Select(issue => issue.Token));
            }
            results.Add(new TranslatedText(sources[index] ?? "", string.Join(" ", texts.Where(text => text.Length > 0)),
                [.. missing.Distinct(StringComparer.Ordinal)], [.. invented.Distinct(StringComparer.Ordinal)]));
        }
        return results;
    }

    private int CompletedBlocks(List<string>[] segments, int from)
    {
        int completed = from;
        while (completed < segments.Length && segments[completed].All(_cache.ContainsKey)) completed++;
        return completed;
    }

    private ISentenceTranslator LoadEngine()
    {
        try
        {
            return _loadEngine();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load the translation model");
            throw;
        }
    }

    private void ReleaseIfIdle()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            ReleaseEngine();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseEngine() => Interlocked.Exchange(ref _engine, null)?.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        // A running translation releases the engine itself when it finishes.
        ReleaseIfIdle();
    }
}
