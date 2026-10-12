using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyCapture.Core.Annotations;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Storage;

/// <summary>Read-only legacy snapshots, bounded streaming copies, and journaled index-last publication.</summary>
public sealed class LegacyLibraryMigrationService : ILegacyLibraryMigrationService
{
    private const string OperationsDirectory = "library-migration";
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions Json = JsonDefaults.Readable;
    internal Action<string>? CheckpointForTest { get; set; }

    public async Task<LegacyMigrationScan> ScanAsync(string sourceDataRoot, AppPaths targetPaths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SourceSnapshot source = await Task.Run(() => ReadSource(sourceDataRoot, targetPaths, cancellationToken, hashPayloads: false), cancellationToken);
            return new(source.Records.Count == 0 && source.Settings is null && source.Folders.Count == 0
                ? LegacyMigrationScanStatus.NoData : LegacyMigrationScanStatus.Ready,
                source.Records.Count(record => record.IsImage), source.Records.Count(record => record.IsVideo),
                source.Folders.Count, source.Files.Sum(file => file.Length), source.Settings is not null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsExpected(error)) { return new(LegacyMigrationScanStatus.Failed, ErrorCode: Code(error)); }
    }

    public async Task<LegacyMigrationResult> ImportAsync(string sourceDataRoot, AppPaths targetPaths,
        CaptureQueue quiescedTargetQueue, AppSettings currentSettings,
        IProgress<LegacyMigrationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quiescedTargetQueue);
        ArgumentNullException.ThrowIfNull(currentSettings);
        string? operation = null;
        Journal? journal = null;
        AppPaths? resolvedTarget = null;
        bool publicationStarted = false;
        bool publicationAdmitted = false;
        try
        {
            await quiescedTargetQueue.DrainMigrationWritesAsync();
            CaptureIndexFile target = quiescedTargetQueue.SnapshotForMigration();
            string targetSnapshot = Serialize(target);
            resolvedTarget = ResolveTarget(targetPaths, currentSettings);
            string expectedIndexHash = OptionalHash(resolvedTarget.IndexFile);
            progress?.Report(new(LegacyMigrationStage.Scanning, 0, 0, 0, 0));
            (operation, journal) = await Task.Run(() => Prepare(sourceDataRoot, resolvedTarget, target, expectedIndexHash,
                progress, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Serialize(quiescedTargetQueue.SnapshotForMigration()) != targetSnapshot)
                throw new MigrationFault("TargetChanged");
            CheckpointForTest?.Invoke("BeforePublication");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(LegacyMigrationStage.Publishing, journal.Copies.Count, journal.Copies.Count,
                journal.Copies.Sum(copy => copy.Length), journal.Copies.Sum(copy => copy.Length)));
            // Publication cannot be canceled halfway. Recovery completes the exact recorded
            // transaction before a replacement process may expose its library.
            Task publication = quiescedTargetQueue.SerializeMigrationPublication(targetSnapshot, () =>
            {
                if (OptionalHash(resolvedTarget.IndexFile) != journal.BeforeIndexHash) throw new MigrationFault("TargetChanged");
                publicationStarted = true;
                journal.Phase = "Publishing";
                SaveJournal(operation, journal);
                Publish(operation, journal, resolvedTarget);
            });
            publicationAdmitted = true;
            await publication;
            quiescedTargetQueue.AdoptMigrationSnapshot(journal.IndexJson);
            return new(LegacyMigrationStatus.Completed, journal.ImportedCount, journal.SkippedCount,
                journal.HasLegacySettings ? Path.Combine(operation, "legacy-settings.json") : null);
        }
        catch (OperationCanceledException)
        {
            if (journal?.Phase == "Publishing") return new(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired");
            if (operation is not null && journal is not null)
            {
                journal.Phase = "Canceled";
                try { SaveJournal(operation, journal); } catch (IOException) { } // Preserve a failed prepared journal.
            }
            return new(LegacyMigrationStatus.Canceled);
        }
        catch (Exception error) when (IsExpected(error))
        {
            if (publicationStarted && operation is not null && journal is not null && resolvedTarget is not null)
            {
                // The durable index is the commit decision, not the in-memory phase: writing
                // the completed receipt may itself fail. Never return with an old live queue.
                try
                {
                    if (OptionalHash(resolvedTarget.IndexFile) == journal.IndexHash)
                        quiescedTargetQueue.AdoptMigrationSnapshot(journal.IndexJson, completed: false);
                }
                catch (Exception classificationError) when (IsExpected(classificationError))
                { return new(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired"); }
                try
                {
                    await Task.Run(() =>
                    {
                        journal.Phase = "Publishing";
                        SaveJournal(operation, journal);
                        Publish(operation, journal, resolvedTarget, runCheckpoints: false);
                    });
                    quiescedTargetQueue.AdoptMigrationSnapshot(journal.IndexJson);
                    return new(LegacyMigrationStatus.Completed, journal.ImportedCount, journal.SkippedCount,
                        journal.HasLegacySettings ? Path.Combine(operation, "legacy-settings.json") : null);
                }
                catch (Exception recoveryError) when (IsExpected(recoveryError))
                {
                    try
                    {
                        if (OptionalHash(resolvedTarget.IndexFile) == journal.IndexHash)
                            quiescedTargetQueue.AdoptMigrationSnapshot(journal.IndexJson, completed: false);
                    }
                    catch (Exception classificationError) when (IsExpected(classificationError)) { }
                    return new(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired");
                }
            }
            if (publicationAdmitted) quiescedTargetQueue.CancelMigrationAdmission();
            // A publishing journal remains retryable; never turn an index already committed
            // into an abandoned operation or pretend a receipt failure means no data changed.
            return new(LegacyMigrationStatus.Failed, ErrorCode: journal?.Phase == "Publishing"
                ? "RecoveryRequired" : Code(error));
        }
    }

    public Task RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            string operations = OperationsRoot(targetPaths);
            if (!Directory.Exists(operations)) return;
            AppPaths? resolved = null;
            foreach (string candidate in Directory.EnumerateDirectories(operations))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string operation = ValidatedFilePaths.DirectoryWithinRoot(operations, candidate);
                if (!Guid.TryParseExact(Path.GetFileName(operation), "N", out _)) throw new MigrationFault("InvalidJournal");
                string journalPath = ValidatedFilePaths.FileWithinRoot(operation, "journal.json", ".json");
                if (!File.Exists(journalPath)) continue; // Unpublished failed preparation, retained intact.
                Journal journal = ReadJournal(journalPath);
                if (journal.Phase == "Publishing")
                {
                    resolved ??= ResolveTargetFromDisk(targetPaths);
                    ValidateJournal(operation, journal, resolved);
                    Publish(operation, journal, resolved);
                }
            }
        }, cancellationToken);

