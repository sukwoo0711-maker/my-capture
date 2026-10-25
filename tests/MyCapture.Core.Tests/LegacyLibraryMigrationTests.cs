using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed partial class LegacyLibraryMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_PreservesSourceStorePreferencesPayloadsOrderAndDeepEmptyFolders(bool relocated)
    {
        using var fixture = new Fixture(relocated);
        CaptureFolder parent = await fixture.SourceQueue.CreateFolderAsync("보관", createdAt: fixture.Created);
        CaptureFolder empty = await fixture.SourceQueue.CreateFolderAsync("Empty", parent.Id, fixture.Created);
        CaptureFolder deepest = parent;
        for (int depth = 1; depth < CaptureFolderNaming.MaximumDepth; depth++)
            deepest = await fixture.SourceQueue.CreateFolderAsync("Level " + depth, deepest.Id, fixture.Created);
        CaptureRecord image = fixture.AddSourceImage("주석 🖼");
        CaptureRecord second = fixture.AddSourceImage("Second");
        CaptureRecord video = fixture.AddSourceVideo();
        // Folder ordering is deliberately the reverse of creation/index order.
        await fixture.SourceQueue.MoveCapturesAsync([second.Id], deepest.Id);
        await fixture.SourceQueue.MoveCapturesAsync([image.Id], deepest.Id);
        long imageSequence = image.FolderSequence, secondSequence = second.FolderSequence;
        long expectedNextSequence = deepest.NextSequence;
        CaptureFolder destination = await fixture.TargetQueue.CreateFolderAsync("Store destination", createdAt: fixture.Created);
        await fixture.TargetQueue.SetNewCaptureFolderAsync(destination.Id, true);
        fixture.TargetQueue.Save();
        fixture.SaveSource();
        Dictionary<string, string> before = TreeHashes(fixture.SourcePaths.DataRoot);
        Dictionary<string, string> captures = TreeHashes(fixture.SourcePaths.CapturesRoot);
        string preferencesHash = Hash(fixture.TargetPaths.SettingsFile);
        string existingIndex = JsonSerializer.Serialize(fixture.Existing, JsonDefaults.Readable);
        var service = new LegacyLibraryMigrationService();
        LegacyMigrationScan scan = await service.ScanAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths);
        Assert.Equal(LegacyMigrationScanStatus.Ready, scan.Status);
        Assert.Equal(2, scan.ImageCount); Assert.Equal(1, scan.VideoCount);

        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);

        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.Equal(3, result.ImportedCount); Assert.Equal(0, result.SkippedCount);
        Assert.Equal(preferencesHash, Hash(fixture.TargetPaths.SettingsFile));
        Assert.Equal(Hash(fixture.SourcePaths.SettingsFile), Hash(result.SettingsBackupPath!));
        Assert.Equal(preferencesHash, Hash(Path.Combine(Path.GetDirectoryName(result.SettingsBackupPath!)!, "store-settings-before.json")));
        Assert.Equal(before.OrderBy(pair => pair.Key), TreeHashes(fixture.SourcePaths.DataRoot).OrderBy(pair => pair.Key));
        Assert.Equal(captures.OrderBy(pair => pair.Key), TreeHashes(fixture.SourcePaths.CapturesRoot).OrderBy(pair => pair.Key));
        Assert.Same(fixture.Existing, fixture.TargetQueue.Find(fixture.Existing.Id));
        Assert.Equal(existingIndex, JsonSerializer.Serialize(fixture.Existing, JsonDefaults.Readable));
        Assert.Equal(destination.Id, fixture.TargetQueue.DefaultCaptureFolderId);
        Assert.True(fixture.TargetQueue.UseCurrentFolderForNewCaptures);
        Assert.Equal(imageSequence, fixture.TargetQueue.Find(image.Id)!.FolderSequence);
        Assert.Equal(secondSequence, fixture.TargetQueue.Find(second.Id)!.FolderSequence);
        CaptureFolder importedDeepest = fixture.TargetQueue.Folders.Single(folder => folder.Name == deepest.Name);
        Assert.Equal(expectedNextSequence, importedDeepest.NextSequence);
        Assert.NotNull(fixture.TargetQueue.Folders.Single(folder => folder.Name == empty.Name).ParentId);
        Assert.Null(fixture.TargetQueue.Folders.Single(folder => folder.Name == parent.Name).ParentId);
        Assert.Equal("이전 MyCapture", fixture.TargetQueue.Folders.Single(folder => folder.Id == fixture.TargetQueue.Find(video.Id)!.FolderId).Name);
        foreach (CaptureRecord source in fixture.SourceQueue.Records)
            AssertPayloads(fixture.SourceQueue, source, fixture.TargetQueue, fixture.TargetQueue.Find(source.Id)!);
        Guid[] ids = fixture.TargetQueue.Records.Select(record => record.Id).ToArray();
        int folderCount = fixture.TargetQueue.Folders.Count;
        LegacyMigrationResult retry = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, retry.Status);
        Assert.Equal(0, retry.ImportedCount); Assert.Equal(3, retry.SkippedCount);
        Assert.Equal(ids, fixture.TargetQueue.Records.Select(record => record.Id));
        Assert.Equal(folderCount, fixture.TargetQueue.Folders.Count);
        // Recovery accepts base LocalState paths before SettingsStore.Load even with a relocated root.
        await service.RecoverPendingAsync(fixture.BaseTargetPaths);
        fixture.TargetQueue.Save();
        var reopened = fixture.ReopenTarget();
        Assert.Equal(ids, reopened.Records.Select(record => record.Id));
        Assert.Equal(preferencesHash, Hash(fixture.TargetPaths.SettingsFile));
    }

    [Fact]
    public async Task IdCollisionWithStoreItem_ImportsOneMappedCopyAndLaterLegacyEditsAreReportedAsAlreadyImported()
    {
        using var fixture = new Fixture();
        CaptureRecord source = fixture.AddSourceImage("Different OCR and title", fixture.Existing.Id);
        source.OcrText = "원본 OCR"; fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.Equal(2, fixture.TargetQueue.Count);
        CaptureRecord imported = fixture.TargetQueue.Records.Single(record => record.Id != fixture.Existing.Id);
        Assert.Equal(source.Title, imported.Title); Assert.Equal(source.OcrText, imported.OcrText);
        Assert.NotEqual(source.Id, imported.Id);
        AssertPayloads(fixture.SourceQueue, source, fixture.TargetQueue, imported);
        LegacyMigrationResult retry = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(0, retry.ImportedCount); Assert.Equal(1, retry.SkippedCount);
        Assert.Equal(2, fixture.TargetQueue.Count);
        // Documented choice: a legacy capture is imported once. Later legacy edits do not add copies.
        source.Title = "Changed after previous import"; fixture.SaveSource();
        File.AppendAllText(fixture.SourceQueue.GetFilePath(source, CaptureFileNames.Rendered), "re-rendered");
        LegacyMigrationResult changed = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, changed.Status);
        Assert.Equal(0, changed.ImportedCount); Assert.Equal(1, changed.SkippedCount);
        Assert.Equal(2, fixture.TargetQueue.Count);
        Assert.Equal("Different OCR and title", fixture.TargetQueue.Find(imported.Id)!.Title);
    }

    [Fact]
    public async Task FolderIdAndNameCollision_AllocatesDistinctFolderAndReusesReceiptOnRetry()
    {
        using var fixture = new Fixture();
        CaptureFolder sourceFolder = await fixture.SourceQueue.CreateFolderAsync("보관", createdAt: fixture.Created);
        CaptureRecord source = fixture.AddSourceImage("Archived");
        await fixture.SourceQueue.MoveCapturesAsync([source.Id], sourceFolder.Id);
        fixture.SaveSource();
        CaptureFolder existing = await fixture.TargetQueue.CreateFolderAsync("보관", createdAt: fixture.Created);
        // Force the exact first deterministic candidate into a preexisting Store folder.
        existing.Id = MappedFolder(sourceFolder.Id);
        fixture.TargetQueue.Save();
        var service = new LegacyLibraryMigrationService();
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        CaptureRecord imported = fixture.TargetQueue.Find(source.Id)!;
        Assert.NotEqual(existing.Id, imported.FolderId);
        Assert.Equal("보관 (1)", fixture.TargetQueue.Folders.Single(folder => folder.Id == imported.FolderId).Name);
        Guid folderId = imported.FolderId!.Value;
        LegacyMigrationResult retry = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(0, retry.ImportedCount); Assert.Equal(1, retry.SkippedCount);
        Assert.Equal(folderId, fixture.TargetQueue.Find(source.Id)!.FolderId);
        Assert.Equal(2, fixture.TargetQueue.Folders.Count);
    }

    [Fact]
    public async Task SameRecordInDifferentLibraries_PreservesBothFolderAssignmentsAndLaterLegacyMovesAreAlreadyImported()
    {
        using var fixture = new Fixture();
        CaptureFolder storeFolder = await fixture.TargetQueue.CreateFolderAsync("Store A", createdAt: fixture.Created);
        await fixture.TargetQueue.MoveCapturesAsync([fixture.Existing.Id], storeFolder.Id);
        CaptureRecord source = fixture.AddSourceImage(fixture.Existing.Title, fixture.Existing.Id);
        CaptureFolder legacyB = await fixture.SourceQueue.CreateFolderAsync("Legacy B", createdAt: fixture.Created);
        CaptureFolder legacyC = await fixture.SourceQueue.CreateFolderAsync("Legacy C", createdAt: fixture.Created);
        await fixture.SourceQueue.MoveCapturesAsync([source.Id], legacyB.Id);
        // Make all non-location metadata exactly equal; location itself is the collision.
        source.UpdatedAt = fixture.Existing.UpdatedAt;
        fixture.SaveSource(); fixture.TargetQueue.Save();
        var service = new LegacyLibraryMigrationService();
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status); Assert.Equal(1, result.ImportedCount);
        Assert.Equal(storeFolder.Id, fixture.TargetQueue.Find(fixture.Existing.Id)!.FolderId);
        CaptureRecord importedB = fixture.TargetQueue.Records.Single(record => record.Id != fixture.Existing.Id);
        Assert.Equal("Legacy B", fixture.TargetQueue.Folders.Single(folder => folder.Id == importedB.FolderId).Name);
        LegacyMigrationResult retry = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(0, retry.ImportedCount); Assert.Equal(1, retry.SkippedCount);
        await fixture.SourceQueue.MoveCapturesAsync([source.Id], legacyC.Id);
        source.UpdatedAt = fixture.Existing.UpdatedAt; fixture.SaveSource();
        // A later legacy move is not a new capture: the earlier copy stays where the user keeps it.
        LegacyMigrationResult moved = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, moved.Status);
        Assert.Equal(0, moved.ImportedCount); Assert.Equal(1, moved.SkippedCount);
        Assert.Equal(2, fixture.TargetQueue.Count);
        Assert.Equal(importedB.FolderId, fixture.TargetQueue.Find(importedB.Id)!.FolderId);
    }

    [Fact]
    public async Task ImportedHistoryAboveLimit_DoesNotEvictExistingStoreHistory()
    {
        using var fixture = new Fixture();
        for (int index = 0; index < 24; index++) fixture.AddSourceImage("Saved " + index, pinned: true);
        fixture.SaveSource();
        int evictions = 0; fixture.TargetQueue.Evicted += (_, _) => evictions++;
        LegacyMigrationResult result = await new LegacyLibraryMigrationService().ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.Equal(25, fixture.TargetQueue.Count); Assert.Equal(1, fixture.TargetQueue.HistoryCount);
        Assert.Equal(0, evictions); Assert.NotNull(fixture.TargetQueue.Find(fixture.Existing.Id));
        Assert.Equal(25, fixture.ReopenTarget().Count);
    }

    [Theory]
    [InlineData("AfterSourceSnapshot")]
    [InlineData("AfterCopy")]
    public async Task SourceChangedDuringCopy_FailsBeforePublication(string checkpoint)
    {
        using var fixture = new Fixture();
        CaptureRecord source = fixture.AddSourceImage("Changing"); fixture.SaveSource();
        string indexHash = Hash(fixture.TargetPaths.IndexFile), settingsHash = Hash(fixture.TargetPaths.SettingsFile);
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == checkpoint) File.AppendAllText(fixture.SourceQueue.GetFilePath(source, CaptureFileNames.Original), "changed");
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Failed, result.Status); Assert.Equal("SourceChanged", result.ErrorCode);
        Assert.Equal(indexHash, Hash(fixture.TargetPaths.IndexFile)); Assert.Equal(settingsHash, Hash(fixture.TargetPaths.SettingsFile));
        Assert.Equal(1, fixture.TargetQueue.Count);
        AssertNoImportLeftovers(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrPreparationIoFailure_LeavesTargetAndSourceIntact(bool cancellation)
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Canceled"); fixture.SaveSource();
        Dictionary<string, string> source = TreeHashes(fixture.SourcePaths.DataRoot);
        string indexHash = Hash(fixture.TargetPaths.IndexFile), settingsHash = Hash(fixture.TargetPaths.SettingsFile);
        using var token = new CancellationTokenSource();
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase != "BeforePublication") return;
            if (cancellation) token.Cancel(); else throw new IOException("Injected disk-full preparation failure");
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings, cancellationToken: token.Token);
        Assert.Equal(cancellation ? LegacyMigrationStatus.Canceled : LegacyMigrationStatus.Failed, result.Status);
        Assert.Equal(indexHash, Hash(fixture.TargetPaths.IndexFile)); Assert.Equal(settingsHash, Hash(fixture.TargetPaths.SettingsFile));
        Assert.Equal(source.OrderBy(pair => pair.Key), TreeHashes(fixture.SourcePaths.DataRoot).OrderBy(pair => pair.Key));
        AssertNoImportLeftovers(fixture);
        fixture.TargetQueue.Save(); // A canceled or failed import never leaves the live library blocked.
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.TargetPaths);
        Assert.Equal(1, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task TargetMutationDuringCopy_IsNotOverwrittenByPublication()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Source"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == "BeforePublication")
            {
                fixture.TargetQueue.TogglePin(fixture.Existing.Id);
                fixture.TargetQueue.SaveRecordMetaOrThrow(fixture.Existing);
                fixture.TargetQueue.Save();
            }
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Failed, result.Status);
        Assert.True(fixture.Existing.IsPinned); Assert.True(fixture.ReopenTarget().Find(fixture.Existing.Id)!.IsPinned);
        Assert.Equal(1, fixture.TargetQueue.Count);
        AssertNoImportLeftovers(fixture);
    }

    [Fact]
    public async Task TargetDiskChangeBeforeCopy_IsDetectedAgainstInitialIndexNotLateSnapshot()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Source"); fixture.SaveSource();
        string? changedIndex = null;
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase != "AfterSourceSnapshot") return;
            JsonObject index = JsonNode.Parse(File.ReadAllText(fixture.TargetPaths.IndexFile))!.AsObject();
            index["records"]![0]!["title"] = "External Store edit";
            changedIndex = index.ToJsonString(); File.WriteAllText(fixture.TargetPaths.IndexFile, changedIndex);
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Failed, result.Status); Assert.Equal("TargetChanged", result.ErrorCode);
        Assert.Equal(changedIndex, File.ReadAllText(fixture.TargetPaths.IndexFile));
        fixture.TargetQueue.Load(); Assert.Equal("External Store edit", fixture.TargetQueue.Find(fixture.Existing.Id)!.Title);
        AssertNoImportLeftovers(fixture);
    }

    [Fact]
    public async Task NewQueueWriterAdmissionIsRejectedDuringPublishingAndResumedAfterCompletion()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Protected"); fixture.SaveSource();
        bool rejected = false;
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == "AfterPayloadPublication")
            {
                Assert.Throws<IOException>(() => fixture.TargetQueue.Save()); rejected = true;
            }
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.True(rejected); Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        fixture.TargetQueue.Save(); Assert.Equal(2, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task LockedIndexAfterCommit_KeepsTheCommittedImportAndNeverBlocksTheLibrary()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Committed"); fixture.SaveSource();
        FileStream? blocker = null;
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase != "AfterIndexPublication") return;
            blocker = new FileStream(fixture.TargetPaths.IndexFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            throw new IOException("Injected postcommit error while the index is locked");
        } };
        try
        {
            // The index write is the commit; whatever fails afterwards cannot undo or hide it.
            LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
            Assert.Equal(LegacyMigrationStatus.Completed, result.Status); Assert.Equal(1, result.ImportedCount);
            Assert.Equal(2, fixture.TargetQueue.Count);
        }
        finally { blocker?.Dispose(); }
        fixture.TargetQueue.Save();
        Assert.Equal(LegacyRecoveryStatus.Clean, (await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths)).Status);
        fixture.TargetQueue.Load(); Assert.Equal(2, fixture.TargetQueue.Count);
    }

    [Theory]
    [InlineData("AfterPayloadPublication")]
    [InlineData("AfterIndexPublication")]
    [InlineData("BeforeCompletedReceipt")]
    public async Task PublicationFailure_IsRecoveredBeforeReturningAndCannotBeOverwrittenByLiveQueue(string checkpoint)
    {
        using var fixture = new Fixture(true); fixture.AddSourceImage("Recovered"); fixture.SaveSource();
        string preferences = Hash(fixture.TargetPaths.SettingsFile);
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == checkpoint) throw new IOException("Injected publication failure");
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status); Assert.Equal(2, fixture.TargetQueue.Count);
        // Retry immediately, without Save/Load masking a faulted prior publication tail.
        service.CheckpointForTest = null;
        LegacyMigrationResult retry = await service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, retry.Status);
        Assert.Equal(0, retry.ImportedCount); Assert.Equal(1, retry.SkippedCount);
        fixture.TargetQueue.Save(); // The live owner must already contain the committed import.
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        Assert.Equal(2, fixture.ReopenTarget().Count); Assert.Equal(preferences, Hash(fixture.TargetPaths.SettingsFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentFinalizationFailure_KeepsCommittedLibraryUsableAndFinishesTheReceiptLater(bool blockReceipt)
    {
        using var fixture = new Fixture(true); fixture.AddSourceImage("Durable"); fixture.SaveSource();
        FileStream? blocker = null;
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase != "AfterIndexPublication") return;
            string file = blockReceipt
                ? Directory.EnumerateFiles(Path.Combine(fixture.TargetPaths.DataRoot, "library-migration"), "journal.json", SearchOption.AllDirectories).Single()
                : Path.Combine(fixture.TargetPaths.DataRoot, "library-folders.json");
            blocker = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } };
        try
        {
            LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
            Assert.Equal(LegacyMigrationStatus.Completed, result.Status); Assert.Equal(1, result.ImportedCount);
            Assert.Equal(2, fixture.TargetQueue.Count);
            if (blockReceipt) fixture.TargetQueue.Save(); // Not blocked: the index already commits the import.
            // Until the receipt exists, a retry still recognizes the committed copy.
            LegacyMigrationResult retry = await new LegacyLibraryMigrationService().ImportAsync(fixture.SourcePaths.DataRoot,
                fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
            Assert.Equal(0, retry.ImportedCount); Assert.Equal(1, retry.SkippedCount);
        }
        finally { blocker?.Dispose(); }
        Assert.Equal(LegacyRecoveryStatus.Clean, (await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths)).Status);
        fixture.TargetQueue.Load();
        Assert.Equal(2, fixture.ReopenTarget().Count);
        fixture.TargetQueue.Save(); Assert.Equal(2, fixture.ReopenTarget().Count);
        Assert.All(Directory.EnumerateFiles(Path.Combine(fixture.TargetPaths.DataRoot, "library-migration"), "journal.json", SearchOption.AllDirectories),
            path => Assert.Equal("Completed", JsonNode.Parse(File.ReadAllText(path))!["phase"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("missing-directory", 0, 1)]
    [InlineData("corrupt-layers", 0, 1)]
    [InlineData("escape-in-torn-index", 1, 0)]
    [InlineData("torn-index-without-backup", 1, 0)]
    [InlineData("settings-without-queue", 1, 0)]
    public async Task DamagedSource_IsSkippedOrRecoveredWithoutChangingTheLegacyFolder(string kind, int imported, int unusable)
    {
        using var fixture = new Fixture(); CaptureRecord record = fixture.AddSourceImage("Damaged"); fixture.SaveSource();
        switch (kind)
        {
            case "missing-directory":
                JsonObject missing = JsonNode.Parse(File.ReadAllText(fixture.SourcePaths.IndexFile))!.AsObject();
                missing["records"]![0]!["relativeDirectory"] = "absent";
                File.WriteAllText(fixture.SourcePaths.IndexFile, missing.ToJsonString()); break;
            case "escape-in-torn-index":
                // The index is rejected, so the capture is found from its own meta.json instead.
                JsonObject escaped = JsonNode.Parse(File.ReadAllText(fixture.SourcePaths.IndexFile))!.AsObject();
                escaped["records"]![0]!["relativeDirectory"] = "../outside";
                File.WriteAllText(fixture.SourcePaths.IndexFile, escaped.ToJsonString()); break;
            case "torn-index-without-backup": File.WriteAllText(fixture.SourcePaths.IndexFile, "{broken"); break;
            case "settings-without-queue": File.WriteAllText(fixture.SourcePaths.SettingsFile, "{\"queue\":null}"); break;
            case "corrupt-layers": File.WriteAllText(fixture.SourceQueue.GetFilePath(record, CaptureFileNames.Layers), "{broken"); break;
        }
        Dictionary<string, string> legacy = TreeHashes(fixture.SourcePaths.DataRoot);
        var service = new LegacyLibraryMigrationService();
        LegacyMigrationScan scan = await service.ScanAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths);
        Assert.Equal(unusable, scan.UnusableCount); Assert.Equal(imported, scan.ImageCount);
        Assert.Equal(kind == "settings-without-queue", scan.SettingsUnreadable);
        Assert.Equal(kind != "settings-without-queue", scan.HasSettings);
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.Equal(imported, result.ImportedCount); Assert.Equal(unusable, result.UnusableCount);
        Assert.Equal(1 + imported, fixture.TargetQueue.Count);
        Assert.Equal(legacy.OrderBy(pair => pair.Key), TreeHashes(fixture.SourcePaths.DataRoot).OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task UnrecognizedCaptureFolderWithoutAnyIndex_ReportsMissingIndex()
    {
        using var fixture = new Fixture(); CaptureRecord record = fixture.AddSourceImage("No sidecar"); fixture.SaveSource();
        global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(fixture.SourcePaths.IndexFile);
        global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(fixture.SourceQueue.GetFilePath(record, CaptureFileNames.Meta));
        LegacyMigrationScan scan = await new LegacyLibraryMigrationService().ScanAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths);
        Assert.Equal(LegacyMigrationScanStatus.Failed, scan.Status); Assert.Equal("MissingIndex", scan.ErrorCode);
    }

    [Fact]
    public async Task SettingsOnlySource_IsBackedUpWithoutChangingStoreSettings()
    {
        using var fixture = new Fixture();
        string hash = Hash(fixture.TargetPaths.SettingsFile);
        var service = new LegacyLibraryMigrationService();
        LegacyMigrationScan scan = await service.ScanAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths);
        Assert.Equal(LegacyMigrationScanStatus.Ready, scan.Status); Assert.True(scan.HasSettings); Assert.Equal(0, scan.ImageCount);
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status); Assert.Equal(0, result.ImportedCount);
        Assert.Equal(Hash(fixture.SourcePaths.SettingsFile), Hash(result.SettingsBackupPath!));
        Assert.Equal(hash, Hash(fixture.TargetPaths.SettingsFile)); Assert.Equal(1, fixture.TargetQueue.Count);
    }

    [Fact]
    public async Task StartupWithoutPendingJournal_DoesNotInterceptExistingSettingsRecovery()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.TargetPaths.DataRoot, "library-migration"));
        File.WriteAllText(fixture.TargetPaths.SettingsFile, "{torn primary");
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        // No migration journal means SettingsStore, not migration, owns the normal .bak path.
    }

    [Theory]
    [InlineData("AfterPayloadPublication")]
    [InlineData("AfterIndexPublication")]
    [InlineData("BeforeCompletedReceipt")]
    public async Task CrashJournal_ReplaysOnlyItsOwnedTransactionBeforeFreshQueueLoad(string checkpoint)
    {
        using var fixture = new Fixture(true);
        fixture.AddSourceImage("Crash protected"); fixture.SaveSource();
        Dictionary<string, string> source = TreeHashes(fixture.SourcePaths.DataRoot);
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == checkpoint) throw new SimulatedProcessCrash();
        } };
        await Assert.ThrowsAsync<SimulatedProcessCrash>(() => service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings));
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        fixture.TargetQueue.Load();
        Assert.Equal(2, fixture.TargetQueue.Count);
        Assert.Equal(source.OrderBy(pair => pair.Key), TreeHashes(fixture.SourcePaths.DataRoot).OrderBy(pair => pair.Key));
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        Assert.Equal(2, fixture.ReopenTarget().Count);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("null-records")]
    public async Task MalformedRecoveryJournal_CannotTouchAnOutsideFileOrBlockStartup(string kind)
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Owned"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == "AfterPayloadPublication") throw new SimulatedProcessCrash();
        } };
        await Assert.ThrowsAsync<SimulatedProcessCrash>(() => service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings));
        string journalPath = Directory.EnumerateFiles(Path.Combine(fixture.TargetPaths.DataRoot, "library-migration"),
            "journal.json", SearchOption.AllDirectories).Single();
        JsonObject journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        if (kind == "path") journal["records"]![0]!["files"]![0]!["name"] = "..\\..\\..\\outside.png";
        else journal["records"] = null;
        File.WriteAllText(journalPath, journal.ToJsonString());
        string index = Hash(fixture.TargetPaths.IndexFile);
        LegacyRecoveryResult recovery = await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        // A tampered plan is never replayed; an unreadable one is left alone. Neither stops the launch.
        Assert.Equal(kind == "path" ? LegacyRecoveryStatus.DecisionRequired : LegacyRecoveryStatus.Clean, recovery.Status);
        if (kind == "path") Assert.Equal("InvalidJournal", recovery.ErrorCode);
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        await new LegacyLibraryMigrationService().AbandonPendingAsync(fixture.BaseTargetPaths);
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        Assert.False(File.Exists(Path.Combine(fixture.TargetPaths.DataRoot, "outside.png")));
        Assert.False(File.Exists(Path.Combine(fixture.TargetPaths.CapturesRoot, "outside.png")));
        Assert.Equal(1, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task CaseVariantSourceRoot_RecognizesItemsAlreadyImported()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Case"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(LegacyMigrationStatus.Completed, (await service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings)).Status);
        Guid folder = fixture.TargetQueue.Records.Single(record => record.Id != fixture.Existing.Id).FolderId!.Value;
        if (!OperatingSystem.IsWindows()) return;
        // Capture ids, not path spelling, identify what was imported.
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot.ToUpperInvariant(),
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.Equal(0, result.ImportedCount); Assert.Equal(1, result.SkippedCount);
        Assert.Single(fixture.TargetQueue.Folders); Assert.Equal(folder, fixture.TargetQueue.Folders.Single().Id);
    }

    [Fact]
    public async Task ReparseCaptureDirectory_IsRejectedWithoutReadingLinkedPayload()
    {
        using var fixture = new Fixture();
        CaptureRecord source = fixture.AddSourceImage("Linked"); fixture.SaveSource();
        string link = Path.Combine(fixture.SourcePaths.CapturesRoot, "linked");
        try { Directory.CreateSymbolicLink(link, fixture.TargetQueue.GetDirectory(fixture.Existing)); }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is UnauthorizedAccessException or IOException)
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    "New-Item -ItemType Junction -Path $env:MYCAPTURE_MIGRATION_TEST_LINK -Target $env:MYCAPTURE_MIGRATION_TEST_TARGET | Out-Null" }
            };
            start.Environment["MYCAPTURE_MIGRATION_TEST_LINK"] = link;
            start.Environment["MYCAPTURE_MIGRATION_TEST_TARGET"] = fixture.TargetQueue.GetDirectory(fixture.Existing);
            using var command = Process.Start(start)!;
            Assert.True(command.WaitForExit(5000));
            Assert.True(command.ExitCode == 0, command.StandardOutput.ReadToEnd() + command.StandardError.ReadToEnd());
        }
        Assert.NotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint));
        JsonObject index = JsonNode.Parse(File.ReadAllText(fixture.SourcePaths.IndexFile))!.AsObject();
        index["records"]![0]!["relativeDirectory"] = "linked";
        File.WriteAllText(fixture.SourcePaths.IndexFile, index.ToJsonString());
        string target = Hash(fixture.TargetPaths.IndexFile);
        LegacyMigrationScan result = await new LegacyLibraryMigrationService().ScanAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths);
        Assert.Equal(LegacyMigrationScanStatus.Failed, result.Status);
        Assert.Equal(target, Hash(fixture.TargetPaths.IndexFile));
    }

    private sealed class SimulatedProcessCrash : Exception;

    [Fact]
    public async Task OverlappingSourceAndTarget_IsRejected()
    {
        using var fixture = new Fixture();
        LegacyMigrationScan scan = await new LegacyLibraryMigrationService().ScanAsync(fixture.TargetPaths.DataRoot, fixture.TargetPaths);
        Assert.Equal(LegacyMigrationScanStatus.Failed, scan.Status); Assert.Equal("OverlappingRoots", scan.ErrorCode);
    }

    [Fact]
    public async Task LargeVideo_StreamsCompletePayloadWithBoundedProgress()
    {
        using var fixture = new Fixture(); CaptureRecord video = fixture.AddSourceVideo();
        const int mebibytes = 32;
        byte[] buffer = new byte[128 * 1024]; Random.Shared.NextBytes(buffer);
        using (var file = new FileStream(fixture.SourceQueue.GetFilePath(video, CaptureFileNames.VideoSource), FileMode.Create, FileAccess.Write))
            for (int block = 0; block < mebibytes * 8; block++) file.Write(buffer);
        fixture.SaveSource();
        var progress = new ImmediateProgress(); var elapsed = Stopwatch.StartNew();
        LegacyMigrationResult result = await new LegacyLibraryMigrationService().ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings, progress);
        elapsed.Stop();
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.Equal(Hash(fixture.SourceQueue.GetFilePath(video, CaptureFileNames.VideoSource)),
            Hash(fixture.TargetQueue.GetFilePath(fixture.TargetQueue.Find(video.Id)!, CaptureFileNames.VideoSource)));
        Assert.InRange(progress.Values.Count, 2, 1000);
        Assert.True(progress.Values.Zip(progress.Values.Skip(1)).Where(pair => pair.First.Stage == pair.Second.Stage)
            .All(pair => pair.Second.CopiedBytes >= pair.First.CopiedBytes));
        Console.WriteLine($"Migration streaming smoke: {mebibytes} MiB, {elapsed.Elapsed.TotalMilliseconds:F1} ms, {progress.Values.Count} progress notifications.");
    }

    private sealed class ImmediateProgress : IProgress<LegacyMigrationProgress>
    {
        internal List<LegacyMigrationProgress> Values { get; } = [];
        public void Report(LegacyMigrationProgress value) { lock (Values) Values.Add(value); }
    }

    private static Guid MappedFolder(Guid original) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        "mycapture-legacy-import\nfolder\n" + original.ToString("N"))).AsSpan(0, 16));

    /// <summary>No staging, partial copy or operation directory remains after a canceled or failed import.</summary>
    private static void AssertNoImportLeftovers(Fixture fixture)
    {
        Assert.Empty(Directory.EnumerateDirectories(fixture.TargetPaths.CapturesRoot, "import-*"));
        Assert.Empty(Directory.EnumerateFiles(fixture.TargetPaths.CapturesRoot, "*.import-part", SearchOption.AllDirectories));
        string operations = Path.Combine(fixture.TargetPaths.DataRoot, "library-migration");
        if (Directory.Exists(operations)) Assert.Empty(Directory.EnumerateFileSystemEntries(operations));
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static Dictionary<string, string> TreeHashes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), Hash);
    private static void AssertPayloads(CaptureQueue sourceQueue, CaptureRecord source, CaptureQueue targetQueue, CaptureRecord target)
    {
        foreach (string file in Directory.EnumerateFiles(sourceQueue.GetDirectory(source)))
            if (Path.GetFileName(file) != CaptureFileNames.Meta && !Path.GetFileName(file).EndsWith(".bak"))
                Assert.Equal(Hash(file), Hash(targetQueue.GetFilePath(target, Path.GetFileName(file))));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _language = UiText.UseLanguage("ko-KR");
        private readonly TempWorkspace _workspace = new();
        internal DateTimeOffset Created { get; } = DateTimeOffset.UtcNow.AddMinutes(-5);
        internal AppPaths SourcePaths { get; }
        internal AppPaths BaseTargetPaths { get; }
        internal AppPaths TargetPaths { get; }
        internal AppSettings SourceSettings { get; } = NewInstallDefaults.Create();
        internal AppSettings TargetSettings { get; } = NewInstallDefaults.Create();
        internal CaptureQueue SourceQueue { get; }
        internal CaptureQueue TargetQueue { get; }
        internal CaptureRecord Existing { get; }
        internal Fixture(bool relocated = false)
        {
            AppPaths source = AppPaths.CreateForRoot(Path.Combine(_workspace.Root, "legacy"));
            BaseTargetPaths = AppPaths.CreateForPackage(Path.Combine(_workspace.Root, "package", "LocalState"),
                Path.Combine(_workspace.Root, "package", "LocalCache"), Path.Combine(_workspace.Root, "Pictures"));
            if (relocated)
            {
                SourceSettings.Queue.CapturesDirectoryOverride = Path.Combine(_workspace.Root, "legacy-library");
                TargetSettings.Queue.CapturesDirectoryOverride = Path.Combine(_workspace.Root, "store-library");
            }
            SourcePaths = relocated ? source.WithCapturesRoot(SourceSettings.Queue.CapturesDirectoryOverride) : source;
            TargetPaths = relocated ? BaseTargetPaths.WithCapturesRoot(TargetSettings.Queue.CapturesDirectoryOverride) : BaseTargetPaths;
            SourcePaths.EnsureCreated(); TargetPaths.EnsureCreated();
            SourceSettings.Queue.MaxItems = 10; TargetSettings.Queue.MaxItems = 10;
            SourceSettings.General.Language = "ko-KR"; TargetSettings.General.Language = "en-US";
            TargetSettings.Pin.ZoomStep = 0.23; TargetSettings.StoreUpdate.ObserveLaunch("4.1.2.0", true);
            new SettingsStore(SourcePaths, NullLogger<SettingsStore>.Instance).Save(SourceSettings);
            new SettingsStore(TargetPaths, NullLogger<SettingsStore>.Instance).Save(TargetSettings);
            SourceQueue = NewQueue(SourcePaths, SourceSettings); TargetQueue = NewQueue(TargetPaths, TargetSettings);
            Existing = AddImage(TargetQueue, "Store item", null, false);
            TargetQueue.Save();
        }
        internal CaptureRecord AddSourceImage(string title, Guid? id = null, bool pinned = false) => AddImage(SourceQueue, title, id, pinned);
        private CaptureRecord AddImage(CaptureQueue queue, string title, Guid? id, bool pinned)
        {
            var record = new CaptureRecord { Id = id ?? Guid.NewGuid(), Title = title, Width = 1, Height = 1,
                CreatedAt = Created, UpdatedAt = Created, HasAnnotations = true, IsPinned = pinned };
            queue.Add(record); Directory.CreateDirectory(queue.GetDirectory(record));
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jWZkAAAAASUVORK5CYII=");
            foreach (string name in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered, "asset-01.png" })
                File.WriteAllBytes(queue.GetFilePath(record, name), png);
            var document = AnnotationDocument.CreateFor(1, 1);
            document.Add(new TextAnnotation { Text = "보존 🖊", Rect = new RectD(0, 0, 1, 1) });
            document.Add(new ImageAnnotation { AssetFileName = "asset-01.png", SourceWidth = 1, SourceHeight = 1, Rect = new RectD(0, 0, 1, 1) });
            File.WriteAllText(queue.GetFilePath(record, CaptureFileNames.Layers), document.ToJson(), Encoding.UTF8);
            queue.SaveRecordMetaOrThrow(record); return record;
        }
        internal CaptureRecord AddSourceVideo()
        {
            var record = new CaptureRecord { Title = "Clip", MediaKind = CaptureMediaKind.Video, Width = 640, Height = 360,
                DurationMs = 2500, FrameRate = 30, FrameCount = 75, CreatedAt = Created, UpdatedAt = Created };
            SourceQueue.Add(record); Directory.CreateDirectory(SourceQueue.GetDirectory(record));
            File.WriteAllBytes(SourceQueue.GetFilePath(record, CaptureFileNames.VideoSource), [1, 2, 3, 4]);
            File.WriteAllBytes(SourceQueue.GetFilePath(record, CaptureFileNames.VideoRendered), [5, 6, 7, 8]);
            File.WriteAllText(SourceQueue.GetFilePath(record, CaptureFileNames.VideoEdits), "{\"trimStartMs\":100,\"trimEndMs\":2000}");
            SourceQueue.SaveRecordMetaOrThrow(record); return record;
        }
        internal void SaveSource()
        {
            foreach (CaptureRecord record in SourceQueue.Records) SourceQueue.SaveRecordMetaOrThrow(record);
            SourceQueue.Save();
        }
        internal CaptureQueue ReopenTarget() { var queue = NewQueue(TargetPaths, TargetSettings); queue.Load(); return queue; }
        private static CaptureQueue NewQueue(AppPaths paths, AppSettings settings) => new(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        public void Dispose()
        {
            try { _workspace.Dispose(); }
            finally { _language.Dispose(); }
        }
    }
}
