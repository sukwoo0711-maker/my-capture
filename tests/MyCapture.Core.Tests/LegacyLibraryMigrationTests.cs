using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class LegacyLibraryMigrationTests
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
    public async Task IdCollisionWithSamePixelsButDifferentMetadata_PreservesBothVersionsAndRetryIsIdempotent()
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
        source.Title = "Changed after previous import"; fixture.SaveSource();
        LegacyMigrationResult changed = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(1, changed.ImportedCount);
        Assert.Equal(3, fixture.TargetQueue.Count);
        Assert.Contains(fixture.TargetQueue.Records, record => record.Title == imported.Title);
        Assert.Contains(fixture.TargetQueue.Records, record => record.Title == source.Title);
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
        existing.Id = MappedFolder(fixture.SourcePaths.DataRoot, sourceFolder.Id);
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
        fixture.TargetQueue.Save(); // The live owner must already contain the committed import.
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        Assert.Equal(2, fixture.ReopenTarget().Count); Assert.Equal(preferences, Hash(fixture.TargetPaths.SettingsFile));
        LegacyMigrationResult retry = await new LegacyLibraryMigrationService().ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(0, retry.ImportedCount); Assert.Equal(1, retry.SkippedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentFinalizationFailure_KeepsCommittedOwnerStateAndRecoversOnNextProcess(bool blockReceipt)
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
        LegacyMigrationResult result;
        try
        {
            result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
            Assert.Equal(LegacyMigrationStatus.Failed, result.Status); Assert.Equal("RecoveryRequired", result.ErrorCode);
            Assert.Equal(2, fixture.TargetQueue.Count);
            Assert.Throws<IOException>(() => fixture.TargetQueue.Save());
            Assert.Throws<IOException>(() => fixture.TargetQueue.Load());
        }
        finally { blocker?.Dispose(); }
        await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        fixture.TargetQueue.Load();
        Assert.Equal(2, fixture.ReopenTarget().Count);
        fixture.TargetQueue.Save(); Assert.Equal(2, fixture.ReopenTarget().Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("escape")]
    [InlineData("corrupt-index")]
    [InlineData("null-queue")]
    [InlineData("corrupt-layers")]
    public async Task InvalidSource_IsVisibleFailureWithNoTargetPublication(string kind)
    {
        using var fixture = new Fixture(); CaptureRecord record = fixture.AddSourceImage("Invalid"); fixture.SaveSource();
        switch (kind)
        {
            case "missing":
                JsonObject missing = JsonNode.Parse(File.ReadAllText(fixture.SourcePaths.IndexFile))!.AsObject();
                missing["records"]![0]!["relativeDirectory"] = "absent";
                File.WriteAllText(fixture.SourcePaths.IndexFile, missing.ToJsonString()); break;
            case "escape":
                JsonObject escaped = JsonNode.Parse(File.ReadAllText(fixture.SourcePaths.IndexFile))!.AsObject();
                escaped["records"]![0]!["relativeDirectory"] = "../outside";
                File.WriteAllText(fixture.SourcePaths.IndexFile, escaped.ToJsonString()); break;
            case "corrupt-index": File.WriteAllText(fixture.SourcePaths.IndexFile, "{broken"); break;
            case "null-queue": File.WriteAllText(fixture.SourcePaths.SettingsFile, "{\"queue\":null}"); break;
            case "corrupt-layers": File.WriteAllText(fixture.SourceQueue.GetFilePath(record, CaptureFileNames.Layers), "{broken"); break;
        }
        string indexHash = Hash(fixture.TargetPaths.IndexFile);
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(LegacyMigrationScanStatus.Failed, (await service.ScanAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths)).Status);
        Assert.Equal(LegacyMigrationStatus.Failed, (await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings)).Status);
        Assert.Equal(indexHash, Hash(fixture.TargetPaths.IndexFile)); Assert.Equal(1, fixture.TargetQueue.Count);
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
    [InlineData("null-copies")]
    public async Task MalformedRecoveryJournal_CannotTouchAnOutsideFile(string kind)
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
        if (kind == "path") journal["copies"]![0]!["targetRelative"] = "../outside.png";
        else journal["copies"] = null;
        File.WriteAllText(journalPath, journal.ToJsonString());
        string index = Hash(fixture.TargetPaths.IndexFile);
        await Assert.ThrowsAsync<IOException>(() => new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths));
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        Assert.False(File.Exists(Path.Combine(fixture.TargetPaths.DataRoot, "outside.png")));
    }

    [Fact]
    public async Task CaseVariantSourceRetry_DoesNotConflatePotentialCaseSensitiveDirectories()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Case"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(LegacyMigrationStatus.Completed, (await service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings)).Status);
        Guid folder = fixture.TargetQueue.Records.Single(record => record.Id != fixture.Existing.Id).FolderId!.Value;
        if (!OperatingSystem.IsWindows()) return;
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot.ToUpperInvariant(),
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Failed, result.Status); Assert.Equal("SourceRootCaseMismatch", result.ErrorCode);
        Assert.Single(fixture.TargetQueue.Folders); Assert.Equal(folder, fixture.TargetQueue.Folders.Single().Id);
    }

    [Fact]
    public async Task ReparseCaptureDirectory_IsRejectedWithoutReadingLinkedPayload()
    {
        using var fixture = new Fixture();
        CaptureRecord source = fixture.AddSourceImage("Linked"); fixture.SaveSource();
        string link = Path.Combine(fixture.SourcePaths.CapturesRoot, "linked");
        try { Directory.CreateSymbolicLink(link, fixture.TargetQueue.GetDirectory(fixture.Existing)); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            using var command = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "/d", "/c", "mklink", "/J", link, fixture.TargetQueue.GetDirectory(fixture.Existing) }
            })!;
            Assert.True(command.WaitForExit(5000)); Assert.Equal(0, command.ExitCode);
        }
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

    private static Guid MappedFolder(string source, Guid original) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        source + "\nfolder\n" + original.ToString("N"))).AsSpan(0, 16));
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
        public void Dispose() => _workspace.Dispose();
    }
}
