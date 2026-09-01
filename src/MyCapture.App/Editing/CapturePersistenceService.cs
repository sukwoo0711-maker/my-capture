using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Diagnostics;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Persists captures into the queue: the original the instant a region is selected, then
/// the annotated result when editing commits.
/// </summary>
/// <remarks>
/// <para>
/// Two-phase on purpose. The moment the user frames a region we write the untouched pixels
/// and index them, so a crash, a power loss, or the user abandoning the editor can never
/// lose the capture they just took — the single promise the product's queue makes. The
/// second phase replaces the rendered PNG with the flattened annotation result, writes the
/// layer document and any image sidecars, refreshes the thumbnail, and re-indexes.
/// </para>
/// <para>
/// Every file write goes through <see cref="AtomicFile"/> / <see cref="ImageCodec"/> so a
/// half-written file can never stand in for a good one, and each phase ends by saving the
/// index and the per-capture recovery metadata as one logical, ordered step.
/// </para>
/// <para>
/// Image encoding and durable file flushes run on isolated STA workers. Queue collection
/// mutation and the final staged-file swap return to the caller context, keeping WPF state
/// thread-affine while capture-to-editor transitions remain responsive.
/// </para>
/// </remarks>
internal sealed class CapturePersistenceService
{
    private const string FinalizeJournalFileName = ".finalize-journal.json";
    private const string FinalizeCommitMarkerFileName = ".finalize-commit-ready";
    private readonly CaptureQueue _queue;
    private readonly Func<QueueSettings> _queueSettings;
    private readonly ILogger<CapturePersistenceService> _log;
    private readonly ConcurrentDictionary<Guid, byte> _busyRecords = new();
    private readonly object _blockedGate = new();
    private readonly Dictionary<Guid, BlockReason> _blockedReasons = [];
    private readonly Dictionary<Guid, IDisposable> _blockedEvictionLeases = [];
    private readonly ConcurrentDictionary<Guid, int> _activeEditSessions = new();

    /// <summary>
    /// Test-only fault-injection point invoked immediately before each staged file swap.
    /// Production leaves it unset. It makes transaction rollback independently verifiable
    /// without relying on timing-sensitive antivirus or file-lock behaviour.
    /// </summary>
    internal Action<int, string>? BeforeStagedFileCommit { get; set; }

    /// <summary>
    /// Test-only fault-injection point immediately before protocol-critical meta persistence.
    /// It verifies that original/finalize journals remain live when metadata cannot be written.
    /// </summary>
    internal Action<Guid>? BeforeRecordMetadataCommit { get; set; }

