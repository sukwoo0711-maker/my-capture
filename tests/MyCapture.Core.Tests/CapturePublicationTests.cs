using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CapturePublicationTests
{
    [Fact]
    public async Task ImmutablePublication_PrecedesLaterSynchronousSave_AndSaveDrainsWriter()
    {
        using var workspace = new TempWorkspace();
        var queue = NewQueue(workspace);
        CaptureRecord record = AddRecord(queue);
        record.OcrText = "first";
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var observed = new List<string>();
        queue.BeforePublicationWriteForTest = (_, content) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            observed.Add(content);
        };
        using CaptureWriteReservation reservation = await queue.ReservePublicationAsync();
        Task first = queue.PublishRecordAsync(record, reservation);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        record.OcrText = "second";
        Task drain = Task.Run(queue.Save);
        try { Assert.False(first.IsCompleted); Assert.False(drain.IsCompleted); }
        finally { release.Set(); }
        await Task.WhenAll(first, drain).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, observed.Count);
        Assert.Contains("first", observed[0]);
        Assert.Contains("first", observed[1]);
        Assert.Contains("second", observed[2]);
        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(workspace.Paths.IndexFile));
        Assert.Equal("second", index.RootElement.GetProperty("records")[0].GetProperty("ocrText").GetString());
    }

    [Fact]
    public async Task SyncSave_CompletesWithAllAsyncSlotsReservedButNotEnqueued()
    {
        using var workspace = new TempWorkspace();
        var queue = NewQueue(workspace);
        CaptureRecord record = AddRecord(queue);
        var reservations = new List<CaptureWriteReservation>();
        try
        {
            for (int i = 0; i < 8; i++) reservations.Add(await queue.ReservePublicationAsync());
            using var cancellation = new CancellationTokenSource();
            Task<CaptureWriteReservation> blocked = queue.ReservePublicationAsync(cancellation.Token);
            Assert.False(blocked.IsCompleted);
            await Task.Run(queue.Save).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(blocked.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            record.OcrText = "after barrier";
            await queue.PublishRecordAsync(record, reservations[0]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("after barrier", File.ReadAllText(workspace.Paths.IndexFile));
        }
        finally { foreach (CaptureWriteReservation reservation in reservations) reservation.Dispose(); }
    }

    [Fact]
    public async Task FailedPublication_DoesNotPoisonLaterWrites_AndReturnsCapacity()
    {
        using var workspace = new TempWorkspace();
        var queue = NewQueue(workspace);
        CaptureRecord record = AddRecord(queue);
        int calls = 0;
        queue.BeforePublicationWriteForTest = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("injected disk failure");
        };
        using CaptureWriteReservation first = await queue.ReservePublicationAsync();
        await Assert.ThrowsAsync<IOException>(() => queue.PublishRecordAsync(record, first));
        queue.Save();
        using CaptureWriteReservation next = await queue.ReservePublicationAsync();
        record.OcrText = "recovered";
        await queue.PublishRecordAsync(record, next);
        Assert.Contains("recovered", File.ReadAllText(workspace.Paths.IndexFile));
        var slots = new List<CaptureWriteReservation>();
        try
        {
            for (int i = 0; i < 8; i++) slots.Add(await queue.ReservePublicationAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { foreach (CaptureWriteReservation slot in slots) slot.Dispose(); }
    }

    private static CaptureQueue NewQueue(TempWorkspace workspace) =>
        new(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    private static CaptureRecord AddRecord(CaptureQueue queue)
    {
        var record = new CaptureRecord();
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        queue.Add(record);
        return record;
    }
}
