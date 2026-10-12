using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Gallery;

/// <summary>
/// One user deletion the Recycle Bin can restore. <see cref="Record"/> is a minimal snapshot:
/// OCR text, page links, window titles, tags and source details are not journaled. Restore reads
/// the full metadata from the restored directory and uses this snapshot only as a fallback.
/// </summary>
public sealed record DeletedLibraryCapture(Guid Token, CaptureRecord Record, string OriginalPath,
    string Identity, string Reason, DateTimeOffset DeletedAt);

/// <summary>
/// The Recycle Bin holds only captures the user deletes from the library. Automatic capacity
/// and retention eviction, and a confirmed permanent deletion, delete files from disk instead.
/// Durable capture identities survive both application restart and Shell recycling.
/// </summary>
public sealed class RecoverableLibraryService
{
    /// <summary>Remembered deletions; the restore menu lists 20, older journals are purged.</summary>
    internal const int MaxJournalEntries = 200;
    private readonly CaptureQueue _queue;
    private readonly IRecoverableTrash _trash;
    private readonly string _journalRoot;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private bool _maintenanceRunning;
    private readonly Dictionary<string, HashSet<Guid>> _pendingMaintenance = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _attemptedMaintenance = new();
    private readonly HashSet<Task> _pendingRestores = [];
    public Task MaintenanceCompletion { get; private set; } = Task.CompletedTask;
    internal Action<string>? BeforeJournalWriteForTest { get; set; }
    internal Action<Guid>? BeforePermanentDeleteForTest { get; set; }
    internal int JournalCapacity { get; set; } = MaxJournalEntries;

    public RecoverableLibraryService(CaptureQueue queue, AppPaths paths, IRecoverableTrash trash, ILogger log)
    {
        _queue = queue;
        _trash = trash;
        _log = log;
        _journalRoot = ValidatedFilePaths.CreateDirectory(Path.Combine(paths.DataRoot, "deleted-captures"));
    }

    public ICaptureRemovalTransaction? PrepareRemoval(CaptureRecord record, string reason)
    {
        if (CaptureQueue.IsPermanentRemovalReason(reason))
            return new PermanentRemoval(this, record, TryGetDirectory(record));
        string original = _queue.GetDirectory(record);
        if (!Directory.Exists(original)) return null;
        string identity = _trash.GetIdentity(original);
        // Snapshot before any Shell action. A failed receipt write still leaves enough
        // information to find this exact recycled filesystem object after restart.
        var entry = new DeletedLibraryCapture(Guid.NewGuid(), MinimalSnapshot(record), original, identity, reason,
            DateTimeOffset.UtcNow);
        Write(entry.Token, "intent", entry);
        RecycledPathReceipt? receipt = null;
        try
        {
            receipt = _trash.RecycleDirectory(original);
            Write(entry.Token, "recycled", receipt);
        }
        catch (Exception ex)
        {
            if (receipt is null && Directory.Exists(original))
            {
                // Nothing moved. Drop this attempt's journal so refusals do not accumulate.
                if (MatchesIdentity(original, identity)) TryDeleteJournal(entry.Token);
                if (ex is RecycleBinUnavailableException) throw;
                _log.LogWarning(ex, "Could not recycle capture {Id}; retaining its files and index", record.Id);
                return null;
            }
            // The move may have completed before receipt persistence failed. Attempt
            // immediate restoration; otherwise retain the durable intent as recoverable
            // state, and let the queue remove the absent original from its normal index.
            _log.LogWarning(ex, "Capture {Id} is in the Recycle Bin; recovering its durable intent", record.Id);
            try { receipt ??= _trash.FindRecycledDirectory(original, identity); }
            catch (Exception lookupError) { _log.LogWarning(lookupError, "Could not resolve a pending recycled capture {Id}", record.Id); }
            if (receipt is not null && TryRestoreFiles(entry, receipt))
            {
                // The capture never left the index; its journal has nothing left to restore.
                TryDeleteJournal(entry.Token);
                return null;
            }
            // A Shell restore may restore the original before reporting a later failure.
            // Preserve the normal record whenever its exact original object is present.
            if (Directory.Exists(original) && MatchesIdentity(original, identity)) return null;
        }
        // Emptied items are purged at startup; here only the count cap needs enforcing.
        if (Directory.EnumerateFiles(_journalRoot, "*.intent.json").Skip(JournalCapacity).Any()) TryPurgeJournal();
        return new RemovalTransaction(this, entry, receipt);
    }

