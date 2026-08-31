using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Crash-recovery, optimistic-generation, and eviction-lease contracts around capture
/// finalisation. These tests deliberately construct the private on-disk journal format so
/// recovery is verified from a fresh service instance rather than through in-process state.
/// </summary>
public sealed class CaptureTransactionRecoveryTests
{
    private const string JournalFileName = ".finalize-journal.json";
    private const string CommitMarkerFileName = ".finalize-commit-ready";

    [Fact]
    public void Finalize_WhenAStagedSwapFails_RestoresEveryPreviousGenerationAndRecordField() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService persistence = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = persistence.PersistOriginal(
            SolidBitmap(48, 32, 0x21, 0x43, 0x65),
            1.0,
            "rollback source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        string[] generationFiles =
        [
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
        ];
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(directory, generationFiles);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);

        AnnotationDocument newDocument = AnnotatedDocument(48, 32);
        BitmapSource newRendered = SolidBitmap(48, 32, 0xF1, 0x32, 0x54);
        int swapCallbacks = 0;
        persistence.BeforeStagedFileCommit = (index, _) =>
        {
            swapCallbacks++;
            if (index == 2)
            {
                throw new InvalidOperationException("deterministic fault after two swaps");
            }
        };

        IOException failure = Assert.Throws<IOException>(() => persistence.Finalize(
            record,
            newRendered,
            newDocument,
            new Dictionary<string, BitmapSource>()));

        Assert.Contains("previous file generation was restored", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, swapCallbacks);
        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        AssertMetaEqual(previousRecord, queue.GetFilePath(record, CaptureFileNames.Meta));
        Assert.Empty(ValidStageDirectories(directory));
        Assert.False(persistence.IsBusy(record.Id));

