using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A capture saved before its page and application were read receives them afterwards, exactly as an
/// immediate save would have stored them, and never resurrects a capture deleted in the meantime.
/// </summary>
public sealed class CaptureSourceBackfillTests
{
    private const string Url = "https://example.com/pull/313?view=split#review";
    private const string Tag = "웹페이지";
    private static readonly CaptureSourceDetails Edge = new("Microsoft Edge", @"C:\Edge\msedge.exe");

    [Fact]
    public void LateSourceReachesRecordMetadataIndexAndBothImages() => StaTestHost.Run(() =>
    {
        using var library = new Library();
        CaptureRecord record = library.SaveWithoutSource();
        long bytesBefore = record.TotalBytes;
        int persisted = 0;
        library.Persistence.ImagePersisted += (_, _) => persisted++;

        Assert.True(library.Persistence.BackfillSourceAsync(record, Url, Edge, Tag).GetAwaiter().GetResult());

        Assert.Equal(Url, record.SourcePageUrl);
        Assert.Equal(Edge, record.SourceDetails);
        Assert.Equal(Tag, record.Tags);
        Assert.Equal(1, persisted);
        Assert.False(library.Persistence.IsBusy(record.Id));
        CaptureRecord reloaded = Assert.Single(library.Reload().Records);
        Assert.Equal(Url, reloaded.SourcePageUrl);
        Assert.Equal(Edge, reloaded.SourceDetails);
        Assert.Equal(Tag, reloaded.Tags);
        CaptureRecord meta = JsonSerializer.Deserialize<CaptureRecord>(
            File.ReadAllText(library.Queue.GetFilePath(record, CaptureFileNames.Meta)),
            MyCapture.Core.Serialization.JsonDefaults.Readable)!;
        Assert.Equal(Url, meta.SourcePageUrl);
        Assert.Equal(Edge, meta.SourceDetails);

        foreach (string name in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered })
        {
            string path = library.Queue.GetFilePath(record, name);
            byte[] png = File.ReadAllBytes(path);
            // Same bytes as a save that knew the page: the link only, never the application.
            Assert.Equal(Url, PngSourceUrlMetadata.TryReadSourceUrl(png));
            Assert.False(png.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Edge.AppPath)) >= 0);
            Assert.NotNull(ImageCodec.TryLoad(path));
            Assert.False(File.Exists(path + AtomicFile.BackupSuffix));
        }

        Assert.True(record.TotalBytes > bytesBefore);
    });

    [Fact]
    public void LateSourceNeverReplacesDetailsTheCaptureAlreadyHas() => StaTestHost.Run(() =>
    {
        using var library = new Library();
        CaptureRecord record = library.SaveWithoutSource(sourcePageUrl: "https://example.org/known");
        record.Tags = "내 태그";
        Assert.False(library.Persistence.BackfillSourceAsync(record, Url, Edge, Tag).GetAwaiter().GetResult());
        Assert.Equal("https://example.org/known", record.SourcePageUrl);
        Assert.True(record.SourceDetails.IsEmpty);

        CaptureRecord tagged = library.SaveWithoutSource();
        tagged.Tags = "내 태그";
        Assert.True(library.Persistence.BackfillSourceAsync(tagged, Url, Edge, Tag).GetAwaiter().GetResult());
        Assert.Equal("내 태그", tagged.Tags); // Tags the user typed meanwhile are kept.

        CaptureRecord empty = library.SaveWithoutSource();
        Assert.False(library.Persistence.BackfillSourceAsync(empty, "", CaptureSourceDetails.Empty, Tag).GetAwaiter().GetResult());
        Assert.Empty(empty.Tags);
    });

    [Fact]
    public void CaptureDeletedBeforeItsSourceArrivesIsNeverWrittenAgain() => StaTestHost.Run(() =>
    {
        using var library = new Library();
        CaptureRecord record = library.SaveWithoutSource();
        string directory = library.Queue.GetDirectory(record);
        Assert.True(library.Queue.Remove(record.Id));
        library.Queue.Save();
        Assert.False(Directory.Exists(directory));

        Assert.False(library.Persistence.BackfillSourceAsync(record, Url, Edge, Tag).GetAwaiter().GetResult());

        Assert.False(Directory.Exists(directory));
        Assert.Null(library.Queue.Find(record.Id));
        Assert.Empty(library.Reload().Records);
    });

    [Fact]
    public void UnsavedCaptureKeepsTheLateSourceInMemoryForItsOwnLaterSave() => StaTestHost.Run(() =>
    {
        using var library = new Library();
        BitmapSource bitmap = Bitmap();
        CaptureRecord pending = CapturePersistenceService.CreatePendingRecord(bitmap, 1, "Docs", "DISPLAY1");
        Assert.True(library.Persistence.BackfillSourceAsync(pending, Url, Edge, Tag, unpublished: true)
            .GetAwaiter().GetResult());
        Assert.False(Directory.Exists(library.Queue.GetDirectory(pending)));
        Assert.Equal(Url, pending.SourcePageUrl);

        // The retry that saves it writes the page exactly as a direct save does.
        library.Persistence.PersistPendingOriginalAsync(pending, bitmap).GetAwaiter().GetResult();
        Assert.Equal(Url, PngSourceUrlMetadata.TryReadSourceUrl(
            File.ReadAllBytes(library.Queue.GetFilePath(pending, CaptureFileNames.Original))));
        Assert.Equal(Edge, Assert.Single(library.Reload().Records).SourceDetails);
    });

    [Fact]
    public void EditorSaveStartedDuringTheBackfillWaitsForItAndKeepsThePage() => StaTestHost.Run(() =>
    {
        using var library = new Library();
        CaptureRecord record = library.SaveWithoutSource();
        Task<bool> backfill = library.Persistence.BackfillSourceAsync(record, Url, Edge, Tag);
        // The backfill owns the capture's write slot while it embeds the link in the images.
        Assert.True(library.Persistence.IsWriteInProgress(record.Id) || backfill.IsCompleted);
        BitmapSource edited = Bitmap(0x20);
        library.Persistence.FinalizeAsync(record, edited, AnnotationDocument.CreateFor(edited.PixelWidth, edited.PixelHeight),
            new Dictionary<string, BitmapSource>()).GetAwaiter().GetResult();

        Assert.True(backfill.GetAwaiter().GetResult());
        Assert.Equal(1, record.ContentRevision);
        Assert.Equal(Url, PngSourceUrlMetadata.TryReadSourceUrl(
            File.ReadAllBytes(library.Queue.GetFilePath(record, CaptureFileNames.Rendered))));
        Assert.Equal(Url, Assert.Single(library.Reload().Records).SourcePageUrl);
    });

    private static BitmapSource Bitmap(byte value = 0x7A)
    {
        byte[] pixels = Enumerable.Repeat(value, 12 * 8 * 4).ToArray();
        BitmapSource bitmap = BitmapSource.Create(12, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 12 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class Library : IDisposable
    {
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-backfill-").FullName;
        private readonly AppPaths _paths;
        private readonly AppSettings _settings = new();

        internal Library()
        {
            _paths = AppPaths.CreateForRoot(_root);
            Queue = new CaptureQueue(_paths, _settings.Queue, NullLogger<CaptureQueue>.Instance);
            // As the app does: a removed capture's folder is deleted.
            Queue.Evicted += (_, e) =>
            {
                string directory = Queue.GetDirectory(e.Record);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            };
            Persistence = new CapturePersistenceService(Queue, _paths, () => _settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
        }

        internal CaptureQueue Queue { get; }

        internal CapturePersistenceService Persistence { get; }

        internal CaptureRecord SaveWithoutSource(string sourcePageUrl = "")
        {
            BitmapSource bitmap = Bitmap();
            CaptureRecord record = CapturePersistenceService.CreatePendingRecord(bitmap, 1, "Docs", "DISPLAY1",
                sourcePageUrl: sourcePageUrl);
            return Persistence.PersistPendingOriginalAsync(record, bitmap).GetAwaiter().GetResult();
        }

        internal CaptureQueue Reload()
        {
            var reloaded = new CaptureQueue(_paths, _settings.Queue, NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            return reloaded;
        }

        public void Dispose() => TestRecycleBin.DeleteDirectory(_root, recursive: true);
    }
}