    internal static bool HasPendingPublication(AppPaths paths)
    {
        string operations = OperationsRoot(paths);
        if (!Directory.Exists(operations)) return false;
        foreach (string candidate in Directory.EnumerateDirectories(operations))
        {
            string operation = ValidatedFilePaths.DirectoryWithinRoot(operations, candidate);
            string journal = ValidatedFilePaths.FileWithinRoot(operation, "journal.json", ".json");
            if (File.Exists(journal) && ReadJournal(journal).Phase == "Publishing") return true;
        }
        return false;
    }

    private (string Operation, Journal Journal) Prepare(string sourceRoot, AppPaths targetPaths, CaptureIndexFile target, string expectedIndexHash,
        IProgress<LegacyMigrationProgress>? progress, CancellationToken cancellationToken)
    {
        SourceSnapshot source = ReadSource(sourceRoot, targetPaths, cancellationToken);
        CheckpointForTest?.Invoke("AfterSourceSnapshot");
        string operations = OperationsRoot(targetPaths);
        Directory.CreateDirectory(operations);
        string operation = ValidatedFilePaths.DirectoryWithinRoot(operations, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operation);
        string stage = ValidatedFilePaths.CreateDirectory(Path.Combine(operation, "staging"));
        var journal = new Journal { SourceRoot = source.Root, TargetRoot = targetPaths.DataRoot, CapturesRoot = targetPaths.CapturesRoot,
            BeforeIndexHash = expectedIndexHash, Phase = "Prepared" };
        BackupIfPresent(targetPaths.SettingsFile, Path.Combine(operation, "store-settings-before.json"));
        if (source.Settings is not null)
        {
            string originalSettings = ValidatedFilePaths.FileWithinRoot(source.Root, "settings.json", ".json");
            StreamCopy(originalSettings, Path.Combine(operation, "legacy-settings.json"), Hash(originalSettings, cancellationToken),
                cancellationToken, _ => { });
            journal.HasLegacySettings = true;
        }

        CaptureFolderState state = target.FolderState ?? new();
        target.FolderState = state;
        (Dictionary<Guid, Guid> previousMap, Dictionary<Guid, string> previousRecords) = ReadCompletedMappings(operations, source.Root, targetPaths);
        var folderMap = new Dictionary<Guid, Guid>();
        var usedIds = state.Folders.Select(folder => folder.Id).ToHashSet();
        var foldersById = state.Folders.ToDictionary(folder => folder.Id);
        foreach (CaptureFolder folder in source.Folders)
            folderMap.Add(folder.Id, AllocateFolderId(source.Root, folder.Id.ToString("N"), previousMap,
                folder.Id, usedIds));
        Guid historyId = AllocateFolderId(source.Root, "unassigned", previousMap, Guid.Empty, usedIds);
        journal.FolderMap = new(folderMap) { [Guid.Empty] = historyId };
        foreach (CaptureFolder folder in source.Folders)
        {
            Guid id = folderMap[folder.Id];
            if (foldersById.ContainsKey(id)) continue;
            var added = new CaptureFolder { Id = id, ParentId = folder.ParentId is { } parent ? folderMap[parent] : null,
                Name = folder.Name, CreatedAt = folder.CreatedAt, NextSequence = folder.NextSequence };
            added.Name = UniqueFolderName(state.Folders, added);
            state.Folders.Add(added);
            foldersById.Add(id, added);
        }
        Dictionary<Guid, List<SourceFile>> filesByRecord = source.Files.GroupBy(file => file.RecordId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var recordsById = target.Records.ToDictionary(record => record.Id);
        var sequencesByFolder = state.Assignments.GroupBy(assignment => assignment.FolderId)
            .ToDictionary(group => group.Key, group => group.Select(assignment => assignment.Sequence).ToHashSet());
        long copied = 0;
        long total = source.Files.Where(file => file.Name != CaptureFileNames.Meta).Sum(file => file.Length);
        int completed = 0;
        var timer = Stopwatch.StartNew();
        foreach (CaptureRecord record in source.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<SourceFile> files = filesByRecord[record.Id];
            string payloadKey = string.Join("\n", files.Where(file => file.Name != CaptureFileNames.Meta)
                .OrderBy(file => file.Name).Select(file => file.Name + ":" + file.Hash)) + SemanticRecord(record)
                + "\nfolder:" + record.FolderId + ":" + record.FolderSequence;
            string sourceIdentity = TextHash(payloadKey);
            Guid desiredFolder = record.FolderId is { } sourceFolder ? folderMap[sourceFolder] : historyId;
            recordsById.TryGetValue(record.Id, out CaptureRecord? same);
            Guid id = same is null ? record.Id : WasImported(same, sourceIdentity, previousRecords)
                && SamePayload(same, record, files, targetPaths, desiredFolder)
                ? same.Id : MappedId(source.Root, "capture", record.Id.ToString("N") + payloadKey);
            recordsById.TryGetValue(id, out CaptureRecord? mapped);
            if (mapped is not null)
            {
                if (!WasImported(mapped, sourceIdentity, previousRecords)
                    || !SamePayload(mapped, record, files, targetPaths, desiredFolder)) throw new MigrationFault("IdConflict");
                journal.RecordSources[id] = sourceIdentity;
                journal.SkippedCount++; completed++; continue;
            }
            CaptureRecord imported = JsonSerializer.Deserialize<CaptureRecord>(Serialize(record), Json)!;
            imported.Id = id;
            imported.RelativeDirectory = Path.Combine("import-" + Path.GetFileName(operation), id.ToString("N"));
            imported.FolderPinBeforeAssignment ??= imported.IsPinned;
            imported.IsPinned = true;
            imported.FolderId = desiredFolder;
            if (imported.FolderId == historyId && !foldersById.ContainsKey(historyId))
            {
                var history = new CaptureFolder { Id = historyId, Name = "이전 MyCapture", CreatedAt = record.CreatedAt };
                history.Name = UniqueFolderName(state.Folders, history); state.Folders.Add(history); foldersById.Add(history.Id, history);
            }
            CaptureFolder destination = foldersById[imported.FolderId.Value];
            if (!sequencesByFolder.TryGetValue(destination.Id, out HashSet<long>? sequences))
                sequencesByFolder.Add(destination.Id, sequences = []);
            if (record.FolderId is not null && record.FolderSequence > 0
                && !sequences.Contains(record.FolderSequence))
            {
                imported.FolderSequence = record.FolderSequence;
                destination.NextSequence = Math.Max(destination.NextSequence, record.FolderSequence + 1);
            }
            else imported.FolderSequence = destination.NextSequence++;
            sequences.Add(imported.FolderSequence);
            string stagedDirectory = ValidatedFilePaths.CreateDirectory(Path.Combine(stage, id.ToString("N")));
            foreach (SourceFile file in files.Where(file => file.Name != CaptureFileNames.Meta))
            {
                string staged = ValidatedFilePaths.FileWithinRoot(stagedDirectory, file.Name);
                StreamCopy(file.Path, staged, file.Hash, cancellationToken, bytes =>
                {
                    copied += bytes;
                    if (timer.ElapsedMilliseconds >= 75)
                    {
                        progress?.Report(new(LegacyMigrationStage.Copying, completed, source.Records.Count, copied, total));
                        timer.Restart();
                    }
                });
                journal.Copies.Add(new() { StageRelative = Path.GetRelativePath(operation, staged),
                    TargetRelative = Path.Combine(imported.RelativeDirectory, file.Name), Hash = file.Hash, Length = file.Length });
            }
            string meta = ValidatedFilePaths.FileWithinRoot(stagedDirectory, CaptureFileNames.Meta, ".json");
            File.WriteAllText(meta, Serialize(imported), new UTF8Encoding(false));
            journal.Copies.Add(new() { StageRelative = Path.GetRelativePath(operation, meta),
                TargetRelative = Path.Combine(imported.RelativeDirectory, CaptureFileNames.Meta), Hash = Hash(meta), Length = new FileInfo(meta).Length });
            target.Records.Add(imported);
            recordsById.Add(imported.Id, imported);
            journal.RecordSources[imported.Id] = sourceIdentity;
            state.Assignments.Add(new() { CaptureId = id, FolderId = imported.FolderId.Value,
                Sequence = imported.FolderSequence, WasPinned = imported.FolderPinBeforeAssignment ?? false });
            journal.ImportedCount++; completed++;
        }
        target.Records = target.Records.OrderByDescending(record => record.CreatedAt).ToList();
        if (!foldersById.ContainsKey(historyId)) journal.FolderMap.Remove(Guid.Empty);
        // Existing Store destination choices remain in the target folder state.
        journal.IndexJson = Serialize(target);
        _ = CaptureQueue.ParseMigrationIndex(journal.IndexJson);
        journal.IndexHash = TextHash(journal.IndexJson);
        journal.FoldersJson = Serialize(state);
        CheckpointForTest?.Invoke("AfterCopy");
        SourceSnapshot after = ReadSource(sourceRoot, targetPaths, cancellationToken);
        if (source.Fingerprint != after.Fingerprint) throw new MigrationFault("SourceChanged");
        cancellationToken.ThrowIfCancellationRequested();
        SaveJournal(operation, journal);
        progress?.Report(new(LegacyMigrationStage.Copying, source.Records.Count, source.Records.Count, copied, total));
        return (operation, journal);
    }

    private void Publish(string operation, Journal journal, AppPaths paths, bool runCheckpoints = true)
    {
        ValidateJournal(operation, journal, paths);
        string actualIndex = OptionalHash(paths.IndexFile);
        if (actualIndex == journal.IndexHash)
        {
            VerifyPublishedCopies(journal, paths);
            journal.Phase = "Completed"; SaveJournal(operation, journal); return;
        }
        if (actualIndex != journal.BeforeIndexHash) throw new MigrationFault("TargetChanged");
        foreach (CopyEntry copy in journal.Copies)
        {
            string source = ValidatedFilePaths.FileWithinRoot(operation, copy.StageRelative);
            string destination = ValidatedFilePaths.FileWithinRoot(paths.CapturesRoot, copy.TargetRelative);
            if (File.Exists(destination))
            {
                if (Hash(destination) != copy.Hash) throw new MigrationFault("TargetChanged");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Failed/disk-full output remains a noncanonical owned partial, never a
                // half-written original/meta that a retry or index rebuild could activate.
                string partial = ValidatedFilePaths.FileWithinRoot(paths.CapturesRoot,
                    copy.TargetRelative + "." + Guid.NewGuid().ToString("N") + ".import-part");
                StreamCopy(source, partial, copy.Hash, CancellationToken.None, _ => { });
                File.Move(partial, destination, overwrite: false);
            }
        }
        if (runCheckpoints) CheckpointForTest?.Invoke("AfterPayloadPublication");
        // The index carries authoritative folder state. Write its recovery sidecar only
        // after the index so an interrupted precommit never exposes imported folders.
        AtomicFile.WriteAllText(paths.IndexFile, journal.IndexJson);
        if (runCheckpoints) CheckpointForTest?.Invoke("AfterIndexPublication");
        AtomicFile.WriteAllText(ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "library-folders.json", ".json"), journal.FoldersJson);
        journal.Phase = "Completed";
        if (runCheckpoints) CheckpointForTest?.Invoke("BeforeCompletedReceipt");
        SaveJournal(operation, journal);
        if (runCheckpoints) CheckpointForTest?.Invoke("AfterReceipt");
    }

