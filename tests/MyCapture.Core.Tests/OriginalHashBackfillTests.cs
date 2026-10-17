using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class OriginalHashBackfillTests
{
    [Fact]
    public Task RewriteWhileBackfillWaitsForReservationRejectsOldGeneration() => QueueOwnerThread.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var reservations = new List<CaptureWriteReservation>();
        for (int i = 0; i < 8; i++) reservations.Add(await fixture.Queue.ReservePublicationAsync());
        var read = Signal();
        fixture.Service.AfterDuplicateReadForTest = _ => { read.SetResult(); return Task.CompletedTask; };
        Task<CaptureImportResult> import = fixture.Service.ImportAsync([fixture.OldSource]);
        try
        {
            // The owner continuation reaches ReservePublicationAsync before this resumes.
            await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(import.IsCompleted);
            using (fixture.Queue.BeginOriginalChange(fixture.Record))
                File.WriteAllBytes(fixture.Original, Fixture.NewBytes);
            fixture.AssertDurableHash(null);
        }
        finally
        {
            foreach (CaptureWriteReservation reservation in reservations) reservation.Dispose();
        }
        Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false), await import);
        fixture.AssertDurableHash(null);
    });

    [Fact]
    public Task CompletedRewriteRejectsOldBackfillAndLaterHashesNewOriginal() => QueueOwnerThread.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.Record;
        var read = Signal();
        var resume = Signal();
        fixture.Service.AfterDuplicateReadForTest = async _ => { read.SetResult(); await resume.Task; };
        Task<CaptureImportResult> import = fixture.Service.ImportAsync([fixture.OldSource]);
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using (await fixture.Queue.BeginOriginalChangeAsync(record))
            {
                fixture.AssertDurableHash(null);
                File.WriteAllBytes(fixture.Original, Fixture.NewBytes);
            }
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false), await import);
        fixture.AssertDurableHash(null);
        fixture.Service.AfterDuplicateReadForTest = null;
        Assert.Equal(1, (await fixture.Service.ImportAsync([fixture.NewSource])).Duplicates);
        fixture.AssertDurableHash(Hash(Fixture.NewBytes));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChangingOriginalIsSkippedBeforeInvalidationGetsPublicationCapacity(bool cached) => QueueOwnerThread.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        if (cached)
        {
            fixture.Record.OriginalContentHash = Hash(Fixture.OldBytes);
            fixture.Queue.SaveRecordMetaOrThrow(fixture.Record);
            fixture.Queue.Save();
        }
        var reservations = new List<CaptureWriteReservation>();
        for (int i = 0; i < 8; i++) reservations.Add(await fixture.Queue.ReservePublicationAsync());
        Task<IDisposable> change = fixture.Queue.BeginOriginalChangeAsync(fixture.Record);
        Assert.False(change.IsCompleted);
        var prepared = Signal();
        var finishImport = Signal();
        int reads = 0;
        var service = new CaptureImportService(fixture.Queue, async (record, _, _) =>
        {
            record.Width = record.Height = 1;
            prepared.SetResult();
            await finishImport.Task;
        })
        {
            BeforeDuplicateReadForTest = _ => { reads++; return Task.CompletedTask; },
        };
        Task<CaptureImportResult> import = service.ImportAsync([fixture.OldSource]);
        try
        {
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, reads);
        }
        finally
        {
            foreach (CaptureWriteReservation reservation in reservations) reservation.Dispose();
            finishImport.TrySetResult();
        }
        using (await change)
        {
            Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false), await import);
            fixture.AssertDurableHash(null);
        }
        Assert.True(fixture.Queue.TryGetOriginalGeneration(fixture.Record, out _));
    });

    [Fact]
    public Task BackfillDoesNotOverwriteHashPublishedWhileReading() => QueueOwnerThread.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Service.AfterDuplicateReadForTest = async record =>
        {
            using CaptureWriteReservation reservation = await fixture.Queue.ReservePublicationAsync();
            record.OriginalContentHash = "already-published";
            await fixture.Queue.PublishRecordAsync(record, reservation);
        };
        Assert.Equal(1, (await fixture.Service.ImportAsync([fixture.OldSource])).Imported);
        fixture.AssertDurableHash("already-published");
    });

    [Fact]
    public Task FailedInvalidationReleasesChangeAndRetryPersistsNull() => QueueOwnerThread.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Record.OriginalContentHash = Hash(Fixture.OldBytes);
        fixture.Queue.SaveRecordMetaOrThrow(fixture.Record);
        fixture.Queue.Save();
        fixture.Queue.BeforePublicationWriteForTest = (_, _) => throw new IOException("injected invalidation failure");
        await Assert.ThrowsAsync<IOException>(() => fixture.Queue.BeginOriginalChangeAsync(fixture.Record));
        Assert.True(fixture.Queue.TryGetOriginalGeneration(fixture.Record, out long failedGeneration));
        Assert.Equal(Fixture.OldBytes, File.ReadAllBytes(fixture.Original));
        fixture.Queue.BeforePublicationWriteForTest = null;
        using (await fixture.Queue.BeginOriginalChangeAsync(fixture.Record)) fixture.AssertDurableHash(null);
        Assert.True(fixture.Queue.TryGetOriginalGeneration(fixture.Record, out long retriedGeneration));
        Assert.True(retriedGeneration > failedGeneration);
    });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] OldBytes = [1, 2, 3];
        internal static readonly byte[] NewBytes = [4, 5, 6];
        private readonly AppPaths _paths;
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-original-hash-").FullName;
        internal Fixture()
        {
            string root = _root;
            Directory.CreateDirectory(root);
            _paths = AppPaths.CreateForRoot(Path.Combine(root, "library"));
            _paths.EnsureCreated();
            Queue = new CaptureQueue(_paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Record = new CaptureRecord { Width = 1, Height = 1 };
            Record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(Record.Id, Record.CreatedAt);
            Directory.CreateDirectory(Queue.GetDirectory(Record));
            File.WriteAllBytes(Original, OldBytes);
            Queue.Add(Record);
            Queue.SaveRecordMetaOrThrow(Record);
            Queue.Save();
            OldSource = Path.Combine(root, "old.png");
            NewSource = Path.Combine(root, "new.png");
            File.WriteAllBytes(OldSource, OldBytes);
            File.WriteAllBytes(NewSource, NewBytes);
            Service = new CaptureImportService(Queue, (record, _, _) =>
            {
                record.Width = record.Height = 1;
                return Task.CompletedTask;
            });
        }
        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
        internal CaptureQueue Queue { get; }
        internal CaptureRecord Record { get; }
        internal CaptureImportService Service { get; }
        internal string OldSource { get; }
        internal string NewSource { get; }
        internal string Original => Queue.GetFilePath(Record, CaptureFileNames.Original);
        internal void AssertDurableHash(string? expected)
        {
            Assert.Equal(expected, Record.OriginalContentHash);
            var reloaded = new CaptureQueue(_paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            Assert.Equal(expected, reloaded.Find(Record.Id)!.OriginalContentHash);
            CaptureRecord meta = JsonSerializer.Deserialize<CaptureRecord>(
                File.ReadAllText(Queue.GetFilePath(Record, CaptureFileNames.Meta)), JsonDefaults.Readable)!;
            Assert.Equal(expected, meta.OriginalContentHash);
        }
    }
}
