using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>Reviewer reproductions (2026-10-13 items 4-6) written against the original public API.</summary>
public sealed partial class LegacyLibraryMigrationTests
{
    [Fact]
    public async Task Probe_PersistentPublicationFailure_LeavesTheLibraryUsableAndStartupRecoverable()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Blocked"); fixture.SaveSource();
        string index = Hash(fixture.TargetPaths.IndexFile);
        FileStream? blocker = null;
        var service = new LegacyLibraryMigrationService { CheckpointForTest = phase =>
        {
            // A reader without delete sharing makes every index replacement fail, like a full disk.
            if (phase == "BeforePublication") blocker = new FileStream(fixture.TargetPaths.IndexFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        } };
        try
        {
            LegacyMigrationResult result = await service.ImportAsync(fixture.SourcePaths.DataRoot, fixture.TargetPaths,
                fixture.TargetQueue, fixture.TargetSettings);
            Assert.Equal(LegacyMigrationStatus.Failed, result.Status);
            Assert.NotEqual("RecoveryRequired", result.ErrorCode);
            // The next launch still starts while the failure persists.
            await new LegacyLibraryMigrationService().RecoverPendingAsync(fixture.BaseTargetPaths);
        }
        finally { blocker?.Dispose(); }
        Assert.Equal(index, Hash(fixture.TargetPaths.IndexFile));
        fixture.TargetQueue.Save(); // The live library is not left blocked for later captures.
        Assert.Equal(1, fixture.ReopenTarget().Count);
        Assert.Empty(Directory.EnumerateFiles(fixture.TargetPaths.CapturesRoot, "*.import-part", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(fixture.TargetPaths.CapturesRoot, "import-*"));
    }

    [Theory]
    [InlineData("ocr")]
    [InlineData("move")]
    public async Task Probe_TargetSideEditOfImportedItem_IsNotImportedAgainAndNeverConflicts(string edit)
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Imported once"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(LegacyMigrationStatus.Completed, (await service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings)).Status);
        CaptureRecord imported = fixture.TargetQueue.Records.Single(record => record.Id != fixture.Existing.Id);
        if (edit == "ocr")
        {
            imported.OcrText = "자동 OCR 결과"; imported.OcrContentRevision = imported.ContentRevision;
            fixture.TargetQueue.SaveRecordMetaOrThrow(imported);
        }
        else
        {
            CaptureFolder chosen = await fixture.TargetQueue.CreateFolderAsync("Store에서 옮김", createdAt: fixture.Created);
            await fixture.TargetQueue.MoveCapturesAsync([imported.Id], chosen.Id);
        }
        fixture.TargetQueue.Save();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            LegacyMigrationResult again = await service.ImportAsync(fixture.SourcePaths.DataRoot,
                fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
            Assert.Equal(LegacyMigrationStatus.Completed, again.Status);
            Assert.Equal(0, again.ImportedCount); Assert.Equal(1, again.SkippedCount);
            Assert.Equal(2, fixture.TargetQueue.Count);
        }
    }

    [Fact]
    public async Task Probe_CompletedImport_KeepsNoSecondCopyOfTheLibrary()
    {
        using var fixture = new Fixture(); fixture.AddSourceImage("Staged"); fixture.AddSourceVideo(); fixture.SaveSource();
        Assert.Equal(LegacyMigrationStatus.Completed, (await new LegacyLibraryMigrationService().ImportAsync(
            fixture.SourcePaths.DataRoot, fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings)).Status);
        string operations = Path.Combine(fixture.TargetPaths.DataRoot, "library-migration");
        Assert.Empty(Directory.EnumerateDirectories(operations, "staging", SearchOption.AllDirectories));
        Assert.DoesNotContain(Directory.EnumerateFiles(operations, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path) is CaptureFileNames.Original or CaptureFileNames.VideoSource);
    }

    [Fact]
    public async Task Probe_ChangedStorageLocationAfterImport_StillImportsAndSkipsPreviousItems()
    {
        using var fixture = new Fixture(true); fixture.AddSourceImage("Before move"); fixture.SaveSource();
        var service = new LegacyLibraryMigrationService();
        Assert.Equal(LegacyMigrationStatus.Completed, (await service.ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings)).Status);
        string moved = Path.Combine(Path.GetDirectoryName(fixture.TargetPaths.CapturesRoot)!, "moved-library");
        Directory.Move(fixture.TargetPaths.CapturesRoot, moved);
        fixture.TargetSettings.Queue.CapturesDirectoryOverride = moved;
        new SettingsStore(fixture.BaseTargetPaths, NullLogger<SettingsStore>.Instance).Save(fixture.TargetSettings);
        AppPaths relocated = fixture.BaseTargetPaths.WithCapturesRoot(moved);
        var queue = new CaptureQueue(relocated, fixture.TargetSettings.Queue, NullLogger<CaptureQueue>.Instance);
        queue.Load();
        LegacyMigrationResult again = await service.ImportAsync(fixture.SourcePaths.DataRoot, relocated, queue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, again.Status);
        Assert.Equal(0, again.ImportedCount); Assert.Equal(1, again.SkippedCount); Assert.Equal(2, queue.Count);
    }

    [Theory]
    [InlineData("missing-original")]
    [InlineData("annotations-without-layers")]
    [InlineData("missing-asset")]
    [InlineData("corrupt-layers")]
    [InlineData("corrupt-settings")]
    [InlineData("torn-index-with-backup")]
    public async Task Probe_OneBadRecordOrFile_DoesNotAbortTheWholeImport(string kind)
    {
        using var fixture = new Fixture();
        CaptureRecord good = fixture.AddSourceImage("Good");
        CaptureRecord bad = fixture.AddSourceImage("Bad");
        fixture.SaveSource();
        switch (kind)
        {
            case "missing-original": global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(fixture.SourceQueue.GetFilePath(bad, CaptureFileNames.Original)); break;
            case "annotations-without-layers": global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(fixture.SourceQueue.GetFilePath(bad, CaptureFileNames.Layers)); break;
            case "missing-asset": global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(fixture.SourceQueue.GetFilePath(bad, "asset-01.png")); break;
            case "corrupt-layers": File.WriteAllText(fixture.SourceQueue.GetFilePath(bad, CaptureFileNames.Layers), "{torn"); break;
            case "corrupt-settings": File.WriteAllText(fixture.SourcePaths.SettingsFile, "{torn"); break;
            case "torn-index-with-backup": fixture.SourceQueue.Save(); File.WriteAllText(fixture.SourcePaths.IndexFile, "{torn"); break;
        }
        LegacyMigrationResult result = await new LegacyLibraryMigrationService().ImportAsync(fixture.SourcePaths.DataRoot,
            fixture.TargetPaths, fixture.TargetQueue, fixture.TargetSettings);
        Assert.Equal(LegacyMigrationStatus.Completed, result.Status);
        Assert.NotNull(fixture.TargetQueue.Find(good.Id));
        bool badUsable = kind is "corrupt-settings" or "torn-index-with-backup";
        Assert.Equal(badUsable ? 2 : 1, result.ImportedCount);
        Assert.Equal(badUsable, fixture.TargetQueue.Find(bad.Id) is not null);
    }
}
