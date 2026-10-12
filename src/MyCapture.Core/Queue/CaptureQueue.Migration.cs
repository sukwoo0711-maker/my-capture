using System.Text.Json;

namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    internal CaptureIndexFile SnapshotForMigration() => ParseMigrationIndex(
        JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions));

    internal static CaptureIndexFile ParseMigrationIndex(string json)
    {
        if (!TryParseIndex(json, out CaptureIndexFile? index) || index is null
            || (index.FolderState is not null && !IsValidFolderState(index.FolderState)))
            throw new IOException("Invalid library index or folder hierarchy.");
        return index;
    }

    internal Task DrainMigrationWritesAsync()
    {
        lock (_writeGate) return _publicationTail;
    }

    // The caller prepares immutable data on the owner. Disk publication joins the same
    // serial writer as capture/folder saves; observable state is reloaded by the owner.
    internal Task SerializeMigrationPublication(string expectedSnapshot, Action publication)
    {
        lock (_writeGate)
        {
            if (JsonSerializer.Serialize(SnapshotForMigration(), MyCapture.Core.Serialization.JsonDefaults.Readable) != expectedSnapshot)
                throw new IOException("The target library changed during migration.");
            return _publicationTail = _publicationTail.ContinueWith(_ => publication(),
                CancellationToken.None, TaskContinuationOptions.LongRunning, TaskScheduler.Default);
        }
    }
}
