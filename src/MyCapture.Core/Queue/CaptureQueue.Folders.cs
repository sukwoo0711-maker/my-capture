using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Storage;

namespace MyCapture.Core.Queue;

public sealed partial class CaptureQueue
{
    private readonly List<CaptureFolder> _folders = [];
    private readonly SemaphoreSlim _folderMutationGate = new(1, 1);
    private readonly HashSet<Guid> _deletingFolders = [];
    private bool _folderStateActivated;
    private string FolderRecoveryFile => Path.Combine(_paths.DataRoot, "library-folders.json");

    public IReadOnlyList<CaptureFolder> Folders => _folders.AsReadOnly();
    public Guid? DefaultCaptureFolderId => _limits.DefaultCaptureFolderId;
    public bool UseCurrentFolderForNewCaptures => _limits.UseCurrentFolderForNewCaptures;
    public string FolderFilePrefix => _limits.FolderFilePrefix;
    public CaptureFolder? FindFolder(Guid id) => _folders.FirstOrDefault(folder => folder.Id == id);

    public IReadOnlyList<CaptureFolder> GetFolderPath(Guid id)
    {
        var path = new List<CaptureFolder>();
        var seen = new HashSet<Guid>();
        CaptureFolder? current = FindFolder(id);
        while (current is not null && seen.Add(current.Id))
        {
            path.Add(current);
            current = current.ParentId is { } parent ? FindFolder(parent) : null;
        }
        path.Reverse();
        return path;
    }

    public DateOnly GetFolderDate(Guid id)
    {
        IReadOnlyList<CaptureFolder> path = GetFolderPath(id);
        if (path.Count == 0) throw new ArgumentException("The folder no longer exists.", nameof(id));
        return path[0].CreatedDate;
    }

    public string GetFolderDisplayPath(Guid id) =>
        string.Join(" / ", GetFolderPath(id).Select(folder => folder.Name));

    public string GetExportFileName(CaptureRecord record)
    {
        string stem = !string.IsNullOrWhiteSpace(record.Title)
            ? CaptureFolderNaming.SafeStem(record.Title)
            : record.FolderId is not null
                ? CaptureFolderNaming.SequenceName(_limits.FolderFilePrefix, record.FolderSequence)
                : "MyCapture_" + record.CreatedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return stem + (record.IsVideo ? ".mp4" : ".png");
    }

    public IReadOnlyList<CaptureRecord> RecordsInScope(CaptureFolderScope scope)
    {
        if (scope.IsAll) return _records.ToArray();
        HashSet<Guid> folders = FolderIdsInScope(scope);
        return _records.Where(record => record.FolderId is { } folderId
            ? folders.Contains(folderId)
            : scope.FolderId is null && scope.Date == DateOnly.FromDateTime(record.CreatedAt.DateTime)).ToArray();
    }

    public IReadOnlyList<CaptureFolder> FoldersInScope(CaptureFolderScope scope)
    {
        HashSet<Guid> ids = FolderIdsInScope(scope);
        return _folders.Where(folder => ids.Contains(folder.Id)).ToArray();
    }

    private HashSet<Guid> FolderIdsInScope(CaptureFolderScope scope)
    {
        if (scope.IsAll) return _folders.Select(folder => folder.Id).ToHashSet();
        var selected = new HashSet<Guid>();
        foreach (CaptureFolder folder in _folders)
        {
            IReadOnlyList<CaptureFolder> path = GetFolderPath(folder.Id);
            if (scope.FolderId is { } id ? path.Any(ancestor => ancestor.Id == id)
                : path.Count > 0 && path[0].CreatedDate == scope.Date)
                selected.Add(folder.Id);
        }
        return selected;
    }

    public CaptureFolderDeletionPreview PreviewFolderDeletion(CaptureFolderScope scope)
    {
        int folders = FolderIdsInScope(scope).Count;
        IReadOnlyList<CaptureRecord> records = RecordsInScope(scope);
        return new(folders, Math.Max(0, folders - (scope.FolderId is null ? 0 : 1)),
            records.Count(record => record.IsImage), records.Count(record => record.IsVideo));
    }