    /// <summary>
    /// Deletions the user can still restore. Entries already restored, entries whose original
    /// is back in place, and entries whose Recycle Bin item was emptied are hidden. The check
    /// reads the recorded receipt from disk; it never enumerates the Recycle Bin.
    /// </summary>
    public IReadOnlyList<DeletedLibraryCapture> RecentDeleted()
    {
        return ReadIntents().Where(entry => !IsRestored(entry.Token)
                && !(Directory.Exists(entry.OriginalPath) && MatchesIdentity(entry.OriginalPath, entry.Identity))
                && !IsRecycledItemGone(entry))
                .OrderByDescending(entry => entry.DeletedAt).ToArray();
    }

    /// <summary>
    /// Removes journals that can no longer restore anything (restores reconciled into the index,
    /// and items emptied from the Recycle Bin) and keeps at most <see cref="MaxJournalEntries"/>
    /// of the newest. Journals are app-owned and deleted directly. Runs off the queue owner, so
    /// it never reads the index itself; the owner passes the tokens it reconciled.
    /// </summary>
    internal int PurgeJournal(IReadOnlySet<Guid>? reconciled = null)
    {
        int purged = 0;
        var kept = new List<DeletedLibraryCapture>();
        foreach (DeletedLibraryCapture entry in ReadIntents())
        {
            bool completed = reconciled?.Contains(entry.Token) == true && IsRestored(entry.Token);
            if (completed || IsRecycledItemGone(entry))
            {
                if (TryDeleteJournal(entry.Token)) purged++;
                continue;
            }
            kept.Add(entry);
        }
        foreach (DeletedLibraryCapture entry in kept.OrderByDescending(entry => entry.DeletedAt).Skip(JournalCapacity))
            if (TryDeleteJournal(entry.Token)) purged++;
        // Journals written before the minimal snapshot still carry OCR text and links: rewrite
        // them in place without a backup copy.
        foreach (DeletedLibraryCapture entry in kept.OrderByDescending(entry => entry.DeletedAt).Take(JournalCapacity))
        {
            CaptureRecord minimal = MinimalSnapshot(entry.Record);
            if (JsonSerializer.Serialize(minimal) == JsonSerializer.Serialize(entry.Record)) continue;
            try
            {
                lock (_gate)
                    AtomicFile.WriteExportBytes(JournalPath(entry.Token, "intent"),
                        JsonSerializer.SerializeToUtf8Bytes(entry with { Record = minimal }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.LogWarning(ex, "Could not minimise deleted-capture journal {Token}", entry.Token); }
        }
        // Receipts and markers whose intent is gone (an interrupted purge) restore nothing.
        foreach (string file in Directory.EnumerateFiles(_journalRoot, "*.json"))
        {
            string name = Path.GetFileName(file);
            int dot = name.IndexOf('.');
            if (dot != 32 || !Guid.TryParseExact(name[..dot], "N", out Guid token)
                || name[dot..] is not (".recycled.json" or ".restored.json")
                || File.Exists(JournalPath(token, "intent"))) continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return purged;
    }

    private void TryPurgeJournal(IReadOnlySet<Guid>? reconciled = null)
    {
        try { PurgeJournal(reconciled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.LogWarning(ex, "Could not purge deleted-capture journals"); }
    }

    /// <summary>The recorded Recycle Bin item no longer exists although its volume is present.</summary>
    private bool IsRecycledItemGone(DeletedLibraryCapture entry)
    {
        if (ReadReceipt(entry) is not { } receipt) return false; // unresolved: restore may still find it
        if (Directory.Exists(receipt.RecycledPath) || File.Exists(receipt.RecycledPath)) return false;
        if (Directory.Exists(entry.OriginalPath)) return false; // restored elsewhere; startup reconciles it
        // A disconnected drive is not an emptied Recycle Bin.
        string? volume = Path.GetPathRoot(receipt.RecycledPath);
        return !string.IsNullOrEmpty(volume) && Directory.Exists(volume);
    }

    private static CaptureRecord MinimalSnapshot(CaptureRecord record)
    {
        CaptureRecord snapshot = JsonSerializer.Deserialize<CaptureRecord>(JsonSerializer.Serialize(record))!;
        snapshot.OcrText = null;
        snapshot.OcrLanguage = null;
        snapshot.OcrContentRevision = null;
        snapshot.SourceWindowTitle = string.Empty;
        snapshot.SourcePageUrl = string.Empty;
        snapshot.SetSourceDetails(null);
        snapshot.Tags = string.Empty;
        return snapshot;
    }

    private string? TryGetDirectory(CaptureRecord record)
    {
        try { return _queue.GetDirectory(record); }
        catch (InvalidDataException) { return null; } // outside the captures root: never delete it
    }

    /// <summary>
    /// Deletes an evicted or permanently deleted capture directory after the index stopped
    /// naming it. <see cref="CaptureQueue.GetDirectory"/> already confined the path to the
    /// captures root without links below it; .NET's recursive delete removes nested links
    /// themselves and never descends into their targets.
    /// </summary>
    private bool DeleteCaptureDirectory(Guid id, string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return true;
        BeforePermanentDeleteForTest?.Invoke(id);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            _log.LogWarning("Capture {Id} directory became a link; leaving it in place", id);
            return false;
        }
        Directory.Delete(directory, recursive: true);
        return !Directory.Exists(directory);
    }

    public Task<IReadOnlyList<DeletedLibraryCapture>> RecentDeletedAsync(int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return Task.Run<IReadOnlyList<DeletedLibraryCapture>>(() => RecentDeleted().Take(limit).ToArray());
    }

    /// <summary>Connect on the queue owner (the UI dispatcher in production). Capacity and
    /// retention eviction delete files on a worker after the index publication; user
    /// deletions move to the Recycle Bin on a worker. Publication resumes on that owner.
    /// Overlapping triggers do not retry a failed removal continuously when the transaction
    /// releases its eviction leases.</summary>
    public void ConnectAutomaticMaintenance(Func<Guid, bool>? canRemove = null,
        Action<CaptureBatchRemovalResult>? completed = null)
    {
        _queue.PrepareRemoval = (record, reason) =>
        {
            if (reason is "capacity" or "retention-age") _attemptedMaintenance.TryAdd(record.Id, 0);
            return PrepareRemoval(record, reason);
        };
        _queue.ScheduleAutomaticRemoval = (ids, reason) =>
        {
            if (_maintenanceRunning)
            {
                if (!_pendingMaintenance.TryGetValue(reason, out var pending)) _pendingMaintenance[reason] = pending = [];
                pending.UnionWith(ids);
                return;
            }
            _maintenanceRunning = true;
            MaintenanceCompletion = RunAutomaticMaintenanceAsync(ids, reason, canRemove, completed);
        };
    }

    private async Task RunAutomaticMaintenanceAsync(IReadOnlyList<Guid> ids, string reason,
        Func<Guid, bool>? canRemove, Action<CaptureBatchRemovalResult>? completed)
    {
        try
        {
            // Do not begin a publication/reservation from inside Add/lease release.
            await Task.Yield();
            while (true)
            {
                _attemptedMaintenance.Clear();
                CaptureBatchRemovalResult result = await _queue.RemoveAutomaticallyAsync(ids, reason, canRemove);
                completed?.Invoke(result);
                // Failed/attempted IDs are not retried because a trigger overlapped this
                // same transaction. Distinct new work is drained against current state.
                foreach (HashSet<Guid> pending in _pendingMaintenance.Values) pending.ExceptWith(_attemptedMaintenance.Keys);
                var next = _pendingMaintenance.FirstOrDefault(item => item.Value.Count > 0);
                if (next.Value is null) break;
                reason = next.Key;
                ids = next.Value.ToArray();
                _pendingMaintenance.Remove(reason);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Automatic library maintenance failed"); }
        finally { _pendingMaintenance.Clear(); _maintenanceRunning = false; }
    }

    public async Task RecoverInterruptedRestoresAsync(CancellationToken cancellationToken = default)
    {
        DeletedLibraryCapture[] restored = await Task.Run(() =>
        {
            var result = new List<DeletedLibraryCapture>();
            var budget = System.Diagnostics.Stopwatch.StartNew();
            foreach (DeletedLibraryCapture entry in ReadIntents())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.Elapsed >= TimeSpan.FromSeconds(5)) break;
                if (Directory.Exists(entry.OriginalPath))
                {
                    if (MatchesIdentity(entry.OriginalPath, entry.Identity)) result.Add(entry);
                    continue;
                }
                if (IsRestored(entry.Token)) continue;
                // Resolve an intent without a receipt once, with the Shell backend's
                // bounded lookup. An unresolved entry remains recoverable intent; it
                // cannot block normal capture or become a nonexistent normal record.
                try
                {
                    if (!File.Exists(JournalPath(entry.Token, "recycled")) && ResolveReceipt(entry) is { } receipt)
                        Write(entry.Token, "recycled", receipt);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Pending capture {Id} remains unresolved", entry.Record.Id); }
            }
            return result.ToArray();
        }, cancellationToken);
        var reconciled = new HashSet<Guid>();
        foreach (DeletedLibraryCapture entry in restored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_queue.Find(entry.Record.Id) is null)
                    _queue.RestoreRecord(await Task.Run(() => RestoredRecord(entry), cancellationToken));
                MarkRestored(entry.Token);
                reconciled.Add(entry.Token);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Interrupted capture restore {Id} remains pending", entry.Record.Id); }
        }
        // Reconciliation first: a completed journal is purged only once its capture is indexed.
        await Task.Run(() => TryPurgeJournal(reconciled), cancellationToken);
    }

    /// <summary>
    /// The restored directory carries its own metadata (meta.json), including OCR text and
    /// source details that the journal deliberately omits. The minimal snapshot is a fallback.
    /// </summary>
    private CaptureRecord RestoredRecord(DeletedLibraryCapture entry)
    {
        string meta = Path.Combine(entry.OriginalPath, CaptureFileNames.Meta);
        try
        {
            if (File.Exists(meta)
                && JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(meta), JsonDefaults.Compact) is { } full
                && full.Id == entry.Record.Id)
            {
                full.RelativeDirectory = entry.Record.RelativeDirectory;
                return full;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Restored capture {Id} metadata is unreadable; using the journal snapshot", entry.Record.Id);
        }
        return entry.Record;
    }

    public Task WaitForPendingOperationsAsync()
    {
        lock (_gate) return Task.WhenAll(_pendingRestores.Append(MaintenanceCompletion).Append(_queue.WaitForPendingRemovalsAsync()).ToArray());
    }

    public Task RestoreAsync(Guid token)
    {
        Task operation = RestoreCoreAsync(token);
        lock (_gate) _pendingRestores.Add(operation);
        return ObserveRestoreAsync(operation);
    }

    private async Task ObserveRestoreAsync(Task operation)
    {
        try { await operation; }
        finally { lock (_gate) _pendingRestores.Remove(operation); }
    }

    private async Task RestoreCoreAsync(Guid token)
    {
        DeletedLibraryCapture entry = await Task.Run(() => ReadIntent(JournalPath(token, "intent")))
            ?? throw new IOException("The deleted capture record is unavailable.");
        if (_queue.Find(entry.Record.Id) is not null) throw new IOException("The capture is already in the library.");
        CaptureRecord record = await Task.Run(() =>
        {
            RecycledPathReceipt receipt = ResolveReceipt(entry)
                ?? throw new IOException("The recorded Recycle Bin item is unavailable.");
            _trash.RestoreDirectory(receipt);
            return RestoredRecord(entry);
        });
        // A failure here leaves the restored original + durable intent for startup recovery.
        // Mark completion only after the capture metadata and index are actually durable.
        _queue.RestoreRecord(record);
        MarkRestored(entry.Token);
        // The capture is indexed again; its journal has nothing left to restore.
        await Task.Run(() => TryDeleteJournal(entry.Token));
    }

    private RecycledPathReceipt? ResolveReceipt(DeletedLibraryCapture entry) =>
        ReadReceipt(entry) ?? _trash.FindRecycledDirectory(entry.OriginalPath, entry.Identity);

    /// <summary>The persisted receipt only; no Shell lookup.</summary>
    private RecycledPathReceipt? ReadReceipt(DeletedLibraryCapture entry)
    {
        string file = JournalPath(entry.Token, "recycled");
        if (!File.Exists(file)) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<RecycledPathReceipt>(File.ReadAllText(file));
            if (receipt is not null && receipt.Identity == entry.Identity
                && string.Equals(receipt.OriginalPath, entry.OriginalPath, StringComparison.OrdinalIgnoreCase)) return receipt;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return null;
    }

    private bool TryRestoreFiles(DeletedLibraryCapture entry, RecycledPathReceipt receipt)
    {
        try
        {
            _trash.RestoreDirectory(receipt);
            if (!MatchesIdentity(entry.OriginalPath, entry.Identity)) return false;
            // A rollback still has its normal index entry. The completion marker is best
            // effort here: the original identity also makes restart recovery idempotent.
            try { MarkRestored(entry.Token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log.LogWarning(ex, "Could not mark capture rollback completion"); }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not complete recycled capture restore {Id}", entry.Record.Id);
            // This is index rollback admission, not a claim of Shell metadata completion.
            return Directory.Exists(entry.OriginalPath) && MatchesIdentity(entry.OriginalPath, entry.Identity);
        }
    }

    private bool MatchesIdentity(string path, string identity)
    {
        try { return _trash.GetIdentity(path) == identity; }
        catch (IOException) { return false; }
    }
    private IEnumerable<DeletedLibraryCapture> ReadIntents()
    {
        foreach (string file in Directory.EnumerateFiles(_journalRoot, "*.intent.json"))
        {
            if (ReadIntent(file) is { } entry) yield return entry;
        }
    }
    private DeletedLibraryCapture? ReadIntent(string file)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<DeletedLibraryCapture>(File.ReadAllText(file));
            return entry is not null && string.Equals(JournalPath(entry.Token, "intent"), file, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_queue.GetDirectory(entry.Record), entry.OriginalPath, StringComparison.OrdinalIgnoreCase) ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        { _log.LogWarning(ex, "Could not read or validate a deleted-capture intent"); return null; }
    }
    private bool IsRestored(Guid token)
    {
        try
        {
            return JsonSerializer.Deserialize<RestoredCompletion>(File.ReadAllText(JournalPath(token, "restored")))
                is { RestoredAt: var time } && time != default;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }
    private sealed record RestoredCompletion(DateTimeOffset RestoredAt);
    private string JournalPath(Guid token, string phase) => Path.Combine(_journalRoot, $"{token:N}.{phase}.json");
    private void Write<T>(Guid token, string phase, T value)
    {
        lock (_gate)
        {
            string file = JournalPath(token, phase);
            BeforeJournalWriteForTest?.Invoke(phase);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
    }
    private void MarkRestored(Guid token)
    {
        lock (_gate)
            // A corrupt/incomplete marker is preserved; exact original reconciliation is
            // still idempotent on startup instead of overwriting that evidence.
            if (!File.Exists(JournalPath(token, "restored"))) Write(token, "restored", new RestoredCompletion(DateTimeOffset.UtcNow));
    }

    /// <summary>Deletes one deletion's journal files. The intent goes first: a partial failure
    /// leaves only an invisible receipt, which a later purge removes, never a restore entry
    /// that lost its completion marker.</summary>
    private void DeleteJournal(Guid token)
    {
        lock (_gate)
        {
            foreach (string phase in new[] { "intent", "recycled", "restored" })
            {
                string file = JournalPath(token, phase);
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }

    private bool TryDeleteJournal(Guid token)
    {
        try { DeleteJournal(token); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not delete deleted-capture journal {Token}", token);
            return false;
        }
    }

    private sealed class PermanentRemoval(RecoverableLibraryService owner, CaptureRecord record, string? directory)
        : ICaptureRemovalTransaction
    {
        // Nothing changes before the index publication, so there is nothing to undo.
        public bool Rollback() => true;
        public bool Complete() => owner.DeleteCaptureDirectory(record.Id, directory);
    }
    private sealed class RemovalTransaction(RecoverableLibraryService owner, DeletedLibraryCapture entry,
        RecycledPathReceipt? receipt) : ICaptureRemovalTransaction
    {
        public bool Rollback()
        {
            try
            {
                receipt ??= owner.ResolveReceipt(entry);
                return receipt is not null && owner.TryRestoreFiles(entry, receipt);
            }
            catch (Exception ex) { owner._log.LogWarning(ex, "Recycled capture rollback remains pending"); return false; }
        }
    }
}
