using System.Collections.ObjectModel;
using System.Text.Json;
using MyCapture.Core.Serialization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.Core.Queue;

/// <summary>
/// Serialised form of the index file.
/// </summary>
internal sealed class CaptureIndexFile
{
    public int SchemaVersion { get; set; } = 2;

    public List<CaptureRecord> Records { get; set; } = [];
}

/// <summary>
/// Reports an eviction so the caller can delete the backing files.
/// </summary>
public sealed record CaptureEvictedEventArgs(CaptureRecord Record, string Reason);

public sealed record CaptureBatchRemovalResult(int RemovedCount, int CleanupPendingCount);

/// <summary>
/// The persistent capture queue.
/// </summary>
/// <remarks>
/// <para>
/// Neither Snipaste nor AlCapture retains a re-openable history across restarts;
/// this type is the product's central differentiator. It keeps a bounded, ordered set
/// of captures alive between sessions, and every capture stays re-editable because
/// its annotation layer is stored beside it.
/// </para>
/// <para>
/// Ordering is newest-created-first for gallery display. Eviction uses least-recent activity
/// (<see cref="CaptureRecord.UpdatedAt"/>) so a capture the user just re-edited is not the one
/// that disappears when its larger rendered generation crosses the byte cap.
/// </para>
/// <para>
/// Eviction is enforced on insert against two independent caps. Item count alone is
/// not enough — 300 captures of a 4K display is several gigabytes — and a byte cap
/// alone is not enough either, because a few hundred tiny captures should still be
/// bounded so the gallery stays navigable.
/// </para>
/// <para>
/// Pinned records are never evicted. If pins alone exceed a cap the queue stops
/// evicting rather than breaking the user's explicit instruction, and reports the
/// condition so the UI can say so.
/// </para>
/// </remarks>
public sealed class CaptureQueue
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonDefaults.Compact;

    private static readonly JsonSerializerOptions MetaSerializerOptions = JsonDefaults.Readable;

    private readonly AppPaths _paths;
    private readonly ILogger<CaptureQueue> _log;
    private readonly BatchRecordCollection _records = [];
    private readonly Lock _writeGate = new();
    private readonly SemaphoreSlim _publicationSlots = new(8, 8);
    private Task _publicationTail = Task.CompletedTask;
    internal Action<string, string>? BeforePublicationWriteForTest { get; set; }
    private readonly object _evictionLeaseGate = new();
    private readonly Dictionary<Guid, int> _evictionLeaseCounts = [];
    private int _evictionSuspensionCount;

    private QueueSettings _limits;
    private long _totalBytes;

    public CaptureQueue(AppPaths paths, QueueSettings limits, ILogger<CaptureQueue> log)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Records = new ReadOnlyObservableCollection<CaptureRecord>(_records);
    }

    /// <summary>
    /// Retained captures, newest first.
    /// </summary>
    public ReadOnlyObservableCollection<CaptureRecord> Records { get; }

    public int Count => _records.Count;

    public long TotalBytes => _totalBytes;

    /// <summary>
    /// True when pinned records plus the protected current capture exceed a configured cap,
    /// so eviction can no longer bring the queue back within limits without data loss.
    /// </summary>
    public bool IsOverCapacityDueToPins { get; private set; }

    /// <summary>
    /// Raised for each evicted record. The handler is responsible for deleting files.
    /// </summary>
    /// <remarks>
    /// File deletion is not performed here so that the queue stays a pure in-memory
    /// index with one serialisation concern, and so a deletion failure cannot leave
    /// the index and the filesystem disagreeing about what exists.
    /// </remarks>
    public event EventHandler<CaptureEvictedEventArgs>? Evicted;

    /// <summary>
    /// Temporarily excludes a record from capacity eviction while an editor or persistence
    /// transaction owns it. Leases are reference-counted so a window-level edit lease and a
    /// short finalisation lease can safely overlap.
    /// </summary>
    public IDisposable AcquireEvictionLease(Guid id)
    {
        lock (_evictionLeaseGate)
        {
            _evictionLeaseCounts.TryGetValue(id, out int count);
            _evictionLeaseCounts[id] = checked(count + 1);
        }

        return new EvictionLease(this, id);
    }

    /// <summary>
    /// Defers all capacity eviction until the returned scope is disposed. Startup uses this
    /// while crash journals are recovered so an over-capacity load cannot delete a record's
    /// rollback files before persistence has inspected them.
    /// </summary>
    public IDisposable SuspendEviction()
    {
        _ = Interlocked.Increment(ref _evictionSuspensionCount);
        return new EvictionSuspension(this);
    }

    public void UpdateLimits(QueueSettings limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        EnforceLimits();
    }

    /// <summary>Expires managed history in creation order. Pins and active editors retain ownership.
    /// Explicit exports live outside the managed capture directories and are never visited.</summary>
    public int ExpireHistory(DateTimeOffset now)
    {
        if (Volatile.Read(ref _evictionSuspensionCount) > 0)
        {
            return 0;
        }

        CaptureRecord[] expired = _records
            .Where(record => record.IsImage && record.CreatedAt <= now.AddDays(-7)
                             && !record.IsPinned && !IsEvictionLeased(record.Id))
            .OrderBy(record => record.CreatedAt)
            .ToArray();
        foreach (CaptureRecord record in expired)
        {
            _records.Remove(record);
            _totalBytes = Math.Max(0, _totalBytes - record.TotalBytes);
            Evicted?.Invoke(this, new CaptureEvictedEventArgs(record, "retention-age"));
        }

        if (expired.Length > 0)
        {
            UpdatePinPressureFlag();
            Save();
        }
        return expired.Length;
    }

    /// <summary>
    /// Loads the index, falling back to rebuilding it from the capture directories.
    /// </summary>
    public void Load()
    {
        _paths.EnsureCreated();
        AtomicFile.CleanUpTemp(_paths.IndexFile);

        string? text = AtomicFile.ReadAllTextWithRecovery(
            _paths.IndexFile,
            candidate => TryParseIndex(candidate, out _));

        List<CaptureRecord> records;

        if (text is not null && TryParseIndex(text, out CaptureIndexFile? index) && index is not null)
        {
            records = index.Records;
            HashSet<Guid> indexedIds = records.Select(record => record.Id).ToHashSet();
            // A v1 build did not understand video records and could rewrite index.json without
            // them. On the first v2 load, merge every durable meta sidecar once; later v2 loads
            // keep the fast pending-only startup path.
            bool legacyIndex = index.SchemaVersion == 1;
            foreach (CaptureRecord recovered in RebuildFromDisk(pendingOnly: !legacyIndex))
            {
                if (indexedIds.Add(recovered.Id))
                {
                    records.Add(recovered);
                    _log.LogWarning(
                        "Recovered unindexed capture {Id} from its durable sidecar",
                        recovered.Id);
                }
            }
        }
        else
        {
            if (File.Exists(_paths.IndexFile))
            {
                _log.LogWarning("Capture index unreadable; rebuilding from capture directories");
            }

            records = RebuildFromDisk();
        }

        // Drop records whose files are gone. This happens when a user clears the
        // capture folder by hand, and a phantom entry produces a broken thumbnail
        // that looks like data loss.
        records = records
            .Where(record =>
            {
                if (!TryResolveDirectory(record, out string directory))
                {
                    _log.LogWarning(
                        "Dropped capture {Id} because its storage path escaped the captures root",
                        record.Id);
                    return false;
                }

                string primaryFile = record.IsVideo
                    ? CaptureFileNames.VideoSource
                    : CaptureFileNames.Original;
                return File.Exists(Path.Combine(directory, primaryFile));
            })
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        _records.Clear();
        foreach (CaptureRecord record in records)
        {
            _records.Add(record);
        }

        RecalculateTotalBytes();
        EnforceLimits();

        _log.LogInformation(
            "Capture queue loaded: {Count} records, {Megabytes:0.0} MB",
            _records.Count,
            _totalBytes / 1024.0 / 1024.0);
    }

    /// <summary>
    /// Inserts <paramref name="record"/> at the head and enforces the caps.
    /// </summary>
    public void Add(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrEmpty(record.RelativeDirectory))
        {
            record.RelativeDirectory = BuildRelativeDirectory(record.Id, record.CreatedAt);
        }

        if (!TryResolveDirectory(record, out _))
        {
            throw new ArgumentException(
                "The capture directory must be a descendant of the configured captures root.",
                nameof(record));
        }

        _records.Insert(0, record);
        _totalBytes += record.TotalBytes;

        EnforceLimits();
    }

    public bool Remove(Guid id)
    {
        CaptureRecord? record = Find(id);
        if (record is null || IsEvictionLeased(id))
        {
            return false;
        }

        _records.Remove(record);
        _totalBytes -= record.TotalBytes;
        if (_totalBytes < 0)
        {
            _totalBytes = 0;
        }

        Evicted?.Invoke(this, new CaptureEvictedEventArgs(record, "manual"));
        UpdatePinPressureFlag();
        return true;
    }

    /// <summary>
    /// Owner-context batch removal with one collection reset and one durable index write.
    /// Admission precedes readiness/lease validation. Manual removal includes pins and videos.
    /// Only this API delivers Evicted on the disk worker, after index durability; handlers must
    /// not touch UI. The existing synchronous Remove contract is unchanged.
    /// </summary>
    public async Task<CaptureBatchRemovalResult> RemoveManyAsync(IEnumerable<Guid> ids, Func<Guid, bool>? canRemove = null,
        CancellationToken cancellationToken = default)
    {
        Guid[] requested = ids.Distinct().ToArray();
        using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        List<CaptureRecord> removed;
        lock (_evictionLeaseGate)
        {
            removed = _records.Where(r => requested.Contains(r.Id)
                && !_evictionLeaseCounts.ContainsKey(r.Id) && (canRemove?.Invoke(r.Id) ?? true)).ToList();
            if (removed.Count == 0) return new(0, 0);
            _records.ReplaceWith(_records.Except(removed).ToArray());
            _totalBytes = Math.Max(0, _totalBytes - removed.Sum(r => r.TotalBytes));
            UpdatePinPressureFlag();
        }
        Task publication;
        int cleanupPending = 0;
        lock (_writeGate)
        {
            string json = JsonSerializer.Serialize(new CaptureIndexFile { Records = [.. _records] }, SerializerOptions);
            publication = EnqueuePublication(reservation, [(_paths.IndexFile, json)], () =>
            {
                foreach (CaptureRecord record in removed)
                {
                    try
                    {
                        // Revalidate ownership/reparse boundaries immediately before the handler.
                        if (!TryResolveDirectory(record, out _)) { cleanupPending++; continue; }
                        Evicted?.Invoke(this, new CaptureEvictedEventArgs(record, "manual-batch"));
                        if (!TryResolveDirectory(record, out string directory) || Directory.Exists(directory)) cleanupPending++;
                    }
                    catch (Exception ex)
                    {
                        cleanupPending++;
                        _log.LogWarning(ex, "Could not clean up removed capture {Id}", record.Id);
                    }
                }
            });
        }
        try { await publication; }
        catch (Exception originalFailure)
        {
            // Restore only this operation's missing records. Adds/edits that happened while
            // disk was pending survive; a corrective FIFO snapshot follows any newer saves.
            CaptureRecord[] restore = removed.Where(r => Find(r.Id) is null).ToArray();
            _records.ReplaceWith(_records.Concat(restore).OrderByDescending(r => r.CreatedAt).ToArray());
            _totalBytes += restore.Sum(r => r.TotalBytes);
            UpdatePinPressureFlag();
            Task correction;
            lock (_writeGate)
            {
                string json = JsonSerializer.Serialize(new CaptureIndexFile { Records = [.. _records] }, SerializerOptions);
                correction = EnqueuePublication(null, [(_paths.IndexFile, json)]);
            }
            try { await correction; }
            catch (Exception correctionFailure)
            {
                throw new AggregateException("Batch deletion and its corrective index write both failed; files were retained.",
                    originalFailure, correctionFailure);
            }
            throw;
        }
        return new(removed.Count, cleanupPending);
    }

    private sealed class BatchRecordCollection : ObservableCollection<CaptureRecord>
    {
        internal void ReplaceWith(IReadOnlyList<CaptureRecord> records)
        {
            Items.Clear();
            foreach (CaptureRecord record in records) Items.Add(record);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }

    public CaptureRecord? Find(Guid id) => _records.FirstOrDefault(r => r.Id == id);

    public bool TogglePin(Guid id)
    {
        CaptureRecord? record = Find(id);
        if (record is null)
        {
            return false;
        }

        record.IsPinned = !record.IsPinned;
        record.UpdatedAt = DateTimeOffset.Now;

        // Unpinning can make the queue evictable again.
        EnforceLimits();
        return record.IsPinned;
    }

    /// <summary>
    /// Records that a capture's files changed size, keeping the byte total accurate.
    /// </summary>
    public void UpdateByteCount(Guid id, long newTotalBytes)
    {
        CaptureRecord? record = Find(id);
        if (record is null)
        {
            return;
        }

        _totalBytes += newTotalBytes - record.TotalBytes;
        record.TotalBytes = Math.Max(0, newTotalBytes);
        record.UpdatedAt = DateTimeOffset.Now;

        if (_totalBytes < 0)
        {
            _totalBytes = 0;
        }

        EnforceLimits();
    }

    /// <summary>
    /// Persists the index atomically.
    /// </summary>
    public void Save()
    {
        Task publication;
        lock (_writeGate)
        {
            var index = new CaptureIndexFile { Records = [.. _records] };
            string json = JsonSerializer.Serialize(index, SerializerOptions);
            publication = EnqueuePublication(null, [(_paths.IndexFile, json)]);
        }
        // The writer never needs this gate or the caller dispatcher. This is also the exit drain.
        publication.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Writes the per-capture metadata copy used to rebuild a lost index.
    /// </summary>
    public void SaveRecordMeta(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            SaveRecordMetaOrThrow(record);
        }
        catch (IOException ex)
        {
            // Recovery metadata is best-effort. Failing to write it must not fail the
            // capture the user just took.
            _log.LogWarning(ex, "Could not write recovery metadata for {Id}", record.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Could not write recovery metadata for {Id}", record.Id);
        }
    }

    /// <summary>
    /// Writes recovery metadata and propagates persistence failures. Crash-recovery protocols
    /// use this strict form so they never delete a pending marker/journal unless both meta and
    /// index durably name the same generation.
    /// </summary>
    public void SaveRecordMetaOrThrow(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Task publication;
        lock (_writeGate)
        {
            string path = GetFilePath(record, CaptureFileNames.Meta);
            string json = JsonSerializer.Serialize(record, MetaSerializerOptions);
            publication = EnqueuePublication(null, [(path, json)]);
        }
        publication.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Waits for capacity before a caller mutates or serializes owner-thread records. After
    /// awaiting, revalidate the record and generation on its owner context before publication.
    /// </summary>
    public async Task<CaptureWriteReservation> ReservePublicationAsync(CancellationToken cancellationToken = default)
    {
        await _publicationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new CaptureWriteReservation(_publicationSlots);
    }

    /// <summary>
    /// Serializes on the calling owner thread, then durably writes metadata and index as one
    /// ordered job. Hold an eviction lease until completion. No mutable record reaches the writer.
    /// </summary>
    public Task PublishRecordAsync(CaptureRecord record, CaptureWriteReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_writeGate)
        {
            string path = GetFilePath(record, CaptureFileNames.Meta);
            string metadata = JsonSerializer.Serialize(record, MetaSerializerOptions);
            string index = JsonSerializer.Serialize(new CaptureIndexFile { Records = [.. _records] }, SerializerOptions);
            return EnqueuePublication(reservation, [(path, metadata), (_paths.IndexFile, index)]);
        }
    }

    // Called only under _writeGate, after immutable serialization. A failed predecessor does
    // not poison later writes: each caller observes its own failure, and recovery may publish next.
    private Task EnqueuePublication(CaptureWriteReservation? reservation, (string Path, string Content)[] files,
        Action? afterDurableWrite = null)
    {
        // Synchronous owner saves bypass the async pool: slots can belong to awaiters whose
        // continuations still need that owner thread. Waiting for those slots here deadlocks.
        reservation?.Accept(_publicationSlots);
        _publicationTail = _publicationTail.ContinueWith(_ =>
        {
            try
            {
                foreach ((string path, string content) in files)
                {
                    BeforePublicationWriteForTest?.Invoke(path, content);
                    AtomicFile.WriteAllText(path, content);
                }
                afterDurableWrite?.Invoke();
            }
            finally { reservation?.Complete(); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return _publicationTail;
    }

    public string GetDirectory(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!TryResolveDirectory(record, out string directory))
        {
            throw new InvalidDataException(
                $"Capture {record.Id} has a directory outside the configured captures root.");
        }

        return directory;
    }

    public string GetFilePath(CaptureRecord record, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName is "." or ".."
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("A capture file name cannot contain a directory.", nameof(fileName));
        }

        return Path.Combine(GetDirectory(record), fileName);
    }

    public static string BuildRelativeDirectory(Guid id, DateTimeOffset createdAt) =>
        Path.Combine(
            createdAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            id.ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    private bool TryResolveDirectory(CaptureRecord record, out string directory)
    {
        directory = string.Empty;
        string relative = record.RelativeDirectory;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return false;
        }

        try
        {
            string root = Path.GetFullPath(_paths.CapturesRoot);
            string rootPrefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            // Path.Combine preserves the exact configured root prefix for legitimate relative
            // paths. An ordinal comparison also keeps a case-sensitive NTFS sibling named only
            // by different casing from masquerading as the configured root.
            if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal)
                || ContainsReparsePointBelowRoot(root, candidate))
            {
                return false;
            }

            directory = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePointBelowRoot(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                // Once a component does not exist, no deeper component can exist either.
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void EnforceLimits()
    {
        if (Volatile.Read(ref _evictionSuspensionCount) > 0)
        {
            return;
        }

        // Walk from the oldest end, skipping pinned records.
        int guard = 0;
        while (IsOverCapacity() && guard++ < 10_000)
        {
            CaptureRecord? victim = FindOldestUnpinned();
            if (victim is null)
            {
                // Everything remaining is pinned. Stop rather than violating an
                // explicit user instruction, and let the UI report the condition.
                break;
            }

            string reason = _records.Count > _limits.MaxItems ? "item-limit" : "byte-limit";

            _records.Remove(victim);
            _totalBytes -= victim.TotalBytes;
            if (_totalBytes < 0)
            {
                _totalBytes = 0;
            }

            _log.LogInformation(
                "Evicting capture {Id} from {CreatedAt:u} ({Reason})",
                victim.Id,
                victim.CreatedAt,
                reason);

            Evicted?.Invoke(this, new CaptureEvictedEventArgs(victim, reason));
        }

        UpdatePinPressureFlag();
    }

    private bool IsOverCapacity() =>
        _records.Count > _limits.MaxItems || _totalBytes > _limits.MaxBytes;

    private void UpdatePinPressureFlag()
    {
        int pinnedCount = 0;
        long pinnedBytes = 0;
        foreach (CaptureRecord record in _records)
        {
            if (!record.IsPinned)
            {
                continue;
            }

            pinnedCount++;
            pinnedBytes = SaturatingAdd(pinnedBytes, record.TotalBytes);
        }

        int retainedCount = pinnedCount;
        long retainedBytes = pinnedBytes;
        CaptureRecord? current = _records.FirstOrDefault();
        if (current is not null && !current.IsPinned)
        {
            retainedCount++;
            retainedBytes = SaturatingAdd(retainedBytes, current.TotalBytes);
        }

        // A short edit/recovery lease can defer eviction too, but it is intentionally absent
        // from this projection: otherwise a transient operation would show a lasting pin
        // warning. The newest unpinned item is included because the queue explicitly protects
        // the capture the user just made from vanishing behind older pins.
        IsOverCapacityDueToPins = IsOverCapacity()
                                  && pinnedCount > 0
                                  && (retainedCount > _limits.MaxItems
                                      || retainedBytes > _limits.MaxBytes);
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - Math.Max(0, right) ? long.MaxValue : left + Math.Max(0, right);

    private CaptureRecord? FindOldestUnpinned()
    {
        var candidates = new List<CaptureRecord>();
        CaptureRecord? soleUnpinned = null;
        int unpinnedCount = 0;
        foreach (CaptureRecord record in _records)
        {
            if (record.IsPinned)
            {
                continue;
            }

            unpinnedCount++;
            soleUnpinned = record;
            if (!IsEvictionLeased(record.Id))
            {
                candidates.Add(record);
            }
        }

        // A new capture inserted ahead of older pins is the user's current working result; do
        // not make it disappear immediately just because those pins consume the cap. The only
        // unpinned capture can still be evicted when it is an older item explicitly unpinned to
        // relieve pressure. A queue containing only one oversized capture also stays useful.
        CaptureRecord? newest = _records.FirstOrDefault();
        if ((unpinnedCount == 1
             && soleUnpinned is not null
             && ReferenceEquals(newest, soleUnpinned))
            || (candidates.Count == 1 && ReferenceEquals(newest, candidates[0])))
        {
            return null;
        }

        return candidates
            .OrderBy(record => record.UpdatedAt)
            .ThenBy(record => record.CreatedAt)
            .FirstOrDefault();
    }

    private bool IsEvictionLeased(Guid id)
    {
        lock (_evictionLeaseGate)
        {
            return _evictionLeaseCounts.ContainsKey(id);
        }
    }

    private void ReleaseEvictionLease(Guid id)
    {
        lock (_evictionLeaseGate)
        {
            if (!_evictionLeaseCounts.TryGetValue(id, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                _evictionLeaseCounts.Remove(id);
            }
            else
            {
                _evictionLeaseCounts[id] = count - 1;
            }
        }

        EnforceLimits();
    }

    private void ReleaseEvictionSuspension()
    {
        int remaining = Interlocked.Decrement(ref _evictionSuspensionCount);
        if (remaining < 0)
        {
            _ = Interlocked.Exchange(ref _evictionSuspensionCount, 0);
            throw new InvalidOperationException("Capture queue eviction suspension was released too many times.");
        }

        if (remaining == 0)
        {
            EnforceLimits();
        }
    }

    private sealed class EvictionLease(CaptureQueue owner, Guid id) : IDisposable
    {
        private CaptureQueue? _owner = owner;

        public void Dispose()
        {
            CaptureQueue? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseEvictionLease(id);
        }
    }

    private sealed class EvictionSuspension(CaptureQueue owner) : IDisposable
    {
        private CaptureQueue? _owner = owner;

        public void Dispose()
        {
            CaptureQueue? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseEvictionSuspension();
        }
    }

    private void RecalculateTotalBytes()
    {
        long total = 0;
        foreach (CaptureRecord record in _records)
        {
            total += record.TotalBytes;
        }

        _totalBytes = total;
    }

    private static bool TryParseIndex(string text, out CaptureIndexFile? index)
    {
        index = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            index = JsonSerializer.Deserialize<CaptureIndexFile>(text, SerializerOptions);
            if (index is null
                || index.SchemaVersion is not (1 or 2)
                || index.Records is null)
            {
                index = null;
                return false;
            }

            var ids = new HashSet<Guid>();
            foreach (CaptureRecord? record in index.Records)
            {
                if (record is null || !IsValidIndexedRecord(record) || !ids.Add(record.Id))
                {
                    index = null;
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidIndexedRecord(CaptureRecord record)
    {
        bool videoValuesValid = record.IsVideo
            ? double.IsFinite(record.DurationMs)
              && record.DurationMs > 0
              && record.FrameRate is >= 1 and <= 120
              && record.FrameCount > 0
            : double.IsFinite(record.DurationMs)
              && record.DurationMs >= 0
              && record.FrameRate >= 0
              && record.FrameCount >= 0;
        return record.Id != Guid.Empty
            && Enum.IsDefined(record.MediaKind)
            && record.ContentRevision >= 0
            && record.Width is >= 1 and <= 65_535
            && record.Height is >= 1 and <= 65_535
            && videoValuesValid
            && double.IsFinite(record.DpiScale)
            && record.DpiScale is > 0 and <= 16
            && record.TotalBytes >= 0
            && (record.OcrContentRevision is null || record.OcrContentRevision >= 0)
            && record.SourceMonitor is not null
            && record.SourceWindowTitle is not null
            && record.Title is not null
            && IsSafeRelativeDirectory(record.RelativeDirectory);
    }

    private static bool IsSafeRelativeDirectory(string? relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory)
            || Path.IsPathRooted(relativeDirectory)
            || relativeDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        string[] segments = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            && segments.All(segment => segment is not "." and not "..");
    }

    /// <summary>
    /// Reconstructs the index by walking the capture tree for <c>meta.json</c> files.
    /// </summary>
    /// <remarks>
    /// This is why each capture carries a metadata copy. Without it, an unreadable
    /// index would mean the user's entire history becomes a folder of anonymous PNGs.
    /// </remarks>
    private List<CaptureRecord> RebuildFromDisk(bool pendingOnly = false)
    {
        var recovered = new Dictionary<Guid, CaptureRecord>();

        if (!Directory.Exists(_paths.CapturesRoot))
        {
            return [];
        }

        var sidecarFiles = new List<string>();
        try
        {
            // Pending first, committed metadata second: if both exist after a crash just before
            // marker cleanup, the fully committed meta record wins for the same ID.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            sidecarFiles.AddRange(Directory.EnumerateFiles(
                // AppPaths canonicalizes the local capture root; the filename is a fixed private
                // sidecar and reparse points are excluded from the recursive walk.
                // codeql[cs/path-injection]
                _paths.CapturesRoot, CaptureFileNames.OriginalPending, options));
            sidecarFiles.AddRange(Directory.EnumerateFiles(
                // codeql[cs/path-injection]
                _paths.CapturesRoot, CaptureFileNames.VideoPending, options));
            if (!pendingOnly)
            {
                sidecarFiles.AddRange(Directory.EnumerateFiles(
                    _paths.CapturesRoot, CaptureFileNames.Meta, options));
            }
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not enumerate capture directories during index rebuild");
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Access denied enumerating capture directories during index rebuild");
            return [];
        }

        foreach (string metaPath in sidecarFiles)
        {
            try
            {
                string json = File.ReadAllText(metaPath);
                CaptureRecord? record = JsonSerializer.Deserialize<CaptureRecord>(json, SerializerOptions);
                if (record is null)
                {
                    continue;
                }

                // Trust the location on disk over the stored path: the folder may have
                // been moved.
                string? directory = Path.GetDirectoryName(metaPath);
                if (directory is not null)
                {
                    record.RelativeDirectory = Path.GetRelativePath(_paths.CapturesRoot, directory);
                }

                recovered[record.Id] = record;
            }
            catch (JsonException)
            {
                // One unreadable sidecar costs one capture, not the rebuild.
            }
            catch (IOException)
            {
            }
        }

        _log.LogInformation("Discovered {Count} capture recovery sidecar record(s)", recovered.Count);
        return recovered.Values.ToList();
    }
}