    public Task<CaptureFolder> CreateFolderAsync(string name, Guid? parentId = null,
        DateTimeOffset? createdAt = null, CancellationToken cancellationToken = default)
    {
        string normalized = CaptureFolderNaming.NormalizeFolderName(name);
        return MutateFolderStateAsync(() =>
        {
            if (_folders.Count >= CaptureFolderNaming.MaximumFolders)
                throw new InvalidOperationException("The library folder limit has been reached.");
            if (parentId is { } parent && FindFolder(parent) is null)
                throw new ArgumentException("The parent folder no longer exists.");
            if (parentId is { } ancestor && GetFolderPath(ancestor).Count >= CaptureFolderNaming.MaximumDepth)
                throw new ArgumentException("The folder hierarchy is too deep.");
            var folder = new CaptureFolder { Name = normalized, ParentId = parentId,
                CreatedAt = createdAt ?? DateTimeOffset.Now };
            EnsureUniqueFolderName(folder, normalized, parentId);
            _folders.Add(folder);
            return folder;
        }, cancellationToken);
    }

    public Task<bool> RenameFolderAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        string normalized = CaptureFolderNaming.NormalizeFolderName(name);
        return MutateFolderStateAsync(() =>
        {
            CaptureFolder folder = FindFolder(id) ?? throw new ArgumentException("The folder no longer exists.");
            EnsureUniqueFolderName(folder, normalized, folder.ParentId);
            bool changed = folder.Name != normalized;
            folder.Name = normalized;
            return changed;
        }, cancellationToken);
    }

    public Task<bool> MoveFolderAsync(Guid id, Guid? parentId, CancellationToken cancellationToken = default) =>
        MutateFolderStateAsync(() =>
        {
            CaptureFolder folder = FindFolder(id) ?? throw new ArgumentException("The folder no longer exists.");
            if (parentId is { } parent)
            {
                if (FindFolder(parent) is null) throw new ArgumentException("The parent folder no longer exists.");
                IReadOnlyList<CaptureFolder> path = GetFolderPath(parent);
                if (path.Any(ancestor => ancestor.Id == id)) throw new ArgumentException("A folder cannot contain itself.");
                int height = FoldersInScope(CaptureFolderScope.ForFolder(id))
                    .Select(child => GetFolderPath(child.Id).Count - GetFolderPath(id).Count + 1).DefaultIfEmpty(1).Max();
                if (path.Count + height > CaptureFolderNaming.MaximumDepth)
                    throw new ArgumentException("The folder hierarchy is too deep.");
            }
            EnsureUniqueFolderName(folder, folder.Name, parentId);
            bool changed = folder.ParentId != parentId;
            folder.ParentId = parentId;
            return changed;
        }, cancellationToken);

    private void EnsureUniqueFolderName(CaptureFolder folder, string name, Guid? parentId)
    {
        if (_folders.Any(other => other.Id != folder.Id && other.ParentId == parentId
            && (parentId is not null || other.CreatedDate == folder.CreatedDate)
            && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A folder with this name already exists in that location.");
    }

    public Task<int> MoveCapturesAsync(IEnumerable<Guid> ids, Guid? folderId,
        CancellationToken cancellationToken = default)
    {
        Guid[] requested = ids.Distinct().ToArray();
        return MutateFolderStateAsync(() =>
        {
            CaptureFolder? folder = folderId is { } destinationId
                ? FindFolder(destinationId) ?? throw new ArgumentException("The destination folder no longer exists.")
                : null;
            int moved = 0;
            foreach (Guid id in requested)
            {
                CaptureRecord? record = Find(id);
                if (record is null || record.FolderId == folderId) continue;
                if (folder is null) ClearFolderAssignment(record);
                else AssignToFolder(record, folder);
                moved++;
            }
            return moved;
        }, cancellationToken);
    }

    public Task<bool> SetNewCaptureFolderAsync(Guid? folderId, bool enabled,
        CancellationToken cancellationToken = default) =>
        MutateFolderStateAsync(() =>
        {
            if (folderId is { } id && FindFolder(id) is null)
                throw new ArgumentException("The destination folder no longer exists.");
            _limits.DefaultCaptureFolderId = enabled ? folderId : null;
            _limits.UseCurrentFolderForNewCaptures = enabled;
            return true;
        }, cancellationToken);

    /// <summary>Resolve the destination on the owner before writing the initial crash journal.</summary>
    public void PrepareNewCaptureFolder(CaptureRecord record)
    {
        if (Find(record.Id) is not null) return;
        if (record.FolderId is { } assigned)
        {
            if (FindFolder(assigned) is { } existing && !_deletingFolders.Contains(assigned))
            {
                record.FolderPinBeforeAssignment ??= record.IsPinned;
                record.IsPinned = true;
                if (record.FolderSequence <= 0) AssignToFolder(record, existing);
                existing.NextSequence = Math.Max(existing.NextSequence, checked(record.FolderSequence + 1));
                record.FolderDestinationResolved = true;
                return;
            }
            ClearFolderAssignment(record);
        }
        if (!record.FolderDestinationResolved && _limits.UseCurrentFolderForNewCaptures
            && _limits.DefaultCaptureFolderId is { } destination && !_deletingFolders.Contains(destination)
            && FindFolder(destination) is { } folder)
            AssignToFolder(record, folder);
        record.FolderDestinationResolved = true;
    }

    private static void ClearFolderAssignment(CaptureRecord record)
    {
        record.IsPinned = record.FolderPinBeforeAssignment ?? record.IsPinned;
        record.FolderPinBeforeAssignment = null;
        record.FolderId = null;
        record.FolderSequence = 0;
    }

    private static void AssignToFolder(CaptureRecord record, CaptureFolder folder)
    {
        long sequence = folder.NextSequence;
        if (sequence >= long.MaxValue - 1) throw new InvalidOperationException("The folder sequence is exhausted.");
        folder.NextSequence = checked(sequence + 1);
        record.FolderPinBeforeAssignment ??= record.IsPinned;
        record.IsPinned = true; // 4.0.2 also retains this record when it ignores folder fields.
        record.FolderId = folder.Id;
        record.FolderSequence = sequence;
    }

    public async Task<CaptureFolderDeletionResult> DeleteFolderScopeAsync(CaptureFolderScope scope,
        Func<Guid, bool>? canRemove = null, CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? expectedCaptureIds = null, IReadOnlyCollection<Guid>? expectedFolderIds = null)
    {
        await _folderMutationGate.WaitAsync(cancellationToken);
        HashSet<Guid> folders = FolderIdsInScope(scope);
        try
        {
            if (expectedFolderIds is not null && !folders.SetEquals(expectedFolderIds)
                || expectedCaptureIds is not null && !RecordsInScope(scope).Select(record => record.Id).ToHashSet().SetEquals(expectedCaptureIds))
                throw new InvalidOperationException("The folder changed after deletion was confirmed.");
            _deletingFolders.UnionWith(folders);
            Guid[] targets = RecordsInScope(scope).Select(record => record.Id).ToArray();
            CaptureBatchRemovalResult result = await RemoveManyAsync(targets, canRemove, cancellationToken);
            int remaining = RecordsInScope(scope).Count;
            int removedFolders = 0;
            if (remaining == 0 && folders.Count > 0)
            {
                // Capture removal is already durable. Complete metadata cleanup even if the
                // caller cancels now; never resurrect captures whose media was removed.
                removedFolders = await MutateFolderStateCoreAsync(() =>
                {
                    int removed = _folders.RemoveAll(folder => folders.Contains(folder.Id));
                    if (_limits.DefaultCaptureFolderId is { } id && folders.Contains(id))
                    {
                        _limits.DefaultCaptureFolderId = null;
                        _limits.UseCurrentFolderForNewCaptures = false;
                    }
                    return removed;
                }, CancellationToken.None);
            }
            return new(removedFolders, result.RemovedCount, remaining, result.CleanupPendingCount);
        }
        finally
        {
            _deletingFolders.ExceptWith(folders);
            _folderMutationGate.Release();
        }
    }

    private async Task<T> MutateFolderStateAsync<T>(Func<T> mutate, CancellationToken cancellationToken)
    {
        await _folderMutationGate.WaitAsync(cancellationToken);
        try { return await MutateFolderStateCoreAsync(mutate, cancellationToken); }
        finally { _folderMutationGate.Release(); }
    }

    private async Task<T> MutateFolderStateCoreAsync<T>(Func<T> mutate, CancellationToken cancellationToken)
    {
        using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable suspension = SuspendEviction();
        CaptureFolder[] previousFolders = _folders.Select(folder => folder.Copy()).ToArray();
        Dictionary<Guid, FolderRecordSnapshot> previousRecords =
            _records.ToDictionary(record => record.Id, record => new FolderRecordSnapshot(record));
        Guid? previousDefault = _limits.DefaultCaptureFolderId;
        bool previousEnabled = _limits.UseCurrentFolderForNewCaptures;
        bool previousActivated = _folderStateActivated;
        bool changed = false;
        Dictionary<Guid, FolderRecordSnapshot> afterRecords = [];
        Guid? tentativeDefault = previousDefault;
        bool tentativeEnabled = previousEnabled;
        try
        {
            T result = mutate();
            _folderStateActivated = true;
            changed = true;
            CaptureRecord[] touched = _records.Where(record => previousRecords.TryGetValue(record.Id, out var before)
                && !before.Matches(record)).ToArray();
            afterRecords = touched.ToDictionary(record => record.Id, record => new FolderRecordSnapshot(record));
            tentativeDefault = _limits.DefaultCaptureFolderId;
            tentativeEnabled = _limits.UseCurrentFolderForNewCaptures;
            Task publication;
            lock (_writeGate)
            {
                var writes = touched.Select(record => (GetFilePath(record, CaptureFileNames.Meta),
                    JsonSerializer.Serialize(record, MetaSerializerOptions))).ToList();
                writes.AddRange(BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
                publication = EnqueuePublication(reservation, writes.ToArray());
            }
            await publication;
            UpdatePinPressureFlag();
            return result;
        }
        catch (Exception originalFailure)
        {
            var allocatedSequences = _folders.ToDictionary(folder => folder.Id, folder => folder.NextSequence);
            _folders.Clear();
            _folders.AddRange(previousFolders);
            foreach (CaptureFolder folder in _folders)
                folder.NextSequence = Math.Max(folder.NextSequence, allocatedSequences.GetValueOrDefault(folder.Id));
            foreach (CaptureRecord record in _records)
            {
                if (previousRecords.TryGetValue(record.Id, out var before)
                    && (!changed || afterRecords.ContainsKey(record.Id)))
                {
                    bool latestPin = record.IsManuallyPinned;
                    bool preservePin = changed && afterRecords.TryGetValue(record.Id, out var after)
                        && after.ManualPin != latestPin;
                    before.Restore(record);
                    if (preservePin)
                    {
                        if (record.FolderId is not null) record.FolderPinBeforeAssignment = latestPin;
                        else record.IsPinned = latestPin;
                    }
                }
                else if (record.FolderId is { } id && FindFolder(id) is null) ClearFolderAssignment(record);
            }
            if (!changed || (_limits.DefaultCaptureFolderId == tentativeDefault
                && _limits.UseCurrentFolderForNewCaptures == tentativeEnabled))
            {
                _limits.DefaultCaptureFolderId = previousDefault;
                _limits.UseCurrentFolderForNewCaptures = previousEnabled;
            }
            _folderStateActivated = previousActivated || changed;
            ReconcileFolderSequences();
            if (changed)
            {
                // A later capture/OCR publication may have observed the tentative folder
                // state. Append a corrective snapshot after it without reverting its pixels,
                // title, OCR or newly added records.
                Task correction;
                lock (_writeGate)
                {
                    var writes = _records.Where(record => afterRecords.ContainsKey(record.Id)
                            || !previousRecords.ContainsKey(record.Id))
                        .Select(record => (GetFilePath(record, CaptureFileNames.Meta),
                            JsonSerializer.Serialize(record, MetaSerializerOptions))).ToList();
                    writes.AddRange(BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
                    correction = EnqueuePublication(null, writes.ToArray());
                }
                try { await correction; }
                catch (Exception correctionFailure)
                {
                    throw new AggregateException("Folder publication and its corrective write failed; media was preserved.",
                        originalFailure, correctionFailure);
                }
            }
            throw;
        }
    }

    private sealed record FolderRecordSnapshot(Guid? FolderId, long Sequence, bool? WasPinned, bool IsPinned)
    {
        internal FolderRecordSnapshot(CaptureRecord record)
            : this(record.FolderId, record.FolderSequence, record.FolderPinBeforeAssignment, record.IsPinned) { }
        internal bool ManualPin => FolderId is not null ? WasPinned ?? IsPinned : IsPinned;
        internal bool Matches(CaptureRecord record) => FolderId == record.FolderId
            && Sequence == record.FolderSequence && WasPinned == record.FolderPinBeforeAssignment && IsPinned == record.IsPinned;
        internal void Restore(CaptureRecord record)
        {
            record.FolderId = FolderId; record.FolderSequence = Sequence;
            record.FolderPinBeforeAssignment = WasPinned; record.IsPinned = IsPinned;
        }
    }

    private CaptureIndexFile CreateIndexSnapshot() => new()
    {
        Records = [.. _records], FolderState = _folderStateActivated ? CreateFolderState() : null,
    };

    private CaptureFolderState CreateFolderState() => new()
    {
        Folders = _folders.Select(folder => folder.Copy()).ToList(),
        Assignments = _records.Where(record => record.FolderId is not null).Select(record => new CaptureFolderAssignment
        {
            CaptureId = record.Id, FolderId = record.FolderId!.Value, Sequence = record.FolderSequence,
            WasPinned = record.FolderPinBeforeAssignment ?? false,
        }).ToList(),
        DefaultCaptureFolderId = _limits.DefaultCaptureFolderId,
        UseCurrentFolderForNewCaptures = _limits.UseCurrentFolderForNewCaptures,
    };

    private (string Path, string Content)[] BuildIndexWrites(string json) => _folderStateActivated
        ? [(FolderRecoveryFile, JsonSerializer.Serialize(CreateFolderState(), SerializerOptions)), (_paths.IndexFile, json)]
        : [(_paths.IndexFile, json)];

    private void RestoreFolderState(CaptureFolderState? state, List<CaptureRecord> records)
    {
        if (state is null || !IsValidFolderState(state))
        {
            string? recovery = AtomicFile.ReadAllTextWithRecovery(FolderRecoveryFile,
                candidate => TryParseFolderState(candidate, out _));
            state = recovery is not null && TryParseFolderState(recovery, out var restored) ? restored : null;
        }
        _folders.Clear();
        _folderStateActivated = state is not null;
        if (state is not null)
        {
            _folders.AddRange(state.Folders.Select(folder => folder.Copy()));
            var assignments = state.Assignments.ToDictionary(assignment => assignment.CaptureId);
            foreach (CaptureRecord record in records)
            {
                if (assignments.TryGetValue(record.Id, out var assignment))
                {
                    record.FolderId = assignment.FolderId;
                    record.FolderSequence = assignment.Sequence;
                    record.FolderPinBeforeAssignment = assignment.WasPinned;
                    record.IsPinned = true;
                }
                else if (record.FolderId is { } pendingFolder && FindFolder(pendingFolder) is not null
                    && record.FolderSequence is > 0 and < long.MaxValue - 1)
                {
                    // Pending-original recovery can add a capture newer than the last index
                    // snapshot. Its durable journal already carries the chosen destination.
                    record.FolderPinBeforeAssignment ??= record.IsPinned;
                    record.IsPinned = true;
                }
                else if (record.FolderId is not null) ClearFolderAssignment(record);
            }
            _limits.DefaultCaptureFolderId = state.DefaultCaptureFolderId;
            _limits.UseCurrentFolderForNewCaptures = state.UseCurrentFolderForNewCaptures;
        }
        else
        {
            // Unrecoverable organization must not make archived captures expire. Keep their
            // compatible pin, but remove dangling navigation rather than hiding their media.
            foreach (CaptureRecord record in records)
            {
                record.FolderId = null; record.FolderSequence = 0; record.FolderPinBeforeAssignment = null;
            }
            _limits.DefaultCaptureFolderId = null;
        }
        foreach (CaptureRecord record in records.Where(record => record.FolderId is not null))
        {
            CaptureFolder? folder = FindFolder(record.FolderId!.Value);
            if (folder is not null) folder.NextSequence = Math.Max(folder.NextSequence, checked(record.FolderSequence + 1));
        }
    }

    private void ReconcileFolderSequences()
    {
        foreach (CaptureRecord record in _records.Where(record => record.FolderId is not null))
        {
            if (FindFolder(record.FolderId!.Value) is { } folder)
                folder.NextSequence = Math.Max(folder.NextSequence, checked(record.FolderSequence + 1));
        }
    }

    private static bool TryParseFolderState(string text, out CaptureFolderState? state)
    {
        state = null;
        try
        {
            state = JsonSerializer.Deserialize<CaptureFolderState>(text, SerializerOptions);
            return state is not null && IsValidFolderState(state);
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsValidFolderState(CaptureFolderState state)
    {
        if (state.Version != 1 || state.Folders is null || state.Assignments is null
            || state.Folders.Count > CaptureFolderNaming.MaximumFolders) return false;
        var folders = new Dictionary<Guid, CaptureFolder>();
        foreach (CaptureFolder? folder in state.Folders)
        {
            if (folder is null || folder.Id == Guid.Empty
                || folder.NextSequence is < 1 or >= long.MaxValue - 1
                || !CaptureFolderNaming.IsValidComponent(folder.Name, CaptureFolderNaming.MaximumNameLength)
                || !folders.TryAdd(folder.Id, folder)) return false;
        }
        var siblings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CaptureFolder folder in state.Folders)
        {
            if (folder.ParentId is { } parent && !folders.ContainsKey(parent)) return false;
            string key = (folder.ParentId?.ToString("N") ?? folder.CreatedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                + "/" + folder.Name;
            if (!siblings.Add(key)) return false;
            var seen = new HashSet<Guid>();
            CaptureFolder? current = folder;
            while (current is not null)
            {
                if (!seen.Add(current.Id) || seen.Count > CaptureFolderNaming.MaximumDepth) return false;
                current = current.ParentId is { } id ? folders[id] : null;
            }
        }
        var captures = new HashSet<Guid>();
        foreach (CaptureFolderAssignment? assignment in state.Assignments)
            if (assignment is null || assignment.CaptureId == Guid.Empty || !captures.Add(assignment.CaptureId)
                || !folders.ContainsKey(assignment.FolderId) || assignment.Sequence is < 1 or >= long.MaxValue - 1)
                return false;
        return state.DefaultCaptureFolderId is null || folders.ContainsKey(state.DefaultCaptureFolderId.Value);
    }
}