    private static void ValidateJournal(string operation, Journal journal, AppPaths paths)
    {
        if (journal.Copies is null || journal.FolderMap is null || journal.RecordSources is null || journal.IndexJson is null || journal.FoldersJson is null
            || journal.IndexHash is null || journal.BeforeIndexHash is null || journal.SourceRoot is null
            || journal.Version != 1 || journal.TargetRoot != paths.DataRoot || journal.CapturesRoot != paths.CapturesRoot
            || journal.Phase is not ("Prepared" or "Publishing" or "Completed" or "Canceled")
            || TextHash(journal.IndexJson) != journal.IndexHash) throw new MigrationFault("InvalidJournal");
        CaptureIndexFile index = CaptureQueue.ParseMigrationIndex(journal.IndexJson);
        string prefix = "import-" + Path.GetFileName(operation) + Path.DirectorySeparatorChar;
        foreach (CopyEntry copy in journal.Copies)
        {
            if (copy is null || copy.StageRelative is null || copy.TargetRelative is null || copy.Hash is null
                || !copy.StageRelative.StartsWith("staging" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !copy.TargetRelative.StartsWith(prefix, StringComparison.Ordinal)
                || copy.Hash.Length != 64 || copy.Length < 0) throw new MigrationFault("InvalidJournal");
            _ = ValidatedFilePaths.FileWithinRoot(operation, copy.StageRelative);
            _ = ValidatedFilePaths.FileWithinRoot(paths.CapturesRoot, copy.TargetRelative);
            if (!index.Records.Any(record => Path.GetDirectoryName(copy.TargetRelative) == record.RelativeDirectory))
                throw new MigrationFault("InvalidJournal");
        }
        if (index.FolderState is null || Serialize(index.FolderState) != journal.FoldersJson)
            throw new MigrationFault("InvalidJournal");
    }

    private static void VerifyPublishedCopies(Journal journal, AppPaths paths)
    {
        foreach (CopyEntry copy in journal.Copies)
            if (OptionalHash(ValidatedFilePaths.FileWithinRoot(paths.CapturesRoot, copy.TargetRelative)) != copy.Hash)
                throw new MigrationFault("MissingPayload");
        AtomicFile.WriteAllText(ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "library-folders.json", ".json"), journal.FoldersJson);
    }

