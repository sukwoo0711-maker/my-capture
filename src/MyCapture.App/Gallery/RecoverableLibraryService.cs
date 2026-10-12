using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Queue;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Gallery;

public sealed record DeletedLibraryCapture(Guid Token, CaptureRecord Record, string OriginalPath,
    string Identity, string Reason, DateTimeOffset DeletedAt);

/// <summary>Durable capture identities survive both application restart and Shell recycling.</summary>
public sealed class RecoverableLibraryService
{
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

    public RecoverableLibraryService(CaptureQueue queue, AppPaths paths, IRecoverableTrash trash, ILogger log)
    {
        _queue = queue;
        _trash = trash;
        _log = log;
        _journalRoot = ValidatedFilePaths.CreateDirectory(Path.Combine(paths.DataRoot, "deleted-captures"));
    }

    public ICaptureRemovalTransaction? PrepareRemoval(CaptureRecord record, string reason)
    {
        string original = _queue.GetDirectory(record);
        if (!Directory.Exists(original)) return null;
        string identity = _trash.GetIdentity(original);
        // Snapshot before any Shell action. A failed receipt write still leaves enough
        // information to find this exact recycled filesystem object after restart.
        CaptureRecord snapshot = JsonSerializer.Deserialize<CaptureRecord>(JsonSerializer.Serialize(record))!;
        var entry = new DeletedLibraryCapture(Guid.NewGuid(), snapshot, original, identity, reason, DateTimeOffset.UtcNow);
        Write(entry.Token, "intent", entry);
        RecycledPathReceipt? receipt = null;
        try
        {
            receipt = _trash.RecycleDirectory(original);
            Write(entry.Token, "recycled", receipt);
        }
        catch (Exception ex)
        {
            if (Directory.Exists(original))
            {
                _log.LogWarning(ex, "Could not recycle capture {Id}; retaining its files and index", record.Id);
                return null;
            }
            // The move may have completed before receipt persistence failed. Attempt
            // immediate restoration; otherwise retain the durable intent as recoverable
            // state, and let the queue remove the absent original from its normal index.
            _log.LogWarning(ex, "Capture {Id} is in the Recycle Bin; recovering its durable intent", record.Id);
            try { receipt ??= _trash.FindRecycledDirectory(original, identity); }
            catch (Exception lookupError) { _log.LogWarning(lookupError, "Could not resolve a pending recycled capture {Id}", record.Id); }
            if (receipt is not null && TryRestoreFiles(entry, receipt)) return null;
            // A Shell restore may restore the original before reporting a later failure.
            // Preserve the normal record whenever its exact original object is present.
            if (Directory.Exists(original) && MatchesIdentity(original, identity)) return null;
        }
        return new RemovalTransaction(this, entry, receipt);
    }

    public IReadOnlyList<DeletedLibraryCapture> RecentDeleted()
    {
        return ReadIntents().Where(entry => !IsRestored(entry.Token)
                && !(Directory.Exists(entry.OriginalPath) && MatchesIdentity(entry.OriginalPath, entry.Identity)))
                .OrderByDescending(entry => entry.DeletedAt).ToArray();
    }

    public Task<IReadOnlyList<DeletedLibraryCapture>> RecentDeletedAsync(int limit = 20)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return Task.Run<IReadOnlyList<DeletedLibraryCapture>>(() => RecentDeleted().Take(limit).ToArray());
    }

    /// <summary>Connect on the queue owner (the UI dispatcher in production). Shell IO runs
    /// on a worker; publication resumes on that owner. Overlapping triggers do not retry a
    /// failed move continuously when the transaction releases its eviction leases.</summary>
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
        catch (Exception ex) { _log.LogWarning(ex, "Automatic recoverable library maintenance failed"); }
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
        foreach (DeletedLibraryCapture entry in restored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_queue.Find(entry.Record.Id) is null) _queue.RestoreRecord(entry.Record);
                MarkRestored(entry.Token);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Interrupted capture restore {Id} remains pending", entry.Record.Id); }
        }
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
        await Task.Run(() =>
        {
            RecycledPathReceipt receipt = ResolveReceipt(entry)
                ?? throw new IOException("The recorded Recycle Bin item is unavailable.");
            _trash.RestoreDirectory(receipt);
        });
        // A failure here leaves the restored original + durable intent for startup recovery.
        // Mark completion only after the capture metadata and index are actually durable.
        _queue.RestoreRecord(entry.Record);
        MarkRestored(entry.Token);
    }

    private RecycledPathReceipt? ResolveReceipt(DeletedLibraryCapture entry)
    {
        string file = JournalPath(entry.Token, "recycled");
        if (File.Exists(file))
        {
            try
            {
                var receipt = JsonSerializer.Deserialize<RecycledPathReceipt>(File.ReadAllText(file));
                if (receipt is not null && receipt.Identity == entry.Identity
                    && string.Equals(receipt.OriginalPath, entry.OriginalPath, StringComparison.OrdinalIgnoreCase)) return receipt;
            }
            catch (JsonException) { }
        }
        return _trash.FindRecycledDirectory(entry.OriginalPath, entry.Identity);
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
