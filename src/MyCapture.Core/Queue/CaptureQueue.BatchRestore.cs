using System.Text.Json;
using MyCapture.Core.Storage;

namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    /// <summary>
    /// Puts several captures restored from the Recycle Bin back into the library with one index
    /// publication: each capture's metadata copy and the index are written together, off the owner
    /// thread. Like <see cref="RestoreRecord"/>, a restored capture starts a fresh retention period
    /// and leaves a folder that no longer exists. A capture already in the library, or whose
    /// directory is not back in place, is skipped. Returns the captures that were indexed.
    /// </summary>
    public async Task<IReadOnlyList<CaptureRecord>> RestoreRecordsAsync(IReadOnlyList<CaptureRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        CaptureRecord[] admitted = records
            .Where(record => Find(record.Id) is null
                && TryResolveDirectory(record, out string path) && Directory.Exists(path))
            .GroupBy(record => record.Id).Select(group => group.First())
            .ToArray();
        if (admitted.Length == 0) return [];

        using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        admitted = admitted.Where(record => Find(record.Id) is null).ToArray();
        if (admitted.Length == 0) return [];
        IDisposable[] leases = admitted.Select(record => AcquireEvictionLease(record.Id)).ToArray();
        IDisposable suspension = SuspendEviction();
        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            foreach (CaptureRecord record in admitted)
            {
                if (record.FolderId is { } folder && FindFolder(folder) is null) record.FolderId = null;
                record.FolderDestinationResolved = true;
                record.RetentionStartedAt = now;
                record.UpdatedAt = now;
            }

            _records.ReplaceWith(_records.Concat(admitted).OrderByDescending(item => item.CreatedAt).ToArray());
            _totalBytes += admitted.Sum(record => record.TotalBytes);
            UpdatePinPressureFlag();
            var writes = new List<(string Path, string Content)>(admitted.Length + 2);
            Task publication;
            lock (_writeGate)
            {
                foreach (CaptureRecord record in admitted)
                    writes.Add((GetFilePath(record, CaptureFileNames.Meta), JsonSerializer.Serialize(record, MetaSerializerOptions)));
                writes.AddRange(BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
                publication = EnqueuePublication(reservation, writes.ToArray());
            }

            try
            {
                await publication;
            }
            catch
            {
                // The directories are back in place; startup recovery indexes them from their
                // journals. Keep this session's index matching what is durable.
                var restoredIds = admitted.Select(record => record.Id).ToHashSet();
                _records.ReplaceWith(_records.Where(record => !restoredIds.Contains(record.Id)).ToArray());
                _totalBytes = Math.Max(0, _totalBytes - admitted.Sum(record => record.TotalBytes));
                UpdatePinPressureFlag();
                throw;
            }

            return admitted;
        }
        finally
        {
            suspension.Dispose();
            foreach (IDisposable lease in leases) lease.Dispose();
        }
    }
}
