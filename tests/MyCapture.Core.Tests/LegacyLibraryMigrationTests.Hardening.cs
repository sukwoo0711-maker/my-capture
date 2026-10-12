using System.Text.Json.Nodes;
using MyCapture.Core.Queue;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>Space, cleanup, recovery and identity guarantees added after the 2026-10-13 review.</summary>
public sealed partial class LegacyLibraryMigrationTests
{
    [Fact]
    public async Task FreeSpace_IsCheckedBeforeAnythingIsWrittenAndOneCopyOfThePayloadIsEnough()
    {
        using var fixture = new Fixture(); CaptureRecord video = fixture.AddSourceVideo();
        const int payload = 96 * 1024 * 1024;
        File.WriteAllBytes(fixture.SourceQueue.GetFilePath(video, CaptureFileNames.VideoSource), new byte[payload]);
        fixture.SaveSource();
        string index = Hash(fixture.TargetPaths.IndexFile);
        long free = 0;
        var service = new LegacyLibraryMigrationService { FreeSpaceForTest = _ => free };
        free = payload / 2;
        LegacyMigrationResult refused = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Failed, refused.Status); Assert.Equal("InsufficientSpace", refused.ErrorCode);
        Assert.Equal(free, refused.AvailableBytes); Assert.True(refused.RequiredBytes > payload);
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        AssertNoImportLeftovers(fixture);
        // Staging is the final copy, so free space for one payload copy plus a fixed reserve suffices.
        free = refused.RequiredBytes;
        Assert.True(free < 2L * payload, $"Required {free} bytes for a {payload}-byte payload.");
        LegacyMigrationResult accepted = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, accepted.Status); Assert.Equal(1, accepted.ImportedCount);
    }

    [Fact]
    public async Task DiskFullWhileStaging_DeletesThePartialAndEveryStagedFile()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("First"); fixture.AddSourceImage("Second"); fixture.SaveSource();
        string index = Hash(fixture.TargetPaths.IndexFile);
        int writes = 0;
        string? partial = null;
        var service = new LegacyLibraryMigrationService { StagingWriteForTest = path =>
        {
            // Records are staged by several workers; the fourth write on any of them fails.
            if (Interlocked.Increment(ref writes) < 4) return;
            partial = path;
            Assert.True(File.Exists(path));
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        } };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Failed, result.Status); Assert.Equal("InsufficientSpace", result.ErrorCode);
        Assert.NotNull(partial); Assert.False(File.Exists(partial));
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        AssertNoImportLeftovers(fixture);
        fixture.TargetQueue.Save();
        Assert.Equal(1, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task CancelWhileCopying_DeletesStagedFilesAndLeavesTheLibraryUnblocked()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("First"); fixture.AddSourceImage("Second"); fixture.SaveSource();
        using var cancellation = new CancellationTokenSource();
        var service = new LegacyLibraryMigrationService { StagingWriteForTest = _ => cancellation.Cancel() };
        LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings, cancellationToken: cancellation.Token);
        Assert.Equal(LegacyMigrationStatus.Canceled, result.Status);
        AssertNoImportLeftovers(fixture);
        fixture.TargetQueue.Save();
        Assert.Equal(1, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task CrashWhileStaging_IsDeletedAtTheNextLaunch()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("First"); fixture.AddSourceImage("Second"); fixture.SaveSource();
        int writes = 0;
        var service = new LegacyLibraryMigrationService { StagingWriteForTest = _ =>
        {
            if (Interlocked.Increment(ref writes) == 4) throw new SimulatedProcessCrash();
        } };
        await Assert.ThrowsAsync<SimulatedProcessCrash>(() => service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings));
        Assert.Single(Directory.EnumerateDirectories(fixture.TargetPaths.CapturesRoot, "import-*"));
        LegacyRecoveryResult recovery = await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        Assert.Equal(LegacyRecoveryStatus.Clean, recovery.Status); Assert.Equal(1, recovery.RolledBackCount);
        AssertNoImportLeftovers(fixture);
        Assert.Equal(1, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task UnfinishedPublicationThatStillCannotFinish_AsksAtLaunchAndAbandonRestoresTheLibrary()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Interrupted"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            if (phase == "AfterPayloadPublication") throw new SimulatedProcessCrash();
        } };
        await Assert.ThrowsAsync<SimulatedProcessCrash>(() => service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings));
        string index = Hash(fixture.TargetPaths.IndexFile);
        LegacyRecoveryResult recovery;
        // The persistent failure (a full disk, here a held index) survives the restart.
        using (new FileStream(fixture.TargetPaths.IndexFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            recovery = await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
            Assert.Equal(LegacyRecoveryStatus.DecisionRequired, recovery.Status);
            Assert.Equal("IoFailure", recovery.ErrorCode);
            Assert.Equal(1, fixture.ReopenTarget().Count); // The library itself still loads.
            LegacyRecoveryResult abandoned = await new LegacyLibraryMigrationService().AbandonPendingAsync(fixture.BaseTargetPaths);
            Assert.Equal(LegacyRecoveryStatus.Clean, abandoned.Status); Assert.Equal(1, abandoned.RolledBackCount);
        }
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        AssertNoImportLeftovers(fixture);
        Assert.Equal(LegacyRecoveryStatus.Clean, (await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths)).Status);
        Assert.Equal(1, fixture.ReopenTarget().Count);
    }

    [Fact]
    public async Task CompletedImport_KeepsOnlyACompactReceiptAndTheSettingsBackups()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Receipt"); fixture.AddSourceVideo(); fixture.SaveSource();
        LegacyMigrationResult result = await new LegacyLibraryMigrationService().ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        string operation = Directory.EnumerateDirectories(Path.Combine(fixture.TargetPaths.DataRoot, "library-migration")).Single();
        Assert.Equal(new[] { "journal.json", "legacy-settings.json", "store-settings-before.json" },
            Directory.EnumerateFileSystemEntries(operation).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray());
        JsonObject receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(operation, "journal.json")))!.AsObject();
        Assert.Equal("Completed", receipt["phase"]!.GetValue<string>());
        Assert.False(receipt.ContainsKey("indexJson"));
        Assert.False(receipt["records"]![0]!.AsObject().ContainsKey("files"));
        Assert.Empty(Directory.EnumerateFiles(fixture.TargetPaths.CapturesRoot, "*.import-part", SearchOption.AllDirectories));
        foreach (CaptureRecord record in fixture.TargetQueue.Records.Where(record => record.Id != fixture.Existing.Id))
            Assert.True(File.Exists(fixture.TargetQueue.GetFilePath(record, CaptureFileNames.Meta)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ImportedItem_IsNotImportedAgainAfterStoreDeletionOrALostReceipt(bool collidesWithStoreItem, bool deletedInStore)
    {
        using var fixture = new Fixture();
        fixture.AddSourceImage("Once", collidesWithStoreItem ? fixture.Existing.Id : null); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(1, (await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings)).ImportedCount);
        int expected = 2;
        if (deletedInStore)
        {
            Assert.True(fixture.TargetQueue.Remove(fixture.TargetQueue.Records.Single(record => record.Id != fixture.Existing.Id).Id));
            fixture.TargetQueue.Save(); expected = 1;
        }
        else
        {
            // Without its receipt the deterministic id and the owned import folder still identify the copy.
            string operations = Path.Combine(fixture.TargetPaths.DataRoot, "library-migration");
            string receipt = Directory.EnumerateDirectories(operations).Single();
            Directory.Move(receipt, Path.Combine(fixture.TargetPaths.DataRoot, "lost-receipt"));
        }
        LegacyMigrationResult again = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, again.Status);
        Assert.Equal(0, again.ImportedCount); Assert.Equal(1, again.SkippedCount);
        Assert.Equal(expected, fixture.TargetQueue.Count);
    }

    [Fact]
    public async Task FolderDeletedInStoreAfterImport_IsNotRecreatedWithoutNewCaptures()
    {
        using var fixture = new Fixture();
        CaptureFolder legacy = await fixture.SourceQueue.CreateFolderAsync("Legacy only", createdAt: fixture.Created);
        CaptureFolder used = await fixture.SourceQueue.CreateFolderAsync("Legacy used", createdAt: fixture.Created);
        fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(LegacyMigrationStatus.Completed, (await service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings)).Status);
        foreach (CaptureFolder folder in fixture.TargetQueue.Folders.ToArray())
            await fixture.TargetQueue.DeleteFolderScopeAsync(CaptureFolderScope.ForFolder(folder.Id));
        Assert.Empty(fixture.TargetQueue.Folders);
        CaptureRecord added = fixture.AddSourceImage("New after first import");
        await fixture.SourceQueue.MoveCapturesAsync([added.Id], used.Id);
        fixture.SaveSource();
        LegacyMigrationResult again = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
            fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(1, again.ImportedCount);
        Assert.Equal(["Legacy used"], fixture.TargetQueue.Folders.Select(folder => folder.Name).ToArray());
        Assert.DoesNotContain(fixture.TargetQueue.Folders, folder => folder.Name == legacy.Name);
    }

    [Fact]
    public async Task ScanningPass_ReportsProgressForEveryRecord()
    {
        using var fixture = new Fixture();
        for (int index = 0; index < 5; index++) fixture.AddSourceImage("Scanned " + index);
        fixture.SaveSource();
        var progress = new ImmediateProgress();
        Assert.Equal(LegacyMigrationStatus.Completed, (await new LegacyLibraryMigrationService().ImportAsync(
            fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings, progress)).Status);
        Assert.Contains(progress.Values, value => value is { Stage: LegacyMigrationStage.Scanning, CompletedItems: 5, TotalItems: 5 });
        Assert.Contains(progress.Values, value => value is { Stage: LegacyMigrationStage.Copying, CompletedItems: 5, TotalItems: 5 });
        Assert.Equal(LegacyMigrationStage.Publishing, progress.Values[^1].Stage);
    }
}
