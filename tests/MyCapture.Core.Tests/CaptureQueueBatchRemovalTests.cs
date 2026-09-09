using System.Collections.Specialized;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureQueueBatchRemovalTests
{
    [Fact]
    public async Task AdmissionWait_RechecksNewLeaseAndBusyState_BeforeRemovingAnyRecord()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var first = new CaptureRecord { Id = Guid.NewGuid() };
        var second = new CaptureRecord { Id = Guid.NewGuid() };
        queue.Add(first); queue.Add(second);
        var reservations = new List<CaptureWriteReservation>();
        for (int i = 0; i < 8; i++) reservations.Add(await queue.ReservePublicationAsync());
        bool busy = false;
        Task<CaptureBatchRemovalResult> pending = queue.RemoveManyAsync([first.Id, second.Id], _ => !busy);
        Assert.False(pending.IsCompleted);
        using IDisposable lease = queue.AcquireEvictionLease(first.Id);
        busy = true;
        foreach (CaptureWriteReservation reservation in reservations) reservation.Dispose();
        CaptureBatchRemovalResult result = await pending;
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(2, queue.Count);
        Assert.False(File.Exists(workspace.Paths.IndexFile));
    }

    [Fact]
    public async Task Batch_RemovesPinsAndVideos_OneResetOneWrite_AndRetainsLeasedOrBusy()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        CaptureRecord[] records = Enumerable.Range(0, 4).Select(i => new CaptureRecord
        { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.Now.AddMinutes(-i), TotalBytes = 10,
            IsPinned = true, MediaKind = i == 0 ? CaptureMediaKind.Video : CaptureMediaKind.Image }).ToArray();
        foreach (var record in records) queue.Add(record);
        int writes = 0, changes = 0;
        queue.BeforePublicationWriteForTest = (_, _) => Interlocked.Increment(ref writes);
        ((INotifyCollectionChanged)queue.Records).CollectionChanged += (_, _) => changes++;
        var evicted = new List<Guid>();
        queue.Evicted += (_, e) => evicted.Add(e.Record.Id);
        using IDisposable lease = queue.AcquireEvictionLease(records[2].Id);
        CaptureBatchRemovalResult removed = await queue.RemoveManyAsync(records.Select(r => r.Id), id => id != records[3].Id);
        Assert.Equal(2, removed.RemovedCount);
        Assert.Equal(1, changes);
        Assert.Equal(1, writes);
        Assert.Equal(20, queue.TotalBytes);
        Assert.Equal(records.Take(2).Select(r => r.Id).Order(), evicted.Order());
        Assert.NotNull(queue.Find(records[2].Id));
        Assert.NotNull(queue.Find(records[3].Id));
    }

    [Fact]
    public async Task FailedPublication_RestoresOnlyRemovedRecords_ThenCorrectsIndexAfterNewerAddAndSave()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var original = new CaptureRecord { Id = Guid.NewGuid(), TotalBytes = 11, CreatedAt = DateTimeOffset.Now };
        queue.Add(original);
        queue.Save();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0, evictions = 0;
        queue.Evicted += (_, _) => evictions++;
        queue.BeforePublicationWriteForTest = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            throw new IOException("synthetic index failure");
        };
        Task<CaptureBatchRemovalResult> removal = queue.RemoveManyAsync([original.Id]);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(queue.Find(original.Id));
        var newer = new CaptureRecord { Id = Guid.NewGuid(), TotalBytes = 23,
            CreatedAt = DateTimeOffset.Now.AddSeconds(1), RelativeDirectory = "newer" };
        queue.Add(newer);
        using CaptureWriteReservation reservation = await queue.ReservePublicationAsync();
        Task newerSave = queue.PublishRecordAsync(newer, reservation);
        release.Set();
        await Assert.ThrowsAsync<IOException>(() => removal);
        await newerSave;
        Assert.Equal(0, evictions); // No files may be deleted before failed durability.
        Assert.Equal(2, queue.Count);
        Assert.Equal(34, queue.TotalBytes);
        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(workspace.Paths.IndexFile));
        string saved = index.RootElement.GetRawText();
        Assert.Contains(original.Id.ToString(), saved);
        Assert.Contains(newer.Id.ToString(), saved);
    }

    [Fact]
    public async Task CleanupFailure_IsReportedWithoutRestoringPartiallyDeletedRecord()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var record = new CaptureRecord { Id = Guid.NewGuid(), TotalBytes = 12 };
        queue.Add(record);
        string directory = queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        string lockedPath = queue.GetFilePath(record, CaptureFileNames.Rendered);
        using var locked = new FileStream(lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        queue.Evicted += (_, _) =>
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        };
        CaptureBatchRemovalResult result = await queue.RemoveManyAsync([record.Id]);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.CleanupPendingCount);
        Assert.Null(queue.Find(record.Id));
        Assert.True(Directory.Exists(directory));
    }
}
