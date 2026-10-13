using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecoverableLibraryServiceTests
{
    [Fact]
    public async Task SuccessfulDelete_RestartsAndRestoresCompleteCaptureAndRetention()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        Assert.False(Directory.Exists(fixture.Original));
        var restarted = new RecoverableLibraryService(fixture.Queue, fixture.Paths, fixture.Trash, NullLogger.Instance);
        var entry = Assert.Single(restarted.RecentDeleted());
        await restarted.RestoreAsync(entry.Token);
        Assert.Equal("original pixels", File.ReadAllText(Path.Combine(fixture.Original, CaptureFileNames.Original)));
        Assert.Equal("annotation layer", File.ReadAllText(Path.Combine(fixture.Original, "annotation.json")));
        Assert.Equal(fixture.Record.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id)!.RetentionStartedAt);
        Assert.Empty(restarted.RecentDeleted());
    }

    [Fact]
    public void RecycleFailure_PreservesOriginalAndNormalIndex()
    {
        var fixture = new Fixture(); fixture.Trash.FailRecycle = true;
        Assert.False(fixture.Queue.Remove(fixture.Record.Id));
        Assert.True(Directory.Exists(fixture.Original));
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Empty(fixture.Service.RecentDeleted());
    }

    [Fact]
    public void IntentWriteFailure_NeverCallsShell()
    {
        var fixture = new Fixture();
        fixture.Service.BeforeJournalWriteForTest = _ => throw new IOException("intent unavailable");
        Assert.False(fixture.Queue.Remove(fixture.Record.Id));
        Assert.Equal(0, fixture.Trash.Moves);
        Assert.True(Directory.Exists(fixture.Original));
    }

    [Fact]
    public void ReceiptWriteFailure_RollsBackAlreadyMovedDirectory()
    {
        var fixture = new Fixture();
        fixture.Service.BeforeJournalWriteForTest = phase => { if (phase == "recycled") throw new IOException("receipt unavailable"); };
        Assert.False(fixture.Queue.Remove(fixture.Record.Id));
        Assert.True(Directory.Exists(fixture.Original));
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Equal(1, fixture.Trash.Restores);
    }

    [Fact]
    public async Task ReceiptAndRollbackFailure_IntentSupportsRecoveryAfterRestart_WithoutPhantomIndex()
    {
        var fixture = new Fixture(); fixture.Trash.FailRestore = true;
        fixture.Service.BeforeJournalWriteForTest = phase => { if (phase == "recycled") throw new IOException("receipt unavailable"); };
        Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        fixture.Trash.FailRestore = false;
        var restarted = new RecoverableLibraryService(fixture.Queue, fixture.Paths, fixture.Trash, NullLogger.Instance);
        await restarted.RestoreAsync(Assert.Single(restarted.RecentDeleted()).Token);
        Assert.True(Directory.Exists(fixture.Original));
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
    }

    [Fact]
    public void ReceiptWriteAndPostRestoreFailure_PreservesVerifiedOriginalAndNormalIndex()
    {
        var fixture = new Fixture(); fixture.Trash.FailAfterRestore = true;
        fixture.Service.BeforeJournalWriteForTest = phase => { if (phase == "recycled") throw new IOException("receipt unavailable"); };
        Assert.False(fixture.Queue.Remove(fixture.Record.Id));
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Equal("original pixels", File.ReadAllText(Path.Combine(fixture.Original, CaptureFileNames.Original)));
        Assert.Empty(fixture.Service.RecentDeleted());
    }

    [Fact]
    public async Task RestoreCollision_PreservesConflictingOriginalAndRecycledCapture()
    {
        var fixture = new Fixture(); Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        var entry = Assert.Single(fixture.Service.RecentDeleted());
        Directory.CreateDirectory(fixture.Original);
        File.WriteAllText(Path.Combine(fixture.Original, "collision.txt"), "preserve this");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RestoreAsync(entry.Token));
        Assert.Equal("preserve this", File.ReadAllText(Path.Combine(fixture.Original, "collision.txt")));
        Assert.Single(fixture.Service.RecentDeleted());
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        Assert.True(Directory.Exists(fixture.Trash.Receipt!.RecycledPath));
    }

    [Fact]
    public async Task RestoreInterruptedAfterShellMove_ReconcilesIndexOnStartup()
    {
        var fixture = new Fixture(); Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        fixture.Trash.RestoreDirectory(fixture.Trash.Receipt!);
        var restarted = new RecoverableLibraryService(fixture.Queue, fixture.Paths, fixture.Trash, NullLogger.Instance);
        await restarted.RecoverInterruptedRestoresAsync();
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Empty(restarted.RecentDeleted());
    }

    [Fact]
    public async Task AutomaticRetention_DeletesFromDiskOffTheOwnerWithoutTheRecycleBin()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Service.BeforePermanentDeleteForTest = _ => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        CaptureBatchRemovalResult? completed = null;
        fixture.Service.ConnectAutomaticMaintenance(completed: result => completed = result);
        Assert.Equal(0, fixture.Queue.ExpireHistory(DateTimeOffset.Now));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The index publication finished before file deletion; the deletion holds no queue lock.
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        Assert.Empty(fixture.Service.RecentDeleted());
        release.Set();
        await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(fixture.Original)); // disk space is actually freed
        Assert.Equal(0, fixture.Trash.Attempts);
        Assert.Equal(1, completed!.RemovedCount);
        Assert.Equal(0, completed.CleanupPendingCount);
        Assert.Empty(fixture.JournalFiles());
    }

    [Fact]
    public async Task AutomaticEviction_DeletionFailureLeavesAnOrphanWithoutRetryingOrRecycling()
    {
        var fixture = new Fixture();
        CaptureBatchRemovalResult? completed = null;
        int attempts = 0;
        fixture.Service.BeforePermanentDeleteForTest = _ => attempts++;
        fixture.Service.ConnectAutomaticMaintenance(completed: result => completed = result);
        using (new FileStream(Path.Combine(fixture.Original, CaptureFileNames.Original), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(0, fixture.Queue.ExpireHistory(DateTimeOffset.Now));
            await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        Assert.Equal(1, attempts);
        Assert.Equal(1, completed!.CleanupPendingCount);
        Assert.Equal(0, fixture.Trash.Attempts);
    }

    [Fact]
    public async Task StartupUnresolvedIntent_UsesBackgroundLookup_AndKeepsDeletedRecordRecoverable()
    {
        var fixture = new Fixture(); fixture.Trash.FailRestore = true;
        fixture.Service.BeforeJournalWriteForTest = phase => { if (phase == "recycled") throw new IOException("receipt unavailable"); };
        Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Trash.BeforeFind = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); throw new IOException("bounded lookup unavailable"); };
        var restarted = new RecoverableLibraryService(fixture.Queue, fixture.Paths, fixture.Trash, NullLogger.Instance);
        Task recovery = restarted.RecoverInterruptedRestoresAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(restarted.RecentDeleted());
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        release.Set();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(restarted.RecentDeleted());
        Assert.True(Directory.Exists(fixture.Trash.Receipt!.RecycledPath));
    }

    [Fact]
    public async Task CaptureAddedDuringDelayedAutomaticDeletion_DrainsFreshCapacityAfterCompletion()
    {
        var fixture = new Fixture();
        var middle = fixture.AddRecord(2);
        string middleDirectory = fixture.Queue.GetDirectory(middle);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int deletions = 0;
        fixture.Service.BeforePermanentDeleteForTest = _ =>
        {
            if (Interlocked.Increment(ref deletions) == 1) { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        };
        fixture.Service.ConnectAutomaticMaintenance();
        fixture.Queue.UpdateLimits(new QueueSettings { MaxItems = 1 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newest = fixture.AddRecord(0);
        release.Set();
        await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(newest.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Null(fixture.Queue.Find(middle.Id));
        Assert.False(Directory.Exists(fixture.Original));
        Assert.False(Directory.Exists(middleDirectory));
        Assert.Equal(2, deletions);
        Assert.Equal(0, fixture.Trash.Moves);
    }

    [Fact]
    public async Task UserDelete_WhenWindowsCannotRecycle_KeepsTheCaptureAndReportsIt()
    {
        var fixture = new Fixture(); fixture.Trash.RecycleUnavailable = true;
        CaptureBatchRemovalResult result = await fixture.Queue.RemoveManyAsync([fixture.Record.Id]);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(fixture.Record.Id, Assert.Single(result.RecycleUnavailableIds));
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Equal("original pixels", File.ReadAllText(Path.Combine(fixture.Original, CaptureFileNames.Original)));
        Assert.Empty(fixture.JournalFiles()); // a refused attempt leaves no journal behind
        Assert.Empty(fixture.Service.RecentDeleted());
    }

    [Fact]
    public async Task RecycleRefusal_DeletesPermanentlyOnlyAfterAnExplicitYes()
    {
        int prompts = 0, deletions = 0;
        Task<CaptureBatchRemovalResult> Delete(IReadOnlyList<Guid> ids) { deletions++; return Task.FromResult(new CaptureBatchRemovalResult(ids.Count, 0)); }
        Assert.Null(await LibraryPermanentDeletion.ResolveAsync([], _ => { prompts++; return true; }, Delete));
        Assert.Equal(0, prompts);
        Guid[] refused = [Guid.NewGuid(), Guid.NewGuid()];
        Assert.Null(await LibraryPermanentDeletion.ResolveAsync(refused, count => { prompts++; Assert.Equal(2, count); return false; }, Delete));
        Assert.Equal(0, deletions);
        var result = await LibraryPermanentDeletion.ResolveAsync(refused, _ => { prompts++; return true; }, Delete);
        Assert.Equal(2, result!.RemovedCount);
        Assert.Equal((2, 1), (prompts, deletions));
        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("영구 삭제", UiText.Get("LibrarySelection_PermanentDelete"));
            Assert.EndsWith("복원할 수 없습니다.", UiText.Format("LibrarySelection_RecycleUnavailableMessage", 2));
        }
    }

    [Fact]
    public async Task ConfirmedPermanentDelete_RemovesFilesWithoutTheRecycleBinOrAJournal()
    {
        var fixture = new Fixture(); fixture.Trash.RecycleUnavailable = true;
        CaptureBatchRemovalResult refused = await fixture.Queue.RemoveManyAsync([fixture.Record.Id]);
        CaptureBatchRemovalResult result = await fixture.Queue.RemoveManyPermanentlyAsync(refused.RecycleUnavailableIds);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.CleanupPendingCount);
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        Assert.False(Directory.Exists(fixture.Original));
        Assert.Equal(0, fixture.Trash.Moves);
        Assert.Empty(fixture.JournalFiles());
    }

    [Fact]
    public async Task FolderDelete_WhenWindowsCannotRecycle_ReportsAndPermanentPassRemovesTheFolder()
    {
        var fixture = new Fixture(); fixture.Trash.RecycleUnavailable = true;
        CaptureFolder folder = await fixture.Queue.CreateFolderAsync("Fixture folder");
        await fixture.Queue.MoveCapturesAsync([fixture.Record.Id], folder.Id);
        var scope = CaptureFolderScope.ForFolder(folder.Id);
        CaptureFolderDeletionResult refused = await fixture.Queue.DeleteFolderScopeAsync(scope);
        Assert.Equal(0, refused.RemovedFolders);
        Assert.Equal(fixture.Record.Id, Assert.Single(refused.RecycleUnavailableIds));
        Assert.NotNull(fixture.Queue.FindFolder(folder.Id));
        CaptureFolderDeletionResult permanent = await fixture.Queue.DeleteFolderScopeAsync(scope,
            id => refused.RecycleUnavailableIds.Contains(id), permanent: true);
        Assert.Equal(1, permanent.RemovedFolders);
        Assert.Equal(1, permanent.RemovedCaptures);
        Assert.False(Directory.Exists(fixture.Original));
        Assert.Equal(0, fixture.Trash.Moves);
    }

    [Fact]
    public async Task Journal_KeepsNoOcrTextLinksTitlesOrSourceDetails_AndRestoreRecoversThemFromTheCapture()
    {
        var fixture = new Fixture();
        fixture.Record.OcrText = "secret recognised text"; fixture.Record.OcrLanguage = "ko";
        fixture.Record.SourcePageUrl = "https://example.invalid/private-page";
        fixture.Record.SourceWindowTitle = "Private window title";
        fixture.Record.SetSourceDetails(MyCapture.Core.Capture.CaptureSourceDetails.Create("Fixture App", @"C:\Fixture\app.exe", @"C:\Fixture\Private"));
        fixture.Record.Tags = "private-tag"; fixture.Record.Title = "Visible title";
        fixture.Queue.SaveRecordMetaOrThrow(fixture.Record); fixture.Queue.Save();
        Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        string journal = string.Join("\n", fixture.JournalFiles().Select(File.ReadAllText));
        foreach (string secret in new[] { "secret recognised text", "example.invalid", "Private window title", "app.exe", "Fixture App", "private-tag" })
            Assert.DoesNotContain(secret, journal);
        var entry = Assert.Single(fixture.Service.RecentDeleted());
        Assert.Equal("Visible title", entry.Record.Title);
        await fixture.Service.RestoreAsync(entry.Token);
        CaptureRecord restored = fixture.Queue.Find(fixture.Record.Id)!;
        Assert.Equal("secret recognised text", restored.OcrText);
        Assert.Equal("https://example.invalid/private-page", restored.SourcePageUrl);
        Assert.Equal("Private window title", restored.SourceWindowTitle);
        Assert.Empty(fixture.JournalFiles()); // a completed restore leaves nothing behind
    }

    [Fact]
    public async Task JournalFromAnEarlierBuild_IsMinimisedAtStartupAndStillRestores()
    {
        var fixture = new Fixture(); Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        var entry = Assert.Single(fixture.Service.RecentDeleted());
        string intent = Path.Combine(fixture.Paths.DataRoot, "deleted-captures", $"{entry.Token:N}.intent.json");
        CaptureRecord legacy = entry.Record; legacy.OcrText = "legacy recognised text"; legacy.SourcePageUrl = "https://example.invalid/legacy";
        File.WriteAllText(intent, System.Text.Json.JsonSerializer.Serialize(entry with { Record = legacy }));
        await fixture.Service.RecoverInterruptedRestoresAsync();
        string journal = File.ReadAllText(intent);
        Assert.DoesNotContain("legacy recognised text", journal);
        Assert.DoesNotContain("example.invalid", journal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(intent)!, "*.bak"));
        await fixture.Service.RestoreAsync(Assert.Single(fixture.Service.RecentDeleted()).Token);
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
    }

    [Fact]
    public async Task EmptiedRecycleBinItem_IsHiddenAndItsJournalPurgedAtStartup()
    {
        var fixture = new Fixture(); Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        Assert.Single(fixture.Service.RecentDeleted());
        Directory.Delete(fixture.Trash.Receipt!.RecycledPath, recursive: true); // the user emptied the bin
        Assert.Empty(fixture.Service.RecentDeleted());
        await fixture.Service.RecoverInterruptedRestoresAsync();
        Assert.Empty(fixture.JournalFiles());
    }

    [Fact]
    public void Journal_KeepsOnlyTheNewestEntriesUpToItsCapacity()
    {
        var fixture = new Fixture(); fixture.Service.JournalCapacity = 2;
        var records = new[] { fixture.Record, fixture.AddRecord(3), fixture.AddRecord(2), fixture.AddRecord(1) };
        foreach (CaptureRecord record in records) Assert.True(fixture.Queue.Remove(record.Id));
        var remaining = fixture.Service.RecentDeleted();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(new[] { records[3].Id, records[2].Id }, remaining.Select(entry => entry.Record.Id).ToArray());
        Assert.Equal(2, fixture.JournalFiles().Count(file => file.EndsWith(".intent.json", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CompletedMarkerCannotHideOriginal_WhenIndexRollbackWasInterrupted()
    {
        var fixture = new Fixture(); Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        var entry = Assert.Single(fixture.Service.RecentDeleted());
        fixture.Trash.RestoreDirectory(fixture.Trash.Receipt!);
        File.WriteAllText(Path.Combine(fixture.Paths.DataRoot, "deleted-captures", $"{entry.Token:N}.restored.json"),
            System.Text.Json.JsonSerializer.Serialize(new { RestoredAt = DateTimeOffset.UtcNow }));
        fixture.Queue.Load();
        Assert.Null(fixture.Queue.Find(fixture.Record.Id));
        await fixture.Service.RecoverInterruptedRestoresAsync();
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Equal("original pixels", File.ReadAllText(Path.Combine(fixture.Original, CaptureFileNames.Original)));
    }

    [Fact]
    public async Task PartialCompletionMarkerDoesNotHideRecycledItem()
    {
        var fixture = new Fixture(); Assert.True(fixture.Queue.Remove(fixture.Record.Id));
        var entry = Assert.Single(fixture.Service.RecentDeleted());
        File.WriteAllText(Path.Combine(fixture.Paths.DataRoot, "deleted-captures", $"{entry.Token:N}.restored.json"), "{");
        Assert.Single(await fixture.Service.RecentDeletedAsync());
        await fixture.Service.RestoreAsync(entry.Token);
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Empty(await fixture.Service.RecentDeletedAsync());
    }

    private sealed class Fixture
    {
        public string[] JournalFiles() => Directory.GetFiles(Path.Combine(Paths.DataRoot, "deleted-captures"));
        public AppPaths Paths { get; } = AppPaths.CreateForRoot(TestRecycleBin.CreateTempSubdirectory("mc-library-trash-").FullName);
        public CaptureQueue Queue { get; }
        public CaptureRecord Record { get; } = new() { Id = Guid.NewGuid(), TotalBytes = 25, CreatedAt = DateTimeOffset.Now.AddDays(-40) };
        public FakeTrash Trash { get; }
        public RecoverableLibraryService Service { get; }
        public string Original { get; }
        public Fixture()
        {
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Queue.Add(Record); Original = Queue.GetDirectory(Record); Directory.CreateDirectory(Original);
            File.WriteAllText(Path.Combine(Original, CaptureFileNames.Original), "original pixels");
            File.WriteAllText(Path.Combine(Original, "annotation.json"), "annotation layer");
            Queue.SaveRecordMetaOrThrow(Record); Queue.Save();
            Trash = new FakeTrash(Path.Combine(Paths.DataRoot, "fake-trash"));
            Service = new RecoverableLibraryService(Queue, Paths, Trash, NullLogger.Instance);
            Queue.PrepareRemoval = Service.PrepareRemoval;
        }
        public CaptureRecord AddRecord(int age)
        {
            var record = new CaptureRecord { Id = Guid.NewGuid(), TotalBytes = 25, CreatedAt = DateTimeOffset.Now.AddDays(-age) };
            Queue.Add(record);
            string path = Queue.GetDirectory(record); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, CaptureFileNames.Original), "owned additional pixels");
            Queue.SaveRecordMetaOrThrow(record); Queue.Save();
            return record;
        }
    }
    private sealed class FakeTrash(string root) : IRecoverableTrash
    {
        private readonly WindowsRecycleBin _identity = new();
        public bool FailRecycle { get; set; }
        public bool RecycleUnavailable { get; set; }
        public bool FailRestore { get; set; }
        public bool FailAfterRestore { get; set; }
        public Action? BeforeRecycle { get; set; }
        public Action? BeforeFind { get; set; }
        public int Attempts { get; private set; }
        public int Moves { get; private set; }
        public int Restores { get; private set; }
        public RecycledPathReceipt? Receipt { get; private set; }
        public string GetIdentity(string path) => _identity.GetIdentity(path);
        public RecycledPathReceipt RecycleDirectory(string path)
        {
            Attempts++;
            BeforeRecycle?.Invoke();
            if (FailRecycle) throw new IOException("recycle unavailable");
            if (RecycleUnavailable) throw new RecycleBinUnavailableException();
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string identity = GetIdentity(path); Directory.Move(path, target); Moves++;
            return Receipt = new(path, target, identity, "fixture-shell-id", true);
        }
        public void RestoreDirectory(RecycledPathReceipt receipt)
        {
            if (FailRestore || Directory.Exists(receipt.OriginalPath)) throw new IOException("restore unavailable or collision");
            Directory.Move(receipt.RecycledPath, receipt.OriginalPath); Restores++;
            if (FailAfterRestore) throw new IOException("post-restore metadata unavailable");
        }
        public RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity)
        {
            BeforeFind?.Invoke();
            return Receipt is { } receipt && receipt.Identity == identity && Directory.Exists(receipt.RecycledPath) ? receipt : null;
        }
    }
}