    internal CapturePersistenceService(
        CaptureQueue queue,
        AppPaths paths,
        Func<QueueSettings> queueSettings,
        ILogger<CapturePersistenceService> log)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentNullException.ThrowIfNull(paths);
        _queueSettings = queueSettings ?? throw new ArgumentNullException(nameof(queueSettings));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        RecoverPendingOriginals();
        RecoverInterruptedTransactions();
    }

    /// <summary>
    /// Writes the untouched selection and indexes it. Returns the created record so the
    /// finalise phase can update the same capture in place.
    /// </summary>
    /// <param name="original">The selected pixels, before any annotation.</param>
    /// <param name="dpiScale">DPI scale of the source monitor.</param>
    /// <param name="sourceWindowTitle">Foreground window title at capture time.</param>
    /// <param name="sourceMonitor">Source monitor device name, for diagnostics.</param>
    internal CaptureRecord PersistOriginal(
        BitmapSource original,
        double dpiScale,
        string sourceWindowTitle,
        string sourceMonitor)
    {
        ArgumentNullException.ThrowIfNull(original);

        CaptureRecord record = CreateRecord(original, dpiScale, sourceWindowTitle, sourceMonitor);
        long bytes = WriteOriginalFiles(record, original);
        CompleteOriginal(record, bytes);
        return record;
    }

    internal async Task<CaptureRecord> PersistOriginalAsync(
        BitmapSource original,
        double dpiScale,
        string sourceWindowTitle,
        string sourceMonitor)
    {
        ArgumentNullException.ThrowIfNull(original);

        CaptureRecord record = CreateRecord(original, dpiScale, sourceWindowTitle, sourceMonitor);
        long bytes = await StaThreadTask.RunAsync(
            () => WriteOriginalFiles(record, original),
            "MyCapture original persistence");
        CompleteOriginal(record, bytes);
        return record;
    }

    internal bool IsBusy(Guid recordId) =>
        _busyRecords.ContainsKey(recordId)
        || IsBlocked(recordId)
        || _activeEditSessions.ContainsKey(recordId);

    internal IDisposable AcquireEditLease(Guid recordId)
    {
        _activeEditSessions.AddOrUpdate(recordId, 1, static (_, count) => checked(count + 1));
        return new EditLease(this, recordId, _queue.AcquireEvictionLease(recordId));
    }

    private void ReleaseEditLease(Guid recordId, IDisposable evictionLease)
    {
        while (_activeEditSessions.TryGetValue(recordId, out int count))
        {
            if (count <= 1)
            {
                if (_activeEditSessions.TryRemove(recordId, out _))
                {
                    break;
                }
            }
            else if (_activeEditSessions.TryUpdate(recordId, count - 1, count))
            {
                break;
            }
        }

        evictionLease.Dispose();
    }

    private static CaptureRecord CreateRecord(
        BitmapSource original,
        double dpiScale,
        string sourceWindowTitle,
        string sourceMonitor)
    {
        var record = new CaptureRecord
        {
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
            Width = original.PixelWidth,
            Height = original.PixelHeight,
            DpiScale = dpiScale > 0 ? dpiScale : 1.0,
            SourceWindowTitle = sourceWindowTitle ?? string.Empty,
            SourceMonitor = sourceMonitor ?? string.Empty,
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        return record;
    }

    private long WriteOriginalFiles(
        CaptureRecord record,
        BitmapSource original,
        bool createPendingJournal = true,
        bool rewriteOriginal = true)
    {
        string directory = _queue.GetDirectory(record);
        Directory.CreateDirectory(directory);

        if (createPendingJournal)
        {
            // Written before the first pixel. Queue.Load always merges this sidecar with a valid
            // existing index, and startup repairs derived files before capacity eviction resumes.
            AtomicFile.WriteAllText(
                Path.Combine(directory, CaptureFileNames.OriginalPending),
                JsonSerializer.Serialize(record));
        }

        long bytes = 0;

        // original.png — the unmodified capture.
        string originalPath = Path.Combine(directory, CaptureFileNames.Original);
        bytes += rewriteOriginal
            ? ImageCodec.SavePng(original, originalPath)
            : SafeFileLength(originalPath);

        // rendered.png — identical to the original until annotations are flattened.
        bytes += ImageCodec.SavePng(original, Path.Combine(directory, CaptureFileNames.Rendered));

        // layers.json — an empty document so the capture is immediately re-editable.
        AnnotationDocument emptyDocument = AnnotationDocument.CreateFor(record.Width, record.Height);
        string layersJson = emptyDocument.ToJson();
        AtomicFile.WriteAllText(Path.Combine(directory, CaptureFileNames.Layers), layersJson);
        bytes += ByteLength(layersJson);

        // thumb.jpg — gallery tile.
        bytes += WriteThumbnail(original, directory);

        return bytes;
    }

    private void CompleteOriginal(CaptureRecord record, long bytes)
    {
        record.HasAnnotations = false;
        record.TotalBytes = bytes;

        // Index first (so the record is discoverable) then the recovery sidecar (so a lost
        // index can be rebuilt). The meta.json byte cost is small and recovery-only, so it
        // is intentionally not folded into the tracked byte total.
        _queue.Add(record);
        try
        {
            SaveRecordMetaOrThrow(record);
            _queue.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkBlocked(record.Id, BlockReason.PendingOriginal);
            throw;
        }
        string pendingPath = Path.Combine(_queue.GetDirectory(record), CaptureFileNames.OriginalPending);
        TryDeleteFile(pendingPath);
        if (File.Exists(pendingPath))
        {
            MarkBlocked(record.Id, BlockReason.PendingOriginal);
            throw new IOException(
                $"Capture {record.Id} was saved, but its pending-original marker could not be cleared.");
        }

        _log.LogInformation(
            "Persisted original {Id} ({Width}x{Height}, {Bytes} bytes)",
            record.Id, record.Width, record.Height, bytes);

    }

    private void SaveRecordMetaOrThrow(CaptureRecord record)
    {
        BeforeRecordMetadataCommit?.Invoke(record.Id);
        _queue.SaveRecordMetaOrThrow(record);
    }

    /// <summary>
    /// Replaces the rendered PNG with the flattened annotation result and finalises every
    /// derived file, then re-indexes and updates the tracked byte count.
    /// </summary>
    /// <param name="record">The record returned by <see cref="PersistOriginal"/>.</param>
    /// <param name="flattened">The annotations flattened onto the original at 1:1 pixels.</param>
    /// <param name="document">The annotation layer to persist.</param>
    /// <param name="assetBitmaps">
    /// Decoded, in-memory bitmaps for the image assets used by <paramref name="document"/>,
    /// keyed by their in-session asset name. The bytes are written from these, never from
    /// the source file, so a deleted or moved original cannot lose the asset.
    /// </param>
    internal void Finalize(
        CaptureRecord record,
        BitmapSource flattened,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps,
        long? expectedContentRevision = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(flattened);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assetBitmaps);

        if (!_busyRecords.TryAdd(record.Id, 0))
        {
            throw new InvalidOperationException($"Capture {record.Id} is already being finalized.");
        }

        using IDisposable evictionLease = _queue.AcquireEvictionLease(record.Id);

        try
        {
            RecoverRecordTransactions(record);
            ThrowIfPendingTransactionRemains(record);
            EnsureExpectedGeneration(record, expectedContentRevision);
            FinalizeFiles files = WriteFinalizeFiles(record, flattened, document, assetBitmaps);
            CompleteFinalize(record, files);
        }
        finally
        {
            _busyRecords.TryRemove(record.Id, out _);
        }
    }

    /// <summary>
    /// Writes the image-heavy portion on an isolated STA worker, then updates the UI-owned
    /// queue and its small index on the caller context.
    /// </summary>
    internal async Task FinalizeAsync(
        CaptureRecord record,
        BitmapSource flattened,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps,
        long? expectedContentRevision = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(flattened);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assetBitmaps);

        if (!_busyRecords.TryAdd(record.Id, 0))
        {
            throw new InvalidOperationException($"Capture {record.Id} is already being finalized.");
        }

        using IDisposable evictionLease = _queue.AcquireEvictionLease(record.Id);

        try
        {
            RecoverRecordTransactions(record);
            ThrowIfPendingTransactionRemains(record);
            EnsureExpectedGeneration(record, expectedContentRevision);
            FinalizeFiles files = await StaThreadTask.RunAsync(
                () => WriteFinalizeFiles(record, flattened, document, assetBitmaps),
                "MyCapture capture persistence");
            CompleteFinalize(record, files);
        }
        finally
        {
            _busyRecords.TryRemove(record.Id, out _);
        }
    }

    private void EnsureExpectedGeneration(CaptureRecord record, long? expectedContentRevision)
    {
        CaptureRecord? current = _queue.Find(record.Id);
        if (current is null)
        {
            throw new CaptureGenerationConflictException(
                $"Capture {record.Id} was deleted while it was being edited.");
        }

        if (expectedContentRevision is { } expected && current.ContentRevision != expected)
        {
            throw new CaptureGenerationConflictException(
                $"Capture {record.Id} changed after this editor was opened.");
        }
    }

    private FinalizeFiles WriteFinalizeFiles(
        CaptureRecord record,
        BitmapSource flattened,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps)
    {
        string directory = _queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        string stageDirectory = Path.Combine(directory, $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);

        try
        {
            long bytes = 0;

            // original.png is unchanged; re-measure it so the byte total stays accurate.
            bytes += SafeFileLength(Path.Combine(directory, CaptureFileNames.Original));

            // Encode and flush every new generation into a private same-volume directory. The
            // UI thread later swaps these staged files as one non-interleavable commit window.
            CanonicalizeAssets(stageDirectory, document, assetBitmaps, ref bytes);

            bytes += ImageCodec.SavePng(flattened, Path.Combine(stageDirectory, CaptureFileNames.Rendered));

            document.NormalizeZIndices();
            string layersJson = document.ToJson();
            AtomicFile.WriteAllText(Path.Combine(stageDirectory, CaptureFileNames.Layers), layersJson);
            bytes += ByteLength(layersJson);

            bytes += WriteThumbnail(flattened, stageDirectory);
            return new FinalizeFiles(
                directory,
                stageDirectory,
                bytes,
                !document.IsEmpty,
                document.Items.Count);
        }
        catch
        {
            RetireFinalizeStageDirectory(stageDirectory);
            throw;
        }
    }

    private void CompleteFinalize(CaptureRecord record, FinalizeFiles files)
    {
        var committed = new List<CommittedFile>();
        bool rollbackComplete = false;
        bool commitComplete = false;
        bool commitMarked = false;
        bool previousHasAnnotations = record.HasAnnotations;
        DateTimeOffset previousUpdatedAt = record.UpdatedAt;
        long previousContentRevision = record.ContentRevision;
        long previousTotalBytes = record.TotalBytes;

        try
        {
            string[] stagedPaths = Directory.EnumerateFiles(
                    files.StageDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            DateTimeOffset committedAt = DateTimeOffset.Now;
            long committedContentRevision = checked(previousContentRevision + 1);
            var stagedNames = stagedPaths
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<FinalizeJournalEntry> journalEntries = stagedPaths.Select(path => new FinalizeJournalEntry
            {
                FileName = Path.GetFileName(path),
                HadPreviousFile = File.Exists(Path.Combine(files.TargetDirectory, Path.GetFileName(path))),
            }).ToList();

            foreach (string existingAsset in Directory.EnumerateFiles(
                         files.TargetDirectory,
                         $"{CaptureFileNames.AssetPrefix}*.png",
                         SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(existingAsset);
                if (CaptureFileNames.IsSafeAssetFileName(fileName) && !stagedNames.Contains(fileName))
                {
                    journalEntries.Add(new FinalizeJournalEntry
                    {
                        FileName = fileName,
                        HadPreviousFile = true,
                        DeleteTarget = true,
                    });
                }
            }

            var journal = new FinalizeJournal
            {
                RecordId = record.Id,
                Bytes = files.Bytes,
                HasAnnotations = files.HasAnnotations,
                ItemCount = files.ItemCount,
                UpdatedAt = committedAt,
                ContentRevision = committedContentRevision,
                Files = journalEntries,
            };

            // The journal is flushed before the first target changes. On startup an absent
            // commit marker means "roll back"; a present marker means every file swap finished
            // and metadata must be rolled forward. This closes the process/power-loss gap that
            // an in-process catch block alone cannot cover.
            AtomicFile.WriteAllText(
                Path.Combine(files.StageDirectory, FinalizeJournalFileName),
                JsonSerializer.Serialize(journal));

            string rollbackDirectory = Path.Combine(files.StageDirectory, ".rollback");
            Directory.CreateDirectory(rollbackDirectory);

            for (int index = 0; index < journal.Files.Count; index++)
            {
                FinalizeJournalEntry entry = journal.Files[index];
                string targetPath = Path.Combine(files.TargetDirectory, entry.FileName);
                BeforeStagedFileCommit?.Invoke(index, targetPath);
                committed.Add(entry.DeleteTarget
                    ? CommitTargetDeletion(targetPath, rollbackDirectory)
                    : CommitStagedFile(
                        Path.Combine(files.StageDirectory, entry.FileName),
                        targetPath,
                        rollbackDirectory,
                        entry.HadPreviousFile));
            }

            AtomicFile.WriteAllBytes(
                Path.Combine(files.StageDirectory, FinalizeCommitMarkerFileName),
                [0x4D, 0x43, 0x46, 0x31]); // "MCF1"
            commitMarked = true;

            record.HasAnnotations = files.HasAnnotations;
            record.UpdatedAt = committedAt;
            record.ContentRevision = committedContentRevision;
            record.OcrText = null;
            record.OcrLanguage = null;
            record.OcrContentRevision = null;

            _queue.UpdateByteCount(record.Id, files.Bytes);
            record.UpdatedAt = committedAt;
            SaveRecordMetaOrThrow(record);
            _queue.Save();

            _log.LogInformation(
                "Finalised capture {Id}: {ItemCount} annotation(s), {Bytes} bytes",
                record.Id, files.ItemCount, files.Bytes);
            commitComplete = true;
        }
        catch (Exception commitError)
        {
            if (commitMarked)
            {
                // All file swaps are complete and the durable marker makes this generation the
                // winner. Preserve the journal for startup/next-operation metadata repair; never
                // roll coherent new files back merely because index persistence was unavailable.
                _log.LogError(
                    commitError,
                    "Capture {Id} files committed, but metadata repair is pending in {StageDirectory}",
                    record.Id,
                    LogText.SingleLine(files.StageDirectory));
                MarkBlocked(record.Id, BlockReason.FinalizeTransaction);
                return;
            }

            rollbackComplete = TryRollbackCommittedFiles(committed, files.StageDirectory);

            // Restore the in-memory queue state as well. Atomic queue/meta writes mean a failed
            // save leaves the previous on-disk copy recoverable; this keeps the live model in
            // the same generation as the files we just restored.
            record.HasAnnotations = previousHasAnnotations;
            record.UpdatedAt = previousUpdatedAt;
            record.ContentRevision = previousContentRevision;
            _queue.UpdateByteCount(record.Id, previousTotalBytes);
            record.UpdatedAt = previousUpdatedAt;

            if (rollbackComplete)
            {
                _queue.SaveRecordMeta(record);
                try
                {
                    _queue.Save();
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException)
                {
                    _log.LogError(
                        recoveryError,
                        "Capture {Id} files rolled back, but its restored queue index could not be saved",
                        record.Id);
                }
            }
            else
            {
                MarkBlocked(record.Id, BlockReason.FinalizeTransaction);
                _log.LogCritical(
                    "Capture {Id} finalisation failed and rollback was incomplete; recovery files remain in {StageDirectory}",
                    record.Id,
                    LogText.SingleLine(files.StageDirectory));
            }

            throw new IOException(
                rollbackComplete
                    ? $"Capture {record.Id} finalisation failed; the previous file generation was restored."
                    : $"Capture {record.Id} finalisation and rollback failed; recovery files remain in {files.StageDirectory}.",
                commitError);
        }
        finally
        {
            if (commitComplete || rollbackComplete)
            {
                RetireFinalizeStageDirectory(files.StageDirectory);
            }


            if (HasFinalizeStageDirectory(files.TargetDirectory, record.Id))
            {
                MarkBlocked(record.Id, BlockReason.FinalizeTransaction);
            }
            else
            {
                ClearBlocked(record.Id, BlockReason.FinalizeTransaction);
            }
        }
    }

    private static CommittedFile CommitStagedFile(
        string stagedPath,
        string targetPath,
        string rollbackDirectory,
        bool hadPreviousFile)
    {
        if (hadPreviousFile)
        {
            if (!File.Exists(targetPath))
            {
                throw new IOException($"Expected finalisation target is missing: {targetPath}");
            }

            string backupPath = Path.Combine(rollbackDirectory, Path.GetFileName(targetPath));
            File.Replace(
                stagedPath,
                targetPath,
                backupPath,
                ignoreMetadataErrors: true);
            return new CommittedFile(targetPath, backupPath, HadPreviousFile: true);
        }

        if (File.Exists(targetPath))
        {
            throw new IOException($"Unexpected finalisation target already exists: {targetPath}");
        }

        File.Move(stagedPath, targetPath);
        return new CommittedFile(targetPath, BackupPath: null, HadPreviousFile: false);
    }

    private static CommittedFile CommitTargetDeletion(string targetPath, string rollbackDirectory)
    {
        if (!File.Exists(targetPath))
        {
            throw new IOException($"Expected obsolete asset is missing: {targetPath}");
        }

        string backupPath = Path.Combine(rollbackDirectory, Path.GetFileName(targetPath));
        File.Move(targetPath, backupPath);
        return new CommittedFile(targetPath, backupPath, HadPreviousFile: true);
    }

    private bool TryRollbackCommittedFiles(
        IReadOnlyList<CommittedFile> committed,
        string stageDirectory)
    {
        bool complete = true;
        string intentDirectory = Path.Combine(stageDirectory, ".rollback-intent");
        Directory.CreateDirectory(intentDirectory);
        for (int index = committed.Count - 1; index >= 0; index--)
        {
            CommittedFile file = committed[index];
            try
            {
                string intentPath = Path.Combine(
                    intentDirectory,
                    Path.GetFileName(file.TargetPath) + ".intent");
                if (!File.Exists(intentPath))
                {
                    AtomicFile.WriteAllBytes(intentPath, [0x4D, 0x43, 0x52, 0x31]); // "MCR1"
                }

                if (!file.HadPreviousFile)
                {
                    if (File.Exists(file.TargetPath))
                    {
                        File.Delete(file.TargetPath);
                    }

                    continue;
                }

                if (file.BackupPath is null || !File.Exists(file.BackupPath))
                {
                    complete = false;
                    _log.LogError(
                        "Rollback source is missing for {TargetPath}; staged recovery retained at {StageDirectory}",
                        LogText.SingleLine(file.TargetPath),
                        LogText.SingleLine(stageDirectory));
                    continue;
                }

                RestoreBackupWithoutConsuming(file.BackupPath, file.TargetPath);
            }
            catch (Exception rollbackError) when (rollbackError is IOException
                                                   or UnauthorizedAccessException
                                                   or ArgumentException
                                                   or NotSupportedException)
            {
                complete = false;
                _log.LogError(
                    rollbackError,
                    "Could not roll back {TargetPath}; staged recovery retained at {StageDirectory}",
                    LogText.SingleLine(file.TargetPath),
                    LogText.SingleLine(stageDirectory));
            }
        }

        return complete;
    }

    private static void RestoreBackupWithoutConsuming(string backupPath, string targetPath)
    {
        string restorePath = $"{backupPath}.{Guid.NewGuid():N}.restore";
        try
        {
            using (var source = new FileStream(
                       backupPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            using (var destination = new FileStream(
                       restorePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                File.Replace(
                    restorePath,
                    targetPath,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(restorePath, targetPath);
            }
        }
        finally
        {
            TryDeleteFile(restorePath);
        }
    }

    private void RecoverInterruptedTransactions()
    {
        foreach (CaptureRecord record in _queue.Records.ToArray())
        {
            RecoverRecordTransactions(record);
        }
    }

    private void RecoverPendingOriginals()
    {
        foreach (CaptureRecord record in _queue.Records.ToArray())
        {
            string directory = _queue.GetDirectory(record);
            string pendingPath = Path.Combine(directory, CaptureFileNames.OriginalPending);
            if (!File.Exists(pendingPath))
            {
                continue;
            }

            using IDisposable evictionLease = _queue.AcquireEvictionLease(record.Id);
            try
            {
                string originalPath = Path.Combine(directory, CaptureFileNames.Original);
                BitmapSource? original = ImageCodec.TryLoad(originalPath);
                if (original is null)
                {
                    MarkBlocked(record.Id, BlockReason.PendingOriginal);
                    _log.LogCritical(
                        "Pending original {Id} has no decodable original.png; preserving its recovery files",
                        record.Id);
                    continue;
                }

                record.Width = original.PixelWidth;
                record.Height = original.PixelHeight;
                // original.png is the durable source for this recovery. Rewriting it would
                // create an untracked full-size .bak beside an otherwise valid 8K capture.
                long bytes = WriteOriginalFiles(
                    record,
                    original,
                    createPendingJournal: false,
                    rewriteOriginal: false);
                record.HasAnnotations = false;
                record.ContentRevision = 0;
                record.OcrText = null;
                record.OcrLanguage = null;
                record.OcrContentRevision = null;
                _queue.UpdateByteCount(record.Id, bytes);
                SaveRecordMetaOrThrow(record);
                _queue.Save();

                // Atomic rewrites of the derived files deliberately retain backups in normal
                // operation. Here the untouched original plus pending marker is the recovery
                // source, so those potentially huge copies are terminal debris. Keep the
                // marker and eviction lease until every one has actually gone.
                bool backupsCleared = TryCleanRecoveredOriginalBackups(directory);
                if (backupsCleared)
                {
                    TryDeleteFile(pendingPath);
                }

                if (!backupsCleared || File.Exists(pendingPath))
                {
                    MarkBlocked(record.Id, BlockReason.PendingOriginal);
                }
                else
                {
                    ClearBlocked(record.Id, BlockReason.PendingOriginal);
                }

                _log.LogWarning(
                    "Recovered original capture {Id} that was interrupted before queue indexing completed",
                    record.Id);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or ArgumentException
                                       or NotSupportedException)
            {
                MarkBlocked(record.Id, BlockReason.PendingOriginal);
                _log.LogError(
                    ex,
                    "Could not recover pending original capture {Id}; preserving its files",
                    record.Id);
            }
        }
    }

    private void RecoverRecordTransactions(CaptureRecord record)
    {
        string targetDirectory = _queue.GetDirectory(record);
        if (!Directory.Exists(targetDirectory))
        {
            return;
        }

        PurgeRetiredTransactionDirectories(targetDirectory);

        string[] stages;
        try
        {
            stages = Directory.EnumerateDirectories(
                    targetDirectory,
                    ".stage-*",
                    SearchOption.TopDirectoryOnly)
                .Where(IsFinalizeStageDirectory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkBlocked(record.Id, BlockReason.FinalizeTransaction);
            _log.LogWarning(ex, "Could not inspect pending finalisation journals for {Id}", record.Id);
            return;
        }

        if (stages.Length > 1)
        {
            // A normal writer can have only one transaction per record. With multiple marked
            // generations there is no safe filename-only way to infer which payload owns the
            // current targets, so never choose by random GUID ordering.
            MarkBlocked(record.Id, BlockReason.FinalizeTransaction);
            _log.LogCritical(
                "Capture {Id} has {Count} competing finalisation journals; automatic recovery was refused",
                record.Id,
                stages.Length);
            return;
        }

        foreach (string stageDirectory in stages)
        {
            RecoverTransaction(record, targetDirectory, stageDirectory);
        }

        if (HasFinalizeStageDirectory(targetDirectory, record.Id))
        {
            MarkBlocked(record.Id, BlockReason.FinalizeTransaction);
        }
        else
        {
            ClearBlocked(record.Id, BlockReason.FinalizeTransaction);
        }
    }

    private void ThrowIfPendingTransactionRemains(CaptureRecord record)
    {
        string targetDirectory = _queue.GetDirectory(record);
        if (HasFinalizeStageDirectory(targetDirectory, record.Id))
        {
            throw new IOException(
                $"Capture {record.Id} has an unresolved finalisation journal; its recovery files were preserved.");
        }
    }

    private bool HasFinalizeStageDirectory(string targetDirectory, Guid recordId)
    {
        try
        {
            return Directory.Exists(targetDirectory)
                   && Directory.EnumerateDirectories(targetDirectory, ".stage-*", SearchOption.TopDirectoryOnly)
                       .Any(IsFinalizeStageDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkBlocked(recordId, BlockReason.FinalizeTransaction);
            _log.LogError(ex, "Could not inspect finalisation state for capture {Id}", recordId);
            return true;
        }
    }

    private bool IsBlocked(Guid recordId)
    {
        lock (_blockedGate)
        {
            return _blockedReasons.ContainsKey(recordId);
        }
    }

    private void MarkBlocked(Guid recordId, BlockReason reason)
    {
        lock (_blockedGate)
        {
            _blockedReasons.TryGetValue(recordId, out BlockReason current);
            _blockedReasons[recordId] = current | reason;
            if (!_blockedEvictionLeases.ContainsKey(recordId))
            {
                _blockedEvictionLeases[recordId] = _queue.AcquireEvictionLease(recordId);
            }
        }
    }

    private void ClearBlocked(Guid recordId, BlockReason reason)
    {
        IDisposable? lease = null;
        lock (_blockedGate)
        {
            if (!_blockedReasons.TryGetValue(recordId, out BlockReason current))
            {
                return;
            }

            BlockReason remaining = current & ~reason;
            if (remaining != BlockReason.None)
            {
                _blockedReasons[recordId] = remaining;
                return;
            }

            _blockedReasons.Remove(recordId);
            if (_blockedEvictionLeases.Remove(recordId, out IDisposable? removed))
            {
                lease = removed;
            }
        }

        // Releasing can enforce queue capacity and raise eviction events. Keep that work
        // outside the persistence lock so event handlers can safely query IsBusy.
        lease?.Dispose();
    }

    private void RecoverTransaction(
        CaptureRecord record,
        string targetDirectory,
        string stageDirectory)
    {
        FinalizeJournal? journal = TryReadJournal(record.Id, stageDirectory);
        if (journal is null)
        {
            // Encoding happens entirely inside the stage before a journal or rollback directory
            // is created. A crash in that precommit phase cannot have touched any target, so the
            // orphan is safe to discard instead of blocking the capture forever.
            if (!Directory.Exists(Path.Combine(stageDirectory, ".rollback"))
                && !File.Exists(Path.Combine(stageDirectory, FinalizeCommitMarkerFileName)))
            {
                if (RetireFinalizeStageDirectory(stageDirectory))
                {
                    _log.LogWarning(
                        "Removed precommit staging files left by interrupted encoding for {Id}",
                        record.Id);
                    return;
                }
            }

            _log.LogCritical(
                "Pending finalisation for {Id} has no valid journal; preserving {StageDirectory} for recovery",
                record.Id,
                stageDirectory);
            return;
        }

        string markerPath = Path.Combine(stageDirectory, FinalizeCommitMarkerFileName);
        if (!File.Exists(markerPath) && journal.ContentRevision <= 0)
        {
            // Journals written before content-generation identities existed cannot
            // distinguish an uncommitted swap from residue left after metadata commit and
            // partial recursive cleanup. Either automatic choice can destroy the only good
            // generation, so preserve the complete protocol for explicit recovery.
            _log.LogCritical(
                "Legacy finalisation journal for {Id} has no generation identity; preserving {StageDirectory}",
                record.Id,
                stageDirectory);
            return;
        }

        if (!File.Exists(markerPath)
            && journal.ContentRevision > 0
            && record.ContentRevision >= journal.ContentRevision)
        {
            // Compatibility with builds that recursively deleted a live .stage directory.
            // If metadata already names this generation (or a newer one), rolling it back
            // would resurrect stale pixels. For the equal generation, require the same
            // complete target invariant as a marked commit before retiring the residue.
            if (record.ContentRevision == journal.ContentRevision
                && !IsCommittedGenerationComplete(journal, targetDirectory, stageDirectory))
            {
                _log.LogCritical(
                    "Finalisation cleanup residue for {Id} does not match its committed metadata; preserving {StageDirectory}",
                    record.Id,
                    stageDirectory);
                return;
            }

            if (record.ContentRevision == journal.ContentRevision)
            {
                string? previousOcrText = record.OcrText;
                string? previousOcrLanguage = record.OcrLanguage;
                long? previousOcrRevision = record.OcrContentRevision;
                try
                {
                    record.OcrText = null;
                    record.OcrLanguage = null;
                    record.OcrContentRevision = null;
                    SaveRecordMetaOrThrow(record);
                    _queue.Save();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    record.OcrText = previousOcrText;
                    record.OcrLanguage = previousOcrLanguage;
                    record.OcrContentRevision = previousOcrRevision;
                    _log.LogError(
                        ex,
                        "Could not invalidate stale OCR while retiring committed cleanup residue for {Id}",
                        record.Id);
                    return;
                }
            }

            if (RetireFinalizeStageDirectory(stageDirectory))
            {
                _log.LogWarning(
                    "Retired finalisation cleanup residue for already committed capture {Id}",
                    record.Id);
            }

            return;
        }

        if (File.Exists(markerPath) && record.ContentRevision > journal.ContentRevision)
        {
            // A marked directory from an older generation can survive legacy cleanup or be
            // restored externally. The durable record already names a newer generation, so
            // applying this journal would roll metadata backward while target names still
            // happen to look complete. Retire only the stale protocol directory.
            if (RetireFinalizeStageDirectory(stageDirectory))
            {
                _log.LogWarning(
                    "Retired stale marked finalisation {JournalRevision} behind capture {Id} revision {RecordRevision}",
                    journal.ContentRevision,
                    record.Id,
                    record.ContentRevision);
            }

            return;
        }

        if (File.Exists(markerPath))
        {
            // A marker is written only after every swap. Validate that invariant before making
            // the in-memory/index metadata match the already committed generation.
            if (!IsCommittedGenerationComplete(journal, targetDirectory, stageDirectory))
            {
                _log.LogCritical(
                    "Committed finalisation journal for {Id} is incomplete; preserving {StageDirectory}",
                    record.Id,
                    stageDirectory);
                return;
            }

            try
            {
                record.HasAnnotations = journal.HasAnnotations;
                record.UpdatedAt = journal.UpdatedAt;
                record.ContentRevision = journal.ContentRevision;
                record.OcrText = null;
                record.OcrLanguage = null;
                record.OcrContentRevision = null;
                _queue.UpdateByteCount(record.Id, journal.Bytes);
                record.UpdatedAt = journal.UpdatedAt;
                SaveRecordMetaOrThrow(record);
                _queue.Save();
                if (RetireFinalizeStageDirectory(stageDirectory))
                {
                    _log.LogWarning(
                        "Recovered committed capture generation {Id} from an interrupted metadata update",
                        record.Id);
                }
                else
                {
                    _log.LogError(
                        "Capture {Id} metadata was recovered, but its transaction directory could not be retired",
                        record.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogError(
                    ex,
                    "Could not finish committed capture generation {Id}; preserving {StageDirectory}",
                    record.Id,
                    stageDirectory);
            }

            return;
        }

        var committed = new List<CommittedFile>();
        string rollbackDirectory = Path.Combine(stageDirectory, ".rollback");
        foreach (FinalizeJournalEntry entry in journal.Files)
        {
            string stagedPath = Path.Combine(stageDirectory, entry.FileName);
            string targetPath = Path.Combine(targetDirectory, entry.FileName);
            string backupPath = Path.Combine(rollbackDirectory, entry.FileName);
            string rollbackIntentPath = Path.Combine(
                stageDirectory,
                ".rollback-intent",
                entry.FileName + ".intent");

            if (File.Exists(rollbackIntentPath))
            {
                if (entry.HadPreviousFile)
                {
                    if (File.Exists(backupPath))
                    {
                        committed.Add(new CommittedFile(targetPath, backupPath, HadPreviousFile: true));
                        continue;
                    }

                    if (File.Exists(targetPath))
                    {
                        // Rollback completed and only best-effort stage cleanup was interrupted.
                        continue;
                    }
                }
                else
                {
                    if (File.Exists(targetPath))
                    {
                        committed.Add(new CommittedFile(targetPath, BackupPath: null, HadPreviousFile: false));
                    }

                    // Missing target is already the desired rolled-back state.
                    continue;
                }

                _log.LogCritical(
                    "Rollback intent for {Id} cannot be completed at {FileName}; preserving {StageDirectory}",
                    record.Id,
                    entry.FileName,
                    stageDirectory);
                return;
            }

            if (entry.DeleteTarget)
            {
                if (File.Exists(backupPath) && !File.Exists(targetPath))
                {
                    committed.Add(new CommittedFile(targetPath, backupPath, HadPreviousFile: true));
                    continue;
                }

                if (!File.Exists(backupPath) && File.Exists(targetPath))
                {
                    continue;
                }

                _log.LogCritical(
                    "Interrupted asset deletion for {Id} is ambiguous at {FileName}; preserving {StageDirectory}",
                    record.Id,
                    entry.FileName,
                    stageDirectory);
                return;
            }

            if (entry.HadPreviousFile)
            {
                if (File.Exists(backupPath))
                {
                    committed.Add(new CommittedFile(targetPath, backupPath, HadPreviousFile: true));
                    continue;
                }

                if (File.Exists(stagedPath) && File.Exists(targetPath))
                {
                    // This entry was never swapped; both the staged new file and previous target
                    // are still present.
                    continue;
                }
            }
            else
            {
                if (!File.Exists(stagedPath) && File.Exists(targetPath))
                {
                    committed.Add(new CommittedFile(targetPath, BackupPath: null, HadPreviousFile: false));
                    continue;
                }

                if (File.Exists(stagedPath) && !File.Exists(targetPath))
                {
                    continue;
                }
            }

            _log.LogCritical(
                "Interrupted finalisation for {Id} is ambiguous at {FileName}; preserving {StageDirectory}",
                record.Id,
                entry.FileName,
                stageDirectory);
            return;
        }

        if (TryRollbackCommittedFiles(committed, stageDirectory))
        {
            if (RetireFinalizeStageDirectory(stageDirectory))
            {
                _log.LogWarning(
                    "Rolled back interrupted capture generation {Id} to its previous complete files",
                    record.Id);
            }
            else
            {
                _log.LogError(
                    "Capture {Id} files were rolled back, but its transaction directory could not be retired",
                    record.Id);
            }
        }
    }

    private static bool IsCommittedGenerationComplete(
        FinalizeJournal journal,
        string targetDirectory,
        string stageDirectory)
    {
        foreach (FinalizeJournalEntry entry in journal.Files)
        {
            string stagedPath = Path.Combine(stageDirectory, entry.FileName);
            string targetPath = Path.Combine(targetDirectory, entry.FileName);
            bool complete = entry.DeleteTarget
                ? !File.Exists(targetPath)
                : !File.Exists(stagedPath) && File.Exists(targetPath);
            if (!complete)
            {
                return false;
            }
        }

        return true;
    }

    private FinalizeJournal? TryReadJournal(Guid expectedRecordId, string stageDirectory)
    {
        string path = Path.Combine(stageDirectory, FinalizeJournalFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            FinalizeJournal? journal = JsonSerializer.Deserialize<FinalizeJournal>(File.ReadAllText(path));
            if (journal is null
                || journal.RecordId != expectedRecordId
                || journal.Files.Count == 0
                || journal.Files.Any(entry => !IsSafeFinalizeFileName(entry.FileName))
                || journal.Files.Any(entry => entry.DeleteTarget
                                              && (!entry.HadPreviousFile
                                                  || !CaptureFileNames.IsSafeAssetFileName(entry.FileName)))
                || journal.Files.Select(entry => entry.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                   != journal.Files.Count)
            {
                return null;
            }

            return journal;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or NotSupportedException)
        {
            _log.LogError(ex, "Could not read finalisation journal {JournalPath}", path);
            return null;
        }
    }

    private static bool IsFinalizeStageDirectory(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = ".stage-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static bool IsRetiredFinalizeDirectory(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = ".cleanup-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }

    private static bool IsSafeFinalizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return false;
        }

        if (fileName is CaptureFileNames.Rendered or CaptureFileNames.Layers or CaptureFileNames.Thumbnail)
        {
            return true;
        }

        return CaptureFileNames.IsSafeAssetFileName(fileName);
    }

    private static bool TryCleanRecoveredOriginalBackups(string directory)
    {
        string[] recoverableFiles =
        [
            CaptureFileNames.Original,
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
            CaptureFileNames.OriginalPending,
        ];

        foreach (string fileName in recoverableFiles)
        {
            TryDeleteFile(Path.Combine(directory, fileName + AtomicFile.BackupSuffix));
        }

        return recoverableFiles.All(fileName =>
            !File.Exists(Path.Combine(directory, fileName + AtomicFile.BackupSuffix)));
    }

    private static void PurgeRetiredTransactionDirectories(string targetDirectory)
    {
        try
        {
            foreach (string cleanupDirectory in Directory.EnumerateDirectories(
                         targetDirectory,
                         ".cleanup-*",
                         SearchOption.TopDirectoryOnly).Where(IsRetiredFinalizeDirectory))
            {
                TryDeleteDirectory(cleanupDirectory);
            }
        }
        catch (IOException)
        {
            // A retired directory is outside the recovery protocol. A lock can delay disk
            // reclamation, but it must never make a coherent capture unavailable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Atomically removes a terminal transaction from the recovery namespace before any
    /// recursive cleanup. A crash can therefore leave either a complete .stage directory
    /// (recoverable) or a .cleanup directory (disposable), never a partially deleted protocol.
    /// </summary>
    private static bool RetireFinalizeStageDirectory(string stageDirectory)
    {
        if (!Directory.Exists(stageDirectory))
        {
            return true;
        }

        string? parent = Path.GetDirectoryName(stageDirectory);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        string cleanupDirectory = Path.Combine(parent, $".cleanup-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(stageDirectory, cleanupDirectory);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        TryDeleteDirectory(cleanupDirectory);
        return true;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct FinalizeFiles(
        string TargetDirectory,
        string StageDirectory,
        long Bytes,
        bool HasAnnotations,
        int ItemCount);

    private readonly record struct CommittedFile(
        string TargetPath,
        string? BackupPath,
        bool HadPreviousFile);

    private sealed class FinalizeJournal
    {
        public Guid RecordId { get; set; }

        public long Bytes { get; set; }

        public bool HasAnnotations { get; set; }

        public int ItemCount { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public long ContentRevision { get; set; }

        public List<FinalizeJournalEntry> Files { get; set; } = [];
    }

    private sealed class FinalizeJournalEntry
    {
        public string FileName { get; set; } = string.Empty;

        public bool HadPreviousFile { get; set; }

        public bool DeleteTarget { get; set; }
    }

    [Flags]
    private enum BlockReason
    {
        None = 0,
        PendingOriginal = 1,
        FinalizeTransaction = 2,
    }

    private sealed class EditLease(
        CapturePersistenceService owner,
        Guid recordId,
        IDisposable evictionLease) : IDisposable
    {
        private CapturePersistenceService? _owner = owner;
        private IDisposable? _evictionLease = evictionLease;

        public void Dispose()
        {
            CapturePersistenceService? current = Interlocked.Exchange(ref _owner, null);
            IDisposable? lease = Interlocked.Exchange(ref _evictionLease, null);
            if (current is not null && lease is not null)
            {
                current.ReleaseEditLease(recordId, lease);
            }
            else
            {
                lease?.Dispose();
            }
        }
    }

    /// <summary>
    /// Copies each used asset's decoded pixels into the capture directory as
    /// <c>asset-XX.png</c> and rewrites the document's references to match.
    /// </summary>
    private void CanonicalizeAssets(
        string directory,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> assetBitmaps,
        ref long bytes)
    {
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int index = 1;

        foreach (ImageAnnotation image in document.Items.OfType<ImageAnnotation>())
        {
            string sessionName = image.AssetFileName;
            if (string.IsNullOrEmpty(sessionName))
            {
                continue;
            }

            if (remap.TryGetValue(sessionName, out string? already))
            {
                // The same inserted image used twice shares one sidecar.
                image.AssetFileName = already;
                continue;
            }

            if (!assetBitmaps.TryGetValue(sessionName, out BitmapSource? bitmap))
            {
                // No decoded pixels for this asset (should not happen for a used asset);
                // leave the reference untouched so the item is not silently dropped.
                _log.LogWarning("No in-memory bitmap for asset {Asset}; sidecar not written", sessionName);
                continue;
            }

            string canonicalName =
                $"{CaptureFileNames.AssetPrefix}{index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}.png";
            index++;

            long assetBytes = ImageCodec.SavePng(bitmap, Path.Combine(directory, canonicalName));
            bytes += assetBytes;

            remap[sessionName] = canonicalName;
            image.AssetFileName = canonicalName;
        }
    }

    private long WriteThumbnail(BitmapSource source, string directory)
    {
        int longEdge = Math.Max(16, _queueSettings().ThumbnailLongEdge);
        BitmapSource thumb = ImageCodec.CreateThumbnail(source, longEdge);
        return ImageCodec.SaveJpeg(
            thumb, Path.Combine(directory, CaptureFileNames.Thumbnail), ImageCodec.ThumbnailJpegQuality);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long ByteLength(string text) =>
        System.Text.Encoding.UTF8.GetByteCount(text);
}

/// <summary>
/// Raised when an editor attempts to replace a capture generation that is no longer current.
/// Keeping the editor open is safer than silently overwriting another editor's newer result.
/// </summary>
internal sealed class CaptureGenerationConflictException(string message) : InvalidOperationException(message);
