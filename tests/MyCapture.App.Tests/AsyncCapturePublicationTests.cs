using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AsyncCapturePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_WaitsForDurability_ProtectsRecord_AndRetainsMarkerOnFailure(bool fail) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int owner = Environment.CurrentManagedThreadId;
        int notifiedThread = 0;
        var persistence = new CapturePersistenceService(fixture.Queue, fixture.Paths, () => new QueueSettings(),
            NullLogger<CapturePersistenceService>.Instance);
        persistence.ImagePersisted += (_, _) => notifiedThread = Environment.CurrentManagedThreadId;
        fixture.Queue.BeforePublicationWriteForTest = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            if (fail) throw new IOException("injected publication failure");
        };
        var image = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 64);
        image.Freeze();
        Task<CaptureRecord> pending = persistence.PersistOriginalAsync(image, 1, "synthetic", "synthetic");
        try
        {
            PumpUntil(() => entered.IsSet);
            CaptureRecord record = Assert.Single(fixture.Queue.Records);
            string marker = fixture.Queue.GetFilePath(record, CaptureFileNames.OriginalPending);
            Assert.True(File.Exists(marker));
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, notifiedThread);
            Assert.True(persistence.IsBusy(record.Id));
            Assert.False(fixture.Queue.Remove(record.Id));
            release.Set();
            PumpUntil(() => pending.IsCompleted);
            if (fail)
            {
                Assert.Throws<IOException>(() => pending.GetAwaiter().GetResult());
                Assert.True(File.Exists(marker));
                Assert.True(persistence.IsBusy(record.Id));
                Assert.Equal(0, notifiedThread);
            }
            else
            {
                Assert.Same(record, pending.GetAwaiter().GetResult());
                Assert.False(File.Exists(marker));
                Assert.False(persistence.IsBusy(record.Id));
                Assert.Equal(owner, notifiedThread);
                Assert.True(File.Exists(fixture.Paths.IndexFile));
            }
        }
        finally { release.Set(); PumpUntil(() => pending.IsCompleted); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ocr_RechecksRevisionAndExistenceAfterCapacityWait(bool remove) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.AddRecord();
        var slots = new List<CaptureWriteReservation>();
        try
        {
            for (int i = 0; i < 8; i++) slots.Add(fixture.Queue.ReservePublicationAsync().GetAwaiter().GetResult());
            Task<bool> pending = fixture.Gallery.CacheOcrAsync(record.Id, "stale", "en", record.ContentRevision);
            Assert.False(pending.IsCompleted);
            if (remove) Assert.True(fixture.Queue.Remove(record.Id));
            else record.ContentRevision++;
            slots[0].Dispose();
            PumpUntil(() => pending.IsCompleted);
            Assert.False(pending.GetAwaiter().GetResult());
            Assert.NotEqual("stale", record.OcrText);
            Assert.False(File.Exists(fixture.Paths.IndexFile));
        }
        finally { foreach (CaptureWriteReservation slot in slots) slot.Dispose(); }
    });

    [Fact]
    public void AcceptedOcr_CannotWriteAfterEviction_AndFinalSaveDrainsIt() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.AddRecord();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Queue.BeforePublicationWriteForTest = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        Task<bool> pending = fixture.Gallery.CacheOcrAsync(record.Id, "durable", "en", record.ContentRevision);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(fixture.Queue.Remove(record.Id));
            release.Set();
            // Like OnExit, the dispatcher can synchronously drain disk before await continuations run.
            fixture.Queue.Save();
            Assert.True(File.Exists(fixture.Queue.GetFilePath(record, CaptureFileNames.Meta)));
            PumpUntil(() => pending.IsCompleted);
            Assert.True(pending.GetAwaiter().GetResult());
            Assert.True(fixture.Queue.Remove(record.Id));
            Directory.Delete(fixture.Queue.GetDirectory(record), recursive: true);
            fixture.Queue.Save();
            Assert.False(Directory.Exists(fixture.Queue.GetDirectory(record)));
        }
        finally { release.Set(); PumpUntil(() => pending.IsCompleted); }
    });

    private static void PumpUntil(Func<bool> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(15)) throw new TimeoutException();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly string _root = Directory.CreateTempSubdirectory("mycapture-publication-tests-").FullName;
        internal Fixture()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Paths = AppPaths.CreateForRoot(_root);
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Gallery = new GalleryController(Queue, NullLogger<GalleryController>.Instance);
        }
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal GalleryController Gallery { get; }
        internal CaptureRecord AddRecord()
        {
            var record = new CaptureRecord();
            record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
            Queue.Add(record);
            return record;
        }
        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
            Directory.Delete(_root, recursive: true);
        }
    }
}
