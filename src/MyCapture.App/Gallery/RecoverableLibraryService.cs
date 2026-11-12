using System.IO;
using System.Runtime.ExceptionServices;
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
/// <see cref="Batch"/> names the delete command that removed it, so Undo restores that command's
/// captures together; journals written before batches existed are each their own batch.
/// </summary>
public sealed record DeletedLibraryCapture(Guid Token, CaptureRecord Record, string OriginalPath,
    string Identity, string Reason, DateTimeOffset DeletedAt, Guid? Batch = null)
{
    /// <summary>The batch Undo restores this capture with.</summary>
    public Guid BatchKey => Batch ?? Token;
}

/// <summary>A restorable deletion and the thumbnail inside its Recycle Bin item, when it is there.</summary>
public sealed record DeletedLibraryEntry(DeletedLibraryCapture Capture, string? ThumbnailPath);

/// <summary>
/// The restore menu's content: the newest deletions, and the most recent delete command with the
/// number of its captures that can still be restored.
/// </summary>
public sealed record DeletedLibraryOverview(IReadOnlyList<DeletedLibraryEntry> Recent, Guid? LastBatch, int LastBatchCount);

/// <summary>How many captures of a batch came back, and how many stayed in the Recycle Bin.</summary>
public sealed record LibraryBatchRestoreResult(int Restored, int Failed);

