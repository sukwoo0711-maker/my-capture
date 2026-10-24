using System.Text.Json;

namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    private readonly SemaphoreSlim _batchTagMutationGate = new(1, 1);

    /// <summary>Publishes all changed recovery metadata and a single index snapshot as one ordered job.</summary>
    public async Task<CaptureBatchTagResult> UpdateTagsAsync(IEnumerable<Guid> ids,
        IEnumerable<CaptureTagChange> changes, CancellationToken cancellationToken = default)
    {
        Guid[] selected = ids.Distinct().ToArray();
        IReadOnlyList<CaptureTagChange> intents = CaptureOrganization.NormalizeTagChanges(changes);
        if (intents.Count == 0) return new CaptureBatchTagResult(0, 0, 0);
        await _batchTagMutationGate.WaitAsync(cancellationToken);
        var leases = new List<IDisposable>();
        var updates = new List<(CaptureRecord Record, string Before, string After, DateTimeOffset UpdatedAt)>();
        try
        {
            using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int limited = 0, missing = 0;
            foreach (Guid id in selected)
            {
                CaptureRecord? record = Find(id);
                if (record is null || IsRemovalPending(id)) { missing++; continue; }
                leases.Add(AcquireEvictionLease(id));
                CaptureTagApplication applied = CaptureOrganization.ApplyTagChanges(record.Tags, intents);
                if (applied.LimitReached) limited++;
                if (record.Tags != applied.Tags)
                    updates.Add((record, record.Tags, applied.Tags, record.UpdatedAt));
            }
            if (updates.Count == 0) return new CaptureBatchTagResult(0, limited, missing);
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (var update in updates)
            {
                update.Record.Tags = update.After;
                update.Record.UpdatedAt = now;
            }
            try { await PublishTagsAsync(updates.Select(update => update.Record), reservation); }
            catch (Exception originalFailure)
            {
                foreach (var update in updates)
                {
                    if (update.Record.Tags != update.After) continue;
                    update.Record.Tags = update.Before;
                    if (update.Record.UpdatedAt == now) update.Record.UpdatedAt = update.UpdatedAt;
                }
                // A queued OCR/folder save may include tentative tags. Correct only tag state,
                // after those jobs, while preserving their other metadata and new captures.
                try { await CorrectTagsAsync(updates.Select(update => update.Record)); }
                catch (Exception correctionFailure)
                {
                    throw new AggregateException("Tag publication and its corrective write failed.",
                        originalFailure, correctionFailure);
                }
                throw;
            }
            MetadataState[] before = updates.Select(update => new MetadataState(update.Record.Id, Tags: update.Before)).ToArray();
            MetadataState[] after = updates.Select(update => new MetadataState(update.Record.Id, Tags: update.After)).ToArray();
            LibraryUndo.Record(LibraryUndoKind.Tags, () => RestoreMetadataAsync(before, after));
            return new CaptureBatchTagResult(updates.Count, limited, missing);
        }
        finally
        {
            foreach (IDisposable lease in leases) lease.Dispose();
            _batchTagMutationGate.Release();
        }
    }

    private Task PublishTagsAsync(IEnumerable<CaptureRecord> records, CaptureWriteReservation? reservation)
    {
        lock (_writeGate)
        {
            var writes = records.Select(record => (GetFilePath(record, CaptureFileNames.Meta),
                JsonSerializer.Serialize(record, MetaSerializerOptions))).ToList();
            writes.AddRange(BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
            return EnqueuePublication(reservation, writes.ToArray());
        }
    }

    private async Task CorrectTagsAsync(IEnumerable<CaptureRecord> records)
    {
        var corrections = new List<Task>();
        lock (_writeGate)
        {
            // Each correction is ordered after existing jobs, but a locked sidecar must not
            // prevent the remaining metadata or the restored index from being published.
            foreach (CaptureRecord record in records)
                corrections.Add(EnqueuePublication(null, [(GetFilePath(record, CaptureFileNames.Meta),
                    JsonSerializer.Serialize(record, MetaSerializerOptions))]));
            corrections.Add(EnqueuePublication(null,
                BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions))));
        }
        Task completion = Task.WhenAll(corrections);
        try { await completion; }
        catch
        {
            // Task.WhenAll lists faults in the order their completions are observed, and adjacent publication
            // jobs finish on different threads. Report them in enqueue order instead.
            throw new AggregateException(corrections.Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception!.InnerExceptions));
        }
    }
}
