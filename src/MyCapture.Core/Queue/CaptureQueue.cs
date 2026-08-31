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
    public int SchemaVersion { get; set; } = 1;

    public List<CaptureRecord> Records { get; set; } = [];
}

/// <summary>
/// Reports an eviction so the caller can delete the backing files.
/// </summary>
public sealed record CaptureEvictedEventArgs(CaptureRecord Record, string Reason);

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
    private readonly ObservableCollection<CaptureRecord> _records = [];
    private readonly Lock _writeGate = new();
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
            foreach (CaptureRecord recovered in RebuildFromDisk(pendingOnly: true))
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
            .Where(r => File.Exists(GetFilePath(r, CaptureFileNames.Original)))
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
        // Snapshot and serialization belong inside the same gate as the atomic write. If a
        // background metadata operation serialized an old generation before waiting here, it
        // could otherwise overwrite a newer editor commit after that commit released the gate.
        lock (_writeGate)
        {
            var index = new CaptureIndexFile { Records = [.. _records] };
            string json = JsonSerializer.Serialize(index, SerializerOptions);
            AtomicFile.WriteAllText(_paths.IndexFile, json);
        }
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

        lock (_writeGate)
        {
            string path = GetFilePath(record, CaptureFileNames.Meta);
            string json = JsonSerializer.Serialize(record, MetaSerializerOptions);
            AtomicFile.WriteAllText(path, json);
        }
    }

    public string GetDirectory(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Path.Combine(_paths.CapturesRoot, record.RelativeDirectory);
    }

    public string GetFilePath(CaptureRecord record, string fileName) =>
        Path.Combine(GetDirectory(record), fileName);

    public static string BuildRelativeDirectory(Guid id, DateTimeOffset createdAt) =>
        Path.Combine(
            createdAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            id.ToString("N", System.Globalization.CultureInfo.InvariantCulture));

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
            return index is not null;
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
            sidecarFiles.AddRange(Directory.EnumerateFiles(
                _paths.CapturesRoot, CaptureFileNames.OriginalPending, SearchOption.AllDirectories));
            if (!pendingOnly)
            {
                sidecarFiles.AddRange(Directory.EnumerateFiles(
                    _paths.CapturesRoot, CaptureFileNames.Meta, SearchOption.AllDirectories));
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
