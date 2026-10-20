using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor save (<see cref="CapturePersistenceService.FinalizeAsync"/>) runs its file
/// transaction and waits for its metadata and index writes off the dispatcher, while the record,
/// the queue and <c>ImagePersisted</c> stay on it in the protocol order and at the same
/// durability point as the synchronous <see cref="CapturePersistenceService.Finalize"/>.
/// </summary>
public sealed class CapturePersistenceDispatcherTests
{
    private const string CommitMarkerFileName = ".finalize-commit-ready";

    [Fact]
    public void FinalizeAsync_RunsFileStepsOffTheDispatcher_AndPublishesInProtocolOrder() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.Persistence.PersistOriginal(Solid(48, 32, 0x20), 1, "source", "DISPLAY1");
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        string directory = fixture.Queue.GetDirectory(record);
        string metaPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Meta);
        var events = new ConcurrentQueue<(string Name, bool OnDispatcher)>();
        void Log(string name) => events.Enqueue((name, dispatcher.CheckAccess()));
        fixture.Persistence.FileStepForTest = step => Log("step:" + step);
        fixture.Persistence.BeforeStagedFileCommit = (_, path) => Log("swap:" + Path.GetFileName(path));
        fixture.Persistence.BeforeRecordMetadataCommit = _ => Log("metadata");
        fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
            Log(path == metaPath ? "write:meta" : path == fixture.Paths.IndexFile ? "write:index" : "write:other");
        (long Meta, long Index, int Stages)? atPersisted = null;
        fixture.Persistence.ImagePersisted += (_, persisted) =>
        {
            Log("persisted");
            atPersisted = (ReadMeta(metaPath).ContentRevision, ReloadRevision(fixture, persisted.Id),
                StageDirectories(directory).Length);
        };

        Task pending = fixture.Persistence.FinalizeAsync(
            record, Solid(48, 32, 0xC0), Annotated(48, 32), new Dictionary<string, BitmapSource>());
        PumpUntil(() => pending.IsCompleted);
        pending.GetAwaiter().GetResult();

        (string Name, bool OnDispatcher)[] log = events.ToArray();
        // Every file-system step, staged swap and disk write ran off the dispatcher; the
        // metadata hook (record serialization) and the notification ran on it.
        foreach ((string name, bool onDispatcher) in log)
        {
            Assert.True(onDispatcher == (name is "metadata" or "persisted"), $"{name} ran on the wrong thread");
        }

        string[] order = log.Select(entry => entry.Name).Where(name => !name.StartsWith("swap:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            new[] { "step:scan", "step:pending", "step:commit", "metadata", "write:meta", "write:index", "step:cleanup", "persisted" },
            order);
        string[] swaps = log.Select(entry => entry.Name).Where(name => name.StartsWith("swap:", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains("swap:" + CaptureFileNames.Rendered, swaps);
        int firstSwap = Array.FindIndex(log, entry => entry.Name.StartsWith("swap:", StringComparison.Ordinal));
        int lastSwap = Array.FindLastIndex(log, entry => entry.Name.StartsWith("swap:", StringComparison.Ordinal));
        Assert.True(firstSwap > Array.FindIndex(log, entry => entry.Name == "step:commit"));
        Assert.True(lastSwap < Array.FindIndex(log, entry => entry.Name == "metadata"));

        // Same durability point: metadata and index both name the new generation and the
        // transaction directory is retired before the event.
        Assert.Equal((1L, 1L, 0), atPersisted);
        Assert.Equal(1, record.ContentRevision);
        Assert.True(record.HasAnnotations);
        Assert.False(fixture.Persistence.IsBusy(record.Id));
    });

    [Theory]
    [InlineData("swap")]
    [InlineData("metadata write")]
    [InlineData("index write")]
    public void FinalizeAsync_HeldStepOrWrite_LeavesDispatcherFree_AndKeepsCaptureProtected(string held) =>
        StaTestHost.Run(() =>
        {
            using var fixture = new Fixture();
            CaptureRecord record = fixture.Persistence.PersistOriginal(Solid(40, 30, 0x30), 1, "", "");
            string metaPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Meta);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            void Hold()
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The held step was not released.");
            }

            if (held == "swap")
            {
                fixture.Persistence.BeforeStagedFileCommit = (index, _) =>
                {
                    if (index == 0) Hold();
                };
            }
            else
            {
                string heldPath = held == "metadata write" ? metaPath : fixture.Paths.IndexFile;
                fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
                {
                    if (path == heldPath) Hold();
                };
            }

            int persisted = 0;
            fixture.Persistence.ImagePersisted += (_, _) => persisted++;
            Task pending = fixture.Persistence.FinalizeAsync(
                record, Solid(40, 30, 0xB0), Annotated(40, 30), new Dictionary<string, BitmapSource>());
            try
            {
                PumpUntil(() => entered.IsSet);

                // The dispatcher runs other work while the save waits on the disk.
                bool ran = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ran = true));
                PumpUntil(() => ran);
                Assert.False(pending.IsCompleted);
                Assert.Equal(0, persisted);

                // The capture stays as protected as while its image is encoded.
                Assert.True(fixture.Persistence.IsBusy(record.Id));
                Assert.True(fixture.Persistence.IsWriteInProgress(record.Id));
                Assert.False(fixture.Queue.Remove(record.Id));
                Task second = fixture.Persistence.FinalizeAsync(
                    record, Solid(40, 30, 0x10), Annotated(40, 30), new Dictionary<string, BitmapSource>());
                PumpUntil(() => second.IsCompleted);
                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                    () => second.GetAwaiter().GetResult());
                Assert.Contains("already being finalized", refused.Message, StringComparison.Ordinal);
            }
            finally
            {
                release.Set();
                PumpUntil(() => pending.IsCompleted);
            }

            pending.GetAwaiter().GetResult();
            Assert.Equal(1, persisted);
            Assert.Equal(1, record.ContentRevision);
            Assert.Equal(1, ReadMeta(metaPath).ContentRevision);
            Assert.Equal(1, ReloadRevision(fixture, record.Id));
            Assert.False(fixture.Persistence.IsBusy(record.Id));
        });

    [Fact]
    public void FinalizeAsync_ExitSaveOnTheDispatcherDrainsTheAwaitedMetadataWrite() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        CaptureRecord record = fixture.Persistence.PersistOriginal(Solid(40, 30, 0x40), 1, "", "");
        string metaPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Meta);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path != metaPath || entered.IsSet) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The held write was not released.");
        };
        Task pending = fixture.Persistence.FinalizeAsync(
            record, Solid(40, 30, 0xA0), Annotated(40, 30), new Dictionary<string, BitmapSource>());
        try
        {
            PumpUntil(() => entered.IsSet);
            release.Set();
            // Like App.OnExit: a synchronous Save on the dispatcher waits for every earlier
            // publication, including the metadata write this save is awaiting.
            fixture.Queue.Save();
            Assert.Equal(1, ReadMeta(metaPath).ContentRevision);
            Assert.Equal(1, ReloadRevision(fixture, record.Id));
        }
        finally
        {
            release.Set();
            PumpUntil(() => pending.IsCompleted);
        }

        pending.GetAwaiter().GetResult();
        Assert.Equal(1, ReloadRevision(fixture, record.Id));
        Assert.False(fixture.Persistence.IsBusy(record.Id));
    });

    [Fact]
    public void FinalizeAsync_SwapFailure_RollsBackOffTheDispatcher_AndRestoresThePreviousGeneration() =>
        StaTestHost.Run(() =>
        {
            using var fixture = new Fixture();
            CaptureRecord record = fixture.Persistence.PersistOriginal(Solid(48, 32, 0x21), 1, "rollback", "DISPLAY1");
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            string directory = fixture.Queue.GetDirectory(record);
            string metaPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Meta);
            string[] names = [CaptureFileNames.Rendered, CaptureFileNames.Layers, CaptureFileNames.Thumbnail];
            Dictionary<string, byte[]> previousFiles = names.ToDictionary(
                name => name, name => File.ReadAllBytes(Path.Combine(directory, name)));
            (bool HasAnnotations, DateTimeOffset UpdatedAt, long Revision, long Bytes, long QueueBytes) previous =
                (record.HasAnnotations, record.UpdatedAt, record.ContentRevision, record.TotalBytes, fixture.Queue.TotalBytes);
            var steps = new ConcurrentQueue<(string Step, bool OnDispatcher)>();
            fixture.Persistence.FileStepForTest = step => steps.Enqueue((step, dispatcher.CheckAccess()));
            int swaps = 0;
            fixture.Persistence.BeforeStagedFileCommit = (index, _) =>
            {
                swaps++;
                if (index == 2) throw new IOException("injected swap failure after two swaps");
            };
            int persisted = 0;
            fixture.Persistence.ImagePersisted += (_, _) => persisted++;

            Task pending = fixture.Persistence.FinalizeAsync(
                record, Solid(48, 32, 0xF1), Annotated(48, 32), new Dictionary<string, BitmapSource>());
            PumpUntil(() => pending.IsCompleted);

            IOException failure = Assert.Throws<IOException>(() => pending.GetAwaiter().GetResult());
            Assert.Contains("previous file generation was restored", failure.Message, StringComparison.Ordinal);
            Assert.Equal("injected swap failure after two swaps", failure.InnerException?.Message);
            Assert.Equal(3, swaps);
            Assert.Equal(0, persisted);
            Assert.Equal(new[] { "scan", "pending", "commit", "rollback", "cleanup" }, steps.Select(entry => entry.Step));
            Assert.All(steps, entry => Assert.False(entry.OnDispatcher));

            foreach ((string name, byte[] bytes) in previousFiles)
            {
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, name)));
            }

            Assert.Equal(previous,
                (record.HasAnnotations, record.UpdatedAt, record.ContentRevision, record.TotalBytes, fixture.Queue.TotalBytes));
            CaptureRecord meta = ReadMeta(metaPath);
            Assert.Equal((previous.HasAnnotations, previous.UpdatedAt, previous.Revision),
                (meta.HasAnnotations, meta.UpdatedAt, meta.ContentRevision));
            Assert.Equal(0, ReloadRevision(fixture, record.Id));
            Assert.Empty(StageDirectories(directory));
            Assert.False(fixture.Persistence.IsBusy(record.Id));
        });

    [Fact]
    public void FinalizeAsync_MetadataFailureAfterCommitMarker_KeepsJournal_AndNextSaveRollsItForward() =>
        StaTestHost.Run(() =>
        {
            using var fixture = new Fixture();
            CaptureRecord record = fixture.Persistence.PersistOriginal(Solid(38, 24, 0x24), 1, "strict meta", "DISPLAY1");
            string directory = fixture.Queue.GetDirectory(record);
            string metaPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Meta);
            int persisted = 0;
            fixture.Persistence.ImagePersisted += (_, _) => persisted++;
            fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("injected metadata failure");

            // As in Finalize: the files are committed, so the save completes and notifies, and the
            // marked journal stays for repair with the capture blocked.
            Task first = fixture.Persistence.FinalizeAsync(
                record, Solid(38, 24, 0xC4), Annotated(38, 24), new Dictionary<string, BitmapSource>());
            PumpUntil(() => first.IsCompleted);
            first.GetAwaiter().GetResult();
            Assert.Equal(1, persisted);
            Assert.Equal(1, record.ContentRevision);
            string stage = Assert.Single(StageDirectories(directory));
            Assert.True(File.Exists(Path.Combine(stage, CommitMarkerFileName)));
            Assert.True(fixture.Persistence.IsBusy(record.Id));
            Assert.Equal(0, ReadMeta(metaPath).ContentRevision);

            // The next save rolls the marked generation forward first, then commits its own.
            fixture.Persistence.BeforeRecordMetadataCommit = null;
            Task second = fixture.Persistence.FinalizeAsync(
                record, Solid(38, 24, 0x34), Annotated(38, 24), new Dictionary<string, BitmapSource>());
            PumpUntil(() => second.IsCompleted);
            second.GetAwaiter().GetResult();
            Assert.Equal(2, persisted);
            Assert.Equal(2, record.ContentRevision);
            Assert.Empty(StageDirectories(directory));
            Assert.False(fixture.Persistence.IsBusy(record.Id));
            Assert.Equal(2, ReadMeta(metaPath).ContentRevision);
            Assert.Equal(2, ReloadRevision(fixture, record.Id));
        });

    private static long ReloadRevision(Fixture fixture, Guid id)
    {
        var reloaded = new CaptureQueue(fixture.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        return Assert.IsType<CaptureRecord>(reloaded.Find(id)).ContentRevision;
    }

    private static CaptureRecord ReadMeta(string metaPath) =>
        Assert.IsType<CaptureRecord>(
            JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(metaPath), JsonDefaults.Readable));

    private static string[] StageDirectories(string directory) =>
        Directory.EnumerateDirectories(directory, ".stage-*", SearchOption.TopDirectoryOnly)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path)[".stage-".Length..], "N", out _))
            .ToArray();

    private static AnnotationDocument Annotated(int width, int height)
    {
        AnnotationDocument document = AnnotationDocument.CreateFor(width, height);
        document.Add(new RectangleAnnotation
        {
            Rect = new RectD(2, 3, Math.Max(2, width - 5), Math.Max(2, height - 7)),
            Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
            StrokeThickness = 2,
        });
        return document;
    }

    private static BitmapSource Solid(int width, int height, byte value)
    {
        byte[] pixels = Enumerable.Repeat(value, width * height * 4).ToArray();
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

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
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-persist-dispatcher-").FullName;

        internal Fixture()
        {
            // Awaits resume on this test thread's dispatcher, as they do on the app's UI thread.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Paths = AppPaths.CreateForRoot(_root);
            Paths.EnsureCreated();
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => new QueueSettings(),
                NullLogger<CapturePersistenceService>.Instance);
        }

        internal AppPaths Paths { get; }

        internal CaptureQueue Queue { get; }

        internal CapturePersistenceService Persistence { get; }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
            TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }
}
