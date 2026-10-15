namespace MyCapture.Core.Queue;

internal sealed class ImportRollbackException(Exception originalFailure, Exception correctionFailure)
    : IOException("Import rollback could not be persisted; the media directory was preserved.",
        new AggregateException(originalFailure, correctionFailure));

public sealed partial class CaptureQueue
{
    /// <summary>Publishes a completed import on the queue owner, with no default-folder fallback.</summary>
    public async Task AddImportedAsync(CaptureRecord record, CancellationToken cancellationToken = default)
    {
        using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (record.FolderId is { } folder && !IsAvailableDestination(folder))
            throw new IOException("The import folder is no longer available.");
        if (!IsValidIndexedRecord(record)) throw new InvalidDataException("Invalid imported media metadata.");
        record.FolderDestinationResolved = true;
        using IDisposable suspension = SuspendEviction();
        using IDisposable lease = AcquireEvictionLease(record.Id);
        Add(record);
        try { await PublishRecordAsync(record, reservation); }
        catch (Exception originalFailure)
        {
            _records.Remove(record);
            RecalculateTotalBytes();
            try { await SaveCorrectiveSnapshotAsync(); }
            catch (Exception correctionFailure)
            {
                throw new ImportRollbackException(originalFailure, correctionFailure);
            }
            throw;
        }
    }
}
