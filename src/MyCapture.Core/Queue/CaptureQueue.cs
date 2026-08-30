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
/// Ordering is newest-first, which is the order the gallery displays and the order
/// eviction walks backwards through.
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
    /// True when pinned records alone exceed a configured cap, so eviction can no
    /// longer bring the queue back within limits.
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
        if (record is null)
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
        var index = new CaptureIndexFile { Records = [.. _records] };
        string json = JsonSerializer.Serialize(index, SerializerOptions);

        // Guards against two debounced saves interleaving their temp-file writes.
        lock (_writeGate)
        {
            AtomicFile.WriteAllText(_paths.IndexFile, json);
        }
    }

    /// <summary>
    /// Writes the per-capture metadata copy used to rebuild a lost index.
    /// </summary>
    public void SaveRecordMeta(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string path = GetFilePath(record, CaptureFileNames.Meta);
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, MetaSerializerOptions));
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

    private void UpdatePinPressureFlag() => IsOverCapacityDueToPins = IsOverCapacity();

    private CaptureRecord? FindOldestUnpinned()
    {
        // Records are newest-first, so the oldest unpinned entry is the last one that
        // is not pinned.
        for (int i = _records.Count - 1; i >= 0; i--)
        {
            if (!_records[i].IsPinned)
            {
                return _records[i];
            }
        }

        return null;
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
    private List<CaptureRecord> RebuildFromDisk()
    {
        var recovered = new List<CaptureRecord>();

        if (!Directory.Exists(_paths.CapturesRoot))
        {
            return recovered;
        }

        IEnumerable<string> metaFiles;
        try
        {
            metaFiles = Directory.EnumerateFiles(
                _paths.CapturesRoot, CaptureFileNames.Meta, SearchOption.AllDirectories);
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Could not enumerate capture directories during index rebuild");
            return recovered;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Access denied enumerating capture directories during index rebuild");
            return recovered;
        }

        foreach (string metaPath in metaFiles)
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

                recovered.Add(record);
            }
            catch (JsonException)
            {
                // One unreadable sidecar costs one capture, not the rebuild.
            }
            catch (IOException)
            {
            }
        }

        _log.LogInformation("Rebuilt capture index with {Count} records", recovered.Count);
        return recovered;
    }
}
