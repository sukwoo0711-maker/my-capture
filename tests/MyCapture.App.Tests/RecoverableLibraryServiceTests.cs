using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
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
    public async Task AutomaticMaintenance_DoesNotBlockQueueOrRecentMenu_AndFailureDoesNotSpin()
    {
        var fixture = new Fixture(); fixture.Trash.FailRecycle = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Trash.BeforeRecycle = () => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        fixture.Service.ConnectAutomaticMaintenance();
        Assert.Equal(0, fixture.Queue.ExpireHistory(DateTimeOffset.Now));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.Empty(fixture.Service.RecentDeleted()); // no UI lock held across background Shell work
        release.Set();
        await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Trash.Attempts);
        Assert.NotNull(fixture.Queue.Find(fixture.Record.Id));
        Assert.True(Directory.Exists(fixture.Original));
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
    public async Task CaptureAddedDuringDelayedAutomaticMove_DrainsFreshCapacityAfterCompletion()
    {
        var fixture = new Fixture();
        var middle = fixture.AddRecord(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Trash.BeforeRecycle = () => { if (fixture.Trash.Attempts == 1) { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        fixture.Service.ConnectAutomaticMaintenance();
        fixture.Queue.UpdateLimits(new QueueSettings { MaxItems = 1 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newest = fixture.AddRecord(0);
        release.Set();
        await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(newest.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Null(fixture.Queue.Find(middle.Id));
        Assert.Equal(2, fixture.Trash.Moves);
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
