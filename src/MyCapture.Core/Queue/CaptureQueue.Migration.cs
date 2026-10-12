using System.Text.Json;

namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    private bool _migrationPublicationBlocked;

    private void EnsureMigrationCanLoad()
    {
        lock (_writeGate)
            if (_migrationPublicationBlocked && (!_publicationTail.IsCompleted
                || MyCapture.Core.Storage.LegacyLibraryMigrationService.HasPendingPublication(_paths)))
                throw new IOException("Complete migration recovery before reloading the library.");
    }

    private void ResumeAfterMigrationLoad()
    {
        lock (_writeGate)
        {
            if (!_migrationPublicationBlocked) return;
            if (!_publicationTail.IsCompleted) throw new IOException("Migration publication is still running.");
            _publicationTail = Task.CompletedTask;
            _migrationPublicationBlocked = false;
        }
    }
    internal void CancelMigrationAdmission() => ResumeAfterMigrationLoad();

    internal CaptureIndexFile SnapshotForMigration()
    {
        CaptureIndexFile snapshot = CreateIndexSnapshot();
        snapshot.FolderState ??= CreateFolderState();
        return ParseMigrationIndex(JsonSerializer.Serialize(snapshot, SerializerOptions));
    }

    /// <summary>A legacy index whose folder hierarchy is damaged still lists its captures.</summary>
    internal static bool TryParseMigrationSource(string json, out CaptureIndexFile? index)
    {
        if (!TryParseIndex(json, out index) || index is null) return false;
        if (index.FolderState is not null && !IsValidFolderState(index.FolderState)) index.FolderState = null;
        return true;
    }

    internal static bool TryParseMigrationFolderState(string json, out CaptureFolderState? state) =>
        TryParseFolderState(json, out state);

    internal static bool IsValidMigrationRecord(CaptureRecord record) => IsValidIndexedRecord(record);

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

    internal void AdoptMigrationSnapshot(string json)
    {
        CaptureIndexFile index = ParseMigrationIndex(json);
        var existing = _records.ToDictionary(record => record.Id);
        List<CaptureRecord> records = index.Records.Select(record => existing.TryGetValue(record.Id, out CaptureRecord? current)
            && JsonSerializer.Serialize(current, SerializerOptions) == JsonSerializer.Serialize(record, SerializerOptions)
                ? current : record).ToList();
        RestoreFolderState(index.FolderState, records);
        _records.Clear();
        foreach (CaptureRecord record in records) _records.Add(record);
        RecalculateTotalBytes();
        UpdatePinPressureFlag();
        ResumeAfterMigrationLoad();
    }

    // The caller prepares immutable data on the owner. Disk publication joins the same
    // serial writer as capture/folder saves; observable state is reloaded by the owner.
    internal Task SerializeMigrationPublication(string expectedSnapshot, Action publication)
    {
        lock (_writeGate)
        {
            if (JsonSerializer.Serialize(SnapshotForMigration(), MyCapture.Core.Serialization.JsonDefaults.Readable) != expectedSnapshot)
                throw new IOException("The target library changed during migration.");
            if (_migrationPublicationBlocked) throw new IOException("Complete the previous migration first.");
            _migrationPublicationBlocked = true;
            return _publicationTail = _publicationTail.ContinueWith(_ => publication(),
                CancellationToken.None, TaskContinuationOptions.LongRunning, TaskScheduler.Default);
        }
    }
}
