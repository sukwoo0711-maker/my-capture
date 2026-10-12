using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureQueueRecoverableRemovalTests
{
    private static CaptureQueue CreateQueue(QueueSettings? settings = null) => new(
        AppPaths.CreateForRoot(TestRecycleBin.CreateTempSubdirectory("mc-removal-queue-").FullName),
        settings ?? new QueueSettings(), NullLogger<CaptureQueue>.Instance);
    private static CaptureRecord Record(int age) => new()
    { Id = Guid.NewGuid(), TotalBytes = 10, CreatedAt = DateTimeOffset.Now.AddDays(-age), UpdatedAt = DateTimeOffset.Now.AddDays(-age) };

    [Fact]
    public void RejectedManualMove_PreservesRecordBytesAndDurableIndex()
    {
        var queue = CreateQueue();
        var record = Record(1); queue.Add(record); queue.Save();
        queue.PrepareRemoval = (_, _) => null;
        Assert.False(queue.Remove(record.Id));
        Assert.Same(record, queue.Find(record.Id));
        Assert.Equal(10, queue.TotalBytes);
    }

    [Fact]
    public void FailedCapacityMoves_AttemptEachVictimOnce_KeepNewestAndFailedRecords()
    {
        var queue = CreateQueue(new QueueSettings { MaxItems = 1 });
        var attempts = new Dictionary<Guid, int>();
        queue.PrepareRemoval = (record, _) => { attempts[record.Id] = attempts.GetValueOrDefault(record.Id) + 1; return null; };
        var oldest = Record(3); var middle = Record(2); var newest = Record(1);
        queue.Add(oldest); queue.Add(middle); attempts.Clear(); queue.Add(newest);
        Assert.Equal(3, queue.Count);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts.Values, count => Assert.Equal(1, count));
        Assert.False(attempts.ContainsKey(newest.Id));
    }

    [Fact]
    public void FailedExpiryMoves_RemainAvailableAndReportZeroRemoved()
    {
        var queue = CreateQueue(); var first = Record(40); var second = Record(35);
        queue.Add(first); queue.Add(second);
        queue.PrepareRemoval = (_, _) => throw new IOException("recycling unavailable");
        Assert.Equal(0, queue.ExpireHistory(DateTimeOffset.Now.AddYears(1)));
        Assert.Equal(2, queue.Count);
        Assert.Equal(20, queue.TotalBytes);
    }

    [Theory]
    [InlineData("capacity", true)]
    [InlineData("retention-age", true)]
    [InlineData("item-limit", true)]
    [InlineData("byte-limit", true)]
    [InlineData(CaptureQueue.ManualPermanentReason, true)]
    [InlineData("manual", false)]
    [InlineData("manual-batch", false)]
    public void OnlyEvictionAndConfirmedPermanentDeletionSkipTheRecycleBin(string reason, bool permanent) =>
        Assert.Equal(permanent, CaptureQueue.IsPermanentRemovalReason(reason));

    [Fact]
    public async Task PermanentRemoval_CompletesOnlyAfterTheIndexStopsNamingTheCapture()
    {
        var queue = CreateQueue(); var record = Record(1); queue.Add(record); queue.Save();
        var transaction = new Transaction();
        string? reason = null;
        queue.PrepareRemoval = (_, removalReason) => { reason = removalReason; return transaction; };
        bool indexDroppedFirst = false;
        transaction.OnComplete = () => indexDroppedFirst = queue.Find(record.Id) is null;
        var result = await queue.RemoveManyPermanentlyAsync([record.Id]);
        Assert.Equal(CaptureQueue.ManualPermanentReason, reason);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.CleanupPendingCount);
        Assert.Equal(1, transaction.Completions);
        Assert.True(indexDroppedFirst);
        transaction.CompleteResult = false;
        var second = Record(2); queue.Add(second);
        Assert.Equal(1, (await queue.RemoveManyPermanentlyAsync([second.Id])).CleanupPendingCount);
    }

    [Fact]
    public async Task RecycleUnavailable_KeepsTheRecordAndReportsItsId()
    {
        var queue = CreateQueue(); var kept = Record(1); var moved = Record(2);
        queue.Add(kept); queue.Add(moved);
        queue.PrepareRemoval = (record, _) => record.Id == kept.Id ? throw new RecycleBinUnavailableException() : new Transaction();
        var result = await queue.RemoveManyAsync([kept.Id, moved.Id]);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(kept.Id, Assert.Single(result.RecycleUnavailableIds));
        Assert.Same(kept, queue.Find(kept.Id));
        Assert.Null(queue.Find(moved.Id));
    }

    [Fact]
    public async Task Batch_PartialMoveSuccess_RemovesOnlyAcknowledgedRecords()
    {
        var queue = CreateQueue(); var first = Record(1); var second = Record(2);
        queue.Add(first); queue.Add(second);
        queue.PrepareRemoval = (record, _) => record.Id == first.Id ? new Transaction() : null;
        var result = await queue.RemoveManyAsync([first.Id, second.Id, first.Id]);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.CleanupPendingCount);
        Assert.Null(queue.Find(first.Id));
        Assert.Same(second, queue.Find(second.Id));
        Assert.Equal(10, queue.TotalBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedBatchIndexWrite_RestoresOnlyFilesSuccessfullyRolledBack(bool rollbackSucceeds)
    {
        var queue = CreateQueue(); var record = Record(1); queue.Add(record); queue.Save();
        var transaction = new Transaction(rollbackSucceeds);
        queue.PrepareRemoval = (_, _) => transaction;
        int writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) => { if (++writes == 1) throw new IOException("synthetic index failure"); };
        await Assert.ThrowsAsync<IOException>(() => queue.RemoveManyAsync([record.Id]));
        Assert.Equal(1, transaction.Rollbacks);
        Assert.Equal(rollbackSucceeds, queue.Find(record.Id) is not null);
        Assert.Equal(rollbackSucceeds ? 10 : 0, queue.TotalBytes);
        Assert.True(writes >= 2); // corrective snapshot also became durable
    }

    [Fact]
    public async Task Batch_LeasedAndBusyRecordsNeverEnterShellPreparation()
    {
        var queue = CreateQueue(); var first = Record(1); var second = Record(2);
        queue.Add(first); queue.Add(second);
        using IDisposable lease = queue.AcquireEvictionLease(first.Id);
        queue.PrepareRemoval = (_, _) => throw new InvalidOperationException("must not enter Shell");
        var result = await queue.RemoveManyAsync([first.Id, second.Id], id => id != second.Id);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public async Task CapacityScheduling_ReturnsWithoutShell_AndRevalidatesPinOwnership()
    {
        var queue = CreateQueue(); var oldest = Record(3); var middle = Record(2); var newest = Record(1);
        queue.Add(oldest); queue.Add(middle); queue.Add(newest);
        IReadOnlyList<Guid> scheduled = [];
        int calls = 0;
        queue.PrepareRemoval = (_, _) => { calls++; return new Transaction(); };
        queue.ScheduleAutomaticRemoval = (ids, _) => scheduled = ids;
        queue.UpdateLimits(new QueueSettings { MaxItems = 1 });
        Assert.Equal(0, calls);
        Assert.Equal(3, queue.Count);
        Assert.Contains(oldest.Id, scheduled);
        oldest.IsPinned = true;
        var result = await queue.RemoveAutomaticallyAsync(scheduled, "capacity");
        Assert.Equal(1, result.RemovedCount);
        Assert.Same(oldest, queue.Find(oldest.Id));
        Assert.Same(newest, queue.Find(newest.Id));
        Assert.Null(queue.Find(middle.Id));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AutomaticExpiry_RevalidatesRefreshedRetention_AndBusyCapture()
    {
        var queue = CreateQueue(); var first = Record(40); var second = Record(40); var third = Record(40);
        queue.Add(first); queue.Add(second); queue.Add(third);
        IReadOnlyList<Guid> scheduled = [];
        var prepared = new List<Guid>();
        queue.PrepareRemoval = (record, _) => { prepared.Add(record.Id); return new Transaction(); };
        queue.ScheduleAutomaticRemoval = (ids, _) => scheduled = ids;
        Assert.Equal(0, queue.ExpireHistory(DateTimeOffset.Now));
        Assert.Empty(prepared);
        first.RetentionStartedAt = DateTimeOffset.Now;
        var result = await queue.RemoveAutomaticallyAsync(scheduled, "retention-age", id => id != second.Id);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal([third.Id], prepared);
        Assert.NotNull(queue.Find(first.Id));
        Assert.NotNull(queue.Find(second.Id));
    }

    [Fact]
    public async Task BatchRollbackException_DoesNotPreventOtherRestoresOrCorrectivePublication()
    {
        var queue = CreateQueue(); var first = Record(1); var second = Record(2); queue.Add(first); queue.Add(second); queue.Save();
        queue.PrepareRemoval = (record, _) => record.Id == first.Id ? new ThrowingRollback() : new Transaction();
        int writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) => { if (++writes == 1) throw new IOException("synthetic index failure"); };
        await Assert.ThrowsAsync<IOException>(() => queue.RemoveManyAsync([first.Id, second.Id]));
        Assert.Null(queue.Find(first.Id));
        Assert.Same(second, queue.Find(second.Id));
        Assert.True(writes >= 2);
    }

    [Fact]
    public async Task PendingRemoval_ExclusivelyOwnsEveryCandidate_UntilShellAndIndexFinish()
    {
        var queue = CreateQueue(); var first = Record(1); var second = Record(2);
        queue.Add(first); queue.Add(second);
        var folder = await queue.CreateFolderAsync("owned folder");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        queue.PrepareRemoval = (_, _) => { entered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return null; };
        Task<CaptureBatchRemovalResult> removal = queue.RemoveManyAsync([first.Id, second.Id]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task drain = queue.WaitForPendingRemovalsAsync();
        Assert.False(drain.IsCompleted);
        Assert.True(queue.IsRemovalPending(first.Id));
        Assert.True(queue.IsRemovalPending(second.Id));
        Assert.Throws<IOException>(() => queue.AcquireEvictionLease(second.Id));
        Assert.Throws<IOException>(() => queue.TogglePin(second.Id));
        Assert.Equal(0, await queue.MoveCapturesAsync([second.Id], folder.Id));
        Assert.Null(second.FolderId);
        release.Set();
        await removal.WaitAsync(TimeSpan.FromSeconds(5));
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queue.IsRemovalPending(first.Id));
        Assert.False(queue.IsRemovalPending(second.Id));
        using IDisposable newEditorLease = queue.AcquireEvictionLease(second.Id);
    }

    private sealed class ThrowingRollback : ICaptureRemovalTransaction
    {
        public bool Rollback() => throw new IOException("synthetic unavailable restore");
    }

    private sealed class Transaction(bool success = true) : ICaptureRemovalTransaction
    {
        public int Rollbacks { get; private set; }
        public int Completions { get; private set; }
        public bool CompleteResult { get; set; } = true;
        public Action? OnComplete { get; set; }
        public bool Rollback() { Rollbacks++; return success; }
        public bool Complete() { Completions++; OnComplete?.Invoke(); return CompleteResult; }
    }
}