/// <summary>
/// Manual deletion records an in-app undo receipt. Automatic eviction also recycles its files
/// after index publication, without adding entries to the library's undo menu.
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
    private readonly AsyncLocal<Guid?> _batch = new();
    private readonly AsyncLocal<Action<int>?> _recycleProgress = new();
    private static readonly string[] JournalPhases = ["intent", "recycled", "restored"];
    /// <summary>Retired journal files wait in a folder with this name prefix for one Recycle Bin move.</summary>
    private const string RetiredFolderPrefix = "retired-";
    private readonly HashSet<string> _retiringFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _retentionNoticeFile;
    private RetentionNoticeState? _retentionNotice;
    public Task MaintenanceCompletion { get; private set; } = Task.CompletedTask;
    internal Action<string>? BeforeJournalWriteForTest { get; set; }
    internal Action<Guid>? BeforePermanentDeleteForTest { get; set; }
    /// <summary>Sees each folder of retired journals just before it goes to the Recycle Bin.</summary>
    internal Action<string>? BeforeRetiredFolderRecycleForTest { get; set; }
    internal int JournalCapacity { get; set; } = MaxJournalEntries;

    public RecoverableLibraryService(CaptureQueue queue, AppPaths paths, IRecoverableTrash trash, ILogger log)
    {
        _queue = queue;
        _trash = trash;
        _log = log;
        _journalRoot = ValidatedFilePaths.CreateDirectory(Path.Combine(paths.DataRoot, "deleted-captures"));
        _retentionNoticeFile = Path.Combine(paths.DataRoot, "retention-notice.json");
    }

    /// <summary>
    /// Tags every capture the user deletes in this async flow with <paramref name="batch"/>, so the
    /// whole command can be undone at once, and tells <paramref name="progress"/>, from the removal
    /// worker, how many of its captures are in the Recycle Bin so far. Both flow into the removal
    /// worker with the execution context and end when the scope is disposed.
    /// </summary>
    public IDisposable BeginBatch(Guid batch, Action<int>? progress = null)
    {
        var scope = new BatchScope(this, _batch.Value, _recycleProgress.Value);
        _batch.Value = batch;
        _recycleProgress.Value = progress;
        return scope;
    }

    /// <summary>
    /// Tells <paramref name="progress"/>, from the removal worker, how many captures this async
    /// flow deletes have reached the Recycle Bin so far, without making them one Undo batch.
    /// </summary>
    public IDisposable ReportRecycleProgress(Action<int> progress)
    {
        var scope = new BatchScope(this, _batch.Value, _recycleProgress.Value);
        _recycleProgress.Value = progress;
        return scope;
    }

    private sealed class BatchScope(RecoverableLibraryService owner, Guid? previous, Action<int>? previousProgress) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._batch.Value = previous;
            owner._recycleProgress.Value = previousProgress;
        }
    }

    public ICaptureRemovalTransaction? PrepareRemoval(CaptureRecord record, string reason)
    {
        if (CaptureQueue.IsPermanentRemovalReason(reason))
            return new PermanentRemoval(this, record, TryGetDirectory(record));
        if (BeginRecycle(record, reason) is not { } entry) return null;
        RecycledPathReceipt? receipt = null;
        Exception? failure = null;
        try { receipt = _trash.RecycleDirectory(entry.OriginalPath); }
        catch (Exception ex) { failure = ex; }
        var finished = new List<Guid>(1);
        CaptureRemovalAdmission admission = FinishRecycle(record, entry, receipt, failure, finished);
        RetireJournals(finished);
        if (admission.Failure is { } refused) ExceptionDispatchInfo.Throw(refused);
        if (admission.Transaction is not null) EnforceJournalCapacity();
        return admission.Transaction;
    }

    /// <summary>
    /// Prepares a whole user deletion with one Recycle Bin operation. Every intent is journaled
    /// before the Shell moves anything, the moves share one Shell operation (one move per capture
    /// took about 0.2 s each, over 20 s for a hundred), and each capture then finishes exactly as
    /// <see cref="PrepareRemoval"/> finishes it. The journal's size cap is checked once per batch,
    /// not once per capture. Automatic eviction changes nothing before its index publication.
    /// </summary>
    public IReadOnlyList<CaptureRemovalAdmission> PrepareRemovals(IReadOnlyList<CaptureRecord> records, string reason)
    {
        var admissions = new CaptureRemovalAdmission[records.Count];
        if (CaptureQueue.IsPermanentRemovalReason(reason))
        {
            for (int index = 0; index < records.Count; index++)
                admissions[index] = new(new PermanentRemoval(this, records[index], TryGetDirectory(records[index])));
            return admissions;
        }

        var pending = new List<(int Index, DeletedLibraryCapture Entry)>(records.Count);
        for (int index = 0; index < records.Count; index++)
        {
            try { if (BeginRecycle(records[index], reason) is { } entry) pending.Add((index, entry)); }
            catch (Exception ex) { admissions[index] = new(null, ex); } // nothing moved; the capture stays
        }
        if (pending.Count == 0) return admissions;

        string[] originals = pending.Select(item => item.Entry.OriginalPath).ToArray();
        IReadOnlyList<RecycleOutcome> outcomes;
        try { outcomes = _trash.RecycleDirectories(originals, _recycleProgress.Value); }
        catch (Exception ex) { outcomes = originals.Select(path => new RecycleOutcome(path, null, ex)).ToArray(); }

        var finished = new List<Guid>();
        bool admitted = false;
        for (int position = 0; position < pending.Count; position++)
        {
            (int index, DeletedLibraryCapture entry) = pending[position];
            RecycleOutcome outcome = position < outcomes.Count ? outcomes[position]
                : new RecycleOutcome(entry.OriginalPath, null, new IOException("The Shell did not confirm a recoverable Recycle Bin move."));
            try { admissions[index] = FinishRecycle(records[index], entry, outcome.Receipt, outcome.Failure, finished); }
            catch (Exception ex) { admissions[index] = new(null, ex); }
            admitted |= admissions[index].Transaction is not null;
        }
        RetireJournals(finished);
        if (admitted) EnforceJournalCapacity();
        return admissions;
    }

    /// <summary>
    /// Journals one user deletion before any Shell action; null when its directory is already gone.
    /// A failed receipt write later still leaves enough information to find this exact recycled
    /// filesystem object after restart.
    /// </summary>
    private DeletedLibraryCapture? BeginRecycle(CaptureRecord record, string reason)
    {
        string original = _queue.GetDirectory(record);
        if (!Directory.Exists(original)) return null;
        string identity = _trash.GetIdentity(original);
        var entry = new DeletedLibraryCapture(Guid.NewGuid(), MinimalSnapshot(record), original, identity, reason,
            DateTimeOffset.UtcNow, _batch.Value);
        Write(entry.Token, "intent", entry);
        return entry;
    }

    /// <summary>
    /// Completes one user deletion after its Shell move: records the receipt, or, when the move or
    /// the receipt failed, puts the capture back or keeps its durable intent. A refused move is
    /// returned as the failure. Journals with nothing left to restore are added to
    /// <paramref name="finished"/> for the caller to retire together.
    /// </summary>
    private CaptureRemovalAdmission FinishRecycle(CaptureRecord record, DeletedLibraryCapture entry,
        RecycledPathReceipt? receipt, Exception? failure, List<Guid> finished)
    {
        string original = entry.OriginalPath, identity = entry.Identity;
        if (failure is null)
        {
            try { Write(entry.Token, "recycled", receipt!); }
            catch (Exception ex) { failure = ex; }
        }
        if (failure is null) return new(new RemovalTransaction(this, entry, receipt));

        if (receipt is null && Directory.Exists(original))
        {
            // Nothing moved. Drop this attempt's journal so refusals do not accumulate.
            if (MatchesIdentity(original, identity)) finished.Add(entry.Token);
            if (failure is RecycleBinUnavailableException) return new(null, failure);
            _log.LogWarning(failure, "Could not recycle capture {Id}; retaining its files and index", record.Id);
            return default;
        }
        // The move may have completed before receipt persistence failed. Attempt
        // immediate restoration; otherwise retain the durable intent as recoverable
        // state, and let the queue remove the absent original from its normal index.
        _log.LogWarning(failure, "Capture {Id} is in the Recycle Bin; recovering its durable intent", record.Id);
        try { receipt ??= _trash.FindRecycledDirectory(original, identity); }
        catch (Exception lookupError) { _log.LogWarning(lookupError, "Could not resolve a pending recycled capture {Id}", record.Id); }
        if (receipt is not null && TryRestoreFiles(entry, receipt))
        {
            // The capture never left the index; its journal has nothing left to restore.
            finished.Add(entry.Token);
            return default;
        }
        // A Shell restore may restore the original before reporting a later failure.
        // Preserve the normal record whenever its exact original object is present.
        if (Directory.Exists(original) && MatchesIdentity(original, identity)) return default;
        return new(new RemovalTransaction(this, entry, receipt));
    }

    /// <summary>Emptied items are purged at startup; after a deletion only the count cap needs enforcing.</summary>
    private void EnforceJournalCapacity()
    {
        try
        {
            if (Directory.EnumerateFiles(_journalRoot, "*.intent.json").Skip(JournalCapacity).Any()) TryPurgeJournal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.LogWarning(ex, "Could not count deleted-capture journals"); }
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
    /// of the newest. Retired journals move to the Recycle Bin. Runs off the queue owner, so
    /// it never reads the index itself; the owner passes the tokens it reconciled.
    /// </summary>
    internal int PurgeJournal(IReadOnlySet<Guid>? reconciled = null)
    {
        var finished = new List<Guid>();
        var kept = new List<DeletedLibraryCapture>();
        foreach (DeletedLibraryCapture entry in ReadIntents())
        {
            bool completed = reconciled?.Contains(entry.Token) == true && IsRestored(entry.Token);
            if (completed || IsRecycledItemGone(entry)) finished.Add(entry.Token);
            else kept.Add(entry);
        }
        DeletedLibraryCapture[] newest = kept.OrderByDescending(entry => entry.DeletedAt).ToArray();
        finished.AddRange(newest.Skip(JournalCapacity).Select(entry => entry.Token));
        int purged = RetireJournals(finished).Count;
        // Journals written before the minimal snapshot still carry OCR text and links: rewrite
        // them in place without a backup copy.
        foreach (DeletedLibraryCapture entry in newest.Take(JournalCapacity))
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
        var orphans = new List<(Guid Token, IEnumerable<string> Files)>();
        foreach (string file in Directory.EnumerateFiles(_journalRoot, "*.json"))
        {
            string name = Path.GetFileName(file);
            int dot = name.IndexOf('.');
            if (dot != 32 || !Guid.TryParseExact(name[..dot], "N", out Guid token)
                || name[dot..] is not (".recycled.json" or ".restored.json")
                || File.Exists(JournalPath(token, "intent"))) continue;
            orphans.Add((token, [file]));
        }
        RetireFiles(orphans);
        // A retirement whose Recycle Bin move failed left its folder behind; move it now.
        string[] leftovers;
        lock (_gate)
            leftovers = Directory.EnumerateDirectories(_journalRoot, RetiredFolderPrefix + "*")
                .Where(folder => !_retiringFolders.Contains(Path.GetFileName(folder))).ToArray();
        foreach (string folder in leftovers) RecycleRetiredFolder(folder);
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
    /// Recycles an evicted capture directory after the index stopped
    /// naming it. <see cref="CaptureQueue.GetDirectory"/> already confined the path to the
    /// captures root without links below it. Nested links move without entering their targets.
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
        global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(directory, recursive: true);
        return !Directory.Exists(directory);
    }

    public Task<IReadOnlyList<DeletedLibraryCapture>> RecentDeletedAsync(int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return Task.Run<IReadOnlyList<DeletedLibraryCapture>>(() => RecentDeleted().Take(limit).ToArray());
    }

    /// <summary>
    /// The restore menu's content, read off the caller's thread: the newest <paramref name="limit"/>
    /// deletions with their Recycle Bin thumbnails, and the last delete command's restorable count.
    /// </summary>
    public Task<DeletedLibraryOverview> RecentDeletedOverviewAsync(int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return Task.Run(() =>
        {
            IReadOnlyList<DeletedLibraryCapture> all = RecentDeleted();
            Guid? last = all.Count > 0 ? all[0].BatchKey : null;
            int lastCount = last is { } batch ? all.Count(entry => entry.BatchKey == batch) : 0;
            DeletedLibraryEntry[] recent = all.Take(limit).Select(entry => new DeletedLibraryEntry(entry, RecycledThumbnail(entry))).ToArray();
            return new DeletedLibraryOverview(recent, last, lastCount);
        });
    }

    /// <summary>The thumbnail file inside the recorded Recycle Bin item; no Shell lookup.</summary>
    private string? RecycledThumbnail(DeletedLibraryCapture entry)
    {
        if (ReadReceipt(entry) is not { } receipt) return null;
        string thumbnail = Path.Combine(receipt.RecycledPath, CaptureFileNames.Thumbnail);
        return File.Exists(thumbnail) ? thumbnail : null;
    }

    /// <summary>
    /// Restores every capture of one delete command. The captures leave the Recycle Bin with one
    /// restore (one per capture took about a second each), the library index is written once for
    /// the whole batch, and the finished journals are retired together. A capture whose Recycle
    /// Bin item is gone, or whose original location is taken, stays where it is and counts as failed.
    /// </summary>
    public Task<LibraryBatchRestoreResult> RestoreBatchAsync(Guid batch)
    {
        Task<LibraryBatchRestoreResult> operation = RestoreBatchCoreAsync(batch);
        lock (_gate) _pendingRestores.Add(operation);
        return ObserveBatchRestoreAsync(operation);
    }

    private async Task<LibraryBatchRestoreResult> ObserveBatchRestoreAsync(Task<LibraryBatchRestoreResult> operation)
    {
        try { return await operation; }
        finally { lock (_gate) _pendingRestores.Remove(operation); }
    }

    private async Task<LibraryBatchRestoreResult> RestoreBatchCoreAsync(Guid batch)
    {
        DeletedLibraryCapture[] entries = await Task.Run(() => ReadIntents()
            .Where(entry => entry.BatchKey == batch && !IsRestored(entry.Token)).ToArray());
        // Already back in the library (for example restored one by one): nothing to move.
        DeletedLibraryCapture[] pending = entries.Where(entry => _queue.Find(entry.Record.Id) is null).ToArray();
        var restored = await Task.Run(() =>
        {
            var resolved = new List<(DeletedLibraryCapture Entry, RecycledPathReceipt Receipt)>(pending.Length);
            foreach (DeletedLibraryCapture entry in pending)
            {
                try
                {
                    resolved.Add((entry, ResolveReceipt(entry)
                        ?? throw new IOException("The recorded Recycle Bin item is unavailable.")));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not restore capture {Id} of a deleted batch", entry.Record.Id);
                }
            }
            IReadOnlyList<Exception?> failures;
            try { failures = _trash.RestoreDirectories(resolved.Select(item => item.Receipt).ToArray()); }
            catch (Exception ex) { failures = resolved.Select(_ => (Exception?)ex).ToArray(); }
            var moved = new List<(DeletedLibraryCapture Entry, CaptureRecord Record)>(resolved.Count);
            for (int index = 0; index < resolved.Count; index++)
            {
                DeletedLibraryCapture entry = resolved[index].Entry;
                Exception? failure = index < failures.Count ? failures[index]
                    : new IOException("The Shell did not report this restore.");
                if (failure is null) moved.Add((entry, RestoredRecord(entry)));
                else _log.LogWarning(failure, "Could not restore capture {Id} of a deleted batch", entry.Record.Id);
            }
            return moved;
        });
        if (restored.Count > 0)
        {
            // A failure here leaves the restored originals and their intents for startup recovery,
            // exactly like a single restore.
            await _queue.RestoreRecordsAsync(restored.Select(item => item.Record).ToArray());
            await Task.Run(() =>
            {
                var finished = new List<Guid>(restored.Count);
                foreach ((DeletedLibraryCapture entry, _) in restored)
                {
                    try { MarkRestored(entry.Token); finished.Add(entry.Token); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { _log.LogWarning(ex, "Could not mark capture {Id} restored", entry.Record.Id); }
                }
                RetireJournals(finished);
            });
        }
        return new LibraryBatchRestoreResult(restored.Count, pending.Length - restored.Count);
    }

    // ---- First automatic deletion notice ---------------------------------------------------

    private sealed record RetentionNoticeState(bool Shown, int Pending);

    /// <summary>
    /// Counts images the retention period deleted until the library has told the user once.
    /// Kept in a small app-owned file, so the notice appears once per installation.
    /// </summary>
    internal void NoteRetentionDeletion(int count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            RetentionNoticeState state = ReadRetentionNotice();
            if (state.Shown) return;
            WriteRetentionNotice(state with { Pending = state.Pending + count });
        }
    }

    /// <summary>The images deleted before the first notice, or null once it has been shown.</summary>
    public int? PendingRetentionNotice
    {
        get
        {
            lock (_gate)
            {
                RetentionNoticeState state = ReadRetentionNotice();
                return state.Shown || state.Pending <= 0 ? null : state.Pending;
            }
        }
    }

    /// <summary>Returns the pending count and records the notice as shown, or null when there is none.</summary>
    public int? TakeRetentionNotice()
    {
        lock (_gate)
        {
            RetentionNoticeState state = ReadRetentionNotice();
            if (state.Shown || state.Pending <= 0) return null;
            WriteRetentionNotice(state with { Shown = true });
            return state.Pending;
        }
    }

    private RetentionNoticeState ReadRetentionNotice()
    {
        if (_retentionNotice is { } cached) return cached;
        RetentionNoticeState state = new(false, 0);
        try
        {
            if (File.Exists(_retentionNoticeFile)
                && JsonSerializer.Deserialize<RetentionNoticeState>(File.ReadAllText(_retentionNoticeFile)) is { } stored)
                state = stored with { Pending = Math.Max(0, stored.Pending) };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the retention notice state");
        }
        return _retentionNotice = state;
    }

    private void WriteRetentionNotice(RetentionNoticeState state)
    {
        _retentionNotice = state;
        try { AtomicFile.WriteExportBytes(_retentionNoticeFile, JsonSerializer.SerializeToUtf8Bytes(state)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.LogWarning(ex, "Could not save the retention notice state"); }
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
        _queue.PrepareRemovals = (records, reason) =>
        {
            if (reason is "capacity" or "retention-age")
                foreach (CaptureRecord record in records) _attemptedMaintenance.TryAdd(record.Id, 0);
            return PrepareRemovals(records, reason);
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
                if (reason == "retention-age") NoteRetentionDeletion(result.RemovedCount);
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

    private bool TryDeleteJournal(Guid token) => RetireJournals([token]).Contains(token);

    /// <summary>
    /// Retires the journals of deletions with nothing left to restore. Their files move into one
    /// app-owned folder, which then goes to the Recycle Bin as a single item: one Shell operation
    /// per file cost a hundred-capture Undo hundreds of Shell operations and left as many small
    /// files in the user's Recycle Bin. Each intent moves first, so a partial failure leaves only
    /// an invisible receipt, which a later purge removes, never a restore entry that lost its
    /// completion marker. Returns the tokens whose files all left the journal folder.
    /// </summary>
    private HashSet<Guid> RetireJournals(IEnumerable<Guid> tokens) =>
        RetireFiles(tokens.Distinct().Select(token => (token, JournalPhases.Select(phase => JournalPath(token, phase)))));

    private HashSet<Guid> RetireFiles(IEnumerable<(Guid Token, IEnumerable<string> Files)> groups)
    {
        var retired = new HashSet<Guid>();
        string? folder = null;
        lock (_gate)
        {
            foreach ((Guid token, IEnumerable<string> files) in groups)
            {
                try
                {
                    foreach (string file in files)
                    {
                        if (!File.Exists(file)) continue;
                        if (folder is null)
                        {
                            string name = RetiredFolderPrefix + Guid.NewGuid().ToString("N");
                            folder = ValidatedFilePaths.CreateDirectory(Path.Combine(_journalRoot, name));
                            _retiringFolders.Add(name);
                        }
                        File.Move(file, Path.Combine(folder, Path.GetFileName(file)));
                    }
                    retired.Add(token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _log.LogWarning(ex, "Could not delete deleted-capture journal {Token}", token); }
            }
        }
        // The Shell move runs outside the gate; nothing else touches this uniquely named folder.
        if (folder is not null) RecycleRetiredFolder(folder);
        return retired;
    }

    private void RecycleRetiredFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                BeforeRetiredFolderRecycleForTest?.Invoke(folder);
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _log.LogWarning(ex, "Could not move retired deleted-capture journals to the Recycle Bin"); } // the next purge retries
        finally { lock (_gate) _retiringFolders.Remove(Path.GetFileName(folder)); }
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