        CaptureQueue reloaded = NewQueue(workspace.Paths, settings);
        reloaded.Load();
        CaptureRecord durable = Assert.Single(reloaded.Records);
        AssertRecordEqual(previousRecord, durable, reloaded);
    });

    [Fact]
    public void Constructor_InterruptedPartialSwap_RollsBackFromExactJournalAndRemovesStage() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(40, 28, 0x18, 0x38, 0x58),
            1.0,
            "interrupted source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        string[] generationFiles =
        [
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
        ];
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(directory, generationFiles);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);

        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        WriteGenerationFiles(stage, 40, 28, 0xD4, 0x62, 0x35);
        WriteJournal(
            stage,
            record,
            record.TotalBytes + 321,
            hasAnnotations: true,
            record.UpdatedAt.AddMinutes(1),
            generationFiles.Select(name => new JournalEntry(name, HadPreviousFile: true)).ToArray());

        // This is the exact state left by File.Replace after rendered.png was swapped but
        // before layers.json and thumb.jpg were touched.
        File.Replace(
            Path.Combine(stage, CaptureFileNames.Rendered),
            Path.Combine(directory, CaptureFileNames.Rendered),
            Path.Combine(rollback, CaptureFileNames.Rendered),
            ignoreMetadataErrors: true);

        Assert.NotEqual(
            previousFiles[CaptureFileNames.Rendered],
            File.ReadAllBytes(Path.Combine(directory, CaptureFileNames.Rendered)));
        Assert.True(File.Exists(Path.Combine(rollback, CaptureFileNames.Rendered)));

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        Assert.False(Directory.Exists(stage));
        Assert.Empty(ValidStageDirectories(directory));
        Assert.False(recovered.IsBusy(record.Id));
    });

    [Fact]
    public void Constructor_CommittedGeneration_RollsMetadataForwardAndKeepsCommittedFiles() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(44, 30, 0x12, 0x34, 0x56),
            1.25,
            "roll-forward source",
            "DISPLAY2");

        string directory = queue.GetDirectory(record);
        string[] generationFiles =
        [
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
        ];
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(directory, generationFiles);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);

        foreach ((string name, byte[] bytes) in previousFiles)
        {
            File.WriteAllBytes(Path.Combine(rollback, name), bytes);
        }

        Dictionary<string, byte[]> committedFiles = BuildGenerationBytes(44, 30, 0xD8, 0x48, 0x28);
        foreach ((string name, byte[] bytes) in committedFiles)
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }

        DateTimeOffset committedAt = record.UpdatedAt.AddMinutes(5);
        long committedRevision = checked(record.ContentRevision + 1);
        long committedBytes = new FileInfo(Path.Combine(directory, CaptureFileNames.Original)).Length
                              + generationFiles.Sum(name => new FileInfo(Path.Combine(directory, name)).Length);
        WriteJournal(
            stage,
            record,
            committedBytes,
            hasAnnotations: true,
            committedAt,
            generationFiles.Select(name => new JournalEntry(name, HadPreviousFile: true)).ToArray());
        File.WriteAllBytes(Path.Combine(stage, CommitMarkerFileName), [0x4D, 0x43, 0x46, 0x31]);

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        AssertFilesEqual(directory, committedFiles);
        Assert.True(record.HasAnnotations);
        Assert.Equal(committedAt, record.UpdatedAt);
        Assert.Equal(committedRevision, record.ContentRevision);
        Assert.Equal(committedBytes, record.TotalBytes);
        Assert.Equal(committedBytes, queue.TotalBytes);
        Assert.False(Directory.Exists(stage));
        Assert.False(recovered.IsBusy(record.Id));

        CaptureRecord meta = ReadMeta(queue.GetFilePath(record, CaptureFileNames.Meta));
        Assert.True(meta.HasAnnotations);
        Assert.Equal(committedAt, meta.UpdatedAt);
        Assert.Equal(committedRevision, meta.ContentRevision);
        Assert.Equal(committedBytes, meta.TotalBytes);

        CaptureQueue reloaded = NewQueue(workspace.Paths, settings);
        reloaded.Load();
        CaptureRecord durable = Assert.Single(reloaded.Records);
        Assert.True(durable.HasAnnotations);
        Assert.Equal(committedAt, durable.UpdatedAt);
        Assert.Equal(committedRevision, durable.ContentRevision);
        Assert.Equal(committedBytes, durable.TotalBytes);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_UnmarkedAssetTombstone_RestoresOrKeepsPreviousAsset(bool deletionWasApplied) => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(38, 24, 0x16, 0x36, 0x56),
            1.0,
            "asset rollback source",
            "DISPLAY1");

        const string assetName = "asset-01.png";
        string directory = queue.GetDirectory(record);
        string assetPath = Path.Combine(directory, assetName);
        byte[] assetBytes = ImageCodec.EncodePng(SolidBitmap(9, 7, 0xB4, 0x54, 0x24));
        File.WriteAllBytes(assetPath, assetBytes);
        queue.UpdateByteCount(record.Id, checked(record.TotalBytes + assetBytes.LongLength));
        queue.SaveRecordMeta(record);
        queue.Save();
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);

        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        WriteJournal(
            stage,
            record,
            record.TotalBytes - assetBytes.LongLength,
            hasAnnotations: false,
            record.UpdatedAt.AddMinutes(1),
            new JournalEntry(assetName, HadPreviousFile: true, DeleteTarget: true));
        if (deletionWasApplied)
        {
            File.Move(assetPath, Path.Combine(rollback, assetName));
        }

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        Assert.Equal(assetBytes, File.ReadAllBytes(assetPath));
        AssertRecordEqual(previousRecord, record, queue);
        AssertMetaEqual(previousRecord, queue.GetFilePath(record, CaptureFileNames.Meta));
        Assert.False(Directory.Exists(stage));
        Assert.False(recovered.IsBusy(record.Id));
    });

    [Fact]
    public void Constructor_MarkedAssetTombstone_RollsMetadataForwardAndKeepsAssetDeleted() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(38, 24, 0x26, 0x46, 0x66),
            1.0,
            "asset roll-forward source",
            "DISPLAY1");

        const string assetName = "asset-01.png";
        string directory = queue.GetDirectory(record);
        string assetPath = Path.Combine(directory, assetName);
        byte[] assetBytes = ImageCodec.EncodePng(SolidBitmap(9, 7, 0xC4, 0x64, 0x34));
        File.WriteAllBytes(assetPath, assetBytes);
        queue.UpdateByteCount(record.Id, checked(record.TotalBytes + assetBytes.LongLength));
        queue.SaveRecordMeta(record);
        queue.Save();

        long previousRevision = record.ContentRevision;
        long committedBytes = record.TotalBytes - assetBytes.LongLength;
        DateTimeOffset committedAt = record.UpdatedAt.AddMinutes(1);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        File.Move(assetPath, Path.Combine(rollback, assetName));
        // Terminal cleanup can remove the rollback copy before the process exits. A marked
        // tombstone is complete based on the absent target; recovery must not require backup.
        File.Delete(Path.Combine(rollback, assetName));
        WriteJournal(
            stage,
            record,
            committedBytes,
            hasAnnotations: false,
            committedAt,
            new JournalEntry(assetName, HadPreviousFile: true, DeleteTarget: true));
        File.WriteAllBytes(Path.Combine(stage, CommitMarkerFileName), [0x4D, 0x43, 0x46, 0x31]);

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        Assert.False(File.Exists(assetPath));
        Assert.False(Directory.Exists(stage));
        Assert.False(recovered.IsBusy(record.Id));
        Assert.Equal(previousRevision + 1, record.ContentRevision);
        Assert.Equal(committedAt, record.UpdatedAt);
        Assert.Equal(committedBytes, record.TotalBytes);
        Assert.Equal(committedBytes, queue.TotalBytes);

        CaptureRecord meta = ReadMeta(queue.GetFilePath(record, CaptureFileNames.Meta));
        Assert.Equal(previousRevision + 1, meta.ContentRevision);
        Assert.Equal(committedAt, meta.UpdatedAt);
        Assert.Equal(committedBytes, meta.TotalBytes);

        CaptureQueue reloaded = NewQueue(workspace.Paths, settings);
        reloaded.Load();
        CaptureRecord durable = Assert.Single(reloaded.Records);
        Assert.Equal(previousRevision + 1, durable.ContentRevision);
        Assert.Equal(committedAt, durable.UpdatedAt);
        Assert.Equal(committedBytes, durable.TotalBytes);
    });

    [Fact]
    public void Constructor_TwoCompetingStages_RefusesAutomaticChoiceAndReportsBlockedThroughIsBusy() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(36, 24, 0x24, 0x46, 0x68),
            1.0,
            "competing source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        string firstStage = CreateUnswappedStage(directory, record, 36, 24, 0xC0, 0x20, 0x40, 100);
        string secondStage = CreateUnswappedStage(directory, record, 36, 24, 0x20, 0xC0, 0x40, 200);
        Dictionary<string, byte[]> firstStageFiles = SnapshotTree(firstStage);
        Dictionary<string, byte[]> secondStageFiles = SnapshotTree(secondStage);

        CapturePersistenceService blocked = NewPersistence(queue, workspace.Paths, settings);

        Assert.True(blocked.IsBusy(record.Id));
        AssertFilesEqual(directory, previousFiles);
        AssertTreeEqual(firstStage, firstStageFiles);
        AssertTreeEqual(secondStage, secondStageFiles);
        Assert.Equal(
            new[] { firstStage, secondStage }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            ValidStageDirectories(directory));

        IOException failure = Assert.Throws<IOException>(() => blocked.Finalize(
            record,
            SolidBitmap(36, 24, 0xEE, 0xDD, 0xCC),
            AnnotatedDocument(36, 24),
            new Dictionary<string, BitmapSource>()));

        Assert.Contains("unresolved finalisation journal", failure.Message, StringComparison.Ordinal);
        Assert.True(blocked.IsBusy(record.Id));
        AssertFilesEqual(directory, previousFiles);
        AssertTreeEqual(firstStage, firstStageFiles);
        AssertTreeEqual(secondStage, secondStageFiles);
    });

    [Fact]
    public void EditSessions_RejectStalePeerButAllowOwningClipboardRetryAfterVersionAdvance() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var appSettings = new AppSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, appSettings.Queue);
        CapturePersistenceService persistence = NewPersistence(queue, workspace.Paths, appSettings.Queue);
        CaptureRecord record = persistence.PersistOriginal(
            SolidBitmap(42, 26, 0x30, 0x50, 0x70),
            1.0,
            "conflict source",
            "DISPLAY1");

        // Make the content-generation token deterministic. Metadata-only changes must not
        // invalidate either editor because they deliberately leave this revision unchanged.
        record.ContentRevision = 7;
        queue.SaveRecordMeta(record);
        queue.Save();

        var copyResults = new Queue<bool>([false, true]);
        var commit = new CaptureCommitService(
            persistence,
            () => appSettings,
            () => workspace.Paths,
            NullLogger<CaptureCommitService>.Instance,
            _ => Task.FromResult(copyResults.Dequeue()));
        using (CaptureEditSession first = commit.BeginEditSession(record))
        using (CaptureEditSession second = commit.BeginEditSession(record))
        {
            long initialVersion = first.ExpectedContentRevision;
            Assert.Equal(7, initialVersion);
            Assert.Equal(initialVersion, second.ExpectedContentRevision);
            Assert.True(commit.IsRecordBusy(record.Id));

            Assert.True(queue.TogglePin(record.Id));
            Assert.Equal(initialVersion, record.ContentRevision);

            AnnotationEditingResult copy = EditingResult(EditorCommitAction.CopyToClipboard, 42, 26);
            bool firstClose = commit.CommitAsync(record, copy, first).GetAwaiter().GetResult();

            Assert.False(firstClose);
            Assert.Equal(initialVersion + 1, first.ExpectedContentRevision);
            Assert.Equal(record.ContentRevision, first.ExpectedContentRevision);
            Assert.Equal(initialVersion, second.ExpectedContentRevision);

            CaptureGenerationConflictException conflict = Assert.Throws<CaptureGenerationConflictException>(() =>
                commit.CommitAsync(record, EditingResult(EditorCommitAction.Done, 42, 26), second)
                    .GetAwaiter()
                    .GetResult());
            Assert.Contains("changed after this editor was opened", conflict.Message, StringComparison.Ordinal);
            Assert.True(commit.IsRecordBusy(record.Id));

            bool retryClose = commit.CommitAsync(record, copy, first).GetAwaiter().GetResult();

            Assert.True(retryClose);
            Assert.Equal(initialVersion + 2, first.ExpectedContentRevision);
            Assert.Equal(record.ContentRevision, first.ExpectedContentRevision);
            Assert.Empty(copyResults);
            Assert.True(commit.IsRecordBusy(record.Id));
        }

        Assert.False(commit.IsRecordBusy(record.Id));
    });

    [Fact]
    public void Constructor_JournalLessPrecommitStage_DeletesOnlyStageAndLeavesTargetGenerationUntouched() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(30, 18, 0x13, 0x33, 0x53),
            1.0,
            "precommit source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);
        string stage = NewStageDirectory(directory);
        WriteGenerationFiles(stage, 30, 18, 0xE3, 0x43, 0x23);
        File.WriteAllText(Path.Combine(stage, "encoding-progress.tmp"), "not committed");

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        Assert.False(Directory.Exists(stage));
        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        Assert.False(recovered.IsBusy(record.Id));

        CapturePersistenceService secondPass = NewPersistence(queue, workspace.Paths, settings);
        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        Assert.False(secondPass.IsBusy(record.Id));
    });

    [Fact]
    public void Constructor_RollbackIntentWithRestoredTarget_IsIdempotentAcrossRestarts() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(31, 19, 0x15, 0x35, 0x55),
            1.0,
            "rollback intent source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        string intents = Path.Combine(stage, ".rollback-intent");
        Directory.CreateDirectory(rollback);
        Directory.CreateDirectory(intents);
        WriteJournal(
            stage,
            record,
            record.TotalBytes + 10,
            hasAnnotations: true,
            record.UpdatedAt.AddMinutes(1),
            new JournalEntry(CaptureFileNames.Rendered, HadPreviousFile: true));
        File.WriteAllBytes(
            Path.Combine(rollback, CaptureFileNames.Rendered),
            previousFiles[CaptureFileNames.Rendered]);
        File.WriteAllBytes(
            Path.Combine(intents, CaptureFileNames.Rendered + ".intent"),
            [0x4D, 0x43, 0x52, 0x31]);

        // The previous target has already been restored, but the durable rollback intent and
        // non-consuming backup remain because the process stopped before stage cleanup.
        Assert.Equal(
            previousFiles[CaptureFileNames.Rendered],
            File.ReadAllBytes(Path.Combine(directory, CaptureFileNames.Rendered)));

        CapturePersistenceService firstPass = NewPersistence(queue, workspace.Paths, settings);

        Assert.False(Directory.Exists(stage));
        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        Assert.False(firstPass.IsBusy(record.Id));

        CapturePersistenceService secondPass = NewPersistence(queue, workspace.Paths, settings);
        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        Assert.False(secondPass.IsBusy(record.Id));
    });

    [Fact]
    public void Constructor_RollbackIntentForNewFileAlreadyMissing_IsIdempotent() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(30, 18, 0x17, 0x37, 0x57),
            1.0,
            "new-file rollback intent",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);
        const string newAsset = "asset-01.png";
        string stage = NewStageDirectory(directory);
        string intents = Path.Combine(stage, ".rollback-intent");
        Directory.CreateDirectory(intents);
        WriteJournal(
            stage,
            record,
            record.TotalBytes + 10,
            hasAnnotations: true,
            record.UpdatedAt.AddMinutes(1),
            new JournalEntry(newAsset, HadPreviousFile: false));
        File.WriteAllBytes(
            Path.Combine(intents, newAsset + ".intent"),
            [0x4D, 0x43, 0x52, 0x31]);

        Assert.False(File.Exists(Path.Combine(directory, newAsset)));
        Assert.False(File.Exists(Path.Combine(stage, newAsset)));

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        Assert.False(Directory.Exists(stage));
        Assert.False(File.Exists(Path.Combine(directory, newAsset)));
        AssertRecordEqual(previousRecord, record, queue);
        Assert.False(recovered.IsBusy(record.Id));
    });

    [Fact]
    public void Constructor_LegacyTerminalStageWithoutMarker_NeverRollsBackAnAlreadyIndexedRevision() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(46, 29, 0x17, 0x37, 0x57),
            1.0,
            "legacy terminal source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        string[] generationFiles =
        [
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
        ];
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(directory, generationFiles);
        Dictionary<string, byte[]> committedFiles = BuildGenerationBytes(46, 29, 0xD7, 0x47, 0x27);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        foreach ((string name, byte[] bytes) in previousFiles)
        {
            File.WriteAllBytes(Path.Combine(rollback, name), bytes);
        }

        foreach ((string name, byte[] bytes) in committedFiles)
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }

        DateTimeOffset committedAt = record.UpdatedAt.AddMinutes(3);
        long committedBytes = new FileInfo(Path.Combine(directory, CaptureFileNames.Original)).Length
                              + generationFiles.Sum(name => new FileInfo(Path.Combine(directory, name)).Length);
        WriteJournal(
            stage,
            record,
            committedBytes,
            hasAnnotations: true,
            committedAt,
            generationFiles.Select(name => new JournalEntry(name, HadPreviousFile: true)).ToArray());

        // Legacy cleanup could remove the marker before its journal and rollback backups. The
        // durable index/meta revision proves that these target bytes are already committed.
        record.HasAnnotations = true;
        record.ContentRevision = checked(record.ContentRevision + 1);
        record.UpdatedAt = committedAt;
        queue.UpdateByteCount(record.Id, committedBytes);
        record.UpdatedAt = committedAt;
        queue.SaveRecordMeta(record);
        queue.Save();
        RecordSnapshot committedRecord = SnapshotRecord(record, queue);

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        AssertFilesEqual(directory, committedFiles);
        AssertRecordEqual(committedRecord, record, queue);
        AssertMetaEqual(committedRecord, queue.GetFilePath(record, CaptureFileNames.Meta));
        Assert.False(Directory.Exists(stage));
        Assert.Empty(ValidStageDirectories(directory));
        Assert.False(recovered.IsBusy(record.Id));
    });

    [Fact]
    public void Constructor_LegacyZeroRevisionJournalWithoutMarker_RemainsBlockedAndUntouched() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(45, 28, 0x18, 0x38, 0x58),
            1.0,
            "legacy zero-revision source",
            "DISPLAY1");
        Assert.Equal(0, record.ContentRevision);

        string directory = queue.GetDirectory(record);
        string[] generationFiles =
        [
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
        ];
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(directory, generationFiles);
        Dictionary<string, byte[]> swappedTargets = BuildGenerationBytes(45, 28, 0xD8, 0x48, 0x28);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        foreach ((string name, byte[] bytes) in previousFiles)
        {
            File.WriteAllBytes(Path.Combine(rollback, name), bytes);
        }

        foreach ((string name, byte[] bytes) in swappedTargets)
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }

        // Journals written before content generations were introduced deserialize a missing
        // ContentRevision as zero. With both revisions at zero there is no terminal-commit
        // proof, even though all target swaps and rollback backups happen to be present.
        string legacyJournal = JsonSerializer.Serialize(new
        {
            RecordId = record.Id,
            Bytes = record.TotalBytes + 77,
            HasAnnotations = true,
            ItemCount = 1,
            UpdatedAt = record.UpdatedAt.AddMinutes(1),
            Files = generationFiles.Select(name => new
            {
                FileName = name,
                HadPreviousFile = true,
                DeleteTarget = false,
            }).ToArray(),
        });
        File.WriteAllText(Path.Combine(stage, JournalFileName), legacyJournal);
        Dictionary<string, byte[]> recoveryFiles = SnapshotTree(stage);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);

        CapturePersistenceService blocked = NewPersistence(queue, workspace.Paths, settings);

        Assert.True(blocked.IsBusy(record.Id));
        AssertFilesEqual(directory, swappedTargets);
        AssertRecordEqual(previousRecord, record, queue);
        AssertTreeEqual(stage, recoveryFiles);
        Assert.Equal([stage], ValidStageDirectories(directory));
    });

    [Fact]
    public void Constructor_MarkerWithoutJournal_PreservesTheAmbiguousStageAndTargetBytes() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(35, 21, 0x19, 0x39, 0x59),
            1.0,
            "marker-only source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> targets = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        File.WriteAllBytes(Path.Combine(rollback, CaptureFileNames.Rendered), targets[CaptureFileNames.Rendered]);
        File.WriteAllBytes(Path.Combine(stage, CommitMarkerFileName), [0x4D, 0x43, 0x46, 0x31]);
        Dictionary<string, byte[]> recoveryFiles = SnapshotTree(stage);

        CapturePersistenceService blocked = NewPersistence(queue, workspace.Paths, settings);

        Assert.True(blocked.IsBusy(record.Id));
        AssertFilesEqual(directory, targets);
        AssertRecordEqual(previousRecord, record, queue);
        AssertTreeEqual(stage, recoveryFiles);
        Assert.Equal([stage], ValidStageDirectories(directory));
    });

    [Fact]
    public void Constructor_PartiallyStrippedRollbackTerminalState_NeverCorruptsTargets() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(33, 20, 0x1B, 0x3B, 0x5B),
            1.0,
            "partial terminal source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);
        string stage = NewStageDirectory(directory);
        string rollback = Path.Combine(stage, ".rollback");
        string intents = Path.Combine(stage, ".rollback-intent");
        Directory.CreateDirectory(rollback);
        Directory.CreateDirectory(intents);
        WriteJournal(
            stage,
            record,
            record.TotalBytes + 99,
            hasAnnotations: true,
            record.UpdatedAt.AddMinutes(1),
            new JournalEntry(CaptureFileNames.Rendered, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Layers, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Thumbnail, HadPreviousFile: true));

        // Rollback restored all targets, then terminal cleanup stripped only some artifacts.
        // The remaining intent/backup is enough to be recognizable, but not enough to infer a
        // new generation. Recovery may retire the terminal stage or preserve it as blocked.
        File.WriteAllBytes(
            Path.Combine(rollback, CaptureFileNames.Rendered),
            previousFiles[CaptureFileNames.Rendered]);
        File.WriteAllBytes(
            Path.Combine(intents, CaptureFileNames.Rendered + ".intent"),
            [0x4D, 0x43, 0x52, 0x31]);

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        AssertFilesEqual(directory, previousFiles);
        AssertRecordEqual(previousRecord, record, queue);
        bool stageWasPreserved = Directory.Exists(stage);
        Assert.Equal(stageWasPreserved, recovered.IsBusy(record.Id));
        if (stageWasPreserved)
        {
            Assert.True(File.Exists(Path.Combine(stage, JournalFileName)));
        }
        else
        {
            Assert.Empty(ValidStageDirectories(directory));
        }
    });

    [Fact]
    public void Constructor_RetiredCleanupDirectory_IsPurgedWithoutBecomingATransaction() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(34, 22, 0x1D, 0x3D, 0x5D),
            1.0,
            "retired cleanup source",
            "DISPLAY1");

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> targets = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        RecordSnapshot previousRecord = SnapshotRecord(record, queue);
        string cleanup = Path.Combine(directory, $".cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(cleanup, ".rollback"));
        WriteJournal(
            cleanup,
            record,
            record.TotalBytes + 1,
            hasAnnotations: true,
            record.UpdatedAt.AddMinutes(1),
            new JournalEntry(CaptureFileNames.Rendered, HadPreviousFile: true));
        File.WriteAllBytes(
            Path.Combine(cleanup, ".rollback", CaptureFileNames.Rendered),
            targets[CaptureFileNames.Rendered]);

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        Assert.False(Directory.Exists(cleanup));
        Assert.Empty(ValidStageDirectories(directory));
        Assert.False(recovered.IsBusy(record.Id));
        AssertFilesEqual(directory, targets);
        AssertRecordEqual(previousRecord, record, queue);
    });

    [Fact]
    public void Startup_PendingOriginalMergesIntoValidIndexAndRepairsDerivedFiles() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        CaptureQueue seedQueue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService seedPersistence = NewPersistence(seedQueue, workspace.Paths, settings);
        CaptureRecord existing = seedPersistence.PersistOriginal(
            SolidBitmap(28, 17, 0x12, 0x32, 0x52),
            1.0,
            "indexed source",
            "DISPLAY1");
        seedQueue.Save();

        BitmapSource pendingOriginal = SolidBitmap(37, 23, 0xB2, 0x52, 0x32);
        var pendingRecord = new CaptureRecord
        {
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Width = 999,
            Height = 888,
            DpiScale = 1.5,
            SourceWindowTitle = "pending source",
            SourceMonitor = "DISPLAY2",
            TotalBytes = 0,
            HasAnnotations = true,
            ContentRevision = 9,
        };
        pendingRecord.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(
            pendingRecord.Id,
            pendingRecord.CreatedAt);
        string pendingDirectory = Path.Combine(workspace.Paths.CapturesRoot, pendingRecord.RelativeDirectory);
        Directory.CreateDirectory(pendingDirectory);
        File.WriteAllText(
            Path.Combine(pendingDirectory, CaptureFileNames.OriginalPending),
            JsonSerializer.Serialize(pendingRecord));
        ImageCodec.SavePng(pendingOriginal, Path.Combine(pendingDirectory, CaptureFileNames.Original));
        File.WriteAllBytes(Path.Combine(pendingDirectory, CaptureFileNames.Rendered), [0x00, 0x01, 0x02]);
        File.WriteAllText(Path.Combine(pendingDirectory, CaptureFileNames.Layers), "not-json");

        CaptureQueue startupQueue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService recovered;
        using (startupQueue.SuspendEviction())
        {
            startupQueue.Load();

            Assert.Equal(2, startupQueue.Count);
            Assert.NotNull(startupQueue.Find(existing.Id));
            CaptureRecord provisional = Assert.IsType<CaptureRecord>(startupQueue.Find(pendingRecord.Id));
            Assert.Equal(999, provisional.Width);
            Assert.True(File.Exists(Path.Combine(pendingDirectory, CaptureFileNames.OriginalPending)));

            recovered = NewPersistence(startupQueue, workspace.Paths, settings);

            CaptureRecord repaired = Assert.IsType<CaptureRecord>(startupQueue.Find(pendingRecord.Id));
            Assert.Equal(37, repaired.Width);
            Assert.Equal(23, repaired.Height);
            Assert.False(repaired.HasAnnotations);
            Assert.Equal(0, repaired.ContentRevision);
            Assert.True(repaired.TotalBytes > 0);
            Assert.False(recovered.IsBusy(repaired.Id));
        }

        Assert.Equal(2, startupQueue.Count);
        Assert.False(File.Exists(Path.Combine(pendingDirectory, CaptureFileNames.OriginalPending)));
        Assert.True(File.Exists(Path.Combine(pendingDirectory, CaptureFileNames.Meta)));
        Assert.True(File.Exists(Path.Combine(pendingDirectory, CaptureFileNames.Thumbnail)));
        BitmapSource rendered = Assert.IsAssignableFrom<BitmapSource>(
            ImageCodec.TryLoad(Path.Combine(pendingDirectory, CaptureFileNames.Rendered)));
        Assert.Equal(37, rendered.PixelWidth);
        Assert.Equal(23, rendered.PixelHeight);
        AnnotationDocument layers = Assert.IsType<AnnotationDocument>(AnnotationDocument.TryFromJson(
            File.ReadAllText(Path.Combine(pendingDirectory, CaptureFileNames.Layers))));
        Assert.True(layers.IsEmpty);
        Assert.Equal(37, layers.CanvasWidth);
        Assert.Equal(23, layers.CanvasHeight);

        CaptureQueue reloaded = NewQueue(workspace.Paths, settings);
        reloaded.Load();
        Assert.Equal(2, reloaded.Count);
        Assert.NotNull(reloaded.Find(existing.Id));
        CaptureRecord durable = Assert.IsType<CaptureRecord>(reloaded.Find(pendingRecord.Id));
        Assert.Equal(37, durable.Width);
        Assert.Equal(23, durable.Height);
        Assert.Equal(0, durable.ContentRevision);
        Assert.False(durable.HasAnnotations);
    });

    [Fact]
    public void Startup_PendingMarkerWrittenBeforeOriginal_RemainsUnindexedForLaterRecovery() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        CaptureQueue seedQueue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService seedPersistence = NewPersistence(seedQueue, workspace.Paths, settings);
        CaptureRecord existing = seedPersistence.PersistOriginal(
            SolidBitmap(24, 16, 0x21, 0x41, 0x61),
            1.0,
            "existing source",
            "DISPLAY1");
        seedQueue.Save();

        var pending = new CaptureRecord
        {
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Width = 25,
            Height = 17,
            DpiScale = 1.0,
        };
        pending.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(pending.Id, pending.CreatedAt);
        string pendingDirectory = Path.Combine(workspace.Paths.CapturesRoot, pending.RelativeDirectory);
        Directory.CreateDirectory(pendingDirectory);
        string marker = Path.Combine(pendingDirectory, CaptureFileNames.OriginalPending);
        File.WriteAllText(marker, JsonSerializer.Serialize(pending));

        CaptureQueue startupQueue = NewQueue(workspace.Paths, settings);
        startupQueue.Load();
        CapturePersistenceService persistence = NewPersistence(startupQueue, workspace.Paths, settings);

        Assert.Single(startupQueue.Records);
        Assert.NotNull(startupQueue.Find(existing.Id));
        Assert.Null(startupQueue.Find(pending.Id));
        Assert.False(persistence.IsBusy(pending.Id));
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(pendingDirectory, CaptureFileNames.Meta)));
    });

    [Fact]
    public void Startup_AlreadyIndexedPendingOriginal_RepairsExactlyOnceAndIsIdempotent() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(39, 25, 0x23, 0x43, 0x63),
            1.25,
            "indexed pending source",
            "DISPLAY2");
        string directory = queue.GetDirectory(record);
        string marker = Path.Combine(directory, CaptureFileNames.OriginalPending);
        File.WriteAllText(marker, JsonSerializer.Serialize(record));
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Rendered), [0x01, 0x02, 0x03]);
        File.WriteAllText(Path.Combine(directory, CaptureFileNames.Layers), "broken layers");
        queue.Save();

        CaptureQueue startupQueue = NewQueue(workspace.Paths, settings);
        startupQueue.Load();
        CapturePersistenceService firstPass = NewPersistence(startupQueue, workspace.Paths, settings);
        CaptureRecord repaired = Assert.IsType<CaptureRecord>(startupQueue.Find(record.Id));
        Dictionary<string, byte[]> repairedFiles = SnapshotFiles(
            directory,
            [CaptureFileNames.Original, CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        RecordSnapshot repairedRecord = SnapshotRecord(repaired, startupQueue);

        Assert.Single(startupQueue.Records);
        Assert.False(File.Exists(marker));
        Assert.False(firstPass.IsBusy(record.Id));
        Assert.Equal(39, repaired.Width);
        Assert.Equal(25, repaired.Height);
        Assert.Equal(0, repaired.ContentRevision);
        Assert.False(repaired.HasAnnotations);
        AssertNoMediaBackups(directory);

        CapturePersistenceService secondPass = NewPersistence(startupQueue, workspace.Paths, settings);

        Assert.Single(startupQueue.Records);
        AssertFilesEqual(directory, repairedFiles);
        AssertRecordEqual(repairedRecord, repaired, startupQueue);
        Assert.False(secondPass.IsBusy(record.Id));
        AssertNoMediaBackups(directory);
    });

    [Fact]
    public void Startup_CorruptPendingOriginal_IsBusyAndCannotBeRemovedOrEvicted() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var seedLimits = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        CaptureQueue seedQueue = NewQueue(workspace.Paths, seedLimits);
        CapturePersistenceService seedPersistence = NewPersistence(seedQueue, workspace.Paths, seedLimits);
        CaptureRecord olderHealthy = seedPersistence.PersistOriginal(
            SolidBitmap(26, 18, 0x25, 0x45, 0x65),
            1.0,
            "older healthy",
            "DISPLAY1");
        CaptureRecord newerHealthy = seedPersistence.PersistOriginal(
            SolidBitmap(26, 18, 0x27, 0x47, 0x67),
            1.0,
            "newer healthy",
            "DISPLAY1");
        DateTimeOffset baseline = DateTimeOffset.UtcNow.AddHours(-4);
        olderHealthy.CreatedAt = baseline.AddHours(1);
        olderHealthy.UpdatedAt = baseline.AddHours(1);
        newerHealthy.CreatedAt = baseline.AddHours(2);
        newerHealthy.UpdatedAt = baseline.AddHours(2);
        seedQueue.SaveRecordMeta(olderHealthy);
        seedQueue.SaveRecordMeta(newerHealthy);
        seedQueue.Save();

        var pending = new CaptureRecord
        {
            CreatedAt = baseline,
            UpdatedAt = baseline,
            Width = 30,
            Height = 20,
            TotalBytes = 6,
        };
        pending.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(pending.Id, pending.CreatedAt);
        string pendingDirectory = Path.Combine(workspace.Paths.CapturesRoot, pending.RelativeDirectory);
        Directory.CreateDirectory(pendingDirectory);
        string marker = Path.Combine(pendingDirectory, CaptureFileNames.OriginalPending);
        string original = Path.Combine(pendingDirectory, CaptureFileNames.Original);
        byte[] corruptBytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
        File.WriteAllText(marker, JsonSerializer.Serialize(pending));
        File.WriteAllBytes(original, corruptBytes);

        var startupLimits = new QueueSettings { MaxItems = 1, MaxBytes = long.MaxValue };
        CaptureQueue startupQueue = NewQueue(workspace.Paths, startupLimits);
        var evicted = new List<Guid>();
        startupQueue.Evicted += (_, args) =>
        {
            evicted.Add(args.Record.Id);
            DeleteDirectory(startupQueue.GetDirectory(args.Record));
        };

        CapturePersistenceService blocked;
        using (startupQueue.SuspendEviction())
        {
            startupQueue.Load();
            Assert.Equal(3, startupQueue.Count);
            Assert.NotNull(startupQueue.Find(pending.Id));

            blocked = NewPersistence(startupQueue, workspace.Paths, startupLimits);

            Assert.True(blocked.IsBusy(pending.Id));
            Assert.False(startupQueue.Remove(pending.Id));
            Assert.Empty(evicted);
        }

        Assert.True(blocked.IsBusy(pending.Id));
        Assert.NotNull(startupQueue.Find(pending.Id));
        Assert.False(startupQueue.Remove(pending.Id));
        Assert.Equal(corruptBytes, File.ReadAllBytes(original));
        Assert.True(File.Exists(marker));
        Assert.Empty(ValidStageDirectories(pendingDirectory));
        Assert.DoesNotContain(pending.Id, evicted);
        Assert.Contains(olderHealthy.Id, evicted);
        Assert.Equal(2, startupQueue.Count);

        startupQueue.UpdateLimits(startupLimits);
        Assert.NotNull(startupQueue.Find(pending.Id));
        Assert.True(blocked.IsBusy(pending.Id));
        Assert.Equal(corruptBytes, File.ReadAllBytes(original));
    });

    [Fact]
    public void Startup_SuccessfulPendingRecovery_LeavesNoMediaBackupsOrUntrackedCaptureBytes() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        BitmapSource original = SolidBitmap(128, 96, 0x29, 0x49, 0x69);
        var pending = new CaptureRecord
        {
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Width = original.PixelWidth,
            Height = original.PixelHeight,
            TotalBytes = 0,
        };
        pending.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(pending.Id, pending.CreatedAt);
        string directory = Path.Combine(workspace.Paths.CapturesRoot, pending.RelativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, CaptureFileNames.OriginalPending),
            JsonSerializer.Serialize(pending));
        ImageCodec.SavePng(original, Path.Combine(directory, CaptureFileNames.Original));
        ImageCodec.SavePng(
            SolidBitmap(128, 96, 0xA9, 0x59, 0x39),
            Path.Combine(directory, CaptureFileNames.Rendered));
        File.WriteAllText(Path.Combine(directory, CaptureFileNames.Layers), "stale layers");
        ImageCodec.SaveJpeg(
            SolidBitmap(128, 96, 0x99, 0x69, 0x49),
            Path.Combine(directory, CaptureFileNames.Thumbnail),
            quality: 85);

        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        queue.Load();
        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = Assert.IsType<CaptureRecord>(queue.Find(pending.Id));

        Assert.False(recovered.IsBusy(record.Id));
        Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.OriginalPending)));
        AssertNoMediaBackups(directory);
        Assert.Empty(ValidStageDirectories(directory));

        string[] sidecars =
        [
            CaptureFileNames.Meta,
            CaptureFileNames.Meta + AtomicFile.BackupSuffix,
        ];
        long sidecarBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => sidecars.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
        long actualCaptureTreeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

        Assert.Equal(record.TotalBytes + sidecarBytes, actualCaptureTreeBytes);
        Assert.Equal(record.TotalBytes, queue.TotalBytes);
    });

    [Fact]
    public void StartupSuspension_KeepsRollbackFilesUntilRecoveryThenEnforcesCapacity() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var seedLimits = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        CaptureQueue seedQueue = NewQueue(workspace.Paths, seedLimits);
        CapturePersistenceService seedPersistence = NewPersistence(seedQueue, workspace.Paths, seedLimits);
        CaptureRecord oldest = seedPersistence.PersistOriginal(
            SolidBitmap(32, 20, 0x11, 0x31, 0x51),
            1.0,
            "old capacity victim",
            "DISPLAY1");
        CaptureRecord recovering = seedPersistence.PersistOriginal(
            SolidBitmap(32, 20, 0x22, 0x42, 0x62),
            1.0,
            "new recovering capture",
            "DISPLAY1");

        DateTimeOffset baseline = DateTimeOffset.UtcNow.AddHours(-3);
        oldest.CreatedAt = baseline;
        oldest.UpdatedAt = baseline;
        recovering.CreatedAt = baseline.AddHours(1);
        recovering.UpdatedAt = baseline.AddHours(1);
        seedQueue.SaveRecordMeta(oldest);
        seedQueue.SaveRecordMeta(recovering);
        seedQueue.Save();

        string recoveringDirectory = seedQueue.GetDirectory(recovering);
        Dictionary<string, byte[]> previousFiles = SnapshotFiles(
            recoveringDirectory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        string stage = NewStageDirectory(recoveringDirectory);
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        WriteGenerationFiles(stage, 32, 20, 0xD2, 0x42, 0x32);
        WriteJournal(
            stage,
            recovering,
            recovering.TotalBytes + 100,
            hasAnnotations: true,
            recovering.UpdatedAt.AddMinutes(1),
            new JournalEntry(CaptureFileNames.Rendered, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Layers, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Thumbnail, HadPreviousFile: true));
        File.Replace(
            Path.Combine(stage, CaptureFileNames.Rendered),
            Path.Combine(recoveringDirectory, CaptureFileNames.Rendered),
            Path.Combine(rollback, CaptureFileNames.Rendered),
            ignoreMetadataErrors: true);

        var startupLimits = new QueueSettings { MaxItems = 1, MaxBytes = long.MaxValue };
        CaptureQueue startupQueue = NewQueue(workspace.Paths, startupLimits);
        var evicted = new List<Guid>();
        startupQueue.Evicted += (_, args) =>
        {
            evicted.Add(args.Record.Id);
            DeleteDirectory(startupQueue.GetDirectory(args.Record));
        };

        CapturePersistenceService recovered;
        using (startupQueue.SuspendEviction())
        {
            startupQueue.Load();

            Assert.Equal(2, startupQueue.Count);
            Assert.Empty(evicted);
            Assert.True(Directory.Exists(stage));
            Assert.True(File.Exists(Path.Combine(rollback, CaptureFileNames.Rendered)));
            Assert.NotNull(startupQueue.Find(recovering.Id));
            Assert.NotNull(startupQueue.Find(oldest.Id));

            recovered = NewPersistence(startupQueue, workspace.Paths, startupLimits);

            Assert.Equal(2, startupQueue.Count);
            Assert.Empty(evicted);
            AssertFilesEqual(recoveringDirectory, previousFiles);
            Assert.False(Directory.Exists(stage));
            Assert.False(recovered.IsBusy(recovering.Id));
        }

        CaptureRecord retained = Assert.Single(startupQueue.Records);
        Assert.Equal(recovering.Id, retained.Id);
        Assert.Equal([oldest.Id], evicted);
        Assert.True(Directory.Exists(recoveringDirectory));
        Assert.False(Directory.Exists(seedQueue.GetDirectory(oldest)));
        AssertFilesEqual(recoveringDirectory, previousFiles);
    });

    [Fact]
    public void StartupSuspension_MultipleStagesAcquireLeaseBeforeCapacityCanDeleteRecoveryData() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var seedLimits = new QueueSettings { MaxItems = 20, MaxBytes = long.MaxValue };
        CaptureQueue seedQueue = NewQueue(workspace.Paths, seedLimits);
        CapturePersistenceService seedPersistence = NewPersistence(seedQueue, workspace.Paths, seedLimits);
        CaptureRecord blockedRecord = seedPersistence.PersistOriginal(
            SolidBitmap(34, 22, 0x14, 0x34, 0x54),
            1.0,
            "blocked recovery capture",
            "DISPLAY1");
        CaptureRecord olderHealthy = seedPersistence.PersistOriginal(
            SolidBitmap(34, 22, 0x24, 0x44, 0x64),
            1.0,
            "older healthy capture",
            "DISPLAY1");
        CaptureRecord newerHealthy = seedPersistence.PersistOriginal(
            SolidBitmap(34, 22, 0x34, 0x54, 0x74),
            1.0,
            "newer healthy capture",
            "DISPLAY1");

        DateTimeOffset baseline = DateTimeOffset.UtcNow.AddHours(-4);
        blockedRecord.CreatedAt = baseline;
        blockedRecord.UpdatedAt = baseline;
        olderHealthy.CreatedAt = baseline.AddHours(1);
        olderHealthy.UpdatedAt = baseline.AddHours(1);
        newerHealthy.CreatedAt = baseline.AddHours(2);
        newerHealthy.UpdatedAt = baseline.AddHours(2);
        seedQueue.SaveRecordMeta(blockedRecord);
        seedQueue.SaveRecordMeta(olderHealthy);
        seedQueue.SaveRecordMeta(newerHealthy);
        seedQueue.Save();

        string blockedDirectory = seedQueue.GetDirectory(blockedRecord);
        string firstStage = NewStageDirectory(blockedDirectory);
        string rollback = Path.Combine(firstStage, ".rollback");
        Directory.CreateDirectory(rollback);
        WriteGenerationFiles(firstStage, 34, 22, 0xC4, 0x34, 0x24);
        WriteJournal(
            firstStage,
            blockedRecord,
            blockedRecord.TotalBytes + 100,
            hasAnnotations: true,
            blockedRecord.UpdatedAt.AddMinutes(1),
            new JournalEntry(CaptureFileNames.Rendered, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Layers, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Thumbnail, HadPreviousFile: true));
        File.Replace(
            Path.Combine(firstStage, CaptureFileNames.Rendered),
            Path.Combine(blockedDirectory, CaptureFileNames.Rendered),
            Path.Combine(rollback, CaptureFileNames.Rendered),
            ignoreMetadataErrors: true);
        string secondStage = CreateUnswappedStage(
            blockedDirectory,
            blockedRecord,
            34,
            22,
            0x24,
            0xC4,
            0x34,
            200);
        Dictionary<string, byte[]> targetState = SnapshotFiles(
            blockedDirectory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        Dictionary<string, byte[]> firstStageState = SnapshotTree(firstStage);
        Dictionary<string, byte[]> secondStageState = SnapshotTree(secondStage);

        var startupLimits = new QueueSettings { MaxItems = 2, MaxBytes = long.MaxValue };
        CaptureQueue startupQueue = NewQueue(workspace.Paths, startupLimits);
        var evicted = new List<Guid>();
        startupQueue.Evicted += (_, args) =>
        {
            evicted.Add(args.Record.Id);
            DeleteDirectory(startupQueue.GetDirectory(args.Record));
        };

        CapturePersistenceService blocked;
        using (startupQueue.SuspendEviction())
        {
            startupQueue.Load();
            Assert.Equal(3, startupQueue.Count);
            Assert.Empty(evicted);

            blocked = NewPersistence(startupQueue, workspace.Paths, startupLimits);

            Assert.True(blocked.IsBusy(blockedRecord.Id));
            Assert.Equal(3, startupQueue.Count);
            Assert.Empty(evicted);
            AssertTreeEqual(firstStage, firstStageState);
            AssertTreeEqual(secondStage, secondStageState);
            AssertFilesEqual(blockedDirectory, targetState);
        }

        Assert.Equal(2, startupQueue.Count);
        Assert.Contains(startupQueue.Records, record => record.Id == blockedRecord.Id);
        Assert.Contains(startupQueue.Records, record => record.Id == newerHealthy.Id);
        Assert.Equal([olderHealthy.Id], evicted);
        Assert.True(blocked.IsBusy(blockedRecord.Id));
        Assert.True(Directory.Exists(blockedDirectory));
        AssertTreeEqual(firstStage, firstStageState);
        AssertTreeEqual(secondStage, secondStageState);
        AssertFilesEqual(blockedDirectory, targetState);
        Assert.False(Directory.Exists(seedQueue.GetDirectory(olderHealthy)));
        Assert.True(Directory.Exists(seedQueue.GetDirectory(newerHealthy)));
    });

    [Fact]
    public void EvictionLease_PreservesActiveRecordWhileByteUpdateEnforcesLimit()
    {
        using var workspace = new TestWorkspace();
        var limits = new QueueSettings { MaxItems = 20, MaxBytes = 100 };
        CaptureQueue queue = NewQueue(workspace.Paths, limits);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CaptureRecord active = AddQueueRecord(queue, workspace.Paths, now.AddMinutes(-2), 20);
        CaptureRecord olderDisposable = AddQueueRecord(queue, workspace.Paths, now.AddHours(1), 40);
        CaptureRecord newerDisposable = AddQueueRecord(queue, workspace.Paths, now.AddHours(2), 40);
        var evicted = new List<Guid>();
        queue.Evicted += (_, args) => evicted.Add(args.Record.Id);

        using (queue.AcquireEvictionLease(active.Id))
        {
            queue.UpdateByteCount(active.Id, 60);

            Assert.Same(active, queue.Find(active.Id));
            Assert.Null(queue.Find(olderDisposable.Id));
            Assert.Same(newerDisposable, queue.Find(newerDisposable.Id));
            Assert.Equal([olderDisposable.Id], evicted);
            Assert.Equal(60, active.TotalBytes);
            Assert.Equal(100, queue.TotalBytes);

            // A second explicit enforcement pass must still respect the same active lease.
            queue.UpdateLimits(new QueueSettings { MaxItems = 20, MaxBytes = 100 });
            Assert.Contains(active, queue.Records);
            Assert.Contains(newerDisposable, queue.Records);
        }

        Assert.Contains(active, queue.Records);
        Assert.Contains(newerDisposable, queue.Records);
    }

    [Fact]
    public void PersistOriginal_MetadataFailureKeepsPendingMarkerAndBlockedEvictionLease() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService persistence = NewPersistence(queue, workspace.Paths, settings);
        persistence.BeforeRecordMetadataCommit = id =>
        {
            CaptureRecord pending = Assert.IsType<CaptureRecord>(queue.Find(id));
            Directory.CreateDirectory(queue.GetFilePath(pending, CaptureFileNames.Meta));
        };

        Assert.Throws<IOException>(() => persistence.PersistOriginal(
            SolidBitmap(31, 19, 0x21, 0x41, 0x61),
            1.0,
            "strict original meta",
            "DISPLAY1"));

        CaptureRecord record = Assert.Single(queue.Records);
        string directory = queue.GetDirectory(record);
        Assert.True(File.Exists(Path.Combine(directory, CaptureFileNames.OriginalPending)));
        Assert.True(persistence.IsBusy(record.Id));
        Assert.False(queue.Remove(record.Id));
        Assert.True(File.Exists(Path.Combine(directory, CaptureFileNames.Original)));
    });

    [Fact]
    public void PersistOriginal_IndexFailureKeepsPendingMarkerAndBlockedEvictionLease() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService persistence = NewPersistence(queue, workspace.Paths, settings);
        persistence.BeforeRecordMetadataCommit = _ =>
            Directory.CreateDirectory(workspace.Paths.IndexFile);

        Assert.Throws<IOException>(() => persistence.PersistOriginal(
            SolidBitmap(32, 20, 0x22, 0x42, 0x62),
            1.0,
            "strict original index",
            "DISPLAY1"));

        CaptureRecord record = Assert.Single(queue.Records);
        Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.OriginalPending)));
        Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Meta)));
        Assert.True(persistence.IsBusy(record.Id));
        Assert.False(queue.Remove(record.Id));
    });

    [Fact]
    public void Finalize_MetadataFailureKeepsMarkedJournalUntilRestartRepairsBothIndexes() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService persistence = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = persistence.PersistOriginal(
            SolidBitmap(38, 24, 0x24, 0x44, 0x64),
            1.0,
            "strict finalize meta",
            "DISPLAY1");
        string metaPath = queue.GetFilePath(record, CaptureFileNames.Meta);
        persistence.BeforeRecordMetadataCommit = id =>
        {
            if (id != record.Id || Directory.Exists(metaPath))
            {
                return;
            }

            File.Delete(metaPath);
            Directory.CreateDirectory(metaPath);
        };

        persistence.Finalize(
            record,
            SolidBitmap(38, 24, 0xC4, 0x54, 0x34),
            AnnotatedDocument(38, 24),
            new Dictionary<string, BitmapSource>());

        string directory = queue.GetDirectory(record);
        string stage = Assert.Single(ValidStageDirectories(directory));
        Assert.True(File.Exists(Path.Combine(stage, CommitMarkerFileName)));
        Assert.True(persistence.IsBusy(record.Id));
        Assert.Equal(1, record.ContentRevision);

        var startupQueue = NewQueue(workspace.Paths, settings);
        startupQueue.Load();
        CaptureRecord startupRecord = Assert.Single(startupQueue.Records);
        Assert.Equal(0, startupRecord.ContentRevision);

        CapturePersistenceService stillBlocked = NewPersistence(startupQueue, workspace.Paths, settings);
        Assert.True(stillBlocked.IsBusy(startupRecord.Id));
        Assert.Single(ValidStageDirectories(startupQueue.GetDirectory(startupRecord)));

        Directory.Delete(metaPath);
        var repairQueue = NewQueue(workspace.Paths, settings);
        repairQueue.Load();
        CaptureRecord repairedRecord = Assert.Single(repairQueue.Records);
        CapturePersistenceService recovered = NewPersistence(repairQueue, workspace.Paths, settings);

        Assert.Equal(1, repairedRecord.ContentRevision);
        Assert.False(recovered.IsBusy(repairedRecord.Id));
        Assert.Empty(ValidStageDirectories(repairQueue.GetDirectory(repairedRecord)));
        Assert.True(File.Exists(repairQueue.GetFilePath(repairedRecord, CaptureFileNames.Meta)));
        var secondRestart = NewQueue(workspace.Paths, settings);
        secondRestart.Load();
        Assert.Equal(1, Assert.Single(secondRestart.Records).ContentRevision);
    });

    [Fact]
    public void Constructor_StaleMarkedJournalNeverDowngradesNewerDurableRevision() => RunSta(() =>
    {
        using var workspace = new TestWorkspace();
        var settings = new QueueSettings();
        CaptureQueue queue = NewQueue(workspace.Paths, settings);
        CapturePersistenceService initial = NewPersistence(queue, workspace.Paths, settings);
        CaptureRecord record = initial.PersistOriginal(
            SolidBitmap(29, 18, 0x27, 0x47, 0x67),
            1.0,
            "stale marked journal",
            "DISPLAY1");
        record.ContentRevision = 2;
        queue.SaveRecordMetaOrThrow(record);
        queue.Save();

        string directory = queue.GetDirectory(record);
        Dictionary<string, byte[]> targets = SnapshotFiles(
            directory,
            [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail]);
        string stage = NewStageDirectory(directory);
        string journal = JsonSerializer.Serialize(new
        {
            RecordId = record.Id,
            Bytes = record.TotalBytes - 1,
            HasAnnotations = true,
            ItemCount = 1,
            UpdatedAt = record.UpdatedAt.AddMinutes(-1),
            ContentRevision = 1,
            Files = new[]
            {
                new
                {
                    FileName = CaptureFileNames.Rendered,
                    HadPreviousFile = true,
                    DeleteTarget = false,
                },
            },
        });
        File.WriteAllText(Path.Combine(stage, JournalFileName), journal);
        File.WriteAllBytes(Path.Combine(stage, CommitMarkerFileName), [0x4D, 0x43, 0x46, 0x31]);

        CapturePersistenceService recovered = NewPersistence(queue, workspace.Paths, settings);

        Assert.Equal(2, record.ContentRevision);
        Assert.False(recovered.IsBusy(record.Id));
        Assert.Empty(ValidStageDirectories(directory));
        AssertFilesEqual(directory, targets);
        var durableQueue = NewQueue(workspace.Paths, settings);
        durableQueue.Load();
        CaptureRecord durable = Assert.IsType<CaptureRecord>(durableQueue.Find(record.Id));
        Assert.Equal(2, durable.ContentRevision);
    });

    private static CaptureQueue NewQueue(AppPaths paths, QueueSettings settings) =>
        new(paths, settings, NullLogger<CaptureQueue>.Instance);

    private static CapturePersistenceService NewPersistence(
        CaptureQueue queue,
        AppPaths paths,
        QueueSettings settings) =>
        new(queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);

    private static AnnotationDocument AnnotatedDocument(int width, int height)
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(width, height);
        document.Add(new RectangleAnnotation
        {
            Rect = new RectD(2, 3, Math.Max(2, width - 5), Math.Max(2, height - 7)),
            Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            StrokeThickness = 2,
        });
        return document;
    }

    private static AnnotationEditingResult EditingResult(EditorCommitAction action, int width, int height)
    {
        BitmapSource selected = SolidBitmap(width, height, 0x55, 0x66, 0x77);
        var frame = new FrozenFrame(selected, new RectD(0, 0, width, height), null, 0);
        return new AnnotationEditingResult(
            frame,
            new RectD(0, 0, width, height),
            selected,
            AnnotationDocument.CreateFor(width, height),
            action,
            new Dictionary<string, BitmapSource>(),
            new Dictionary<string, string>());
    }

    private static BitmapSource SolidBitmap(int width, int height, byte r, byte g, byte b)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = b;
            pixels[offset + 1] = g;
            pixels[offset + 2] = r;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static Dictionary<string, byte[]> BuildGenerationBytes(
        int width,
        int height,
        byte r,
        byte g,
        byte b)
    {
        BitmapSource bitmap = SolidBitmap(width, height, r, g, b);
        string layers = AnnotatedDocument(width, height).ToJson();
        return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [CaptureFileNames.Rendered] = ImageCodec.EncodePng(bitmap),
            [CaptureFileNames.Layers] = Encoding.UTF8.GetBytes(layers),
            [CaptureFileNames.Thumbnail] = EncodeJpeg(bitmap),
        };
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteGenerationFiles(
        string directory,
        int width,
        int height,
        byte r,
        byte g,
        byte b)
    {
        foreach ((string name, byte[] bytes) in BuildGenerationBytes(width, height, r, g, b))
        {
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
        }
    }

    private static string CreateUnswappedStage(
        string targetDirectory,
        CaptureRecord record,
        int width,
        int height,
        byte r,
        byte g,
        byte b,
        long extraBytes)
    {
        string stage = NewStageDirectory(targetDirectory);
        WriteGenerationFiles(stage, width, height, r, g, b);
        WriteJournal(
            stage,
            record,
            record.TotalBytes + extraBytes,
            hasAnnotations: true,
            record.UpdatedAt.AddMinutes(extraBytes),
            new JournalEntry(CaptureFileNames.Rendered, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Layers, HadPreviousFile: true),
            new JournalEntry(CaptureFileNames.Thumbnail, HadPreviousFile: true));
        return stage;
    }

    private static string NewStageDirectory(string targetDirectory)
    {
        string stage = Path.Combine(targetDirectory, $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        return stage;
    }

    private static void WriteJournal(
        string stage,
        CaptureRecord record,
        long bytes,
        bool hasAnnotations,
        DateTimeOffset updatedAt,
        params JournalEntry[] entries)
    {
        // CapturePersistenceService intentionally uses default JsonSerializer options for this
        // private journal, so these PascalCase names are part of the recovery contract.
        string json = JsonSerializer.Serialize(new
        {
            RecordId = record.Id,
            Bytes = bytes,
            HasAnnotations = hasAnnotations,
            ItemCount = hasAnnotations ? 1 : 0,
            UpdatedAt = updatedAt,
            ContentRevision = checked(record.ContentRevision + 1),
            Files = entries.Select(entry => new
            {
                entry.FileName,
                entry.HadPreviousFile,
                entry.DeleteTarget,
            }).ToArray(),
        });
        File.WriteAllText(Path.Combine(stage, JournalFileName), json);
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string directory, IEnumerable<string> names) =>
        names.ToDictionary(
            name => name,
            name => File.ReadAllBytes(Path.Combine(directory, name)),
            StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, byte[]> SnapshotTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

    private static void AssertFilesEqual(string directory, IReadOnlyDictionary<string, byte[]> expected)
    {
        foreach ((string name, byte[] bytes) in expected)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, name)));
        }
    }

    private static void AssertTreeEqual(string root, IReadOnlyDictionary<string, byte[]> expected)
    {
        Assert.True(Directory.Exists(root), $"Expected recovery directory to remain: {root}");
        Dictionary<string, byte[]> actual = SnapshotTree(root);
        Assert.Equal(
            expected.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            actual.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        foreach ((string name, byte[] bytes) in expected)
        {
            Assert.Equal(bytes, actual[name]);
        }
    }

    private static void AssertNoMediaBackups(string directory)
    {
        string[] mediaFiles =
        [
            CaptureFileNames.Original,
            CaptureFileNames.Rendered,
            CaptureFileNames.Layers,
            CaptureFileNames.Thumbnail,
        ];
        foreach (string mediaFile in mediaFiles)
        {
            Assert.False(
                File.Exists(Path.Combine(directory, mediaFile + AtomicFile.BackupSuffix)),
                $"Recovery backup should have been retired: {mediaFile}{AtomicFile.BackupSuffix}");
        }
    }

    private static string[] ValidStageDirectories(string directory) =>
        Directory.EnumerateDirectories(directory, ".stage-*", SearchOption.TopDirectoryOnly)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path)[".stage-".Length..], "N", out _))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static RecordSnapshot SnapshotRecord(CaptureRecord record, CaptureQueue queue) =>
        new(
            record.HasAnnotations,
            record.UpdatedAt,
            record.ContentRevision,
            record.TotalBytes,
            queue.TotalBytes);

    private static void AssertRecordEqual(
        RecordSnapshot expected,
        CaptureRecord actual,
        CaptureQueue queue)
    {
        Assert.Equal(expected.HasAnnotations, actual.HasAnnotations);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.Equal(expected.ContentRevision, actual.ContentRevision);
        Assert.Equal(expected.TotalBytes, actual.TotalBytes);
        Assert.Equal(expected.QueueTotalBytes, queue.TotalBytes);
    }

    private static void AssertMetaEqual(RecordSnapshot expected, string metaPath)
    {
        CaptureRecord meta = ReadMeta(metaPath);
        Assert.Equal(expected.HasAnnotations, meta.HasAnnotations);
        Assert.Equal(expected.UpdatedAt, meta.UpdatedAt);
        Assert.Equal(expected.ContentRevision, meta.ContentRevision);
        Assert.Equal(expected.TotalBytes, meta.TotalBytes);
    }

    private static CaptureRecord ReadMeta(string metaPath) =>
        Assert.IsType<CaptureRecord>(
            JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(metaPath), JsonDefaults.Readable));

    private static CaptureRecord AddQueueRecord(
        CaptureQueue queue,
        AppPaths paths,
        DateTimeOffset timestamp,
        long bytes)
    {
        var record = new CaptureRecord
        {
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            Width = 16,
            Height = 12,
            TotalBytes = bytes,
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        string directory = Path.Combine(paths.CapturesRoot, record.RelativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), new byte[16]);
        queue.Add(record);
        return record;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private readonly record struct JournalEntry(
        string FileName,
        bool HadPreviousFile,
        bool DeleteTarget = false);

    private readonly record struct RecordSnapshot(
        bool HasAnnotations,
        DateTimeOffset UpdatedAt,
        long ContentRevision,
        long TotalBytes,
        long QueueTotalBytes);

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "mycapture-transaction-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Paths = AppPaths.CreateForRoot(Root);
        }

        private string Root { get; }

        internal AppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
