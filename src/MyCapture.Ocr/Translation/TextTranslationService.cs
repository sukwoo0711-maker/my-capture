using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MyCapture.Ocr.Translation;

/// <summary>One translated block and the protected source values it could not keep.</summary>
internal sealed record TranslatedText(string Source, string Text, IReadOnlyList<string> Missing, IReadOnlyList<string> Invented)
{
    internal bool NeedsReview => Missing.Count > 0 || Invented.Count > 0;
}

/// <summary>Blocks finished so far, wherever they are in the reading order, out of all blocks of the request.</summary>
internal readonly record struct TranslationProgress(int Completed, int Total);

/// <summary>What the OCR result window uses: the model on disk, then translation.</summary>
internal interface ITextTranslator
{
    long ModelDownloadBytes { get; }

    Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken);

    Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken);

    Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
        IProgress<TranslationProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Loads an installed model ahead of a translation that is about to start; it never asks for one.</summary>
    Task WarmUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// English to Korean translation of recognized text, entirely on this PC. The model loads on
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
    /// Segments per model call at most: a whole screen of similar lines at once, which beat three
    /// calls of 16 for the same 40 lines. The int8 model quantizes activations per call, so a batch
    /// can word a sentence a little differently than translating it alone.
    /// </summary>
    internal const int BatchSize = 40;

    // Fitted to 33 calls of the int8 tc-big model on four threads: a call costs about 2.5 ms and each
    // decoding step 2.2 ms plus 0.29 ms per row, a row's attention adding about two thirds of that at
    // 100 tokens. In row-steps, a step is worth 7.5 rows and a call 8 row-steps.
    private const double StepRows = 7.5;
    private const double CallRowSteps = 8;
    private const double AttentionTokens = 150;

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
        : this(store, log, TranslationModelCatalog.ModelFiles)
    {
    }

    /// <summary>A service for another translation direction's model.</summary>
    internal TextTranslationService(TranslationModelStore store, ILogger<TextTranslationService> log, MarianModelFiles files)
        : this(store, () => MarianTranslator.Load(store.GetValidatedModelDirectoryForLoad(files), files, MarianTranslator.DefaultThreads), log,
            TimeSpan.FromMinutes(1))
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

    /// <summary>
    /// The shortest time between two progress reports inside one model call. A block counts as soon as the model has
    /// finished its last sentence, and every call still ends with a report, so the last one always gives the total.
    /// </summary>
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    internal bool IsEngineLoaded => Volatile.Read(ref _engine) is not null;

    /// <summary>Raised on the releasing thread after the model has left; part of what it freed only a collection returns.</summary>
    internal event Action? EngineReleased;

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

    /// <summary>
    /// Loads an installed model while the text it will translate is still being read. A missing
    /// model is neither requested nor downloaded, and an unused one leaves after the quiet minute.
    /// A failure is left to the translation itself, which loads the model again and reports it.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_disposed || IsEngineLoaded || !await _store.IsInstalledAsync(cancellationToken).ConfigureAwait(false)) return;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || _engine is not null) return;
                SetIdleTimer(Timeout.InfiniteTimeSpan);
                await Task.Run(() => _engine ??= LoadEngine(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (_disposed || !SetIdleTimer(_idleRelease)) ReleaseEngine();
                _gate.Release();
            }
        }
        catch (Exception)
        {
            // LoadEngine has logged a failed load; cancellation and disposal need nothing here.
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            segments[index] = [.. TranslationSegmenter.Split(sources[index] ?? "", engine.CountTokens,
                MaxSegmentTokens, MarianTranslator.MaxSourceTokens)];
        }

        if (_cache.Count > CacheLimit) _cache.Clear();
        var pending = segments.SelectMany(block => block).Where(segment => !_cache.ContainsKey(segment))
            .Distinct(StringComparer.Ordinal).ToList();
        BlockProgress? blocks = progress is null ? null : new BlockProgress(segments, _cache, progress, ProgressInterval);
        foreach (List<string> batch in PlanBatches(pending, engine.CountTokens))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A short sentence of the call counts once the model has ended it, before the longest one has.
            IReadOnlyList<string> translated = engine.Translate(batch,
                blocks is null ? null : row => blocks.Finish(batch[row]), cancellationToken);
            for (int index = 0; index < batch.Count; index++) _cache[batch[index]] = translated[index];
            blocks?.FinishCall(batch);
        }

        var results = new List<TranslatedText>(sources.Count);
        for (int index = 0; index < sources.Count; index++)
        {
            var texts = new List<string>();
            var missing = new List<string>();
            var invented = new List<string>();
            foreach (string segment in segments[index])
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Splits the segments into model calls, longest first. A call decodes as many steps as its
    /// longest segment needs and computes every row at each step, so a few long sentences among
    /// short labels get a call of their own instead of keeping the labels decoding; similar lengths
    /// stay together, which also keeps the model's working memory down. A screen that stays in one
    /// call keeps its order and translates exactly as before.
    /// </summary>
    internal static List<List<string>> PlanBatches(IReadOnlyList<string> segments, Func<string, int> countTokens)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(countTokens);
        if (segments.Count == 0) return [];
        var sorted = segments.Select(segment => (Segment: segment, Tokens: countTokens(segment)))
            .OrderByDescending(item => item.Tokens).ToArray();
        // cost[end]: the cheapest calls for the longest `end` segments; first[end] starts the last of them.
        var cost = new double[sorted.Length + 1];
        var first = new int[sorted.Length + 1];
        for (int end = 1; end <= sorted.Length; end++)
        {
            cost[end] = double.MaxValue;
            for (int start = Math.Max(0, end - BatchSize); start < end; start++)
            {
                // Sorted longest first: the first segment sets the steps the call decodes.
                int steps = sorted[start].Tokens, rows = end - start;
                double candidate = cost[start] + CallRowSteps + steps * (StepRows + rows * (1 + steps / AttentionTokens));
                if (candidate < cost[end]) (cost[end], first[end]) = (candidate, start);
            }
        }
        var batches = new List<List<string>>();
        for (int end = sorted.Length; end > 0; end = first[end])
            batches.Add([.. sorted[first[end]..end].Select(item => item.Segment)]);
        if (batches.Count == 1) return [[.. segments]];
        batches.Reverse();
        return batches;
    }

    /// <summary>
    /// Counts the blocks whose every segment is translated, from the cache or from a model call, and reports the
    /// count when it grows: at once after a call, and at most once per interval while a call is still decoding.
    /// It lives on the translating thread, where the model also reports its finished sentences.
    /// </summary>
    private sealed class BlockProgress
    {
        private readonly IProgress<TranslationProgress> _progress;
        private readonly TimeSpan _interval;
        private readonly int _total;
        private readonly int[] _unfinished;
        // Each segment still to translate, with the block of each of its occurrences.
        private readonly Dictionary<string, List<int>> _waiting = new(StringComparer.Ordinal);
        private int _completed;
        private int _reported;
        private long _reportedAt;

        internal BlockProgress(List<string>[] segments, Dictionary<string, string> cache, IProgress<TranslationProgress> progress,
            TimeSpan interval)
        {
            _progress = progress;
            _interval = interval;
            _total = segments.Length;
            _unfinished = new int[segments.Length];
            for (int block = 0; block < segments.Length; block++)
            {
                foreach (string segment in segments[block])
                {
                    if (cache.ContainsKey(segment)) continue;
                    _unfinished[block]++;
                    if (!_waiting.TryGetValue(segment, out List<int>? blocks)) _waiting[segment] = blocks = [];
                    blocks.Add(block);
                }
                if (_unfinished[block] == 0) _completed++;
            }
            Report(call: true);
        }

        /// <summary>A sentence the model has ended while its call still decodes others.</summary>
        internal void Finish(string segment)
        {
            Mark(segment);
            Report(call: false);
        }

        /// <summary>The end of a call: every segment it carried is translated.</summary>
        internal void FinishCall(IEnumerable<string> batch)
        {
            foreach (string segment in batch) Mark(segment);
            Report(call: true);
        }

        private void Mark(string segment)
        {
            if (!_waiting.Remove(segment, out List<int>? blocks)) return;
            foreach (int block in blocks)
                if (--_unfinished[block] == 0) _completed++;
        }

        private void Report(bool call)
        {
            if (_completed <= _reported) return;
            long now = Stopwatch.GetTimestamp();
            if (!call && _reportedAt != 0 && Stopwatch.GetElapsedTime(_reportedAt, now) < _interval) return;
            _reported = _completed;
            _reportedAt = now;
            _progress.Report(new(_completed, _total));
        }
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

    private void ReleaseEngine()
    {
        if (Interlocked.Exchange(ref _engine, null) is not { } engine) return;
        engine.Dispose();
        EngineReleased?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        // A running translation releases the engine itself when it finishes.
        ReleaseIfIdle();
    }
}
