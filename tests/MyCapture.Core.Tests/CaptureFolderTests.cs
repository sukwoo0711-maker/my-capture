using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureFolderTests
{
    [Fact]
    public async Task NestedFolders_RenameMoveAndScope_KeepPhysicalPathsAndCreationDates()
    {
        var f = new Fixture();
        DateTimeOffset firstDay = new(2026, 10, 8, 23, 0, 0, TimeSpan.FromHours(9));
        var first = await f.Queue.CreateFolderAsync("review", createdAt: firstDay);
        var child = await f.Queue.CreateFolderAsync("nested", first.Id, firstDay.AddDays(1));
        var other = await f.Queue.CreateFolderAsync("work", createdAt: firstDay.AddDays(1));
        var capture = f.Add(firstDay.AddDays(-10));
        string directory = f.Queue.GetDirectory(capture);
        await f.Queue.MoveCapturesAsync([capture.Id], child.Id);
        Assert.Equal(first.CreatedDate, f.Queue.GetFolderDate(child.Id));
        Assert.Single(f.Queue.RecordsInScope(CaptureFolderScope.ForFolder(first.Id)));
        await f.Queue.MoveFolderAsync(child.Id, other.Id);
        await f.Queue.RenameFolderAsync(child.Id, "renamed");
        Assert.Empty(f.Queue.RecordsInScope(CaptureFolderScope.ForFolder(first.Id)));
        Assert.Equal("work / renamed", f.Queue.GetFolderDisplayPath(child.Id));
        Assert.Equal(other.CreatedDate, f.Queue.GetFolderDate(child.Id));
        Assert.Equal(firstDay.AddDays(1), child.CreatedAt);
        Assert.Equal(directory, f.Queue.GetDirectory(capture));
        Assert.True(File.Exists(f.Queue.GetFilePath(capture, CaptureFileNames.Original)));
        var restored = f.Reload();
        Assert.Equal(child.Id, Assert.Single(restored.Records).FolderId);
        Assert.Equal("work / renamed", restored.GetFolderDisplayPath(child.Id));
    }

    [Fact]
    public async Task FolderAssignment_ProtectsAgeCapacityAndLegacyReader_ThenRestoresManualPin()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("archive");
        var archived = f.Add(DateTimeOffset.Now.AddDays(-30));
        var loose = f.Add(DateTimeOffset.Now.AddDays(-29));
        await f.Queue.MoveCapturesAsync([archived.Id], folder.Id);
        Assert.True(archived.IsPinned);
        Assert.False(archived.IsManuallyPinned);
        Assert.Equal(1, f.Queue.ExpireHistory(DateTimeOffset.Now));
        Assert.NotNull(f.Queue.Find(archived.Id));
        Assert.Null(f.Queue.Find(loose.Id));
        f.Queue.UpdateLimits(new QueueSettings { MaxItems = 1, MaxBytes = 1 });
        Assert.NotNull(f.Queue.Find(archived.Id));
        await f.Queue.MoveCapturesAsync([archived.Id], null);
        Assert.False(archived.IsPinned);
        Assert.Equal(0, f.Queue.ExpireHistory(DateTimeOffset.Now));
        Assert.NotNull(archived.RetentionStartedAt);
        Assert.Equal(archived.RetentionStartedAt, f.Reload().Find(archived.Id)!.RetentionStartedAt);
        Assert.Equal(1, f.Queue.ExpireHistory(DateTimeOffset.Now.AddDays(8)));

        var pinned = f.Add(DateTimeOffset.Now.AddDays(-30), pinned: true);
        await f.Queue.MoveCapturesAsync([pinned.Id], folder.Id);
        await f.Queue.MoveCapturesAsync([pinned.Id], null);
        Assert.True(pinned.IsPinned);
        Assert.Equal(0, f.Queue.ExpireHistory(DateTimeOffset.Now));
    }

    [Fact]
    public async Task LegacyRewriteAndBackupRestore_PreserveEmptyFoldersMembershipAndDestination()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("kept");
        await f.Queue.CreateFolderAsync("empty", folder.Id);
        var capture = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([capture.Id], folder.Id);
        await f.Queue.SetNewCaptureFolderAsync(folder.Id, true);
        JsonObject legacy = JsonNode.Parse(File.ReadAllText(f.IndexFile))!.AsObject();
        Assert.Equal(2, legacy["schemaVersion"]!.GetValue<int>());
        legacy.Remove("folderState");
        foreach (JsonObject record in legacy["records"]!.AsArray().OfType<JsonObject>())
        {
            Assert.True(record["isPinned"]!.GetValue<bool>());
            record.Remove("folderId"); record.Remove("folderSequence"); record.Remove("folderPinBeforeAssignment");
        }
        File.WriteAllText(f.IndexFile, legacy.ToJsonString());
        CaptureQueue restored = f.Reload();
        Assert.Equal(2, restored.Folders.Count);
        Assert.Equal(folder.Id, restored.Find(capture.Id)!.FolderId);
        Assert.True(restored.UseCurrentFolderForNewCaptures);
        Assert.Equal(folder.Id, restored.DefaultCaptureFolderId);
        restored.Save();
        await restored.RenameFolderAsync(folder.Id, "latest");
        File.WriteAllText(f.IndexFile, "{invalid");
        CaptureQueue recovered = f.Reload();
        Assert.Equal(2, recovered.Folders.Count);
        Assert.Equal(folder.Id, recovered.Find(capture.Id)!.FolderId);
    }

    [Fact]
    public async Task PreparedCapture_UsesTheSelectedDestinationAtStart_AndDisabledOptionPersists()
    {
        var f = new Fixture();
        var first = await f.Queue.CreateFolderAsync("first");
        var second = await f.Queue.CreateFolderAsync("second");
        await f.Queue.SetNewCaptureFolderAsync(first.Id, true);
        var pending = f.Record(DateTimeOffset.Now);
        f.Queue.PrepareNewCaptureFolder(pending);
        await f.Queue.SetNewCaptureFolderAsync(second.Id, true);
        f.Add(pending);
        Assert.Equal(first.Id, pending.FolderId);
        Assert.Equal(second.Id, f.Add(DateTimeOffset.Now).FolderId);
        await f.Queue.SetNewCaptureFolderAsync(null, false);
        Assert.Null(f.Add(DateTimeOffset.Now).FolderId);
        Assert.False(f.Reload().UseCurrentFolderForNewCaptures);
    }

    [Fact]
    public async Task SequenceSurvivesRestartAndMoves_TitleWins_AndLeavingFolderRestoresPinToggle()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("names");
        var first = f.Add(DateTimeOffset.Now);
        var second = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([first.Id, second.Id], folder.Id);
        Assert.Equal("myCapture01.png", f.Queue.GetExportFileName(first));
        Assert.Equal("myCapture02.png", f.Queue.GetExportFileName(second));
        second.Title = "../CON:review";
        Assert.Equal(".._CON_review.png", f.Queue.GetExportFileName(second));
        Assert.True(f.Queue.TogglePin(first.Id));
        Assert.True(first.IsPinned);
        await f.Queue.MoveCapturesAsync([first.Id], null);
        Assert.True(first.IsPinned);
        var loaded = f.Reload();
        var third = f.Record(DateTimeOffset.Now);
        loaded.Add(third);
        await loaded.MoveCapturesAsync([third.Id], folder.Id);
        Assert.Equal("myCapture03.png", loaded.GetExportFileName(third));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("name.")]
    [InlineData("a\\b")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    public async Task UnsafeNamesAreRejectedWithoutChangingTheCatalog(string name)
    {
        var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Queue.CreateFolderAsync(name));
        Assert.Empty(f.Queue.Folders);
    }

    [Fact]
    public async Task CycleDuplicateAndDepthValidation_PreventInvalidImportedOrMovedTrees()
    {
        var f = new Fixture();
        var root = await f.Queue.CreateFolderAsync("root");
        var child = await f.Queue.CreateFolderAsync("child", root.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Queue.MoveFolderAsync(root.Id, child.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Queue.CreateFolderAsync("CHILD", root.Id));
        var last = child;
        for (int depth = 2; depth < CaptureFolderNaming.MaximumDepth; depth++)
            last = await f.Queue.CreateFolderAsync("level" + depth, last.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Queue.CreateFolderAsync("too deep", last.Id));
        Assert.Equal(CaptureFolderNaming.MaximumDepth, f.Queue.GetFolderPath(last.Id).Count);
    }

    [Fact]
    public async Task FailedIndexPublication_RollsBackFolderAndRecordMetadata_WithoutChangingPixels()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("target");
        var record = f.Add(DateTimeOffset.Now);
        byte[] pixels = File.ReadAllBytes(f.Queue.GetFilePath(record, CaptureFileNames.Rendered));
        int failures = 0;
        f.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path == f.IndexFile && Interlocked.Increment(ref failures) == 1)
                throw new IOException("synthetic folder index failure");
        };
        await Assert.ThrowsAsync<IOException>(() => f.Queue.MoveCapturesAsync([record.Id], folder.Id));
        Assert.Null(record.FolderId);
        Assert.False(record.IsPinned);
        Assert.Null(f.Reload().Find(record.Id)!.FolderId);
        Assert.Equal(pixels, File.ReadAllBytes(f.Queue.GetFilePath(record, CaptureFileNames.Rendered)));
    }

    [Fact]
    public async Task DeleteSubtree_RemovesAllNestedCapturesAndEmptyFolders_ButKeepsOtherDates()
    {
        var f = new Fixture();
        DateTimeOffset day = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var root = await f.Queue.CreateFolderAsync("remove", createdAt: day);
        var child = await f.Queue.CreateFolderAsync("child", root.Id, day);
        await f.Queue.CreateFolderAsync("empty", child.Id, day);
        var other = await f.Queue.CreateFolderAsync("keep", createdAt: day.AddDays(1));
        var image = f.Add(day); var video = f.Add(day, video: true); var keep = f.Add(day.AddDays(1));
        await f.Queue.MoveCapturesAsync([image.Id], root.Id);
        await f.Queue.MoveCapturesAsync([video.Id], child.Id);
        await f.Queue.MoveCapturesAsync([keep.Id], other.Id);
        await f.Queue.SetNewCaptureFolderAsync(child.Id, true);
        var scope = CaptureFolderScope.ForFolder(root.Id);
        var preview = f.Queue.PreviewFolderDeletion(scope);
        Assert.Equal(new CaptureFolderDeletionPreview(3, 2, 1, 1), preview);
        var result = await f.Queue.DeleteFolderScopeAsync(scope);
        Assert.Equal(new CaptureFolderDeletionResult(3, 2, 0, 0), result);
        Assert.Single(f.Queue.Folders);
        Assert.Equal(keep.Id, Assert.Single(f.Queue.Records).Id);
        Assert.Null(f.Queue.DefaultCaptureFolderId);
        Assert.False(f.Queue.UseCurrentFolderForNewCaptures);
        Assert.Single(f.Reload().Records);
    }

    [Fact]
    public async Task DeleteDate_UsesFolderCreationDate_AndPartialFailureKeepsRemainingHierarchy()
    {
        var f = new Fixture();
        DateTimeOffset day = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var folder = await f.Queue.CreateFolderAsync("review", createdAt: day);
        var archived = f.Add(day.AddDays(-10));
        var loose = f.Add(day); var otherDay = f.Add(day.AddDays(1));
        await f.Queue.MoveCapturesAsync([archived.Id], folder.Id);
        var scope = CaptureFolderScope.ForDate(DateOnly.FromDateTime(day.DateTime));
        using (f.Queue.AcquireEvictionLease(archived.Id))
        {
            var partial = await f.Queue.DeleteFolderScopeAsync(scope);
            Assert.Equal(1, partial.RemovedCaptures);
            Assert.Equal(1, partial.RemainingCaptures);
            Assert.NotNull(f.Queue.FindFolder(folder.Id));
        }
        var complete = await f.Queue.DeleteFolderScopeAsync(scope);
        Assert.Equal(1, complete.RemovedCaptures);
        Assert.Equal(otherDay.Id, Assert.Single(f.Queue.Records).Id);
        Assert.Empty(f.Queue.Folders);
    }

    [Fact]
    public async Task CancelBeforeDelete_ChangesNeitherMetadataNorMedia()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("cancel");
        var record = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([record.Id], folder.Id);
        string before = File.ReadAllText(f.IndexFile);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Queue.DeleteFolderScopeAsync(
            CaptureFolderScope.ForFolder(folder.Id), cancellationToken: new CancellationToken(true)));
        Assert.Equal(before, File.ReadAllText(f.IndexFile));
        Assert.NotNull(f.Queue.Find(record.Id));
    }

    [Fact]
    public void QueueSettings_CloneExportAndImport_PreservePrefixAndDefaultFolder()
    {
        var f = new Fixture();
        var settings = new AppSettings { Queue = { FolderFilePrefix = "review",
            UseCurrentFolderForNewCaptures = true, DefaultCaptureFolderId = Guid.NewGuid() } };
        AppSettings clone = settings.DeepClone();
        Assert.Equal(settings.Queue.DefaultCaptureFolderId, clone.Queue.DefaultCaptureFolderId);
        var store = new SettingsStore(f.Paths, NullLogger<SettingsStore>.Instance);
        string backup = Path.Combine(f.Paths.DataRoot, "settings-backup.json");
        store.ExportTo(settings, backup);
        AppSettings imported = store.ImportFrom(backup);
        Assert.Equal("review", imported.Queue.FolderFilePrefix);
        Assert.True(imported.Queue.UseCurrentFolderForNewCaptures);
        Assert.Equal(settings.Queue.DefaultCaptureFolderId, imported.Queue.DefaultCaptureFolderId);
    }

    [Fact]
    public async Task PendingOriginalRecoveryKeepsItsFolderEvenWhenItIsNewerThanTheIndex()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Pending");
        await f.Queue.SetNewCaptureFolderAsync(folder.Id, true);
        CaptureRecord pending = f.Record(DateTimeOffset.Now);
        pending.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(pending.Id, pending.CreatedAt);
        f.Queue.PrepareNewCaptureFolder(pending);
        Directory.CreateDirectory(f.Queue.GetDirectory(pending));
        File.WriteAllBytes(f.Queue.GetFilePath(pending, CaptureFileNames.Original), [1, 2]);
        File.WriteAllText(f.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending), JsonSerializer.Serialize(pending));
        CaptureQueue recovered = f.Reload();
        CaptureRecord actual = Assert.Single(recovered.Records);
        Assert.Equal(folder.Id, actual.FolderId);
        Assert.Equal(1, actual.FolderSequence);
        Assert.True(actual.IsPinned);
        Assert.Equal(2, recovered.FindFolder(folder.Id)!.NextSequence);
    }

    [Fact]
    public async Task FailedFolderRenamePreservesUnrelatedPinAndPendingSequenceAllocations()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Before");
        var unrelated = f.Add(DateTimeOffset.Now);
        await f.Queue.SetNewCaptureFolderAsync(folder.Id, true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int writes = 0;
        f.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path != f.IndexFile || Interlocked.Increment(ref writes) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            throw new IOException("synthetic rename failure");
        };
        Task<bool> rename = f.Queue.RenameFolderAsync(folder.Id, "After");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        f.Queue.TogglePin(unrelated.Id);
        var pending = f.Record(DateTimeOffset.Now);
        f.Queue.PrepareNewCaptureFolder(pending);
        release.Set();
        await Assert.ThrowsAsync<IOException>(() => rename);
        Assert.True(unrelated.IsManuallyPinned);
        Assert.Equal("Before", f.Queue.FindFolder(folder.Id)!.Name);
        var next = f.Record(DateTimeOffset.Now);
        f.Queue.PrepareNewCaptureFolder(next);
        Assert.NotEqual(pending.FolderSequence, next.FolderSequence);
        Assert.True(f.Reload().Find(unrelated.Id)!.IsManuallyPinned);
    }

    [Fact]
    public async Task StaleDeletionConfirmationCannotDeleteNewlyAddedCaptures()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Confirmed");
        var first = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([first.Id], folder.Id);
        var added = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([added.Id], folder.Id);
        await Assert.ThrowsAsync<CaptureFolderChangedException>(() => f.Queue.DeleteFolderScopeAsync(
            CaptureFolderScope.ForFolder(folder.Id), expectedCaptureIds: [first.Id], expectedFolderIds: [folder.Id]));
        Assert.Equal(2, f.Queue.Count);
        Assert.Single(f.Queue.Folders);
    }

    [Fact]
    public async Task RelocatedLibraryImportAndForeignSettingsKeepTheFolderCatalog()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Portable");
        await f.Queue.CreateFolderAsync("Empty", folder.Id);
        var record = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([record.Id], folder.Id);
        var imported = new Fixture();
        string sourceRoot = ValidatedFilePaths.DirectoryRoot(f.Paths.DataRoot);
        string destinationRoot = ValidatedFilePaths.DirectoryRoot(imported.Paths.DataRoot);
        foreach (string directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string source = ValidatedFilePaths.DirectoryWithinRoot(sourceRoot, directory);
            string destination = ValidatedFilePaths.DirectoryWithinRoot(destinationRoot, Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(destination);
        }
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string source = ValidatedFilePaths.FileWithinRoot(sourceRoot, file);
            string destination = ValidatedFilePaths.FileWithinRoot(destinationRoot, Path.GetRelativePath(sourceRoot, source));
            File.Copy(source, destination);
        }
        CaptureQueue queue = imported.Reload();
        Assert.Equal(2, queue.Folders.Count);
        Assert.Equal(folder.Id, Assert.Single(queue.Records).FolderId);
        queue.UpdateLimits(new QueueSettings { DefaultCaptureFolderId = Guid.NewGuid(), UseCurrentFolderForNewCaptures = true });
        Assert.Null(queue.DefaultCaptureFolderId);
        Assert.Equal(2, imported.Reload().Folders.Count);
    }

    [Fact]
    public async Task LeavingAnOldFolderRestartsRetentionAndEvictionOrder_AndLegacyTimestampStillExpires()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("archive");
        var old = f.Add(DateTimeOffset.Now.AddDays(-30));
        await f.Queue.MoveCapturesAsync([old.Id], folder.Id);
        var recent = f.Add(DateTimeOffset.Now.AddHours(-2));
        await f.Queue.MoveCapturesAsync([old.Id], null);
        Assert.Equal(0, f.Queue.ExpireHistory(DateTimeOffset.Now));
        f.Queue.UpdateLimits(new QueueSettings { MaxItems = 2 });
        var latest = f.Add(DateTimeOffset.Now);
        Assert.NotNull(f.Queue.Find(old.Id));
        Assert.NotNull(f.Queue.Find(latest.Id));
        Assert.Null(f.Queue.Find(recent.Id));
        var restored = f.Reload();
        Assert.Equal(old.RetentionStartedAt, restored.Find(old.Id)!.RetentionStartedAt);
        Assert.Equal(0, restored.ExpireHistory(DateTimeOffset.Now));
        Assert.Equal(2, restored.ExpireHistory(DateTimeOffset.Now.AddDays(8)));
        CaptureRecord legacy = JsonSerializer.Deserialize<CaptureRecord>("{\"CreatedAt\":\"2020-01-01T00:00:00Z\"}")!;
        Assert.Null(legacy.RetentionStartedAt);
        Assert.Equal(legacy.CreatedAt, legacy.RetentionReference);
    }

    [Fact]
    public async Task ThreeHundredArchivedCapturesOverTwoGiB_DoNotEvictRecentHistoryOrReportPinPressure()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("archive");
        await f.Queue.SetNewCaptureFolderAsync(folder.Id, true);
        for (int index = 0; index < 300; index++)
        {
            var record = f.Record(DateTimeOffset.Now.AddDays(-30).AddSeconds(index));
            record.TotalBytes = 8L * 1024 * 1024;
            f.Add(record);
        }
        await f.Queue.SetNewCaptureFolderAsync(null, false);
        f.Queue.UpdateLimits(new QueueSettings { MaxItems = 3, MaxBytes = 2L * 1024 * 1024 * 1024 });
        var first = f.Add(DateTimeOffset.Now.AddMinutes(-2));
        var second = f.Add(DateTimeOffset.Now.AddMinutes(-1));
        var third = f.Add(DateTimeOffset.Now);
        Assert.Equal(303, f.Queue.Count);
        Assert.Equal(3, f.Queue.HistoryCount);
        Assert.Equal(12, f.Queue.HistoryBytes);
        Assert.Equal(300 * 8L * 1024 * 1024, f.Queue.FolderBytes);
        Assert.True(f.Queue.IsFolderStorageLarge);
        Assert.False(f.Queue.IsOverCapacityDueToPins);
        Assert.All(new[] { first, second, third }, r => Assert.NotNull(f.Queue.Find(r.Id)));
        f.Queue.ExpireHistory(DateTimeOffset.Now);
        Assert.Equal(303, f.Queue.Count);
        Assert.Equal(303, f.Reload().Count);
    }

    [Fact]
    public async Task NewFolderUsesSelectedPastDateOrParentsDate()
    {
        var f = new Fixture();
        DateOnly past = new(2020, 2, 3);
        var root = await f.Queue.CreateFolderInScopeAsync("past", CaptureFolderScope.ForDate(past));
        var child = await f.Queue.CreateFolderInScopeAsync("child", CaptureFolderScope.ForFolder(root.Id));
        Assert.Equal(past, root.CreatedDate);
        Assert.Equal(past, child.CreatedDate);
        Assert.Equal(past, f.Queue.GetFolderDate(child.Id));
    }

    [Fact]
    public async Task FailedFolderRemovalRestoresRetentionTimestampAlongWithAssignment()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("kept");
        var record = f.Add(DateTimeOffset.Now.AddDays(-30));
        await f.Queue.MoveCapturesAsync([record.Id], folder.Id);
        int attempts = 0;
        f.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path == f.IndexFile && ++attempts == 1) throw new IOException("synthetic publication failure");
        };
        await Assert.ThrowsAsync<IOException>(() => f.Queue.MoveCapturesAsync([record.Id], null));
        Assert.Equal(folder.Id, record.FolderId);
        Assert.Null(record.RetentionStartedAt);
        Assert.Equal(0, f.Queue.ExpireHistory(DateTimeOffset.Now));
    }

    private sealed class Fixture
    {
        internal AppPaths Paths { get; }
        internal string IndexFile => ValidatedFilePaths.FileWithinRoot(Paths.DataRoot, Paths.IndexFile, ".json");
        internal CaptureQueue Queue { get; }
        internal Fixture()
        {
            string root = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ??
                Path.Combine(Path.GetTempPath(), "MyCapture-Folder410-tests");
            Paths = AppPaths.CreateForRoot(ValidatedFilePaths.DirectoryWithinRoot(root, "core-" + Guid.NewGuid().ToString("N")));
            Paths.EnsureCreated();
            Queue = NewQueue();
            // Retain every synthetic payload for diagnostics, including logically removed ones.
            Queue.Evicted += (_, args) =>
            {
                string directory = Queue.GetDirectory(args.Record);
                if (Directory.Exists(directory)) Directory.Move(directory, directory + "-removed-" + Guid.NewGuid().ToString("N"));
            };
        }
        private CaptureQueue NewQueue() => new(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        internal CaptureQueue Reload() { var queue = NewQueue(); queue.Load(); return queue; }
        internal CaptureRecord Record(DateTimeOffset created, bool pinned = false, bool video = false) => new()
        {
            Width = 2, Height = 2, CreatedAt = created, UpdatedAt = created, IsPinned = pinned,
            MediaKind = video ? CaptureMediaKind.Video : CaptureMediaKind.Image,
            DurationMs = video ? 1000 : 0, FrameRate = video ? 30 : 0, FrameCount = video ? 30 : 0, TotalBytes = 4,
        };
        internal CaptureRecord Add(DateTimeOffset created, bool pinned = false, bool video = false) =>
            Add(Record(created, pinned, video));
        internal CaptureRecord Add(CaptureRecord record)
        {
            Queue.Add(record);
            Directory.CreateDirectory(Queue.GetDirectory(record));
            File.WriteAllBytes(Queue.GetFilePath(record, record.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Original), [1, 2]);
            File.WriteAllBytes(Queue.GetFilePath(record, record.IsVideo ? CaptureFileNames.VideoRendered : CaptureFileNames.Rendered), [3, 4]);
            Queue.SaveRecordMetaOrThrow(record);
            Queue.Save();
            return record;
        }
    }
}
