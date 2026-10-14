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

    internal CaptureQueue Queue => _queue;

    public RecoverableLibraryService? Recovery { get; set; }

    public int Count => _queue.Count;

    public long TotalBytes => _queue.TotalBytes;

    /// <summary>True when pins plus the protected current capture hold the queue over capacity.</summary>
    public bool IsOverCapacityDueToPins => _queue.IsOverCapacityDueToPins;

    /// <summary>
    /// Filters the queue by a free-text query over each record's
    /// <see cref="CaptureRecord.SearchHaystack"/>, newest-first, and buckets the survivors
    /// by calendar day relative to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Search and grouping are one operation because the gallery always shows grouped,
    /// filtered results: doing them together keeps a single ordering (newest-first within a
    /// day, newest day first) and avoids the caller re-sorting.
    /// </para>
    /// <para>
    /// With <paramref name="includeFolders"/>, the groups describe a location being browsed, like a
    /// File Explorer folder: they carry the folders a user can open from there (root folders on
    /// their creation day, or a folder's direct children) and only the captures directly in that
    /// location (see <see cref="RecordsInView"/>). A group can then hold folders only, so an
    /// empty new folder is still visible. Without it the result covers every folder under the
    /// location, as a search or a media view needs.
    /// </para>
    /// </remarks>
    public IReadOnlyList<GalleryGroupedRecords> BuildGroups(string? query, DateTimeOffset now,
        CaptureFolderScope scope = default, GallerySortOrder sort = GallerySortOrder.Newest,
        bool includeFolders = false, IDictionary<Guid, CaptureMatchField>? searchMatchFields = null)
    {
        CaptureFolder[] folders = includeFolders ? FolderCardsInScope(scope) : [];
        IEnumerable<CaptureRecord> matches = RecordsInView(scope, browsing: includeFolders);
        string? trimmed = query?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            CaptureSearchQuery prepared = CaptureSearchQuery.Parse(trimmed);
            matches = matches.Where(record =>
            {
                bool matched = MatchesSearch(record, prepared, out CaptureMatchField fields,
                    collectFields: searchMatchFields is not null);
                if (matched && searchMatchFields is not null) searchMatchFields[record.Id] = fields;
                return matched;
            });
        }
        CaptureRecord[] ordered = (sort switch
        {
            GallerySortOrder.Oldest => matches.OrderBy(record => record.CreatedAt).ThenBy(record => record.Id),
            GallerySortOrder.Name => matches.OrderBy(record => _queue.GetExportFileName(record),
                StringComparer.CurrentCultureIgnoreCase).ThenBy(record => record.CreatedAt).ThenBy(record => record.Id),
            _ => matches.OrderByDescending(record => record.CreatedAt).ThenBy(record => record.Id),
        }).ToArray();
        if (!scope.IsAll || sort != GallerySortOrder.Newest)
        {
            if (ordered.Length == 0 && folders.Length == 0) return [];
            string heading = scope.FolderId is { } folderId ? _queue.GetFolderDisplayPath(folderId)
                : scope.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    ?? UiText.Get("Folder410.TreeAll");
            return [new GalleryGroupedRecords(new GalleryDateGroup(
                scope.Date ?? DateOnly.FromDateTime(now.DateTime), heading), ordered) { Folders = folders }];
        }
        ILookup<GalleryDateGroup, CaptureRecord> recordsByDay =
            ordered.ToLookup(record => GalleryDateGrouping.Resolve(record.CreatedAt, now));
        ILookup<GalleryDateGroup, CaptureFolder> foldersByDay =
            folders.ToLookup(folder => GalleryDateGrouping.Resolve(folder.CreatedAt, now));
        return recordsByDay.Select(group => group.Key).Union(foldersByDay.Select(group => group.Key))
            .OrderByDescending(day => day.SortKey)
            .Select(day => new GalleryGroupedRecords(day, recordsByDay[day].ToArray()) { Folders = foldersByDay[day].ToArray() })
            .ToArray();
    }

    private bool MatchesSearch(CaptureRecord record, CaptureSearchQuery query, out CaptureMatchField fields,
        bool collectFields)
    {
        string? folder = record.FolderId is { } id && query.Tokens.Any(token => token.Field == CaptureSearchField.Folder)
            ? _queue.GetFolderDisplayPath(id) : null;
        string fileName = _queue.GetExportFileName(record);
        fields = CaptureMatchField.None;
        return collectFields ? query.TryMatch(record, out fields, folder, fileName) : query.IsMatch(record, folder, fileName);
    }

    /// <summary>
    /// The captures a library view lists for <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Browsing a location works like File Explorer: the library root and a date list only the
    /// captures that are in no folder, and a folder lists only its own captures. Everything filed
    /// deeper is one folder card away, so a capture dropped on a folder leaves the view it was
    /// dragged from at once. Payloads never move; only the listing follows the folder metadata.
    /// </para>
    /// <para>
    /// A search, or the image, video and kept views, cuts across locations instead and covers
    /// every folder under <paramref name="scope"/> (the whole library from the root), so a capture
    /// filed in a folder never becomes unreachable. Such results name the folder on each card
    /// that lives elsewhere. Folder deletion and export keep the subtree meaning of
    /// <see cref="CaptureQueue.RecordsInScope"/>.
    /// </para>
    /// </remarks>
    internal IEnumerable<CaptureRecord> RecordsInView(CaptureFolderScope scope, bool browsing) => browsing
        ? _queue.Records.Where(record => IsDirectlyIn(record, scope))
        : _queue.RecordsInScope(scope);

    /// <summary>True when <paramref name="record"/> is listed while browsing <paramref name="scope"/>.</summary>
    internal static bool IsDirectlyIn(CaptureRecord record, CaptureFolderScope scope) =>
        record.FolderId == scope.FolderId
        && (scope.Date is not { } date || DateOnly.FromDateTime(record.CreatedAt.DateTime) == date);

    /// <summary>Folders opened from the library body: roots for All or a date, children for a folder.</summary>
    private CaptureFolder[] FolderCardsInScope(CaptureFolderScope scope) => _queue.Folders
        .Where(folder => scope.FolderId is { } parent ? folder.ParentId == parent
            : folder.ParentId is null && (scope.Date is not { } date || folder.CreatedDate == date))
        .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    /// <summary>
    /// Toggles the pin on <paramref name="id"/> and persists the change immediately so a pin
    /// survives a crash. Returns the new pinned state, or <see langword="null"/> if the
    /// record is gone.
    /// </summary>
    public bool? TogglePin(Guid id)
    {
        CaptureRecord? record = Find(id);
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

    public CaptureRecord? Find(Guid id) => _queue.IsRemovalPending(id) ? null : _queue.Find(id);

    public Task<CaptureBatchTagResult> UpdateTagsAsync(IEnumerable<Guid> ids,
        IEnumerable<CaptureTagChange> changes, CancellationToken cancellationToken = default) =>
        _queue.UpdateTagsAsync(ids, changes, cancellationToken);

    /// <summary>Edits only user metadata; serializes publication with OCR and image edits.</summary>
    public async Task<bool> UpdateOrganizationAsync(Guid id, string? title, string? tags,
        CancellationToken cancellationToken = default)
    {
        string normalizedTitle = CaptureOrganization.NormalizeTitle(title);
        string normalizedTags = CaptureOrganization.NormalizeTags(tags);
        using CaptureWriteReservation reservation = await _queue.ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CaptureRecord? record = _queue.Find(id);
        if (record is null) return false;
        using IDisposable lease = _queue.AcquireEvictionLease(id);
        string previousTitle = record.Title;
        string previousTags = record.Tags;
        DateTimeOffset previousUpdatedAt = record.UpdatedAt;
        record.Title = normalizedTitle;
        record.Tags = normalizedTags;
        record.UpdatedAt = DateTimeOffset.Now;
        try
        {
            await _queue.PublishRecordAsync(record, reservation);
        }
        catch
        {
            record.Title = previousTitle;
            record.Tags = previousTags;
            record.UpdatedAt = previousUpdatedAt;
            throw;
        }
        // Do not log user-authored labels, which can contain private project names.
        _log.LogInformation("Updated organization metadata for capture {Id}", id);
        return true;
    }

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
            .Where(r => !r.HasCurrentOcrIndex)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

    /// <summary>
    /// Caches an OCR result on the record and persists it immediately (meta then index), so the
    /// recognised text survives a restart and becomes searchable. Best-effort: a persistence
    /// failure is logged, never thrown, because OCR is a non-fatal convenience.
    /// </summary>
    public bool CacheOcr(
        Guid id,
        string text,
        string? languageTag,
        long? expectedContentRevision = null)
    {
        CaptureRecord? record = _queue.Find(id);
        if (record is null)
        {
            return false;
        }

        if (expectedContentRevision is { } expected
            && record.ContentRevision != expected)
        {
            _log.LogInformation(
                "Discarded OCR result for stale capture generation {Id}: expected {Expected}, current {Current}",
                id,
                expected,
                record.ContentRevision);
            return false;
        }

        record.OcrText = text;
        record.OcrLanguage = languageTag;
        record.OcrContentRevision = record.ContentRevision;
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

        return true;
    }

    /// <summary>Publishes OCR without waiting for disk on the owner thread. Admission precedes revision validation.</summary>
    public async Task<bool> CacheOcrAsync(
        Guid id,
        string text,
        string? languageTag,
        long? expectedContentRevision = null,
        CancellationToken cancellationToken = default)
    {
        using CaptureWriteReservation reservation = await _queue.ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        CaptureRecord? record = _queue.Find(id);
        if (record is null || (expectedContentRevision is { } expected && record.ContentRevision != expected))
            return false;

        using IDisposable evictionLease = _queue.AcquireEvictionLease(id);
        record.OcrText = text;
        record.OcrLanguage = languageTag;
        record.OcrContentRevision = record.ContentRevision;
        record.UpdatedAt = DateTimeOffset.Now;
        try
        {
            // Serialization finishes here on the owner context; only immutable strings go to disk.
            await _queue.PublishRecordAsync(record, reservation);
            _log.LogInformation("Cached OCR text ({Length} chars) on {Id}", text.Length, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist cached OCR for {Id}", id);
            return false;
        }
        return true;
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
/// A date group paired with the records that fall inside it, newest-first, and the folders
/// shown ahead of them in name order.
/// </summary>
public sealed record GalleryGroupedRecords(GalleryDateGroup Group, IReadOnlyList<CaptureRecord> Records)
{
    public IReadOnlyList<CaptureFolder> Folders { get; init; } = [];
}
