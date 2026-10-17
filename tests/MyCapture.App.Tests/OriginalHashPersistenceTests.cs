using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OriginalHashPersistenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task UpdatesWithoutOriginalReplacementKeepHashAndDuplicateDecision(bool sourceDetailsOnly) => RunStaAsync(async () =>
    {
        using var library = new Library();
        await library.CacheHash();
        string hash = library.Record.OriginalContentHash!;
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out long before));
        if (sourceDetailsOnly)
            Assert.True(await library.Persistence.BackfillSourceAsync(library.Record, "",
                new CaptureSourceDetails("Editor", @"C:\Editor\editor.exe"), ""));
        else
            await library.Persistence.FinalizeAsync(library.Record, Library.Bitmap(0xB0),
                AnnotationDocument.CreateFor(12, 8), new Dictionary<string, BitmapSource>());
        library.AssertHash(hash);
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out long after));
        Assert.Equal(before, after);
        int reads = 0;
        library.Import.BeforeDuplicateReadForTest = _ => { reads++; return Task.CompletedTask; };
        Assert.Equal(1, (await library.Import.ImportAsync([library.OldSource])).Duplicates);
        Assert.Equal(0, reads);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CompletedRewriteRefusesSuspendedOldHashAndDeduplicatesNewBytes(bool sourceUrl) => RunStaAsync(async () =>
    {
        using var library = new Library();
        var read = Signal();
        var resume = Signal();
        library.Import.AfterDuplicateReadForTest = async record =>
        {
            Assert.Same(library.Record, record);
            read.SetResult();
            await resume.Task;
        };
        Task<CaptureImportResult> import = library.Import.ImportAsync([library.OldSource]);
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await library.Rewrite(sourceUrl);
            library.AssertHash(null);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false), await import);
        library.AssertHash(null);
        library.Import.AfterDuplicateReadForTest = null;
        string newSource = library.CopyCurrentOriginal();
        Assert.NotEqual(Hash(File.ReadAllBytes(library.OldSource)), Hash(File.ReadAllBytes(newSource)));
        Assert.Equal(1, (await library.Import.ImportAsync([newSource])).Duplicates);
        library.AssertHash(Hash(File.ReadAllBytes(newSource)));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task DuplicateCheckDuringRewriteDoesNotReadOrCompareRecord(bool sourceUrl) => RunStaAsync(async () =>
    {
        using var library = new Library();
        await library.CacheHash();
        var changing = Signal();
        var resume = Signal();
        library.Persistence.BeforeOriginalRewriteForTest = async _ =>
        {
            library.AssertHash(null); // Both durable files precede any original write.
            changing.SetResult();
            await resume.Task;
        };
        int reads = 0;
        library.Import.BeforeDuplicateReadForTest = _ => { reads++; return Task.CompletedTask; };
        Task rewrite = library.Rewrite(sourceUrl);
        try
        {
            await changing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(library.Queue.TryGetOriginalGeneration(library.Record, out _));
            Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false),
                await library.Import.ImportAsync([library.OldSource]));
            Assert.Equal(0, reads);
            library.AssertHash(null);
        }
        finally { resume.TrySetResult(); }
        await rewrite;
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out _));
        library.AssertHash(null);
    });

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public Task FailedRewriteKeepsDurableNullAndRetryReplacesOriginal(bool sourceUrl, bool synchronous) => RunStaAsync(async () =>
    {
        using var library = new Library();
        await library.CacheHash();
        byte[] previous = File.ReadAllBytes(library.Original);
        bool faultReached = false;
        if (sourceUrl)
        {
            library.Persistence.BeforeOriginalRewriteForTest = _ =>
            {
                library.AssertHash(null);
                faultReached = true;
                throw new IOException("injected source rewrite failure");
            };
            Assert.False(await library.Persistence.BackfillSourceAsync(library.Record, Library.Url,
                CaptureSourceDetails.Empty, ""));
        }
        else
        {
            library.Persistence.BeforeStagedFileCommit = (_, path) =>
            {
                if (Path.GetFileName(path) != CaptureFileNames.Rendered) return;
                library.AssertHash(null);
                Assert.NotEqual(Hash(previous), Hash(File.ReadAllBytes(library.Original)));
                faultReached = true; // original.png was swapped; rollback must restore it.
                throw new IOException("injected failure after original swap");
            };
            await Assert.ThrowsAsync<IOException>(() => library.Rewrite(false, synchronous));
        }
        Assert.True(faultReached);
        Assert.Equal(previous, File.ReadAllBytes(library.Original));
        library.AssertHash(null);
        Assert.False(library.Persistence.IsBusy(library.Record.Id));
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out _));
        Assert.Equal(1, (await library.Import.ImportAsync([library.OldSource])).Duplicates);
        library.AssertHash(Hash(previous));

        library.Persistence.BeforeOriginalRewriteForTest = null;
        library.Persistence.BeforeStagedFileCommit = null;
        await library.Rewrite(sourceUrl, synchronous);
        library.AssertHash(null);
        Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false),
            await library.Import.ImportAsync([library.OldSource]));
        string newSource = library.CopyCurrentOriginal();
        Assert.Equal(1, (await library.Import.ImportAsync([newSource])).Duplicates);
        library.AssertHash(Hash(File.ReadAllBytes(newSource)));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task FailedNullPublicationNeverStartsRewriteAndCanBeRetried(bool sourceUrl) => RunStaAsync(async () =>
    {
        using var library = new Library();
        await library.CacheHash();
        byte[] previous = File.ReadAllBytes(library.Original);
        bool writerEntered = false;
        library.Persistence.BeforeOriginalRewriteForTest = _ => { writerEntered = true; return Task.CompletedTask; };
        library.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path == library.Paths.IndexFile) throw new IOException("injected null index publication failure");
        };
        if (sourceUrl)
            Assert.False(await library.Persistence.BackfillSourceAsync(library.Record, Library.Url,
                CaptureSourceDetails.Empty, ""));
        else
            await Assert.ThrowsAsync<IOException>(() => library.Rewrite(false));
        Assert.False(writerEntered);
        Assert.Equal(previous, File.ReadAllBytes(library.Original));
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out _));
        library.Queue.BeforePublicationWriteForTest = null;
        await library.Rewrite(sourceUrl);
        Assert.True(writerEntered);
        library.AssertHash(null);
    });

    [Fact]
    public Task ExistingPendingOriginalRetryInvalidatesBeforeWriting() => RunStaAsync(async () =>
    {
        using var library = new Library();
        await library.CacheHash();
        bool invalidationObserved = false;
        library.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path != library.Paths.IndexFile) return;
            if (invalidationObserved) return;
            invalidationObserved = true;
            Assert.Equal(File.ReadAllBytes(library.OldSource), File.ReadAllBytes(library.Original));
        };
        await library.Persistence.PersistPendingOriginalAsync(library.Record, Library.Bitmap(0xB0));
        Assert.True(invalidationObserved);
        library.AssertHash(null);
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out _));
        Assert.NotEqual(Hash(File.ReadAllBytes(library.OldSource)), Hash(File.ReadAllBytes(library.Original)));
    });

    [Fact]
    public Task RecoveryRetainsJournalWhenNullPublicationFailsAndCanRetry() => RunStaAsync(async () =>
    {
        using var library = new Library();
        await library.CacheHash();
        string stage = Path.Combine(library.Queue.GetDirectory(library.Record), ".stage-" + Guid.NewGuid().ToString("N"));
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        File.Copy(library.Original, Path.Combine(rollback, CaptureFileNames.Original));
        MyCapture.Platform.Imaging.ImageCodec.SavePng(Library.Bitmap(0xB0), library.Original);
        byte[] interim = File.ReadAllBytes(library.Original);
        File.WriteAllText(Path.Combine(stage, ".finalize-journal.json"), JsonSerializer.Serialize(new
        {
            RecordId = library.Record.Id, ContentRevision = 1, OriginalWidth = 12, OriginalHeight = 8,
            Files = new[] { new { FileName = CaptureFileNames.Original, HadPreviousFile = true } },
        }));
        library.Queue.BeforePublicationWriteForTest = (_, _) => throw new IOException("injected recovery invalidation failure");
        var recovery = new CapturePersistenceService(library.Queue, library.Paths, () => new QueueSettings(),
            NullLogger<CapturePersistenceService>.Instance);
        Assert.True(recovery.IsBusy(library.Record.Id));
        Assert.True(Directory.Exists(stage));
        Assert.Equal(interim, File.ReadAllBytes(library.Original));
        Assert.True(library.Queue.TryGetOriginalGeneration(library.Record, out _));

        library.Queue.BeforePublicationWriteForTest = null;
        _ = new CapturePersistenceService(library.Queue, library.Paths, () => new QueueSettings(),
            NullLogger<CapturePersistenceService>.Instance);
        Assert.False(Directory.Exists(stage));
        Assert.Equal(File.ReadAllBytes(library.OldSource), File.ReadAllBytes(library.Original));
        library.AssertHash(null);
    });

    [Fact]
    public Task JournalRollbackInvalidatesInterimHashAndRefusesSuspendedBackfill() => RunStaAsync(async () =>
    {
        using var library = new Library();
        string stage = Path.Combine(library.Queue.GetDirectory(library.Record), ".stage-" + Guid.NewGuid().ToString("N"));
        string rollback = Path.Combine(stage, ".rollback");
        Directory.CreateDirectory(rollback);
        File.Copy(library.Original, Path.Combine(rollback, CaptureFileNames.Original));
        MyCapture.Platform.Imaging.ImageCodec.SavePng(Library.Bitmap(0xB0), library.Original);
        string interimSource = library.CopyCurrentOriginal();
        File.WriteAllText(Path.Combine(stage, ".finalize-journal.json"), JsonSerializer.Serialize(new
        {
            RecordId = library.Record.Id, ContentRevision = 1, OriginalWidth = 12, OriginalHeight = 8,
            Files = new[] { new { FileName = CaptureFileNames.Original, HadPreviousFile = true } },
        }));
        var read = Signal();
        var resume = Signal();
        library.Import.AfterDuplicateReadForTest = async _ => { read.SetResult(); await resume.Task; };
        Task<CaptureImportResult> import = library.Import.ImportAsync([interimSource]);
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
            library.Record.OriginalContentHash = Hash(File.ReadAllBytes(interimSource));
            library.Queue.SaveRecordMetaOrThrow(library.Record);
            library.Queue.Save();
            _ = new CapturePersistenceService(library.Queue, library.Paths, () => new QueueSettings(),
                NullLogger<CapturePersistenceService>.Instance);
            Assert.Equal(File.ReadAllBytes(library.OldSource), File.ReadAllBytes(library.Original));
            library.AssertHash(null);
            Assert.False(Directory.Exists(stage));
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(1, (await import).Imported);
        library.AssertHash(null);
        library.Import.AfterDuplicateReadForTest = null;
        Assert.Equal(1, (await library.Import.ImportAsync([library.OldSource])).Duplicates);
        library.AssertHash(Hash(File.ReadAllBytes(library.OldSource)));
    });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Library : IDisposable
    {
        internal const string Url = "https://example.org/source";
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-hash-persistence-").FullName;
        internal Library()
        {
            Paths = AppPaths.CreateForRoot(Path.Combine(_root, "library"));
            Paths.EnsureCreated();
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => new QueueSettings(),
                NullLogger<CapturePersistenceService>.Instance);
            Record = Persistence.PersistOriginal(Bitmap(0x20), 1, "", "");
            OldSource = Path.Combine(_root, "old.png");
            File.Copy(Original, OldSource);
            Import = new CaptureImportService(Queue, (record, _, _) =>
            {
                record.Width = 12;
                record.Height = 8;
                return Task.CompletedTask;
            });
        }
        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal CaptureRecord Record { get; }
        internal CaptureImportService Import { get; }
        internal string Original => Queue.GetFilePath(Record, CaptureFileNames.Original);
        internal string OldSource { get; }
        internal async Task CacheHash()
        {
            Assert.Equal(1, (await Import.ImportAsync([OldSource])).Duplicates);
            AssertHash(Hash(File.ReadAllBytes(Original)));
        }
        internal string CopyCurrentOriginal()
        {
            string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
            File.Copy(Original, path);
            return path;
        }
        internal async Task Rewrite(bool sourceUrl, bool synchronous = false)
        {
            if (sourceUrl)
            {
                Assert.True(await Persistence.BackfillSourceAsync(Record, Url, CaptureSourceDetails.Empty, ""));
                return;
            }
            BitmapSource bitmap = Bitmap(0xB0);
            AnnotationDocument document = AnnotationDocument.CreateFor(12, 8);
            if (synchronous)
                Persistence.Finalize(Record, bitmap, document, new Dictionary<string, BitmapSource>(), replacementOriginal: bitmap);
            else
                await Persistence.FinalizeAsync(Record, bitmap, document, new Dictionary<string, BitmapSource>(), replacementOriginal: bitmap);
        }
        internal void AssertHash(string? expected)
        {
            Assert.Equal(expected, Record.OriginalContentHash);
            var reloaded = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            Assert.Equal(expected, reloaded.Find(Record.Id)!.OriginalContentHash);
            CaptureRecord meta = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(
                Queue.GetFilePath(Record, CaptureFileNames.Meta)), JsonDefaults.Readable)!;
            Assert.Equal(expected, meta.OriginalContentHash);
        }
        internal static BitmapSource Bitmap(byte value)
        {
            byte[] pixels = Enumerable.Repeat(value, 12 * 8 * 4).ToArray();
            BitmapSource bitmap = BitmapSource.Create(12, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 48);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = Signal();
        var thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}
