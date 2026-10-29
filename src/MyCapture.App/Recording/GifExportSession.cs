using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// One export dialog's GIF work for a fixed recording and edit document. Repeated exports that
/// change only size, frame rate or speed reuse decoded and encoded frames through
/// <see cref="GifFrameCache"/>; every output is byte-identical to a one-shot
/// <see cref="AnimatedGifExporter.Export"/> with the same options.
/// </summary>
/// <remarks>
/// Exports run on caller threads (normally one <c>StaThreadTask</c> at a time); the cache is
/// internally synchronised. The document is cloned at construction, so later edits by the
/// caller can never make cached frames stale. Disposal drops every cached frame; an export
/// still running at that moment completes (or cancels) but stores nothing.
/// </remarks>
internal sealed class GifExportSession : IDisposable
{
    private readonly RecordingResult _recording;
    private readonly VideoEditDocument _document;
    private readonly ILogger _log;
    private readonly GifEngineOptions _options;
    private readonly object _stampGate = new();
    private (long Length, DateTime LastWriteUtc)? _sourceStamp;
    private volatile bool _disposed;

    internal GifExportSession(
        RecordingResult recording,
        VideoEditDocument document,
        ILogger? log = null,
        GifEngineOptions? options = null,
        long? decodedBudgetBytes = null,
        long encodedBudgetBytes = GifFrameCache.DefaultEncodedBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(document);
        _recording = recording;
        _document = document.Clone();
        _log = log ?? NullLogger.Instance;
        _options = options ?? GifEngineOptions.Default;
        Cache = new GifFrameCache(decodedBudgetBytes ?? GifFrameCache.DefaultDecodedBudgetBytes(), encodedBudgetBytes);
    }

    internal GifFrameCache Cache { get; }

    /// <summary>Statistics of the most recent completed export, for diagnostics and tests.</summary>
    internal GifRenderStatistics? LastStatistics { get; private set; }

    internal int Export(
        string destinationPath,
        GifExportQuality? quality = null,
        double playbackSpeed = 1.0,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InvalidateIfSourceChanged();
        int frames = AnimatedGifExporter.ExportCore(
            _recording,
            _document,
            destinationPath,
            progress,
            cancellationToken,
            playbackSpeed,
            quality,
            Cache,
            _options,
            _log,
            out GifRenderStatistics statistics);
        LastStatistics = statistics;
        return frames;
    }

    public void Dispose()
    {
        _disposed = true;
        Cache.Close();
    }

    /// <summary>Drops every cached frame when the recording file was replaced or rewritten.</summary>
    private void InvalidateIfSourceChanged()
    {
        var file = new FileInfo(_recording.OutputPath);
        (long, DateTime)? stamp = file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
        lock (_stampGate)
        {
            if (_sourceStamp != stamp)
            {
                Cache.Clear();
                _sourceStamp = stamp;
            }
        }
    }
}

/// <summary>
/// Session-scoped reuse of GIF frames while only output size, frame rate or speed change.
/// </summary>
/// <remarks>
/// <para>
/// Decoded tier: base-size frames (the recording fitted within 960 px) for the densest, 10 fps
/// schedule of the session's trim and layer boundaries. The 5 fps schedule and every speed use a
/// subset of those source times. It is stored all-or-nothing and only when the whole set fits
/// the decoded budget — 1/16 of the memory available to the process, clamped to 64–384 MiB
/// (a 10 s 1080p clip needs about 207 MiB). Otherwise each export decodes again.
/// </para>
/// <para>
/// Encoded tier: per output size, the writer's frame updates (changed rectangles with their
/// single-frame GIF payloads, or "unchanged") for one exact frame sequence, so a speed change or
/// a return to an earlier preset only rewrites the container. An update is a difference from
/// the previous frame of its own sequence, so only the identical source times are served; a
/// 5 fps export of the same size is encoded again from the decoded tier. Limited to 64 MiB by
/// default; least recently used sizes are evicted first and a set larger than the budget is
/// not kept.
/// </para>
/// <para>
/// Invalidation: <see cref="Clear"/> runs when the source file's length or last-write time
/// changes; <see cref="Close"/> (session disposal) drops everything and refuses later stores.
/// Frames are keyed by exact source time and size, so a different trim, layer boundary or base
/// size simply misses.
/// </para>
/// </remarks>
internal sealed class GifFrameCache
{
    internal const long DefaultEncodedBudgetBytes = 64L * 1024 * 1024;
    private const long MinimumDecodedBudgetBytes = 64L * 1024 * 1024;
    private const long MaximumDecodedBudgetBytes = 384L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly long _decodedBudget;
    private readonly long _encodedBudget;
    private readonly List<EncodedSet> _encoded = [];
    private DecodedSet? _decoded;
    private bool _closed;

