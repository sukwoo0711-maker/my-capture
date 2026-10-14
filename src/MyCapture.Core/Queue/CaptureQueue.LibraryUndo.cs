namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    public LibraryUndoHistory LibraryUndo { get; } = new();
    public LibraryActivityTracker Activities { get; } = new();

    private sealed record MetadataState(Guid Id, string? Title = null, string? Tags = null, bool? Pin = null)
    {
        public bool Matches(CaptureRecord record) => (Title is null || Title == record.Title)
            && (Tags is null || Tags == record.Tags) && (Pin is null || Pin == record.IsManuallyPinned);
        public void Apply(CaptureRecord record)
        {
            if (Title is not null) record.Title = Title;
            if (Tags is not null) record.Tags = Tags;
            if (Pin is { } pin)
            {
                if (record.FolderId is not null) record.FolderPinBeforeAssignment = pin;
                else record.IsPinned = pin;
            }
        }
    }

    private async Task<bool> RestoreMetadataAsync(MetadataState[] before, MetadataState[] after)
    {
        await _batchTagMutationGate.WaitAsync();
        var leases = new List<IDisposable>();
        try
        {
            using CaptureWriteReservation reservation = await ReservePublicationAsync();
            if (after.Any(state => Find(state.Id) is not { } record || IsRemovalPending(state.Id) || !state.Matches(record)))
                return false;
            CaptureRecord[] records = before.Select(state => Find(state.Id)!).ToArray();
            DateTimeOffset[] times = records.Select(record => record.UpdatedAt).ToArray();
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (CaptureRecord record in records) leases.Add(AcquireEvictionLease(record.Id));
            for (int i = 0; i < before.Length; i++)
            {
                before[i].Apply(records[i]);
                records[i].UpdatedAt = now;
            }
            try { await PublishTagsAsync(records, reservation); }
            catch
            {
                for (int i = 0; i < after.Length; i++)
                {
                    after[i].Apply(records[i]);
                    if (records[i].UpdatedAt == now) records[i].UpdatedAt = times[i];
                }
                await CorrectTagsAsync(records);
                throw;
            }
            UpdatePinPressureFlag();
            return true;
        }
        finally
        {
            foreach (IDisposable lease in leases) lease.Dispose();
            _batchTagMutationGate.Release();
        }
    }

    public async Task<bool> UpdateOrganizationAsync(Guid id, string? title, string? tags,
        CancellationToken cancellationToken = default)
    {
        string name = CaptureOrganization.NormalizeTitle(title);
        string labels = CaptureOrganization.NormalizeTags(tags);
        await _batchTagMutationGate.WaitAsync(cancellationToken);
        try
        {
            using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Find(id) is not { } record || IsRemovalPending(id)) return false;
            if (record.Title == name && record.Tags == labels) return true;
            using IDisposable lease = AcquireEvictionLease(id);
            var before = new MetadataState(id, record.Title == name ? null : record.Title,
                record.Tags == labels ? null : record.Tags);
            var after = new MetadataState(id, before.Title is null ? null : name, before.Tags is null ? null : labels);
            DateTimeOffset time = record.UpdatedAt;
            DateTimeOffset now = DateTimeOffset.Now;
            after.Apply(record); record.UpdatedAt = now;
            try { await PublishTagsAsync([record], reservation); }
            catch
            {
                before.Apply(record);
                if (record.UpdatedAt == now) record.UpdatedAt = time;
                await CorrectTagsAsync([record]);
                throw;
            }
            LibraryUndo.Record(before.Title is null ? LibraryUndoKind.Tags : LibraryUndoKind.Title,
                () => RestoreMetadataAsync([before], [after]));
            return true;
        }
        finally { _batchTagMutationGate.Release(); }
    }

    public async Task<bool?> ToggleLibraryPinAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _batchTagMutationGate.WaitAsync(cancellationToken);
        try
        {
            using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Find(id) is not { } record || IsRemovalPending(id)) return null;
            using IDisposable lease = AcquireEvictionLease(id);
            var before = new MetadataState(id, Pin: record.IsManuallyPinned);
            var after = new MetadataState(id, Pin: !record.IsManuallyPinned);
            DateTimeOffset time = record.UpdatedAt;
            DateTimeOffset now = DateTimeOffset.Now;
            after.Apply(record); record.UpdatedAt = now;
            try { await PublishTagsAsync([record], reservation); }
            catch
            {
                before.Apply(record);
                if (record.UpdatedAt == now) record.UpdatedAt = time;
                await CorrectTagsAsync([record]);
                throw;
            }
            LibraryUndo.Record(LibraryUndoKind.Pin, () => RestoreMetadataAsync([before], [after]));
            UpdatePinPressureFlag();
            return after.Pin;
        }
        finally { _batchTagMutationGate.Release(); }
    }
}
