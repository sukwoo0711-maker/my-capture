using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Undo;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The gallery's testable layers: date grouping, search/filter, pin persistence,
/// delete/index/directory behaviour, re-edit load, and thumbnail refresh.
/// </summary>
/// <remarks>
/// The pure grouping/filter/pin/delete behaviour runs on a plain thread. Anything that
/// touches WPF imaging (creating real capture directories, decoding thumbnails, loading a
/// re-edit context) runs on a dedicated STA thread, matching the other App tests.
/// </remarks>
public sealed class GalleryTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mycapture-gallery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static BitmapSource Solid(int w, int h, byte r = 0x40, byte g = 0x60, byte b = 0x80)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        byte[] px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = 0xFF;
        }

        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private static CaptureQueue NewQueue(AppPaths paths, QueueSettings limits, EventHandler<CaptureEvictedEventArgs>? evicted = null)
    {
        var queue = new CaptureQueue(paths, limits, NullLogger<CaptureQueue>.Instance);
        if (evicted is not null)
        {
            queue.Evicted += evicted;
        }

        return queue;
    }

    private static GalleryController NewController(CaptureQueue queue) =>
        new(queue, NullLogger<GalleryController>.Instance);

    private static CaptureRecord AddSyntheticRecord(CaptureQueue queue, DateTimeOffset createdAt, string title = "")
    {
        var record = new CaptureRecord
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Width = 100,
            Height = 80,
            Title = title,
            TotalBytes = 1000,
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        queue.Add(record);
        return record;
    }

    // ---- Date grouping -------------------------------------------------------------

    [Fact]
    public void DateGrouping_TodayYesterdayAndOlder()
    {
        var now = new DateTimeOffset(2026, 8, 29, 20, 0, 0, TimeSpan.FromHours(9));

        GalleryDateGroup today = GalleryDateGrouping.Resolve(now.AddHours(-2), now);
        GalleryDateGroup edgeOfToday = GalleryDateGrouping.Resolve(
            new DateTimeOffset(2026, 8, 29, 0, 10, 0, TimeSpan.FromHours(9)), now);
        GalleryDateGroup yesterday = GalleryDateGrouping.Resolve(now.AddDays(-1), now);
        GalleryDateGroup older = GalleryDateGrouping.Resolve(now.AddDays(-10), now);
        GalleryDateGroup lastYear = GalleryDateGrouping.Resolve(now.AddYears(-1), now);

        Assert.Equal("오늘", today.Heading);
        Assert.Equal("오늘", edgeOfToday.Heading);
        Assert.Equal("어제", yesterday.Heading);
        Assert.NotEqual("오늘", older.Heading);
        Assert.NotEqual("어제", older.Heading);
        Assert.Contains("2025", lastYear.Heading);
    }

    [Fact]
    public void BuildGroups_OrdersDaysNewestFirstAndTilesNewestFirst()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                GalleryController controller = NewController(queue);

                var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(9));
                AddSyntheticRecord(queue, now.AddDays(-2).AddHours(1), "older");
                CaptureRecord todayEarly = AddSyntheticRecord(queue, now.AddHours(-5), "today-early");
                CaptureRecord todayLate = AddSyntheticRecord(queue, now.AddHours(-1), "today-late");

                IReadOnlyList<GalleryGroupedRecords> groups = controller.BuildGroups(null, now);

                Assert.Equal(2, groups.Count);
                // Newest day first.
                Assert.Equal("오늘", groups[0].Group.Heading);
                // Newest tile first within the day.
                Assert.Equal(todayLate.Id, groups[0].Records[0].Id);
                Assert.Equal(todayEarly.Id, groups[0].Records[1].Id);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void BuildGroups_FiltersBySearchHaystack()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                GalleryController controller = NewController(queue);

                var now = DateTimeOffset.Now;
                AddSyntheticRecord(queue, now.AddMinutes(-3), "메모장 문서");
                CaptureRecord browser = AddSyntheticRecord(queue, now.AddMinutes(-1), "브라우저 화면");

                IReadOnlyList<GalleryGroupedRecords> hits = controller.BuildGroups("브라우저", now);

                CaptureRecord only = Assert.Single(hits.SelectMany(g => g.Records));
                Assert.Equal(browser.Id, only.Id);

                // Case-insensitive and matches nothing when absent.
                Assert.Empty(controller.BuildGroups("존재하지않음", now).SelectMany(g => g.Records));
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void CacheOcr_PersistsTextAndMakesItSearchable()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                GalleryController controller = NewController(queue);

                var now = DateTimeOffset.Now;
                CaptureRecord record = AddSyntheticRecord(queue, now.AddMinutes(-1), "제목없음");

                controller.CacheOcr(record.Id, "invoice total 12345", "en-US");

                // The record now carries the cached text and language.
                CaptureRecord? reloaded = controller.Find(record.Id);
                Assert.NotNull(reloaded);
                Assert.Equal("invoice total 12345", reloaded!.OcrText);
                Assert.Equal("en-US", reloaded.OcrLanguage);
                Assert.True(reloaded.HasOcrText);

                // The cached text feeds the search haystack, so the capture is now findable by it.
                IReadOnlyList<GalleryGroupedRecords> hits = controller.BuildGroups("invoice", now);
                CaptureRecord only = Assert.Single(hits.SelectMany(g => g.Records));
                Assert.Equal(record.Id, only.Id);

                // The change is persisted to the per-capture meta sidecar for index rebuild.
                string metaPath = queue.GetFilePath(record, CaptureFileNames.Meta);
                Assert.True(File.Exists(metaPath));
                Assert.Contains("invoice total 12345", File.ReadAllText(metaPath), StringComparison.Ordinal);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- Pin persistence -----------------------------------------------------------

    [Fact]
    public void TogglePin_PersistsToIndexAndMetaAndSurvivesReload()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);
                GalleryController controller = NewController(queue);

                CaptureRecord record = persistence.PersistOriginal(Solid(40, 30), 1.0, string.Empty, string.Empty);

                bool? pinned = controller.TogglePin(record.Id);
                Assert.True(pinned);

                // The per-capture meta.json now records the pin (camelCase, per JsonDefaults).
                string metaPath = queue.GetFilePath(record, CaptureFileNames.Meta);
                Assert.Contains("\"isPinned\": true", File.ReadAllText(metaPath));

                // And the index reload keeps it.
                CaptureQueue reloaded = NewQueue(paths, settings);
                reloaded.Load();
                Assert.True(reloaded.Records[0].IsPinned);

                // Toggling back off persists too.
                Assert.False(controller.TogglePin(record.Id));
                CaptureQueue reloadedAgain = NewQueue(paths, settings);
                reloadedAgain.Load();
                Assert.False(reloadedAgain.Records[0].IsPinned);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- Delete: index + directory -------------------------------------------------

    [Fact]
    public void Delete_RemovesFromIndexAndDeletesDirectoryViaEvictedHandler()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();

                // Wire the same Evicted->delete-directory behaviour the shell uses.
                CaptureQueue queue = null!;
                queue = NewQueue(paths, settings, (_, e) =>
                {
                    string dir = queue.GetDirectory(e.Record);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                });

                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);
                GalleryController controller = NewController(queue);

                CaptureRecord record = persistence.PersistOriginal(Solid(40, 30), 1.0, string.Empty, string.Empty);
                string directory = queue.GetDirectory(record);
                Assert.True(Directory.Exists(directory));

                bool deleted = controller.Delete(record.Id);

                Assert.True(deleted);
                Assert.Equal(0, controller.Count);
                Assert.False(Directory.Exists(directory), "capture directory should be deleted");

                // The removal was saved: a reload sees nothing.
                CaptureQueue reloaded = NewQueue(paths, settings);
                reloaded.Load();
                Assert.Empty(reloaded.Records);

                // Deleting a missing id is a no-op.
                Assert.False(controller.Delete(Guid.NewGuid()));
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- Re-edit load --------------------------------------------------------------

    [Fact]
    public void ReeditLoad_RestoresLayerDocumentAndImageAssets()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);

                CaptureRecord record = persistence.PersistOriginal(Solid(120, 90), 1.0, string.Empty, string.Empty);

                // Finalise with a rectangle and an inserted image so layers.json and an
                // asset-XX.png sidecar exist on disk.
                var document = AnnotationDocument.CreateFor(120, 90);
                document.Add(new RectangleAnnotation
                {
                    Rect = new RectD(5, 5, 60, 40),
                    Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
                    StrokeThickness = 3,
                });
                const string sessionAsset = "image-01.png";
                document.Add(new ImageAnnotation
                {
                    AssetFileName = sessionAsset,
                    SourceWidth = 20,
                    SourceHeight = 20,
                    Rect = new RectD(10, 10, 20, 20),
                });

                BitmapSource assetBitmap = Solid(20, 20, 0x10, 0x90, 0x30);
                var decoded = new Dictionary<string, BitmapSource> { [sessionAsset] = assetBitmap };
                var renderer = new AnnotationRenderer(AnnotationImageStore.FromDecoded(decoded));
                BitmapSource flattened = AnnotationFlattener.Flatten(
                    persistenceOriginal(paths, queue, record), document, renderer);
                persistence.Finalize(record, flattened, document, decoded);

                // The canonical sidecar exists now.
                string dir = queue.GetDirectory(record);
                Assert.True(File.Exists(Path.Combine(dir, $"{CaptureFileNames.AssetPrefix}01.png")));

                var loader = new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance);
                GalleryReeditContext? context = loader.TryLoad(record, out GalleryReeditLoader.LoadFailure failure);

                Assert.Equal(GalleryReeditLoader.LoadFailure.None, failure);
                Assert.NotNull(context);

                // The editable base is the original at full size, with a full-image crop.
                Assert.Equal(120, context!.OriginalBitmap.PixelWidth);
                Assert.Equal(90, context.OriginalBitmap.PixelHeight);
                Assert.Equal(0, context.CropRegion.Left);
                Assert.Equal(0, context.CropRegion.Top);
                Assert.Equal(120, context.CropRegion.Width);
                Assert.Equal(90, context.CropRegion.Height);

                // The layer document was restored with both items.
                Assert.Equal(2, context.Document.Items.Count);
                ImageAnnotation image = Assert.Single(context.Document.Items.OfType<ImageAnnotation>());

                // The image asset was decoded from the canonical sidecar and keyed by name.
                Assert.Equal($"{CaptureFileNames.AssetPrefix}01.png", image.AssetFileName);
                Assert.True(context.AssetBitmaps.ContainsKey(image.AssetFileName));
                Assert.True(context.AssetBitmaps[image.AssetFileName].IsFrozen);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void ReeditLoad_MissingOriginalFailsWithoutCrashing()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);

                CaptureRecord record = persistence.PersistOriginal(Solid(40, 30), 1.0, string.Empty, string.Empty);
                File.Delete(queue.GetFilePath(record, CaptureFileNames.Original));

                var loader = new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance);
                GalleryReeditContext? context = loader.TryLoad(record, out GalleryReeditLoader.LoadFailure failure);

                Assert.Null(context);
                Assert.Equal(GalleryReeditLoader.LoadFailure.MissingOriginal, failure);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void ReeditLoad_MissingAssetDropsAnnotationButStillOpens()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);

                CaptureRecord record = persistence.PersistOriginal(Solid(80, 60), 1.0, string.Empty, string.Empty);

                var document = AnnotationDocument.CreateFor(80, 60);
                document.Add(new ImageAnnotation
                {
                    AssetFileName = "image-01.png",
                    SourceWidth = 10,
                    SourceHeight = 10,
                    Rect = new RectD(2, 2, 10, 10),
                });
                BitmapSource asset = Solid(10, 10);
                var decoded = new Dictionary<string, BitmapSource> { ["image-01.png"] = asset };
                var renderer = new AnnotationRenderer(AnnotationImageStore.FromDecoded(decoded));
                BitmapSource flattened = AnnotationFlattener.Flatten(
                    persistenceOriginal(paths, queue, record), document, renderer);
                persistence.Finalize(record, flattened, document, decoded);

                // Delete the canonical sidecar so the load must drop that annotation.
                string dir = queue.GetDirectory(record);
                File.Delete(Path.Combine(dir, $"{CaptureFileNames.AssetPrefix}01.png"));

                var loader = new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance);
                GalleryReeditContext? context = loader.TryLoad(record, out GalleryReeditLoader.LoadFailure failure);

                Assert.Equal(GalleryReeditLoader.LoadFailure.None, failure);
                Assert.NotNull(context);
                // The image annotation was dropped; the capture still opens.
                Assert.Empty(context!.Document.Items.OfType<ImageAnnotation>());
                Assert.Empty(context.AssetBitmaps);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void ReeditLoad_RejectsAssetPathsOutsideCaptureDirectory()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);
                CaptureRecord record = persistence.PersistOriginal(
                    Solid(80, 60), 1.0, string.Empty, string.Empty);

                string outsidePath = Path.Combine(root, "outside.png");
                _ = MyCapture.Platform.Imaging.ImageCodec.SavePng(Solid(8, 8), outsidePath);

                var document = AnnotationDocument.CreateFor(80, 60);
                document.Add(new ImageAnnotation
                {
                    AssetFileName = outsidePath,
                    SourceWidth = 8,
                    SourceHeight = 8,
                    Rect = new RectD(1, 1, 8, 8),
                });
                document.Add(new ImageAnnotation
                {
                    AssetFileName = Path.Combine("..", "..", "..", "outside.png"),
                    SourceWidth = 8,
                    SourceHeight = 8,
                    Rect = new RectD(10, 1, 8, 8),
                });
                File.WriteAllText(
                    queue.GetFilePath(record, CaptureFileNames.Layers),
                    document.ToJson());

                var loader = new GalleryReeditLoader(
                    queue,
                    NullLogger<GalleryReeditLoader>.Instance);
                GalleryReeditContext? context = loader.TryLoad(
                    record,
                    out GalleryReeditLoader.LoadFailure failure);

                Assert.Equal(GalleryReeditLoader.LoadFailure.None, failure);
                Assert.NotNull(context);
                Assert.Empty(context!.Document.Items.OfType<ImageAnnotation>());
                Assert.Empty(context.AssetBitmaps);
                Assert.True(File.Exists(outsidePath));
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- Thumbnail refresh ---------------------------------------------------------

    [Fact]
    public void ThumbnailRefresh_ReloadsDecodedTileFromDisk()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                var persistence = new CapturePersistenceService(
                    queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);

                CaptureRecord record = persistence.PersistOriginal(Solid(300, 200), 1.0, string.Empty, string.Empty);

                var tile = new GalleryItemViewModel(
                    record,
                    r => Path.Combine(queue.GetDirectory(r), CaptureFileNames.Thumbnail),
                    settings.ThumbnailLongEdge);

                BitmapSource? first = tile.Thumbnail;
                Assert.NotNull(first);
                Assert.False(tile.IsBroken);

                // Rewrite the thumbnail on disk, then refresh: a fresh bitmap is decoded.
                string thumbPath = queue.GetFilePath(record, CaptureFileNames.Thumbnail);
                BitmapSource smaller = ImageCodecThumb(Solid(120, 80, 0xFF, 0x00, 0x00), settings.ThumbnailLongEdge);
                _ = MyCapture.Platform.Imaging.ImageCodec.SaveJpeg(
                    smaller, thumbPath, MyCapture.Platform.Imaging.ImageCodec.ThumbnailJpegQuality);

                tile.RefreshThumbnail();
                BitmapSource? refreshed = tile.Thumbnail;

                Assert.NotNull(refreshed);
                Assert.NotSame(first, refreshed);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void ThumbnailBroken_WhenFileMissing()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                var record = new CaptureRecord { Width = 100, Height = 80 };
                var tile = new GalleryItemViewModel(
                    record,
                    _ => Path.Combine(root, "does-not-exist.jpg"),
                    320);

                Assert.Null(tile.Thumbnail);
                Assert.True(tile.IsBroken);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- Caption fallback / external drag export ----------------------------------

    [Fact]
    public void Caption_BlankRecordHasNoUntitledPlaceholder()
    {
        var created = new DateTimeOffset(2026, 8, 30, 9, 7, 0, TimeSpan.FromHours(9));
        var record = new CaptureRecord
        {
            CreatedAt = created,
            UpdatedAt = created,
            Width = 320,
            Height = 180,
        };
        var tile = new GalleryItemViewModel(record, _ => "unused.jpg", 320);

        Assert.Equal(string.Empty, tile.Caption);
        Assert.False(tile.HasCaption);
        Assert.Equal("캡처 09:07", tile.ContextLabel);
        Assert.DoesNotContain("제목 없음", tile.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("제목 없는", tile.AccessibleName, StringComparison.Ordinal);

        record.SourceWindowTitle = "문서 창";
        var titled = new GalleryItemViewModel(record, _ => "unused.jpg", 320);
        Assert.Equal("문서 창", titled.Caption);
        Assert.True(titled.HasCaption);
        Assert.Equal("문서 창", titled.ContextLabel);
    }

    [Fact]
    public void DragExport_StagesUniqueTimestampedCopiesAndAdvertisesFileDropCopy()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                CaptureQueue queue = NewQueue(paths, new QueueSettings());
                var timestamp = new DateTimeOffset(2026, 8, 30, 14, 5, 9, TimeSpan.FromHours(9));
                CaptureRecord record = AddSyntheticRecord(queue, timestamp);
                Directory.CreateDirectory(queue.GetDirectory(record));
                byte[] expected = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];
                File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Rendered), expected);

                string staging = Path.Combine(root, "drag-staging");
                var service = new GalleryDragExportService(queue, staging, () => timestamp);

                string first = service.PrepareExport(record);
                string second = service.PrepareExport(record);

                Assert.Equal("MyCapture_20260830_140509.png", Path.GetFileName(first));
                Assert.Equal("MyCapture_20260830_140509-02.png", Path.GetFileName(second));
                Assert.Equal(expected, File.ReadAllBytes(first));
                Assert.Equal(expected, File.ReadAllBytes(second));
                Assert.Equal("MyCapture_20260830_140509.png", GalleryDragExportService.BuildBaseFileName(timestamp));

                DataObject data = GalleryDragExportService.CreateFileDropData(first);
                string[] droppedPaths = Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop));
                Assert.Equal(Path.GetFullPath(first), Assert.Single(droppedPaths));
                Stream preferred = Assert.IsAssignableFrom<Stream>(
                    data.GetData(GalleryDragExportService.PreferredDropEffectFormat));
                preferred.Position = 0;
                using var reader = new BinaryReader(preferred, System.Text.Encoding.UTF8, leaveOpen: true);
                Assert.Equal((int)DragDropEffects.Copy, reader.ReadInt32());
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- Row splitting / header order / column count -------------------------------

    /// <summary>
    /// A tile whose thumbnail path resolver throws if called — proves row building and column
    /// resolution never touch <see cref="GalleryItemViewModel.Thumbnail"/>, so an unrealised
    /// tile decodes nothing.
    /// </summary>
    private static GalleryItemViewModel UndecodableTile(string title = "")
    {
        var record = new CaptureRecord { Width = 10, Height = 10, Title = title };
        return new GalleryItemViewModel(
            record,
            _ => throw new Xunit.Sdk.XunitException("thumbnail must not be requested during row building"),
            320);
    }

    private static GalleryGroupViewModel Group(string heading, int tileCount)
    {
        var tiles = new List<GalleryItemViewModel>(tileCount);
        for (int i = 0; i < tileCount; i++)
        {
            tiles.Add(UndecodableTile($"{heading}-{i}"));
        }

        return new GalleryGroupViewModel(heading, tiles);
    }

    [Theory]
    [InlineData(0, 2)]        // before first layout → minimum
    [InlineData(-50, 2)]      // nonsense width → minimum
    [InlineData(400, 2)]      // one track fits, floored to the minimum of 2
    [InlineData(512, 2)]      // exactly two tracks → 2
    [InlineData(767, 2)]      // just below the three-track boundary
    [InlineData(768, 3)]      // exactly three tracks → 3
    [InlineData(1024, 4)]     // four tracks fill the default content width
    [InlineData(1280, 5)]     // wide desktop → 5
    [InlineData(1536, 6)]     // extra-wide desktop → 6
    [InlineData(4000, 6)]     // clamped to the maximum of 6
    public void ColumnCountForWidth_ClampsBetweenTwoAndSix(double width, int expected)
    {
        Assert.Equal(expected, GalleryRowBuilder.ColumnCountForWidth(width));
    }

    [Fact]
    public void Build_EmitsHeaderThenTileRowsInOrderForEachGroup()
    {
        var groups = new List<GalleryGroupViewModel>
        {
            Group("오늘", 5),
            Group("어제", 2),
        };

        IReadOnlyList<GalleryRow> rows = GalleryRowBuilder.Build(groups, columns: 3);

        // 오늘: header + ceil(5/3)=2 tile rows; 어제: header + 1 tile row.
        Assert.Equal(5, rows.Count);

        var today = Assert.IsType<GalleryHeaderRow>(rows[0]);
        Assert.Equal("오늘", today.Heading);
        var todayRow1 = Assert.IsType<GalleryTileRow>(rows[1]);
        Assert.Equal(3, todayRow1.Tiles.Count);
        var todayRow2 = Assert.IsType<GalleryTileRow>(rows[2]);
        Assert.Equal(2, todayRow2.Tiles.Count); // partial final row keeps its remainder

        var yesterday = Assert.IsType<GalleryHeaderRow>(rows[3]);
        Assert.Equal("어제", yesterday.Heading);
        var yesterdayRow = Assert.IsType<GalleryTileRow>(rows[4]);
        Assert.Equal(2, yesterdayRow.Tiles.Count);
    }

    [Fact]
    public void Build_PreservesTileOrderAcrossRows()
    {
        GalleryGroupViewModel group = Group("오늘", 4);
        IReadOnlyList<GalleryItemViewModel> original = group.Items;

        IReadOnlyList<GalleryRow> rows = GalleryRowBuilder.Build([group], columns: 2);

        var flat = rows.OfType<GalleryTileRow>().SelectMany(r => r.Tiles).ToList();
        Assert.Equal(original, flat);
    }

    [Fact]
    public void Build_ColumnCountBoundsRowWidth()
    {
        GalleryGroupViewModel group = Group("오늘", 4);

        IReadOnlyList<GalleryTileRow> narrow =
            GalleryRowBuilder.Build([group], columns: 2).OfType<GalleryTileRow>().ToList();
        Assert.Equal(2, narrow.Count);
        Assert.All(narrow, r => Assert.Equal(2, r.Tiles.Count));

        IReadOnlyList<GalleryTileRow> wide =
            GalleryRowBuilder.Build([group], columns: 4).OfType<GalleryTileRow>().ToList();
        GalleryTileRow single = Assert.Single(wide);
        Assert.Equal(4, single.Tiles.Count);
    }

    [Fact]
    public void Build_RaisesColumnsBelowMinimumToTwo()
    {
        GalleryGroupViewModel group = Group("오늘", 3);

        // A caller asking for 1 column must still get rows of at most 2 (never 1).
        IReadOnlyList<GalleryTileRow> rows =
            GalleryRowBuilder.Build([group], columns: 1).OfType<GalleryTileRow>().ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Tiles.Count);
        Assert.Single(rows[1].Tiles);
    }

    [Fact]
    public void SetColumnCountForWidth_RebuildsRowsOnlyWhenColumnCountChanges()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                GalleryController controller = NewController(queue);

                var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(9));
                for (int i = 0; i < 5; i++)
                {
                    AddSyntheticRecord(queue, now.AddMinutes(-i), $"c{i}");
                }

                var vm = new GalleryViewModel(
                    controller,
                    r => Path.Combine(queue.GetDirectory(r), CaptureFileNames.Thumbnail),
                    settings.ThumbnailLongEdge,
                    () => now);

                // The constructor already resolved the minimum (2) columns, so asking for a
                // width still in the 2-column band is a no-op.
                Assert.Equal(2, vm.ColumnCount);
                Assert.False(vm.SetColumnCountForWidth(544));
                Assert.Equal(2, vm.ColumnCount);
                // header + ceil(5/2)=3 tile rows = 4 rows.
                Assert.Equal(4, vm.Rows.Count);
                Assert.IsType<GalleryHeaderRow>(vm.Rows[0]);

                // A resize that stays inside the 2-column band changes nothing.
                Assert.False(vm.SetColumnCountForWidth(500));
                Assert.Equal(2, vm.ColumnCount);
                Assert.Equal(4, vm.Rows.Count);

                // Widen to 4 columns → header + ceil(5/4)=2 tile rows = 3 rows.
                Assert.True(vm.SetColumnCountForWidth(1200));
                Assert.Equal(4, vm.ColumnCount);
                Assert.Equal(3, vm.Rows.Count);

                // First tile row now holds 4 tiles; header stays first.
                Assert.IsType<GalleryHeaderRow>(vm.Rows[0]);
                var firstTileRow = Assert.IsType<GalleryTileRow>(vm.Rows[1]);
                Assert.Equal(4, firstTileRow.Tiles.Count);

                // Narrowing back below the 3-column threshold rebuilds to 2 columns again.
                Assert.True(vm.SetColumnCountForWidth(544));
                Assert.Equal(2, vm.ColumnCount);
                Assert.Equal(4, vm.Rows.Count);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    [Fact]
    public void Rows_ReflectSearchFilterAndHeaderOrder()
    {
        RunSta(() =>
        {
            string root = NewRoot();
            try
            {
                AppPaths paths = AppPaths.CreateForRoot(root);
                var settings = new QueueSettings();
                CaptureQueue queue = NewQueue(paths, settings);
                GalleryController controller = NewController(queue);

                var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.FromHours(9));
                AddSyntheticRecord(queue, now.AddHours(-1), "메모장");
                AddSyntheticRecord(queue, now.AddDays(-1).AddHours(-1), "브라우저");

                var vm = new GalleryViewModel(
                    controller,
                    r => Path.Combine(queue.GetDirectory(r), CaptureFileNames.Thumbnail),
                    settings.ThumbnailLongEdge,
                    () => now);
                vm.SetColumnCountForWidth(1000);

                // Two days → two header rows, newest first.
                var headers = vm.Rows.OfType<GalleryHeaderRow>().Select(h => h.Heading).ToList();
                Assert.Equal(new[] { "오늘", "어제" }, headers);

                // A search that matches only yesterday's tile leaves a single header + row.
                vm.SearchQuery = "브라우저";
                Assert.Equal(2, vm.Rows.Count);
                var only = Assert.IsType<GalleryHeaderRow>(vm.Rows[0]);
                Assert.Equal("어제", only.Heading);
                Assert.IsType<GalleryTileRow>(vm.Rows[1]);
            }
            finally
            {
                DeleteRoot(root);
            }
        });
    }

    // ---- WPF realization: off-screen tiles do not decode ---------------------------

    [Fact]
    public void Realization_OffscreenTileDoesNotRequestThumbnail()
    {
        RunSta(() =>
        {
            // A resolver that records which records were asked for a thumbnail path. The path
            // is only resolved when a tile's Thumbnail getter runs, which only happens when its
            // row container is realised — so this set is exactly the realised tiles.
            var requested = new HashSet<Guid>();

            var tiles = new List<GalleryItemViewModel>();
            for (int i = 0; i < 200; i++)
            {
                var record = new CaptureRecord { Width = 10, Height = 10, Title = $"t{i}" };
                tiles.Add(new GalleryItemViewModel(
                    record,
                    r =>
                    {
                        requested.Add(r.Id);
                        return "does-not-exist.jpg"; // resolves to broken, never decodes a frame
                    },
                    320));
            }

            var group = new GalleryGroupViewModel("오늘", tiles);
            IReadOnlyList<GalleryRow> rows = GalleryRowBuilder.Build([group], columns: 2);

            // Build the same one-outer-list arrangement the window uses: a virtualizing,
            // recycling, content-scrolling ListBox with NO enclosing ScrollViewer. Tile rows
            // bind an Image to Thumbnail exactly as the real tile template does.
            var list = new ListBox
            {
                ItemsSource = rows,
                ItemTemplateSelector = new RowTemplateSelector(
                    BuildHeaderTemplate(), BuildTileRowTemplate()),
            };
            list.SetValue(VirtualizingStackPanel.IsVirtualizingProperty, true);
            list.SetValue(VirtualizingStackPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
            list.SetValue(ScrollViewer.CanContentScrollProperty, true);
            list.ItemsPanel = BuildVirtualizingPanelTemplate();

            // A tall window would realise everything; pin the viewport small so only the top
            // rows are realised and the rest stay virtualized.
            var host = new Window
            {
                Content = list,
                Width = 500,
                Height = 260,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -10000,
                Top = -10000,
            };

            host.Show();
            try
            {
                // Let the Loaded/layout passes run so the ListBox generates containers for the
                // rows that fit the viewport and virtualizes the rest.
                PumpTo(System.Windows.Threading.DispatcherPriority.Loaded);
                list.UpdateLayout();
                PumpTo(System.Windows.Threading.DispatcherPriority.Loaded);

                // Sanity: the outer list virtualized — the first row realised, the last did not.
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(rows.Count - 1));

                Assert.NotEmpty(requested); // some top rows realised and decoded
                Assert.True(requested.Count < tiles.Count,
                    $"expected virtualization to leave most tiles unrealised, but {requested.Count}/{tiles.Count} decoded");

                // The very last tile is far below the viewport; it must not have decoded.
                Assert.DoesNotContain(tiles[^1].Id, requested);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void ReeditSurface_WideViewportFitsFrameWithoutNegativeGeometry()
    {
        RunSta(() =>
        {
            BitmapSource original = Solid(344, 144);
            var frame = new MyCapture.Platform.Capture.FrozenFrame(
                original,
                new RectD(0, 0, original.PixelWidth, original.PixelHeight),
                Monitor: null,
                ElapsedMilliseconds: 0);
            var crop = new RectD(0, 0, original.PixelWidth, original.PixelHeight);
            var document = AnnotationDocument.CreateFor(original.PixelWidth, original.PixelHeight);
            var controller = new AnnotationEditorController(document, new UndoStack());
            var renderer = new AnnotationRenderer(
                AnnotationImageStore.FromDecoded(new Dictionary<string, BitmapSource>()));
            var surface = new AnnotationEditorSurface(frame, crop, controller, renderer);

            // Wider than the source aspect ratio: the old width-only scale made the crop
            // extend below this 800x250 viewport and WPF threw "Width and Height must be
            // non-negative" while drawing the bottom dimmer rectangle.
            surface.Measure(new Size(800, 250));
            surface.Arrange(new Rect(0, 0, 800, 250));
            surface.UpdateLayout();

            var rendered = new RenderTargetBitmap(800, 250, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(surface); // Must not throw.

            Assert.True(surface.FrameRectDip.Left >= 0);
            Assert.True(surface.FrameRectDip.Top >= 0);
            Assert.True(surface.FrameRectDip.Right <= surface.ActualWidth + 0.001);
            Assert.True(surface.FrameRectDip.Bottom <= surface.ActualHeight + 0.001);
            Assert.Equal(250.0 / 144.0, surface.DipPerPixel, 6);

            var imagePoint = new PointD(123, 57);
            Point dipPoint = surface.ToSurfacePoint(imagePoint);
            PointD roundTrip = surface.ToImagePoint(dipPoint);
            Assert.Equal(imagePoint.X, roundTrip.X, 6);
            Assert.Equal(imagePoint.Y, roundTrip.Y, 6);
        });
    }

    private static DataTemplate BuildTileRowTemplate()
    {
        // Tile row: an ItemsControl of Images bound to Thumbnail, matching the production
        // tile template's decode trigger. Kept non-virtual — a row has only 2-4 items.
        var tileRow = new FrameworkElementFactory(typeof(ItemsControl));
        tileRow.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(GalleryTileRow.Tiles)));

        var tileItemPanel = new FrameworkElementFactory(typeof(StackPanel));
        tileItemPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        tileRow.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate { VisualTree = tileItemPanel });

        var image = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        image.SetValue(FrameworkElement.WidthProperty, 220.0);
        image.SetValue(FrameworkElement.HeightProperty, 180.0);
        image.SetBinding(System.Windows.Controls.Image.SourceProperty,
            new System.Windows.Data.Binding(nameof(GalleryItemViewModel.Thumbnail)));
        tileRow.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = image });

        var template = new DataTemplate(typeof(GalleryTileRow)) { VisualTree = tileRow };
        template.Seal();
        return template;
    }

    private static DataTemplate BuildHeaderTemplate()
    {
        var headerText = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        headerText.SetBinding(System.Windows.Controls.TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(GalleryHeaderRow.Heading)));
        var template = new DataTemplate(typeof(GalleryHeaderRow)) { VisualTree = headerText };
        template.Seal();
        return template;
    }

    private static ItemsPanelTemplate BuildVirtualizingPanelTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Vertical);
        var template = new ItemsPanelTemplate { VisualTree = panel };
        template.Seal();
        return template;
    }

    /// <summary>Drains the WPF dispatcher queue up to <paramref name="priority"/> so layout and
    /// virtualization run synchronously before assertions.</summary>
    private static void PumpTo(System.Windows.Threading.DispatcherPriority priority)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            priority,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>Picks the header vs. tile-row template by the row's runtime type.</summary>
    private sealed class RowTemplateSelector : DataTemplateSelector
    {
        private readonly DataTemplate _header;
        private readonly DataTemplate _tileRow;

        public RowTemplateSelector(DataTemplate header, DataTemplate tileRow)
        {
            _header = header;
            _tileRow = tileRow;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is GalleryHeaderRow ? _header : _tileRow;
    }

    // Helper: re-read the persisted original for flatten in the re-edit tests.
    private static BitmapSource persistenceOriginal(AppPaths paths, CaptureQueue queue, CaptureRecord record) =>
        MyCapture.Platform.Imaging.ImageCodec.TryLoad(
            queue.GetFilePath(record, CaptureFileNames.Original))!;

    private static BitmapSource ImageCodecThumb(BitmapSource source, int longEdge) =>
        MyCapture.Platform.Imaging.ImageCodec.CreateThumbnail(source, longEdge);
}
