using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Storage;

/// <summary>
/// Read-only legacy snapshots, one streaming copy staged inside the current library, and a
/// journaled index-last publication. Anything the index write did not commit is rolled back.
/// </summary>
/// <remarks>
/// <para><b>Staging.</b> Payloads are copied once, directly into <c>import-&lt;operation&gt;</c>
/// below the current captures root, so publication writes only the index and no second copy
/// of the library ever exists. A record's <c>meta.json</c> is written after its payload is
/// complete; nothing in that folder is visible until the index names it. Canceled, failed and
/// interrupted operations delete that folder and their journal directly (app-owned files never
/// go to the Recycle Bin).</para>
/// <para><b>Identity.</b> A legacy capture id is imported at most once into a library. Receipts
/// map legacy ids to the ids they produced, so OCR, edits, moves or deletion in the Store library,
/// and title, folder or payload changes in the legacy library, are all reported as already
/// imported instead of creating another copy. Receipts keep only those ids, so they stay valid
/// after the storage location changes.</para>
/// <para><b>Damage.</b> A record whose main file, layers, asset or edits are missing or damaged
/// is skipped and counted; unreadable legacy settings are optional; a torn index falls back to
/// its backup and then to the per-capture <c>meta.json</c> sidecars. The legacy folder is never
/// changed.</para>
/// <para><b>Change detection.</b> The source is snapshotted by size and modification time of
/// every payload and of the root JSON files, before and after copying; each copy also checks
/// the byte count. Content is read exactly once.</para>
/// </remarks>
public sealed class LegacyLibraryMigrationService : ILegacyLibraryMigrationService
{
    private const string OperationsDirectory = "library-migration";
    private const string ImportDirectoryPrefix = "import-";
    private const string PartialSuffix = ".import-part";
    private const string JournalFile = "journal.json";
    private const string RolledBackMarker = "rolled-back";
    private const string LegacySettingsFile = "legacy-settings.json";
    private const int JournalVersion = 2;
    private const int BufferSize = 1024 * 1024;
    private const int StagingWorkers = 4;
    private const long SpaceReserve = 64L * 1024 * 1024;
    private const long MetaAllowance = 8 * 1024;
    private const int ProgressIntervalMilliseconds = 75;
    private static readonly JsonSerializerOptions Json = JsonDefaults.Readable;
    private static readonly JsonSerializerOptions JournalJson = JsonDefaults.Compact;

    internal Action<string>? CheckpointForTest { get; set; }

    /// <summary>Free bytes for a directory, or <see langword="null"/> when the volume cannot say.</summary>
    internal Func<string, long?>? FreeSpaceForTest { get; set; }

    /// <summary>Called with each partial copy after its first block is written.</summary>
    internal Action<string>? StagingWriteForTest { get; set; }