    internal GifFrameCache(long decodedBudgetBytes, long encodedBudgetBytes)
    {
        _decodedBudget = Math.Max(0, decodedBudgetBytes);
        _encodedBudget = Math.Max(0, encodedBudgetBytes);
    }

    internal static long DefaultDecodedBudgetBytes() =>
        Math.Clamp(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16,
            MinimumDecodedBudgetBytes,
            MaximumDecodedBudgetBytes);

    internal long DecodedBytes
    {
        get
        {
            lock (_gate)
            {
                return _decoded?.Bytes ?? 0;
            }
        }
    }

    internal long EncodedBytes
    {
        get
        {
            lock (_gate)
            {
                return _encoded.Sum(set => set.Bytes);
            }
        }
    }

    internal bool CanHoldDecoded(int width, int height, int frameCount)
    {
        lock (_gate)
        {
            return !_closed && (long)width * height * 4 * frameCount <= _decodedBudget;
        }
    }

    /// <summary>Also used while collecting updates, before an oversized set is retained.</summary>
    internal bool CanHoldEncoded(long bytes)
    {
        lock (_gate)
        {
            return !_closed && bytes <= _encodedBudget;
        }
    }

    /// <summary>Base frames aligned to <paramref name="times"/>, or null unless all are cached.</summary>
    internal GifSourceFrame[]? TryGetDecoded(int width, int height, IReadOnlyList<double> times)
    {
        lock (_gate)
        {
            if (_decoded is not { } set || set.Width != width || set.Height != height)
            {
                return null;
            }

            int[]? map = Map(times, set.Times);
            return map is null ? null : Array.ConvertAll(map, index => set.Frames[index]);
        }
    }

    internal void StoreDecoded(int width, int height, IReadOnlyList<double> times, GifSourceFrame[] frames)
    {
        if (frames.Length != times.Count || frames.Any(frame => frame.Pixels is null))
        {
            return;
        }

        long bytes = frames.Select(frame => frame.Pixels).Distinct().Sum(pixels => (long)pixels.Length);
        lock (_gate)
        {
            if (!_closed && bytes <= _decodedBudget)
            {
                _decoded = new DecodedSet(width, height, times.ToArray(), frames, bytes);
            }
        }
    }

    /// <summary>
    /// Frame updates of exactly the sequence <paramref name="times"/>, or null. A subset is never
    /// served: each update depends on the frame before it in its own sequence.
    /// </summary>
    internal GifFrameUpdate[]? TryGetEncoded(int width, int height, IReadOnlyList<double> times)
    {
        lock (_gate)
        {
            for (int index = _encoded.Count - 1; index >= 0; index--)
            {
                EncodedSet set = _encoded[index];
                if (set.Width != width || set.Height != height || !set.Times.AsSpan().SequenceEqual(times.ToArray()))
                {
                    continue;
                }

                // Most recently used sets live at the end.
                _encoded.RemoveAt(index);
                _encoded.Add(set);
                return (GifFrameUpdate[])set.Frames.Clone();
            }

            return null;
        }
    }

    internal void StoreEncoded(int width, int height, IReadOnlyList<double> times, GifFrameUpdate[] frames)
    {
        if (frames.Length != times.Count || frames.Any(frame => frame is null))
        {
            return;
        }

        // An unchanged update shares one empty payload; count a small fixed cost per entry.
        long bytes = frames.Sum(frame => (long)frame.Payload.Length + 64);
        lock (_gate)
        {
            if (_closed || bytes > _encodedBudget)
            {
                return;
            }

            _ = _encoded.RemoveAll(set => set.Width == width && set.Height == height);
            _encoded.Add(new EncodedSet(width, height, times.ToArray(), frames, bytes));
            while (_encoded.Sum(set => set.Bytes) > _encodedBudget)
            {
                _encoded.RemoveAt(0);
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _decoded = null;
            _encoded.Clear();
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _decoded = null;
            _encoded.Clear();
        }
    }

    /// <summary>Position of every requested time inside <paramref name="cached"/>, or null.</summary>
    private static int[]? Map(IReadOnlyList<double> requested, double[] cached)
    {
        var map = new int[requested.Count];
        int cursor = 0;
        for (int index = 0; index < requested.Count; index++)
        {
            while (cursor < cached.Length && cached[cursor] < requested[index])
            {
                cursor++;
            }

            if (cursor == cached.Length || cached[cursor] != requested[index])
            {
                return null;
            }

            map[index] = cursor++;
        }

        return map;
    }

    private sealed record DecodedSet(int Width, int Height, double[] Times, GifSourceFrame[] Frames, long Bytes);

    private sealed record EncodedSet(int Width, int Height, double[] Times, GifFrameUpdate[] Frames, long Bytes);
}
