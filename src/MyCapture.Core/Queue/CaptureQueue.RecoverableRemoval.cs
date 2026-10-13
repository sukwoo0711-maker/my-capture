using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Storage;

namespace MyCapture.Core.Queue;

/// <summary>
/// A removal prepared before the index changes. A user deletion has already moved the capture
/// to the Recycle Bin, and <see cref="Rollback"/> restores its exact original directory. A
/// permanent removal (automatic eviction, or a confirmed permanent deletion) changes nothing
/// until <see cref="Complete"/>, which runs only after the index stops naming the capture.
/// </summary>
public interface ICaptureRemovalTransaction
{
    bool Rollback();

    /// <summary>Runs off the queue owner after the index publication is durable.</summary>
    /// <returns><see langword="false"/> when the files could not be removed yet.</returns>
    bool Complete() => true;
}

public sealed partial class CaptureQueue
{
    /// <summary>A confirmed permanent deletion after Windows refused a Recycle Bin move.</summary>
    public const string ManualPermanentReason = "manual-permanent";

    /// <summary>Capacity and retention eviction run without a user request.</summary>
    public static bool IsAutomaticRemovalReason(string reason) =>
        reason is "capacity" or "retention-age" or "item-limit" or "byte-limit";

    /// <summary>
    /// Automatic eviction exists to free disk space, and a confirmed permanent deletion is
    /// the user's explicit choice; both delete files. Every other removal is a user deletion
    /// that goes to the Recycle Bin.
    /// </summary>
    public static bool IsPermanentRemovalReason(string reason) =>
        IsAutomaticRemovalReason(reason) || reason == ManualPermanentReason;

    private readonly HashSet<Guid> _removalReservations = [];
    private int _automaticLeaseReleaseDepth;
    private readonly object _removalOperationGate = new();
    private readonly HashSet<Task> _pendingRemovalOperations = [];

    public Task WaitForPendingRemovalsAsync()
    {
        lock (_removalOperationGate) return Task.WhenAll(_pendingRemovalOperations.ToArray());
    }

    public bool IsRemovalPending(Guid id)
    {
        lock (_evictionLeaseGate) return _removalReservations.Contains(id);
    }

    private IDisposable? ReserveRemoval(Guid id)
    {
        lock (_evictionLeaseGate)
        {
            if (_removalReservations.Contains(id) || _evictionLeaseCounts.ContainsKey(id))
                return null;
            _removalReservations.Add(id);
            _evictionLeaseCounts[id] = 1;
        }
        return new RemovalReservation(this, id);
    }

    private sealed class RemovalReservation(CaptureQueue owner, Guid id) : IDisposable
    {
        private CaptureQueue? _owner = owner;
        public void Dispose()
        {
            CaptureQueue? current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            lock (current._evictionLeaseGate) current._removalReservations.Remove(id);
            current.ReleaseEvictionLease(id);
        }
    }
    /// <summary>Production supplies a recoverable file move before a record can leave the index.
    /// Null preserves the standalone index contract. A rejected move returns null.</summary>
    public Func<CaptureRecord, string, ICaptureRemovalTransaction?>? PrepareRemoval { get; set; }

    /// <summary>Invoked on the queue owner; production schedules Shell work without blocking capture.</summary>
    public Action<IReadOnlyList<Guid>, string>? ScheduleAutomaticRemoval { get; set; }

    /// <summary>Owner-thread trigger after startup/selection/persistence releases its busy state.
    /// Does not synchronously save the index or run production Shell work.</summary>
    public void RequestAutomaticMaintenance()
    {
        EnforceLimits();
        ExpireHistory(DateTimeOffset.Now);
    }

    public Task<CaptureBatchRemovalResult> RemoveAutomaticallyAsync(IEnumerable<Guid> ids, string reason,
        Func<Guid, bool>? canRemove = null, CancellationToken cancellationToken = default)
    {
        if (PrepareRemoval is null) throw new InvalidOperationException("Recoverable removal is not connected.");
        if (reason is not ("retention-age" or "capacity")) throw new ArgumentOutOfRangeException(nameof(reason));
        return RemoveManyRecoverablyAsync(ids, canRemove, cancellationToken, reason);
    }

    private bool TryRemoveRecoverably(CaptureRecord record, string reason)
    {
        IDisposable? ownership = ReserveRemoval(record.Id);
        if (ownership is null) return false;
        _automaticLeaseReleaseDepth++;
        try { return TryRemoveRecoverablyCore(record, reason); }
        finally
        {
            try { ownership.Dispose(); }
            finally { _automaticLeaseReleaseDepth--; }
        }
    }