    private static SourceSnapshot ReadSource(string root, AppPaths targetPaths, CancellationToken cancellationToken, bool hashPayloads = true)
    {
        string sourceRoot = ValidatedFilePaths.DirectoryRoot(root);
        if (!Directory.Exists(sourceRoot)) throw new MigrationFault("SourceMissing");
        AppPaths target = ResolveTargetFromDisk(targetPaths);
        string settingsPath = ValidatedFilePaths.FileWithinRoot(sourceRoot, "settings.json", ".json");
        string? settings = File.Exists(settingsPath) ? File.ReadAllText(settingsPath, Encoding.UTF8) : null;
        AppSettings? preferences = settings is null ? null : JsonSerializer.Deserialize<AppSettings>(settings, Json);
        if (settings is not null && (preferences is null || preferences.Queue is null)) throw new MigrationFault("InvalidSource");
        string captureRoot = string.IsNullOrWhiteSpace(preferences?.Queue.CapturesDirectoryOverride)
            ? Path.Combine(sourceRoot, "captures") : preferences.Queue.CapturesDirectoryOverride;
        captureRoot = ValidatedFilePaths.DirectoryRoot(captureRoot);
        foreach (string source in new[] { sourceRoot, captureRoot })
            foreach (string destination in new[] { target.DataRoot, target.CapturesRoot })
                if (Overlaps(source, destination)) throw new MigrationFault("OverlappingRoots");
        string indexPath = ValidatedFilePaths.FileWithinRoot(sourceRoot, "index.json", ".json");
        string? indexJson = File.Exists(indexPath) ? File.ReadAllText(indexPath, Encoding.UTF8) : null;
        CaptureIndexFile index;
        if (indexJson is null)
        {
            if (Directory.Exists(captureRoot) && Directory.EnumerateFileSystemEntries(captureRoot).Any())
                throw new MigrationFault("MissingIndex");
            index = new();
        }
        else index = CaptureQueue.ParseMigrationIndex(indexJson);
        CaptureFolderState folders = index.FolderState ?? new();
        string folderPath = ValidatedFilePaths.FileWithinRoot(sourceRoot, "library-folders.json", ".json");
        string? folderJson = File.Exists(folderPath) ? File.ReadAllText(folderPath, Encoding.UTF8) : null;
        if (index.FolderState is null && folderJson is not null)
        {
            index.FolderState = JsonSerializer.Deserialize<CaptureFolderState>(folderJson, Json);
            index = CaptureQueue.ParseMigrationIndex(Serialize(index)); folders = index.FolderState!;
        }
        var snapshot = new SourceSnapshot { Root = sourceRoot, Settings = settings, Records = index.Records, Folders = folders.Folders };
        var assignments = folders.Assignments.ToDictionary(assignment => assignment.CaptureId);
        var folderIds = folders.Folders.Select(folder => folder.Id).ToHashSet();
        foreach (CaptureRecord record in snapshot.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assignments.TryGetValue(record.Id, out CaptureFolderAssignment? assignment))
            {
                record.FolderId = assignment.FolderId; record.FolderSequence = assignment.Sequence;
                record.FolderPinBeforeAssignment = assignment.WasPinned;
            }
            if (record.FolderId is { } folder && !folderIds.Contains(folder))
                throw new MigrationFault("InvalidSource");
            string directory = ValidatedFilePaths.DirectoryWithinRoot(captureRoot, record.RelativeDirectory);
            string primary = record.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Original;
            if (!File.Exists(ValidatedFilePaths.FileWithinRoot(directory, primary))) throw new MigrationFault("MissingPayload");
            var recordFiles = new List<SourceFile>();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(path);
                if (!IsPayloadName(name)) continue; // drafts, pending markers, cache and old temporary files are not activated.
                string validated = ValidatedFilePaths.FileWithinRoot(directory, name);
                var file = new SourceFile(record.Id, name, validated, new FileInfo(validated).Length,
                    hashPayloads ? Hash(validated, cancellationToken) : "");
                snapshot.Files.Add(file); recordFiles.Add(file);
            }
            string meta = ValidatedFilePaths.FileWithinRoot(directory, CaptureFileNames.Meta, ".json");
            if (File.Exists(meta))
            {
                CaptureRecord? metadata = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(meta, Encoding.UTF8), Json);
                if (metadata is null || metadata.Id != record.Id) throw new MigrationFault("InvalidSource");
                _ = CaptureQueue.ParseMigrationIndex(Serialize(new CaptureIndexFile { Records = [metadata] }));
            }
            string videoEdits = ValidatedFilePaths.FileWithinRoot(directory, CaptureFileNames.VideoEdits, ".json");
            if (File.Exists(videoEdits)) { using JsonDocument edits = JsonDocument.Parse(File.ReadAllText(videoEdits, Encoding.UTF8)); }
            if (record.HasAnnotations && !recordFiles.Any(file => file.Name == CaptureFileNames.Layers))
                throw new MigrationFault("MissingPayload");
            string layers = ValidatedFilePaths.FileWithinRoot(directory, CaptureFileNames.Layers, ".json");
            if (File.Exists(layers))
            {
                AnnotationDocument? document = AnnotationDocument.TryFromJson(File.ReadAllText(layers, Encoding.UTF8));
                if (document is null) throw new MigrationFault("InvalidSource");
                foreach (ImageAnnotation image in document.Items.OfType<ImageAnnotation>())
                    if (!CaptureFileNames.IsSafeAssetFileName(image.AssetFileName)
                        || !recordFiles.Any(file => file.Name == image.AssetFileName))
                        throw new MigrationFault("MissingPayload");
            }
        }
        snapshot.Fingerprint = TextHash((settings ?? "") + (indexJson ?? "") + (folderJson ?? "")
            + string.Join("\n", snapshot.Files.OrderBy(file => file.Path).Select(file => file.Path + ":" + file.Hash)));
        return snapshot;
    }

    private static bool IsPayloadName(string name) => name is CaptureFileNames.Original or CaptureFileNames.Rendered
        or CaptureFileNames.Thumbnail or CaptureFileNames.Meta or CaptureFileNames.Layers
        or CaptureFileNames.VideoSource or CaptureFileNames.VideoRendered or CaptureFileNames.VideoEdits
        || CaptureFileNames.IsSafeAssetFileName(name);

    private static bool WasImported(CaptureRecord target, string identity, Dictionary<Guid, string> receipts) =>
        receipts.TryGetValue(target.Id, out string? previous) && previous == identity;

    private static bool SamePayload(CaptureRecord target, CaptureRecord original, List<SourceFile> source, AppPaths paths, Guid desiredFolder)
    {
        if (target.FolderId != desiredFolder || SemanticRecord(target) != SemanticRecord(original)) return false;
        string directory = ValidatedFilePaths.DirectoryWithinRoot(paths.CapturesRoot, target.RelativeDirectory);
        string[] targetNames = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName)
            .Where(name => name is not null && name != CaptureFileNames.Meta && IsPayloadName(name)).Select(name => name!).Order().ToArray();
        SourceFile[] sourcePayload = source.Where(file => file.Name != CaptureFileNames.Meta).OrderBy(file => file.Name).ToArray();
        return targetNames.SequenceEqual(sourcePayload.Select(file => file.Name)) && sourcePayload.All(file =>
            OptionalHash(ValidatedFilePaths.FileWithinRoot(directory, file.Name)) == file.Hash);
    }

    private static string SemanticRecord(CaptureRecord record)
    {
        CaptureRecord normalized = JsonSerializer.Deserialize<CaptureRecord>(Serialize(record), Json)!;
        normalized.Id = Guid.Empty; normalized.RelativeDirectory = "";
        normalized.IsPinned = normalized.FolderPinBeforeAssignment ?? normalized.IsPinned;
        normalized.FolderId = null; normalized.FolderSequence = 0; normalized.FolderPinBeforeAssignment = null;
        return Serialize(normalized);
    }

    private static (Dictionary<Guid, Guid> Folders, Dictionary<Guid, string> Records) ReadCompletedMappings(string operations, string sourceRoot, AppPaths paths)
    {
        var result = new Dictionary<Guid, Guid>();
        var records = new Dictionary<Guid, string>();
        foreach (string candidate in Directory.EnumerateDirectories(operations))
        {
            string operation = ValidatedFilePaths.DirectoryWithinRoot(operations, candidate);
            if (!Guid.TryParseExact(Path.GetFileName(operation), "N", out _)) throw new MigrationFault("InvalidJournal");
            string receipt = ValidatedFilePaths.FileWithinRoot(operation, "journal.json", ".json");
            if (!File.Exists(receipt)) continue;
            Journal previous = ReadJournal(receipt);
            if (previous.Phase != "Completed") continue;
            if (OperatingSystem.IsWindows() && previous.SourceRoot != sourceRoot
                && string.Equals(previous.SourceRoot, sourceRoot, StringComparison.OrdinalIgnoreCase))
                throw new MigrationFault("SourceRootCaseMismatch");
            if (previous.SourceRoot != sourceRoot) continue;
            ValidateJournal(operation, previous, paths);
            foreach ((Guid original, Guid mapped) in previous.FolderMap) result[original] = mapped;
            foreach ((Guid id, string identity) in previous.RecordSources) records[id] = identity;
        }
        return (result, records);
    }

    private static Guid AllocateFolderId(string source, string value, Dictionary<Guid, Guid> previous,
        Guid original, HashSet<Guid> used)
    {
        if (previous.TryGetValue(original, out Guid committed)) { used.Add(committed); return committed; }
        for (int suffix = 0; ; suffix++)
        {
            Guid candidate = MappedId(source, "folder", value + (suffix == 0 ? "" : ":" + suffix));
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string UniqueFolderName(List<CaptureFolder> existing, CaptureFolder folder)
    {
        string original = CaptureFolderNaming.NormalizeFolderName(folder.Name);
        for (int suffix = 0; ; suffix++)
        {
            string tail = suffix == 0 ? "" : " (" + suffix + ")";
            string candidate = original[..Math.Min(original.Length, CaptureFolderNaming.MaximumNameLength - tail.Length)] + tail;
            if (!existing.Any(item => item.ParentId == folder.ParentId
                && (folder.ParentId is not null || item.CreatedDate == folder.CreatedDate)
                && string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private static AppPaths ResolveTarget(AppPaths paths, AppSettings settings)
    {
        if (settings.Queue is null) throw new MigrationFault("InvalidTarget");
        _ = ValidatedFilePaths.DirectoryRoot(paths.DataRoot);
        string root = string.IsNullOrWhiteSpace(settings.Queue.CapturesDirectoryOverride)
            ? paths.CapturesRoot : settings.Queue.CapturesDirectoryOverride;
        return paths.WithCapturesRoot(ValidatedFilePaths.DirectoryRoot(root));
    }

    private static AppPaths ResolveTargetFromDisk(AppPaths paths)
    {
        string settings = ValidatedFilePaths.SettingsFileWithinRoot(paths.DataRoot, paths.SettingsFile);
        string? recovered = AtomicFile.ReadAllTextWithRecovery(settings, text =>
            JsonSerializer.Deserialize<AppSettings>(text, Json)?.Queue is not null);
        if (recovered is null && (File.Exists(settings) || File.Exists(settings + AtomicFile.BackupSuffix)))
            throw new MigrationFault("InvalidTarget");
        return ResolveTarget(paths, recovered is null ? new() : JsonSerializer.Deserialize<AppSettings>(recovered, Json)!);
    }

    private static bool Overlaps(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string OperationsRoot(AppPaths paths) => ValidatedFilePaths.DirectoryWithinRoot(paths.DataRoot, OperationsDirectory);
    private static void BackupIfPresent(string source, string destination)
    {
        if (File.Exists(source)) StreamCopy(source, destination, Hash(source), CancellationToken.None, _ => { });
    }
    private static void StreamCopy(string source, string destination, string expectedHash, CancellationToken cancellationToken, Action<int> copied)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); copied(read);
        }
        output.Flush(flushToDisk: true);
        if (Convert.ToHexString(hash.GetHashAndReset()) != expectedHash) throw new MigrationFault("SourceChanged");
    }
    private static string Hash(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string OptionalHash(string path) => File.Exists(path) ? Hash(path) : "";
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static Guid MappedId(string source, string category, string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + category + "\n" + value)).AsSpan(0, 16));
    private static void SaveJournal(string operation, Journal journal) =>
        AtomicFile.WriteAllText(ValidatedFilePaths.FileWithinRoot(operation, "journal.json", ".json"), Serialize(journal));
    private static Journal ReadJournal(string path) => JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), Json)
        ?? throw new MigrationFault("InvalidJournal");
    private static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException
        or JsonException or NotSupportedException or InvalidOperationException or OverflowException;
    private static string Code(Exception error) => error is MigrationFault fault ? fault.Code : error is JsonException
        ? "InvalidSource" : "IoFailure";

    private sealed class MigrationFault(string code) : IOException(code) { internal string Code { get; } = code; }
    private sealed class SourceSnapshot
    {
        public string Root { get; init; } = "";
        public string? Settings { get; init; }
        public List<CaptureRecord> Records { get; init; } = [];
        public List<CaptureFolder> Folders { get; init; } = [];
        public List<SourceFile> Files { get; } = [];
        public string Fingerprint { get; set; } = "";
    }
    private sealed record SourceFile(Guid RecordId, string Name, string Path, long Length, string Hash);
    private sealed class Journal
    {
        public int Version { get; set; } = 1;
        public string Phase { get; set; } = "Prepared";
        public string SourceRoot { get; set; } = "";
        public Dictionary<Guid, Guid> FolderMap { get; set; } = [];
        public Dictionary<Guid, string> RecordSources { get; set; } = [];
        public string TargetRoot { get; set; } = "";
        public string CapturesRoot { get; set; } = "";
        public string BeforeIndexHash { get; set; } = "";
        public string IndexJson { get; set; } = "";
        public string IndexHash { get; set; } = "";
        public string FoldersJson { get; set; } = "";
        public bool HasLegacySettings { get; set; }
        public int ImportedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<CopyEntry> Copies { get; set; } = [];
    }
    private sealed class CopyEntry
    {
        public string StageRelative { get; set; } = "";
        public string TargetRelative { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Length { get; set; }
    }
}
