using System.IO;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>
/// The gallery's queue/storage operations, kept free of WPF so they can be unit-tested.
/// </summary>
/// <remarks>
/// <para>
/// The dedicated gallery is a view over the one shared <see cref="CaptureQueue"/>. Every
/// mutation the gallery offers — pin, delete — must keep the in-memory index, the on-disk
/// index, the per-capture recovery metadata, and the backing directory in agreement, and
/// must do so through the same eviction/deletion path the capture pipeline already uses.
/// Concentrating those rules here (instead of in a window's code-behind) is what lets the
/// tests assert them without a message pump.
/// </para>
/// <para>
/// Deletion deliberately goes through <see cref="CaptureQueue.Remove"/>: the queue raises
/// <see cref="CaptureQueue.Evicted"/>, whose existing handler deletes the directory, so a
/// gallery delete and an eviction reclaim bytes by exactly the same code. The controller
/// then saves the index so the removal survives a restart.
/// </para>
/// </remarks>
public sealed class GalleryController
{
    private readonly CaptureQueue _queue;
    private readonly ILogger<GalleryController> _log;

    public GalleryController(CaptureQueue queue, ILogger<GalleryController> log)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Retained captures, newest first. Mirrors the live queue collection.</summary>
    public IReadOnlyList<CaptureRecord> Records => _queue.Records;

    public int Count => _queue.Count;

    public long TotalBytes => _queue.TotalBytes;

    /// <summary>True when pinned records alone hold the queue over its capacity.</summary>
    public bool IsOverCapacityDueToPins => _queue.IsOverCapacityDueToPins;

    /// <summary>
    /// Filters the queue by a free-text query over each record's
    /// <see cref="CaptureRecord.SearchHaystack"/>, newest-first, and buckets the survivors
    /// by calendar day relative to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// Search and grouping are one operation because the gallery always shows grouped,
    /// filtered results: doing them together keeps a single ordering (newest-first within a
    /// day, newest day first) and avoids the caller re-sorting.
    /// </remarks>
    public IReadOnlyList<GalleryGroupedRecords> BuildGroups(string? query, DateTimeOffset now)
    {
        IEnumerable<CaptureRecord> matches = _queue.Records;

        string? trimmed = query?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            matches = matches.Where(r => Matches(r, trimmed));
        }

        return matches
            .OrderByDescending(r => r.CreatedAt)
            .GroupBy(r => GalleryDateGrouping.Resolve(r.CreatedAt, now))
            .OrderByDescending(g => g.Key.SortKey)
            .Select(g => new GalleryGroupedRecords(
                g.Key,
                g.OrderByDescending(r => r.CreatedAt).ToList()))
            .ToList();
    }

    /// <summary>
    /// Toggles the pin on <paramref name="id"/> and persists the change immediately so a pin
    /// survives a crash. Returns the new pinned state, or <see langword="null"/> if the
    /// record is gone.
    /// </summary>
    public bool? TogglePin(Guid id)
    {
        CaptureRecord? record = _queue.Find(id);
        if (record is null)
        {
            return null;
        }

        bool pinned = _queue.TogglePin(id);

        // Persist meta then index so a rebuilt index and a good index agree on the pin.
        _queue.SaveRecordMeta(record);
        _queue.Save();

        _log.LogInformation("Toggled pin on {Id} to {Pinned}", id, pinned);
        return pinned;
    }

    /// <summary>
    /// Removes <paramref name="id"/> from the queue (which fires the eviction handler that
    /// deletes its directory) and saves the index. Returns whether a record was removed.
    /// </summary>
    public bool Delete(Guid id)
    {
        if (!_queue.Remove(id))
        {
            return false;
        }

        _queue.Save();
        _log.LogInformation("Deleted capture {Id} from the gallery", id);
        return true;
    }

    public CaptureRecord? Find(Guid id) => _queue.Find(id);

    /// <summary>
    /// How much of the queue is full-text searchable right now (i.e. carries OCR text).
    /// Drives the "N captures not yet searchable — index now?" affordance.
    /// </summary>
    public OcrCoverage MeasureOcrCoverage() => CaptureTextSearch.MeasureCoverage(_queue.Records);

    /// <summary>
    /// Records still missing OCR text, newest first — the work list for batch OCR indexing.
    /// </summary>
    public IReadOnlyList<CaptureRecord> RecordsMissingOcr() =>
        _queue.Records
            .Where(r => !r.HasOcrText)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

    /// <summary>
    /// Caches an OCR result on the record and persists it immediately (meta then index), so the
    /// recognised text survives a restart and becomes searchable. Best-effort: a persistence
    /// failure is logged, never thrown, because OCR is a non-fatal convenience.
    /// </summary>
    public void CacheOcr(Guid id, string text, string? languageTag)
    {
        CaptureRecord? record = _queue.Find(id);
        if (record is null)
        {
            return;
        }

        record.OcrText = text;
        record.OcrLanguage = languageTag;
        record.UpdatedAt = DateTimeOffset.Now;

        try
        {
            _queue.SaveRecordMeta(record);
            _queue.Save();
            _log.LogInformation("Cached OCR text ({Length} chars) on {Id}", text.Length, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist cached OCR for {Id}", id);
        }
    }

    /// <summary>
    /// Re-measures a finalised capture's byte total and refreshes it in the queue, used
    /// after a re-edit commit changes the files on disk. Best-effort; a metadata refresh
    /// must never throw into the caller.
    /// </summary>
    public void RefreshByteCount(Guid id, long totalBytes)
    {
        _queue.UpdateByteCount(id, totalBytes);
        CaptureRecord? record = _queue.Find(id);
        if (record is not null)
        {
            _queue.SaveRecordMeta(record);
        }

        _queue.Save();
    }

    private static bool Matches(CaptureRecord record, string query) =>
        CaptureTextSearch.IsMatch(record, query);
}

/// <summary>
/// A date group paired with the records that fall inside it, newest-first.
/// </summary>
public sealed record GalleryGroupedRecords(GalleryDateGroup Group, IReadOnlyList<CaptureRecord> Records);