    private bool TryRemoveRecoverablyCore(CaptureRecord record, string reason)
    {
        ICaptureRemovalTransaction? transaction;
        try { transaction = PrepareRemoval!(record, reason); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Recycle admission failed for capture {Id}; retaining the record", record.Id);
            return false;
        }
        if (transaction is null) return false;
        int position = _records.IndexOf(record);
        _records.Remove(record);
        _totalBytes = Math.Max(0, _totalBytes - record.TotalBytes);
        try { Save(); }
        catch
        {
            // Failed restoration leaves the capture recoverable through its durable trash
            // journal, rather than putting a nonexistent original back into the normal index.
            if (TryRollback(transaction, record))
            {
                _records.Insert(Math.Min(position, _records.Count), record);
                _totalBytes += record.TotalBytes;
            }
            Save();
            throw;
        }
        UpdatePinPressureFlag();
        Evicted?.Invoke(this, new CaptureEvictedEventArgs(record, reason));
        TryComplete(transaction, record);
        return true;
    }

    /// <summary>
    /// Deletes the captures from disk after the user explicitly confirmed it for items Windows
    /// could not move to the Recycle Bin. Without a connected removal service this is the
    /// standalone index removal, whose <see cref="Evicted"/> handler owns the files.
    /// </summary>
    public Task<CaptureBatchRemovalResult> RemoveManyPermanentlyAsync(IEnumerable<Guid> ids,
        Func<Guid, bool>? canRemove = null, CancellationToken cancellationToken = default) =>
        PrepareRemoval is null
            ? RemoveManyAsync(ids, canRemove, cancellationToken)
            : RemoveManyRecoverablyAsync(ids, canRemove, cancellationToken, ManualPermanentReason);

    private Task<CaptureBatchRemovalResult> RemoveManyRecoverablyAsync(IEnumerable<Guid> ids,
        Func<Guid, bool>? canRemove, CancellationToken cancellationToken, string reason = "manual-batch")
    {
        Task<CaptureBatchRemovalResult> operation = RemoveManyRecoverablyCoreAsync(ids, canRemove, cancellationToken, reason);
        lock (_removalOperationGate) _pendingRemovalOperations.Add(operation);
        return ObserveRemovalAsync(operation);
    }

    private bool TryComplete(ICaptureRemovalTransaction transaction, CaptureRecord record)
    {
        try { return transaction.Complete(); }
        catch (Exception ex)
        {
            // The index no longer names the capture; leftover bytes are an orphan, not data loss.
            _log.LogWarning(ex, "Could not remove the files of evicted capture {Id}", record.Id);
            return false;
        }
    }

    private async Task<CaptureBatchRemovalResult> ObserveRemovalAsync(Task<CaptureBatchRemovalResult> operation)
    {
        try { return await operation; }
        finally { lock (_removalOperationGate) _pendingRemovalOperations.Remove(operation); }
    }

    private async Task<CaptureBatchRemovalResult> RemoveManyRecoverablyCoreAsync(IEnumerable<Guid> ids,
        Func<Guid, bool>? canRemove, CancellationToken cancellationToken, string reason)
    {
        var completions = new List<(CaptureRecord Record, ICaptureRemovalTransaction Transaction)>();
        CaptureBatchRemovalResult result = await RemoveFromIndexAsync(ids, canRemove, cancellationToken, reason, completions);
        if (completions.Count == 0) return result;
        // File deletion for permanent removals runs after the publication slot is released and
        // off the owner thread, so eviction never delays a capture save or the UI.
        int pending = await Task.Run(() => completions.Count(item => !TryComplete(item.Transaction, item.Record)));
        return pending == 0 ? result : result with { CleanupPendingCount = result.CleanupPendingCount + pending };
    }