    public async Task<LegacyMigrationScan> ScanAsync(string sourceDataRoot, AppPaths targetPaths,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SourceSnapshot source = await Task.Run(() => ReadSource(sourceDataRoot, targetPaths, null, cancellationToken),
                cancellationToken);
            bool empty = source.Records.Count == 0 && source.Settings is null && source.Folders.Count == 0;
            return new(empty ? LegacyMigrationScanStatus.NoData : LegacyMigrationScanStatus.Ready,
                source.Records.Count(record => record.IsImage), source.Records.Count(record => record.IsVideo),
                source.Folders.Count, source.PayloadBytes, source.Settings is not null,
                UnusableCount: source.UnusableCount, SettingsUnreadable: source.SettingsUnreadable);
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
        var operation = new OperationState();
        bool admitted = false;
        try
        {
            await quiescedTargetQueue.DrainMigrationWritesAsync();
            CaptureIndexFile snapshot = quiescedTargetQueue.SnapshotForMigration();
            string snapshotJson = Serialize(snapshot);
            AppPaths target = ResolveTarget(targetPaths, currentSettings);
            operation.Target = target;
            progress?.Report(new(LegacyMigrationStage.Scanning, 0, 0, 0, 0));
            await Task.Run(() =>
            {
                // Finish earlier committed receipts and remove stale staging before planning.
                _ = Settle(target, snapshot.Records, SettleMode.InProcess, cancellationToken);
                string beforeIndexHash = OptionalHash(target.IndexFile);
                Prepare(sourceDataRoot, target, snapshot, beforeIndexHash, operation, progress, cancellationToken);
            }, cancellationToken);
            Journal journal = operation.Journal!;
            string directory = operation.Directory!;
            cancellationToken.ThrowIfCancellationRequested();
            if (!operation.ChangesLibrary)
            {
                // Everything was imported before: keep the receipt, never rewrite or lock the library.
                await Task.Run(() => FinishCommitted(directory, journal, target, writeSidecar: false, runCheckpoints: false));
                return Completed(operation);
            }
            if (Serialize(quiescedTargetQueue.SnapshotForMigration()) != snapshotJson)
                throw new MigrationFault("TargetChanged");
            CheckpointForTest?.Invoke("BeforePublication");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(LegacyMigrationStage.Publishing, journal.ImportedCount, journal.ImportedCount,
                operation.PayloadBytes, operation.PayloadBytes));
            // From here the import is one index write. It is not canceled halfway; a failure
            // before that write rolls back, and a failure after it keeps the committed library.
            Task publication = quiescedTargetQueue.SerializeMigrationPublication(snapshotJson, () =>
            {
                try { Publish(directory, journal, target, operation, runCheckpoints: true); }
                catch (Exception error) when (!operation.Committed && error is IOException and not MigrationFault)
                {
                    // One immediate retry absorbs a transient scanner or indexer lock.
                    Publish(directory, journal, target, operation, runCheckpoints: false);
                }
            });
            admitted = true;
            await publication;
            quiescedTargetQueue.AdoptMigrationSnapshot(journal.IndexJson!);
            return Completed(operation);
        }
        catch (OperationCanceledException) when (!operation.Committed)
        {
            await Task.Run(() => RollBack(operation));
            if (admitted) quiescedTargetQueue.CancelMigrationAdmission();
            return new(LegacyMigrationStatus.Canceled);
        }
        catch (Exception error) when (IsExpected(error))
        {
            if (operation.Committed)
            {
                // The index already names the import, so the library continues with it. A receipt
                // that cannot be written now is finished by the next import or launch.
                quiescedTargetQueue.AdoptMigrationSnapshot(operation.Journal!.IndexJson!);
                try
                {
                    await Task.Run(() => FinishCommitted(operation.Directory!, operation.Journal!, operation.Target!,
                        writeSidecar: true, runCheckpoints: false));
                }
                catch (Exception finishError) when (IsExpected(finishError)) { }
                return Completed(operation);
            }
            await Task.Run(() => RollBack(operation));
            if (admitted) quiescedTargetQueue.CancelMigrationAdmission();
            return new(LegacyMigrationStatus.Failed, ErrorCode: Code(error),
                RequiredBytes: (error as MigrationFault)?.RequiredBytes ?? 0,
                AvailableBytes: (error as MigrationFault)?.AvailableBytes ?? 0);
        }
    }

    public Task<LegacyRecoveryResult> RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
        Task.Run(() => Settle(targetPaths, null, SettleMode.Recover, cancellationToken), cancellationToken);

    public Task<LegacyRecoveryResult> AbandonPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
        Task.Run(() => Settle(targetPaths, null, SettleMode.Abandon, cancellationToken), cancellationToken);

    /// <summary>True while a publication journal awaits recovery; a blocked queue must not reload until then.</summary>
    internal static bool HasPendingPublication(AppPaths paths)
    {
        string operations = OperationsRoot(paths);
        if (!Directory.Exists(operations)) return false;
        foreach (string candidate in Directory.EnumerateDirectories(operations))
        {
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _)) continue;
            string operation = ValidatedFilePaths.DirectoryWithinRoot(operations, candidate);
            if (TryReadJournal(operation, out _) is { Phase: Phases.Publishing }
                && !File.Exists(Path.Combine(operation, RolledBackMarker))) return true;
        }
        return false;
    }

    private static LegacyMigrationResult Completed(OperationState operation)
    {
        Journal journal = operation.Journal!;
        return new(LegacyMigrationStatus.Completed, journal.ImportedCount, journal.SkippedCount,
            journal.HasLegacySettings ? Path.Combine(operation.Directory!, LegacySettingsFile) : null,
            UnusableCount: journal.UnusableCount);
    }

    // ---- Preparation -------------------------------------------------------------------

    private void Prepare(string sourceRoot, AppPaths target, CaptureIndexFile snapshot, string beforeIndexHash,
        OperationState operation, IProgress<LegacyMigrationProgress>? progress, CancellationToken cancellationToken)
    {
        SourceSnapshot source = ReadSource(sourceRoot, target, progress, cancellationToken);
        string fingerprint = Fingerprint(source);
        CheckpointForTest?.Invoke("AfterSourceSnapshot");
        string operations = ValidatedFilePaths.CreateDirectory(OperationsRoot(target));
        Receipts previous = ReadReceipts(operations, snapshot.Records);
        string name = Guid.NewGuid().ToString("N");
        Plan plan = BuildPlan(source, snapshot, previous, ImportDirectoryPrefix + name);
        string indexJson = Serialize(snapshot);
        _ = CaptureQueue.ParseMigrationIndex(indexJson);
        string foldersJson = Serialize(snapshot.FolderState!);
        EnsureFreeSpace(target, plan, Encoding.UTF8.GetByteCount(indexJson));
        cancellationToken.ThrowIfCancellationRequested();

        string directory = ValidatedFilePaths.CreateDirectory(ValidatedFilePaths.DirectoryWithinRoot(operations, name));
        var journal = new Journal
        {
            SourceRoot = source.Root, TargetRoot = target.DataRoot, CapturesRoot = target.CapturesRoot,
            BeforeIndexHash = beforeIndexHash, SkippedCount = plan.Skipped, UnusableCount = source.UnusableCount,
        };
        operation.Directory = directory;
        operation.Journal = journal;
        // The owner record exists before anything is written into the library folder.
        SaveJournal(directory, journal);
        BackupIfPresent(target.SettingsFile, ValidatedFilePaths.FileWithinRoot(directory, "store-settings-before.json", ".json"));
        if (source.Settings is { } settings)
        {
            File.WriteAllText(ValidatedFilePaths.FileWithinRoot(directory, LegacySettingsFile, ".json"), settings,
                new UTF8Encoding(false));
            journal.HasLegacySettings = true;
        }

        Stage(target, ImportDirectoryPrefix + name, plan, operation, progress, cancellationToken);
        CheckpointForTest?.Invoke("AfterCopy");
        if (Fingerprint(source) != fingerprint) throw new MigrationFault("SourceChanged");
        cancellationToken.ThrowIfCancellationRequested();
        journal.IndexJson = indexJson;
        journal.IndexHash = TextHash(indexJson);
        journal.FoldersJson = foldersJson;
        journal.FolderMap = plan.FolderMap;
        journal.Records = plan.Records.Select(item => new ImportedRecord
        {
            LegacyId = item.Source.Id, TargetId = item.Imported.Id,
            Files = item.Files.Select(file => new StagedFile { Name = file.Name, Length = file.Length }).ToList(),
        }).ToList();
        journal.ImportedCount = plan.Records.Count;
        operation.ChangesLibrary = plan.ChangesLibrary;
    }

    private void Stage(AppPaths target, string importDirectory, Plan plan, OperationState operation,
        IProgress<LegacyMigrationProgress>? progress, CancellationToken cancellationToken)
    {
        operation.PayloadBytes = plan.PayloadBytes;
        if (plan.Records.Count == 0) return;
        string importRoot = ValidatedFilePaths.CreateDirectory(
            ValidatedFilePaths.DirectoryWithinRoot(target.CapturesRoot, importDirectory));
        long copied = 0;
        int completed = 0;
        var timer = Stopwatch.StartNew();
        var gate = new object();
        void Report(bool force)
        {
            // Serialized so the reported byte count never goes backwards across workers.
            lock (gate)
            {
                if (!force && timer.ElapsedMilliseconds < ProgressIntervalMilliseconds) return;
                timer.Restart();
                progress?.Report(new(LegacyMigrationStage.Copying, Volatile.Read(ref completed), plan.Records.Count,
                    Interlocked.Read(ref copied), plan.PayloadBytes));
            }
        }
        // Each record ends with durable writes, so a few records in flight hide that latency the
        // way a multithreaded copy does; one large file still streams sequentially.
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, StagingWorkers),
        };
        try
        {
            Parallel.ForEach(plan.Records, options, item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The import folder is new and validated; each record folder and payload name below it
                // comes from this plan (a GUID and a fixed payload name), never from the source paths.
                string recordDirectory = ValidatedFilePaths.DirectoryWithinValidatedRoot(importRoot, item.Imported.Id.ToString("N"));
                Directory.CreateDirectory(recordDirectory);
                foreach (SourceFile file in item.Files)
                {
                    CopyPayload(file, Path.Combine(recordDirectory, file.Name), cancellationToken, bytes =>
                    {
                        Interlocked.Add(ref copied, bytes);
                        Report(force: false);
                    });
                }
                // The sidecar follows a complete payload, so an index rebuild never sees half a record.
                // It is new in an uncommitted folder, so one durable write replaces a temp-and-swap.
                using (var meta = new FileStream(Path.Combine(recordDirectory, CaptureFileNames.Meta), FileMode.CreateNew,
                           FileAccess.Write, FileShare.None, 1))
                {
                    meta.Write(Encoding.UTF8.GetBytes(Serialize(item.Imported)));
                    meta.Flush(flushToDisk: true);
                }
                Interlocked.Increment(ref completed);
            });
        }
        catch (AggregateException error)
        {
            // Every worker has stopped. A real failure (a full disk, a crash) outranks the
            // cancellations it caused in the other workers.
            Exception first = error.Flatten().InnerExceptions.FirstOrDefault(inner => inner is not OperationCanceledException)
                ?? error.Flatten().InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
        Report(force: true);
    }

    private void CopyPayload(SourceFile file, string destination, CancellationToken cancellationToken, Action<int> copied)
    {
        string partial = destination + "." + Guid.NewGuid().ToString("N") + PartialSuffix;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                if (input.Length != file.Length) throw new MigrationFault("SourceChanged");
                // Reserving the full length reports a full disk before any bytes are written.
                if (file.Length > 0) output.SetLength(file.Length);
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, BufferSize)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    if (total == 0) StagingWriteForTest?.Invoke(partial);
                    total += read;
                    copied(read);
                }
                if (total != file.Length) throw new MigrationFault("SourceChanged");
                output.Flush(flushToDisk: true);
            }
            File.Move(partial, destination, overwrite: false);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private void EnsureFreeSpace(AppPaths target, Plan plan, long indexBytes)
    {
        // Payloads and their sidecars go to the captures volume once. The data volume holds the
        // journal copy of the index, the index temporary file and its backup.
        var needs = new List<(string Directory, long Bytes)>
        {
            (target.CapturesRoot, plan.PayloadBytes + plan.Records.Count * MetaAllowance),
            (target.DataRoot, 4 * indexBytes + 1024 * 1024),
        };
        foreach (var volume in needs.GroupBy(need => VolumeOf(need.Directory), StringComparer.OrdinalIgnoreCase))
        {
            long required = volume.Sum(need => need.Bytes) + SpaceReserve;
            string directory = volume.First().Directory;
            long? available = FreeSpaceForTest is { } probe ? probe(directory) : QueryFreeSpace(directory);
            if (available is { } free && free < required)
                throw new MigrationFault("InsufficientSpace") { RequiredBytes = required, AvailableBytes = free };
        }
    }

    private static string VolumeOf(string directory) => Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory;

    private static long? QueryFreeSpace(string directory)
    {
        try
        {
            string root = VolumeOf(directory);
            // DriveInfo describes lettered volumes; a share is checked by the copy itself.
            return root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    // ---- Planning ----------------------------------------------------------------------

    private static Plan BuildPlan(SourceSnapshot source, CaptureIndexFile target, Receipts previous, string importDirectory)
    {
        CaptureFolderState state = target.FolderState ??= new();
        var plan = new Plan();
        var recordsById = target.Records.ToDictionary(record => record.Id);
        var planned = new List<(CaptureRecord Source, Guid Id)>();
        foreach (CaptureRecord record in source.Records)
        {
            if (previous.Records.ContainsKey(record.Id)) { plan.Skipped++; continue; }
            Guid id = record.Id;
            if (recordsById.TryGetValue(id, out CaptureRecord? existing))
            {
                // A copy this library already owns, even without its receipt, is never copied again.
                if (IsImportOwned(existing)) { plan.Skipped++; continue; }
                id = MappedId("capture", record.Id.ToString("N"));
                if (recordsById.ContainsKey(id)) { plan.Skipped++; continue; }
            }
            recordsById.Add(id, record);
            planned.Add((record, id));
        }

        var foldersById = state.Folders.ToDictionary(folder => folder.Id);
        var usedFolderIds = foldersById.Keys.ToHashSet();
        var sourceFolders = source.Folders.ToDictionary(folder => folder.Id);
        Guid Allocate(string value)
        {
            for (int suffix = 0; ; suffix++)
            {
                Guid candidate = MappedId("folder", value + (suffix == 0 ? "" : ":" + suffix));
                if (usedFolderIds.Add(candidate)) return candidate;
            }
        }
        foreach (CaptureFolder folder in source.Folders)
            plan.FolderMap[folder.Id] = previous.Folders.TryGetValue(folder.Id, out Guid known) ? known : Allocate(folder.Id.ToString("N"));
        Guid historyId = previous.Folders.TryGetValue(Guid.Empty, out Guid history) ? history : Allocate("unassigned");

        // Create new legacy folders, and folders a newly imported capture needs. A folder that an
        // earlier import created and the user later deleted is not recreated for nothing.
        var create = new HashSet<Guid>();
        foreach (CaptureFolder folder in source.Folders)
            if (!foldersById.ContainsKey(plan.FolderMap[folder.Id]) && !previous.Folders.ContainsKey(folder.Id)) create.Add(folder.Id);
        foreach (var (record, _) in planned)
            if (record.FolderId is { } legacyFolder) create.Add(legacyFolder);
        foreach (Guid legacy in create.ToArray())
            for (Guid? parent = sourceFolders[legacy].ParentId; parent is { } id; parent = sourceFolders[id].ParentId)
                create.Add(id);
        var names = new FolderNames(state.Folders);
        foreach (CaptureFolder folder in source.Folders.Where(folder => create.Contains(folder.Id))
                     .OrderBy(folder => Depth(folder, sourceFolders)))
        {
            Guid id = plan.FolderMap[folder.Id];
            if (foldersById.ContainsKey(id)) continue;
            var added = new CaptureFolder
            {
                Id = id, ParentId = folder.ParentId is { } parent ? plan.FolderMap[parent] : null,
                Name = folder.Name, CreatedAt = folder.CreatedAt, NextSequence = folder.NextSequence,
            };
            names.Add(added);
            state.Folders.Add(added);
            foldersById.Add(id, added);
            plan.CreatedFolders++;
        }

        var sequencesByFolder = state.Assignments.GroupBy(assignment => assignment.FolderId)
            .ToDictionary(group => group.Key, group => group.Select(assignment => assignment.Sequence).ToHashSet());
        foreach (var (record, id) in planned)
        {
            CaptureRecord imported = JsonSerializer.Deserialize<CaptureRecord>(Serialize(record), Json)!;
            imported.Id = id;
            imported.RelativeDirectory = Path.Combine(importDirectory, id.ToString("N"));
            imported.FolderPinBeforeAssignment ??= imported.IsPinned;
            imported.IsPinned = true;
            imported.FolderId = record.FolderId is { } sourceFolder ? plan.FolderMap[sourceFolder] : historyId;
            if (imported.FolderId == historyId && !foldersById.ContainsKey(historyId))
            {
                var created = new CaptureFolder { Id = historyId, Name = UiText.Get("LegacyImport.HistoryFolder"), CreatedAt = record.CreatedAt };
                names.Add(created);
                state.Folders.Add(created);
                foldersById.Add(historyId, created);
            }
            CaptureFolder destination = foldersById[imported.FolderId.Value];
            if (!sequencesByFolder.TryGetValue(destination.Id, out HashSet<long>? sequences))
                sequencesByFolder.Add(destination.Id, sequences = []);
            if (record.FolderId is not null && record.FolderSequence > 0 && !sequences.Contains(record.FolderSequence))
            {
                imported.FolderSequence = record.FolderSequence;
                destination.NextSequence = Math.Max(destination.NextSequence, record.FolderSequence + 1);
            }
            else imported.FolderSequence = destination.NextSequence++;
            sequences.Add(imported.FolderSequence);
            target.Records.Add(imported);
            state.Assignments.Add(new()
            {
                CaptureId = id, FolderId = imported.FolderId.Value, Sequence = imported.FolderSequence,
                WasPinned = imported.FolderPinBeforeAssignment ?? false,
            });
            List<SourceFile> files = source.Files[record.Id];
            plan.Records.Add(new(record, imported, files));
            plan.PayloadBytes += files.Sum(file => file.Length);
        }
        if (foldersById.ContainsKey(historyId)) plan.FolderMap[Guid.Empty] = historyId;
        target.Records = target.Records.OrderByDescending(record => record.CreatedAt).ToList();
        return plan;
    }

    private static int Depth(CaptureFolder folder, Dictionary<Guid, CaptureFolder> folders)
    {
        int depth = 0;
        for (Guid? parent = folder.ParentId; parent is { } id; parent = folders[id].ParentId) depth++;
        return depth;
    }

    private static bool IsImportOwned(CaptureRecord record) =>
        record.RelativeDirectory.StartsWith(ImportDirectoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sibling names stay unique in O(1) per folder, like the queue's own validation key.</summary>
    private sealed class FolderNames
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        internal FolderNames(IEnumerable<CaptureFolder> existing) { foreach (CaptureFolder folder in existing) _keys.Add(Key(folder, folder.Name)); }
        private static string Key(CaptureFolder folder, string name) =>
            (folder.ParentId?.ToString("N") ?? folder.CreatedDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)) + "/" + name;
        internal void Add(CaptureFolder folder)
        {
            string original = CaptureFolderNaming.NormalizeFolderName(folder.Name);
            for (int suffix = 0; ; suffix++)
            {
                string tail = suffix == 0 ? "" : " (" + suffix + ")";
                string candidate = original[..Math.Min(original.Length, CaptureFolderNaming.MaximumNameLength - tail.Length)] + tail;
                if (!_keys.Add(Key(folder, candidate))) continue;
                folder.Name = candidate;
                return;
            }
        }
    }

    private static Receipts ReadReceipts(string operations, IReadOnlyCollection<CaptureRecord> liveRecords)
    {
        var receipts = new Receipts();
        HashSet<string> referenced = ReferencedImports(liveRecords);
        foreach (string candidate in Directory.EnumerateDirectories(operations))
        {
            string name = Path.GetFileName(candidate);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            Journal? journal = TryReadJournal(candidate, out _);
            if (journal is null) continue;
            // A committed journal whose receipt is still pending counts like its receipt.
            bool committed = journal.Phase == Phases.Completed
                || (journal.Phase == Phases.Publishing && referenced.Contains(ImportDirectoryPrefix + name));
            if (!committed) continue;
            foreach (ImportedRecord record in journal.Records) receipts.Records[record.LegacyId] = record.TargetId;
            foreach ((Guid legacy, Guid mapped) in journal.FolderMap) receipts.Folders[legacy] = mapped;
        }
        return receipts;
    }

    // ---- Publication and settlement ----------------------------------------------------

    private void Publish(string directory, Journal journal, AppPaths target, OperationState operation, bool runCheckpoints)
    {
        if (OptionalHash(target.IndexFile) != journal.BeforeIndexHash) throw new MigrationFault("TargetChanged");
        journal.Phase = Phases.Publishing;
        SaveJournal(directory, journal);
        if (runCheckpoints) CheckpointForTest?.Invoke("AfterPayloadPublication");
        // The commit point. AtomicFile either replaces the index or leaves the previous one.
        AtomicFile.WriteAllText(target.IndexFile, journal.IndexJson!);
        operation.Committed = true;
        if (runCheckpoints) CheckpointForTest?.Invoke("AfterIndexPublication");
        FinishCommitted(directory, journal, target, writeSidecar: true, runCheckpoints);
    }

    private void FinishCommitted(string directory, Journal journal, AppPaths paths, bool writeSidecar, bool runCheckpoints)
    {
        // The index carries authoritative folder state; its recovery sidecar follows the index.
        if (writeSidecar && journal.FoldersJson is { Length: > 0 } folders)
            AtomicFile.WriteAllText(ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "library-folders.json", ".json"), folders);
        if (runCheckpoints) CheckpointForTest?.Invoke("BeforeCompletedReceipt");
        SaveJournal(directory, journal.ToReceipt());
        journal.Phase = Phases.Completed;
        if (runCheckpoints) CheckpointForTest?.Invoke("AfterReceipt");
        // The previous journal held a full index copy; the compact receipt replaces it.
        TryDeleteFile(Path.Combine(directory, JournalFile + AtomicFile.BackupSuffix));
        TryDeleteFile(Path.Combine(directory, RolledBackMarker));
    }

    private static void RollBack(OperationState operation)
    {
        if (operation.Directory is not { } directory) return;
        try
        {
            TryCreateMarker(directory);
            if (operation.Target is { } target) DeleteImportDirectory(target.CapturesRoot, Path.GetFileName(directory));
            DeleteTree(directory);
        }
        catch (Exception error) when (IsExpected(error))
        {
            // The marker keeps the operation owned; the next import or launch deletes it.
        }
    }

    private LegacyRecoveryResult Settle(AppPaths paths, IReadOnlyCollection<CaptureRecord>? liveRecords, SettleMode mode,
        CancellationToken cancellationToken)
    {
        int completed = 0, rolledBack = 0;
        string? decision = null;
        string operations;
        try
        {
            operations = OperationsRoot(paths);
            if (!Directory.Exists(operations)) return new(LegacyRecoveryStatus.Clean);
        }
        catch (Exception error) when (IsExpected(error))
        {
            return new(mode == SettleMode.Recover ? LegacyRecoveryStatus.DecisionRequired : LegacyRecoveryStatus.Clean, Code(error));
        }
        IndexView index = liveRecords is not null ? IndexView.Live(liveRecords, paths.IndexFile) : IndexView.FromDisk(paths.IndexFile);
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(operations).ToArray(); }
        catch (Exception error) when (IsExpected(error))
        {
            return new(mode == SettleMode.Recover ? LegacyRecoveryStatus.DecisionRequired : LegacyRecoveryStatus.Clean, Code(error));
        }
        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(candidate);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            // Cleanup and receipt failures are retried later and never block a launch: the index
            // either already names the import or never will. Only an unfinished publication asks.
            try
            {
                string directory = ValidatedFilePaths.DirectoryWithinRoot(operations, candidate);
                Journal? journal = TryReadJournal(directory, out bool missing);
                if (journal is null)
                {
                    // A directory created just before its first journal write owns nothing else. An
                    // unreadable journal cannot be acted on safely and is left as it is.
                    if (missing) DeleteTree(directory);
                    continue;
                }
                switch (Classify(name, journal, index))
                {
                    case Commit.Committed:
                        if (journal.IndexJson is not null || File.Exists(Path.Combine(directory, JournalFile + AtomicFile.BackupSuffix)))
                            FinishCommitted(directory, journal, paths, writeSidecar: index.Hash == journal.IndexHash, runCheckpoints: false);
                        completed++;
                        break;
                    case Commit.Uncommitted when mode == SettleMode.Recover && journal.Phase == Phases.Publishing
                        && index.Hash == journal.BeforeIndexHash && !File.Exists(Path.Combine(directory, RolledBackMarker)):
                        try
                        {
                            RollForward(directory, name, journal, paths);
                            completed++;
                        }
                        catch (Exception error) when (IsExpected(error)) { decision ??= Code(error); }
                        break;
                    case Commit.Uncommitted:
                        TryCreateMarker(directory);
                        if (journal.CapturesRoot.Length > 0) DeleteImportDirectory(journal.CapturesRoot, name);
                        DeleteTree(directory);
                        rolledBack++;
                        break;
                    default:
                        // The index cannot be read, so neither direction is provably safe.
                        if (mode == SettleMode.Abandon) TryCreateMarker(directory);
                        else if (mode == SettleMode.Recover && !File.Exists(Path.Combine(directory, RolledBackMarker)))
                            decision ??= "TargetUnreadable";
                        break;
                }
            }
            catch (Exception error) when (IsExpected(error)) { }
        }
        return new(decision is null ? LegacyRecoveryStatus.Clean : LegacyRecoveryStatus.DecisionRequired, decision, completed, rolledBack);
    }

    private static Commit Classify(string name, Journal journal, IndexView index)
    {
        if (journal.Phase == Phases.Completed) return Commit.Committed;
        if (journal.Phase == Phases.Publishing && index.Hash is not null && index.Hash == journal.IndexHash) return Commit.Committed;
        if (index.Imports?.Contains(ImportDirectoryPrefix + name) == true) return Commit.Committed;
        // Staging never reaches the index: it is safe to roll back even when the index is unreadable.
        if (journal.Phase == Phases.Staging) return Commit.Uncommitted;
        if (index.Imports is null) return Commit.Unknown;
        return Commit.Uncommitted;
    }

    private void RollForward(string directory, string name, Journal journal, AppPaths paths)
    {
        AppPaths target = ResolveTargetFromDisk(paths);
        if (!string.Equals(journal.CapturesRoot, target.CapturesRoot, StringComparison.Ordinal)) throw new MigrationFault("TargetChanged");
        ValidateJournal(name, journal);
        string import = ValidatedFilePaths.DirectoryWithinRoot(target.CapturesRoot, ImportDirectoryPrefix + name);
        foreach (ImportedRecord record in journal.Records)
        {
            // ValidateJournal admitted only GUID folders and fixed payload names below this folder.
            string recordDirectory = ValidatedFilePaths.DirectoryWithinValidatedRoot(import, record.TargetId.ToString("N"));
            foreach (StagedFile file in record.Files!)
                if (new FileInfo(Path.Combine(recordDirectory, file.Name)) is not { Exists: true } info
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != file.Length)
                    throw new MigrationFault("MissingPayload");
            if (!File.Exists(Path.Combine(recordDirectory, CaptureFileNames.Meta))) throw new MigrationFault("MissingPayload");
        }
        var operation = new OperationState { Directory = directory, Journal = journal, Target = target };
        Publish(directory, journal, target, operation, runCheckpoints: false);
    }

    /// <summary>Linear validation: every record names a target inside this operation's own folder.</summary>
    private static void ValidateJournal(string name, Journal journal)
    {
        if (journal.IndexJson is null || journal.IndexHash is null || journal.FoldersJson is null || journal.Records is null
            || journal.FolderMap is null || TextHash(journal.IndexJson) != journal.IndexHash) throw new MigrationFault("InvalidJournal");
        CaptureIndexFile index;
        try { index = CaptureQueue.ParseMigrationIndex(journal.IndexJson); }
        catch (IOException) { throw new MigrationFault("InvalidJournal"); }
        if (index.FolderState is null || Serialize(index.FolderState) != journal.FoldersJson) throw new MigrationFault("InvalidJournal");
        var records = index.Records.ToDictionary(record => record.Id);
        string import = ImportDirectoryPrefix + name;
        foreach (ImportedRecord? record in journal.Records)
        {
            if (record?.Files is null || !records.TryGetValue(record.TargetId, out CaptureRecord? indexed)
                || indexed.RelativeDirectory != Path.Combine(import, record.TargetId.ToString("N")))
                throw new MigrationFault("InvalidJournal");
            foreach (StagedFile? file in record.Files)
                if (file?.Name is null || file.Name == CaptureFileNames.Meta || !IsPayloadName(file.Name) || file.Length < 0)
                    throw new MigrationFault("InvalidJournal");
        }
    }

    private static void DeleteImportDirectory(string capturesRoot, string operationName)
    {
        string root = ValidatedFilePaths.DirectoryRoot(capturesRoot);
        DeleteTree(ValidatedFilePaths.DirectoryWithinRoot(root, ImportDirectoryPrefix + operationName));
    }

    /// <summary>Deletes app-owned files directly; a linked entry is removed without following it.</summary>
    private static void DeleteTree(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) return;
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to delete a linked directory.");
        foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child && (child.Attributes & FileAttributes.ReparsePoint) == 0) DeleteTree(child.FullName);
            else entry.Delete();
        }
        info.Delete();
    }

    private static void TryCreateMarker(string directory)
    {
        try { using var _ = new FileStream(Path.Combine(directory, RolledBackMarker), FileMode.OpenOrCreate, FileAccess.Write); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ---- Source snapshot ---------------------------------------------------------------

    private static SourceSnapshot ReadSource(string root, AppPaths targetPaths, IProgress<LegacyMigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string sourceRoot = ValidatedFilePaths.DirectoryRoot(root);
        if (!Directory.Exists(sourceRoot)) throw new MigrationFault("SourceMissing");
        AppPaths target = ResolveTargetFromDisk(targetPaths);
        string settingsPath = ValidatedFilePaths.FileWithinRoot(sourceRoot, "settings.json", ".json");
        // Settings are optional. A damaged file only means its preferences are not offered.
        string? settings = AtomicFile.ReadAllTextWithRecovery(settingsPath, text => ReadSettings(text) is not null);
        string? captureOverride = settings is null ? null : ReadSettings(settings)!.Queue.CapturesDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(captureOverride) && !Path.IsPathFullyQualified(captureOverride))
            throw new MigrationFault("InvalidSource");
        string captureRoot = ValidatedFilePaths.DirectoryRoot(string.IsNullOrWhiteSpace(captureOverride)
            ? Path.Combine(sourceRoot, "captures") : captureOverride);
        foreach (string source in new[] { sourceRoot, captureRoot })
            foreach (string destination in new[] { target.DataRoot, target.CapturesRoot })
                if (Overlaps(source, destination)) throw new MigrationFault("OverlappingRoots");

        string indexPath = ValidatedFilePaths.FileWithinRoot(sourceRoot, "index.json", ".json");
        string? indexJson = AtomicFile.ReadAllTextWithRecovery(indexPath, text => CaptureQueue.TryParseMigrationSource(text, out _));
        CaptureIndexFile index;
        if (indexJson is not null && CaptureQueue.TryParseMigrationSource(indexJson, out CaptureIndexFile? parsed)) index = parsed!;
        else if (Directory.Exists(captureRoot) && Directory.EnumerateFileSystemEntries(captureRoot).Any())
        {
            // The legacy app itself rebuilds from these sidecars when both index copies are torn.
            index = new() { Records = RebuildFromSidecars(captureRoot, cancellationToken) };
            if (index.Records.Count == 0) throw new MigrationFault("MissingIndex");
        }
        else index = new();
        CaptureFolderState? folders = index.FolderState;
        if (folders is null)
        {
            string folderPath = ValidatedFilePaths.FileWithinRoot(sourceRoot, "library-folders.json", ".json");
            string? folderJson = AtomicFile.ReadAllTextWithRecovery(folderPath, text => CaptureQueue.TryParseMigrationFolderState(text, out _));
            if (folderJson is not null) CaptureQueue.TryParseMigrationFolderState(folderJson, out folders);
        }
        folders ??= new();

        var snapshot = new SourceSnapshot
        {
            Root = sourceRoot, Settings = settings, Folders = folders.Folders,
            SettingsUnreadable = settings is null && (File.Exists(settingsPath) || File.Exists(settingsPath + AtomicFile.BackupSuffix)),
        };
        var assignments = folders.Assignments.ToDictionary(assignment => assignment.CaptureId);
        var folderIds = folders.Folders.Select(folder => folder.Id).ToHashSet();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        for (int position = 0; position < index.Records.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureRecord record = index.Records[position];
            if (assignments.TryGetValue(record.Id, out CaptureFolderAssignment? assignment))
            {
                record.FolderId = assignment.FolderId; record.FolderSequence = assignment.Sequence;
                record.FolderPinBeforeAssignment = assignment.WasPinned;
            }
            // A dangling folder reference keeps the capture; it goes to the unassigned history folder.
            if (record.FolderId is { } folder && !folderIds.Contains(folder)) { record.FolderId = null; record.FolderSequence = 0; }
            string directory;
            try { directory = ValidatedFilePaths.DirectoryWithinValidatedRoot(captureRoot, record.RelativeDirectory); }
            catch (IOException) { throw new MigrationFault("InvalidSource"); } // links and escapes are never followed
            // Two records naming one folder would import the same files twice; only the first is used.
            if (directories.Add(directory) && ReadRecordFiles(record, directory) is { } files)
            {
                snapshot.Records.Add(record);
                snapshot.Files.Add(record.Id, files);
                snapshot.Directories.Add(directory);
                snapshot.PayloadBytes += files.Sum(file => file.Length);
            }
            else snapshot.UnusableCount++;
            if (timer.ElapsedMilliseconds >= ProgressIntervalMilliseconds)
            {
                progress?.Report(new(LegacyMigrationStage.Scanning, position + 1, index.Records.Count, 0, 0));
                timer.Restart();
            }
        }
        progress?.Report(new(LegacyMigrationStage.Scanning, index.Records.Count, index.Records.Count, 0, 0));
        return snapshot;
    }

    /// <summary>The payload files of a usable record, or <see langword="null"/> to skip and report it.</summary>
    private static List<SourceFile>? ReadRecordFiles(CaptureRecord record, string directory)
    {
        try
        {
            var folder = new DirectoryInfo(directory);
            if (!folder.Exists) return null;
            var files = new List<SourceFile>();
            foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos())
            {
                // Drafts, pending markers, caches, old temporary files and the source meta.json
                // (the import writes its own) are not copied.
                if (entry.Name == CaptureFileNames.Meta || !IsPayloadName(entry.Name)) continue;
                // A payload name that is a folder or a link is never followed.
                if (entry is not FileInfo file || (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    return null;
                files.Add(new(entry.Name, file.FullName, file.Length));
            }
            string primary = record.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Original;
            if (!files.Exists(file => file.Name == primary)) return null;
            if (files.Find(file => file.Name == CaptureFileNames.VideoEdits) is { } edits)
            {
                using JsonDocument _ = JsonDocument.Parse(File.ReadAllText(edits.Path, Encoding.UTF8));
            }
            SourceFile? layers = files.Find(file => file.Name == CaptureFileNames.Layers);
            if (record.HasAnnotations && layers is null) return null;
            if (layers is not null)
            {
                AnnotationDocument? document = AnnotationDocument.TryFromJson(File.ReadAllText(layers.Path, Encoding.UTF8));
                if (document is null) return null;
                foreach (ImageAnnotation image in document.Items.OfType<ImageAnnotation>())
                    if (!CaptureFileNames.IsSafeAssetFileName(image.AssetFileName)
                        || !files.Exists(file => file.Name == image.AssetFileName)) return null;
            }
            return files;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static List<CaptureRecord> RebuildFromSidecars(string captureRoot, CancellationToken cancellationToken)
    {
        var records = new Dictionary<Guid, CaptureRecord>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string path in Directory.EnumerateFiles(captureRoot, CaptureFileNames.Meta, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CaptureRecord? record = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(path, Encoding.UTF8), Json);
                if (record is null) continue;
                record.RelativeDirectory = Path.GetRelativePath(captureRoot, Path.GetDirectoryName(path)!);
                if (CaptureQueue.IsValidMigrationRecord(record)) records.TryAdd(record.Id, record);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return records.Values.OrderByDescending(record => record.CreatedAt).ToList();
    }

    private static AppSettings? ReadSettings(string text) =>
        JsonSerializer.Deserialize<AppSettings>(text, Json) is { Queue: not null } settings ? settings : null;

    /// <summary>Size and write time of every payload and of the root JSON files; never file content.</summary>
    private static string Fingerprint(SourceSnapshot source)
    {
        var text = new StringBuilder();
        foreach (string file in new[] { "settings.json", "index.json", "library-folders.json" })
            text.Append(file).Append(':').Append(Stamp(Path.Combine(source.Root, file))).Append('\n');
        foreach (string directory in source.Directories)
        {
            text.Append(directory).Append('\n');
            if (!Directory.Exists(directory)) { text.Append("missing\n"); continue; }
            foreach (string path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path);
                if (IsPayloadName(name)) text.Append(name).Append(':').Append(Stamp(path)).Append('\n');
            }
        }
        return TextHash(text.ToString());
    }

    private static string Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length + "@" + info.LastWriteTimeUtc.Ticks : "-";
    }

    private static bool IsPayloadName(string name) => name is CaptureFileNames.Original or CaptureFileNames.Rendered
        or CaptureFileNames.Thumbnail or CaptureFileNames.Meta or CaptureFileNames.Layers
        or CaptureFileNames.VideoSource or CaptureFileNames.VideoRendered or CaptureFileNames.VideoEdits
        || CaptureFileNames.IsSafeAssetFileName(name);

    // ---- Shared helpers ----------------------------------------------------------------

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
        string? recovered = AtomicFile.ReadAllTextWithRecovery(settings, text => ReadSettings(text) is not null);
        if (recovered is null && (File.Exists(settings) || File.Exists(settings + AtomicFile.BackupSuffix)))
            throw new MigrationFault("InvalidTarget");
        return ResolveTarget(paths, recovered is null ? new() : ReadSettings(recovered)!);
    }

    private static HashSet<string> ReferencedImports(IEnumerable<CaptureRecord> records)
    {
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CaptureRecord record in records)
            if (IsImportOwned(record))
                imports.Add(record.RelativeDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
        return imports;
    }

    private static bool Overlaps(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string OperationsRoot(AppPaths paths) => ValidatedFilePaths.DirectoryWithinRoot(paths.DataRoot, OperationsDirectory);
    private static void BackupIfPresent(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination, overwrite: false);
    }
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string OptionalHash(string path) => File.Exists(path) ? Hash(path) : "";
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Deterministic per legacy id, so a lost receipt still recognizes its own earlier copy.</summary>
    private static Guid MappedId(string category, string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("mycapture-legacy-import\n" + category + "\n" + value)).AsSpan(0, 16));
    private static void SaveJournal(string operation, Journal journal) =>
        AtomicFile.WriteAllText(ValidatedFilePaths.FileWithinRoot(operation, JournalFile, ".json"),
            JsonSerializer.Serialize(journal, JournalJson));

    /// <summary>Reads only the primary journal: a backup is an older phase and must never drive a decision.</summary>
    private static Journal? TryReadJournal(string operation, out bool missing)
    {
        string path = ValidatedFilePaths.FileWithinRoot(operation, JournalFile, ".json");
        missing = !File.Exists(path);
        if (missing) return null;
        try
        {
            Journal? journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path, Encoding.UTF8), JournalJson);
            return journal is { Version: JournalVersion, Phase: Phases.Staging or Phases.Publishing or Phases.Completed,
                SourceRoot: not null, CapturesRoot: not null, BeforeIndexHash: not null, Records: not null, FolderMap: not null }
                ? journal : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException
        or JsonException or NotSupportedException or InvalidOperationException or OverflowException;

    private static string Code(Exception error) => error switch
    {
        MigrationFault fault => fault.Code,
        JsonException => "InvalidSource",
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => "InsufficientSpace", // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
        _ => "IoFailure",
    };

    private enum SettleMode { InProcess, Recover, Abandon }
    private enum Commit { Committed, Uncommitted, Unknown }

    private static class Phases
    {
        internal const string Staging = "Staging";
        internal const string Publishing = "Publishing";
        internal const string Completed = "Completed";
    }

    private sealed class MigrationFault(string code) : IOException(code)
    {
        internal string Code { get; } = code;
        internal long RequiredBytes { get; init; }
        internal long AvailableBytes { get; init; }
    }

    private sealed class OperationState
    {
        internal string? Directory { get; set; }
        internal Journal? Journal { get; set; }
        internal AppPaths? Target { get; set; }
        internal long PayloadBytes { get; set; }
        internal bool ChangesLibrary { get; set; }
        internal volatile bool Committed;
    }

    /// <summary>The primary index only: a backup is the state before the last write.</summary>
    private sealed class IndexView
    {
        /// <summary>SHA-256 of the primary index, <c>""</c> when absent, <see langword="null"/> when unreadable.</summary>
        internal string? Hash { get; private init; }
        /// <summary>Top-level import folders the index names, or <see langword="null"/> when unknown.</summary>
        internal HashSet<string>? Imports { get; private init; }

        internal static IndexView Live(IReadOnlyCollection<CaptureRecord> records, string path) =>
            new() { Hash = TryHash(path), Imports = ReferencedImports(records) };

        internal static IndexView FromDisk(string path)
        {
            string? hash = TryHash(path);
            if (hash is null) return new();
            var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hash.Length > 0 && !AddImports(path, imports)) return new() { Hash = hash };
            // After a replace that failed midway the committed index can sit in the backup alone.
            if (File.Exists(path + AtomicFile.BackupSuffix)) _ = AddImports(path + AtomicFile.BackupSuffix, imports);
            return new() { Hash = hash, Imports = imports };
        }

        private static string? TryHash(string path)
        {
            try { return OptionalHash(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }

        private static bool AddImports(string path, HashSet<string> imports)
        {
            try
            {
                if (!CaptureQueue.TryParseMigrationSource(File.ReadAllText(path, Encoding.UTF8), out CaptureIndexFile? index)) return false;
                imports.UnionWith(ReferencedImports(index!.Records));
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    private sealed class SourceSnapshot
    {
        public string Root { get; init; } = "";
        public string? Settings { get; init; }
        public bool SettingsUnreadable { get; init; }
        public List<CaptureRecord> Records { get; } = [];
        public List<CaptureFolder> Folders { get; init; } = [];
        public Dictionary<Guid, List<SourceFile>> Files { get; } = [];
        public List<string> Directories { get; } = [];
        public long PayloadBytes { get; set; }
        public int UnusableCount { get; set; }
    }

    private sealed record SourceFile(string Name, string Path, long Length);
    private sealed record PlannedRecord(CaptureRecord Source, CaptureRecord Imported, List<SourceFile> Files);

    private sealed class Plan
    {
        internal List<PlannedRecord> Records { get; } = [];
        internal Dictionary<Guid, Guid> FolderMap { get; } = [];
        internal long PayloadBytes { get; set; }
        internal int Skipped { get; set; }
        internal int CreatedFolders { get; set; }
        internal bool ChangesLibrary => Records.Count > 0 || CreatedFolders > 0;
    }

    private sealed class Receipts
    {
        internal Dictionary<Guid, Guid> Records { get; } = [];
        internal Dictionary<Guid, Guid> Folders { get; } = [];
    }

    private sealed class Journal
    {
        public int Version { get; set; } = JournalVersion;
        public string Phase { get; set; } = Phases.Staging;
        public string SourceRoot { get; set; } = "";
        public string TargetRoot { get; set; } = "";
        public string CapturesRoot { get; set; } = "";
        public string BeforeIndexHash { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IndexJson { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IndexHash { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FoldersJson { get; set; }
        public bool HasLegacySettings { get; set; }
        public int ImportedCount { get; set; }
        public int SkippedCount { get; set; }
        public int UnusableCount { get; set; }
        public Dictionary<Guid, Guid> FolderMap { get; set; } = [];
        public List<ImportedRecord> Records { get; set; } = [];

        /// <summary>Only the id mappings: later imports need nothing else, wherever the library moves.</summary>
        internal Journal ToReceipt() => new()
        {
            Phase = Phases.Completed, SourceRoot = SourceRoot, TargetRoot = TargetRoot, CapturesRoot = CapturesRoot,
            HasLegacySettings = HasLegacySettings, ImportedCount = ImportedCount, SkippedCount = SkippedCount,
            UnusableCount = UnusableCount, FolderMap = FolderMap,
            Records = Records.Select(record => new ImportedRecord { LegacyId = record.LegacyId, TargetId = record.TargetId }).ToList(),
        };
    }

    private sealed class ImportedRecord
    {
        public Guid LegacyId { get; set; }
        public Guid TargetId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<StagedFile>? Files { get; set; }
    }

    private sealed class StagedFile
    {
        public string Name { get; set; } = "";
        public long Length { get; set; }
    }
}