    private async Task<CaptureBatchRemovalResult> RemoveFromIndexAsync(IEnumerable<Guid> ids,
        Func<Guid, bool>? canRemove, CancellationToken cancellationToken, string reason,
        List<(CaptureRecord Record, ICaptureRemovalTransaction Transaction)> completions)
    {
        HashSet<Guid> requested = ids.ToHashSet();
        using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var admissions = new List<(CaptureRecord Record, IDisposable Lease)>();
        foreach (CaptureRecord candidate in SelectRemovalCandidates(requested, reason, canRemove))
            if (ReserveRemoval(candidate.Id) is { } lease) admissions.Add((candidate, lease));
        if (admissions.Count == 0) return new(0, 0);
        CaptureRecord[] candidates = admissions.Select(item => item.Record).ToArray();
        IDisposable[] leases = admissions.Select(item => item.Lease).ToArray();
        var unavailable = new List<Guid>();
        try
        {
            // Shell IO is serialized away from the UI owner. Lease each candidate until the
            // move/index transaction ends, then publish one snapshot on the existing FIFO.
            var prepared = await Task.Run(() =>
            {
                var result = new List<(CaptureRecord Record, ICaptureRemovalTransaction Transaction)>();
                foreach (CaptureRecord record in candidates)
                {
                    try
                    {
                        if (PrepareRemoval!(record, reason) is { } transaction) result.Add((record, transaction));
                    }
                    catch (RecycleBinUnavailableException ex)
                    {
                        // Nothing moved or deleted. The caller may ask the user about a
                        // permanent deletion; it never happens implicitly.
                        _log.LogWarning(ex, "Capture {Id} cannot be moved to the Recycle Bin; keeping it", record.Id);
                        unavailable.Add(record.Id);
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Recycle admission failed for capture {Id}", record.Id); }
                }
                return result;
            });
            if (prepared.Count == 0) return new(0, 0) { RecycleUnavailableIds = unavailable };
            CaptureRecord[] removed = prepared.Select(item => item.Record).ToArray();
            _records.ReplaceWith(_records.Except(removed).ToArray());
            _totalBytes = Math.Max(0, _totalBytes - removed.Sum(record => record.TotalBytes));
            UpdatePinPressureFlag();
            Task publication;
            lock (_writeGate)
                publication = EnqueuePublication(reservation, BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
            try { await publication; }
            catch
            {
                var restored = await Task.Run(() => prepared.Where(item => TryRollback(item.Transaction, item.Record)).Select(item => item.Record).ToArray());
                CaptureRecord[] missing = restored.Where(record => Find(record.Id) is null).ToArray();
                _records.ReplaceWith(_records.Concat(missing).OrderByDescending(record => record.CreatedAt).ToArray());
                _totalBytes += missing.Sum(record => record.TotalBytes);
                UpdatePinPressureFlag();
                await SaveCorrectiveSnapshotAsync();
                throw;
            }
            foreach (CaptureRecord record in removed) Evicted?.Invoke(this, new CaptureEvictedEventArgs(record, reason));
            completions.AddRange(prepared);
            return new(removed.Length, 0) { RecycleUnavailableIds = unavailable };
        }
        finally
        {
            bool automatic = IsAutomaticRemovalReason(reason);
            if (automatic) _automaticLeaseReleaseDepth++;
            try { foreach (IDisposable lease in leases) lease.Dispose(); }
            finally { if (automatic) _automaticLeaseReleaseDepth--; }
        }
    }

    private CaptureRecord[] SelectRemovalCandidates(HashSet<Guid> requested, string reason, Func<Guid, bool>? canRemove)
    {
        bool Available(CaptureRecord record) => requested.Contains(record.Id) && !IsEvictionLeased(record.Id)
            && (canRemove?.Invoke(record.Id) ?? true);
        if (!IsAutomaticRemovalReason(reason)) return _records.Where(Available).ToArray();
        if (Volatile.Read(ref _evictionSuspensionCount) > 0) return [];
        if (reason == "retention-age")
        {
            DateTimeOffset cutoff = DateTimeOffset.Now - CaptureRetention.TimeToLive(_limits);
            return _records.Where(record => Available(record) && record.IsImage && !record.IsPinned
                && record.FolderId is null && record.RetentionReference <= cutoff).ToArray();
        }
        // Re-evaluate the current cap and ownership after waiting for the publication slot.
        var excluded = _records.Where(record => !Available(record)).Select(record => record.Id).ToHashSet();
        var selected = new List<CaptureRecord>();
        int count = HistoryCount;
        long bytes = HistoryBytes;
        while (count > _limits.MaxItems || bytes > _limits.MaxBytes)
        {
            CaptureRecord? victim = FindOldestUnpinned(excluded);
            if (victim is null) break;
            selected.Add(victim);
            excluded.Add(victim.Id);
            count--;
            bytes = Math.Max(0, bytes - victim.TotalBytes);
        }
        return selected.ToArray();
    }

    private bool TryRollback(ICaptureRemovalTransaction transaction, CaptureRecord record)
    {
        try { return transaction.Rollback(); }
        catch (Exception ex) { _log.LogWarning(ex, "Recycle rollback failed for capture {Id}", record.Id); return false; }
    }

    private Task SaveCorrectiveSnapshotAsync()
    {
        lock (_writeGate)
            return EnqueuePublication(null, BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
    }

    /// <summary>Restores an already verified directory, without assigning today's capture destination.</summary>
    public void RestoreRecord(CaptureRecord record)
    {
        if (Find(record.Id) is not null) throw new IOException("The library already contains this capture.");
        if (!TryResolveDirectory(record, out string path) || !Directory.Exists(path))
            throw new IOException("The restored capture directory is unavailable.");
        using IDisposable lease = AcquireEvictionLease(record.Id);
        using IDisposable suspension = SuspendEviction();
        if (record.FolderId is { } folder && FindFolder(folder) is null) record.FolderId = null;
        record.FolderDestinationResolved = true;
        record.RetentionStartedAt = DateTimeOffset.Now;
        record.UpdatedAt = DateTimeOffset.Now;
        _records.ReplaceWith(_records.Append(record).OrderByDescending(item => item.CreatedAt).ToArray());
        _totalBytes += record.TotalBytes;
        SaveRecordMetaOrThrow(record);
        Save();
    }
}
