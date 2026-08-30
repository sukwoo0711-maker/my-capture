using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The two-phase capture persistence and the annotation flattener.
/// </summary>
/// <remarks>
/// WPF imaging runs on a dedicated STA thread so these behave the same as under the UI
/// thread regardless of the runner's apartment state.
/// </remarks>
public sealed class CapturePersistenceTests
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
        string root = Path.Combine(Path.GetTempPath(), "mycapture-app-tests", Guid.NewGuid().ToString("N"));
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

    private static BitmapSource SolidBitmap(int width, int height, byte r, byte g, byte b)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static CaptureQueue NewQueue(AppPaths paths, QueueSettings? limits = null) =>
        new(paths, limits ?? new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    private static CapturePersistenceService NewPersistence(CaptureQueue queue, AppPaths paths, QueueSettings settings) =>
        new(queue, paths, () => settings, NullLogger<CapturePersistenceService>.Instance);

    [Fact]
    public void PersistOriginal_WritesEveryFileAndIndexesTheRecord() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var settings = new QueueSettings();
            CaptureQueue queue = NewQueue(paths, settings);
            CapturePersistenceService persistence = NewPersistence(queue, paths, settings);

            BitmapSource original = SolidBitmap(40, 30, 0x20, 0x40, 0x80);
            CaptureRecord record = persistence.PersistOriginal(original, 1.5, "메모장", "\\\\.\\DISPLAY1");

            string dir = queue.GetDirectory(record);
            Assert.True(File.Exists(Path.Combine(dir, CaptureFileNames.Original)), "original.png missing");
            Assert.True(File.Exists(Path.Combine(dir, CaptureFileNames.Rendered)), "rendered.png missing");
            Assert.True(File.Exists(Path.Combine(dir, CaptureFileNames.Layers)), "layers.json missing");
            Assert.True(File.Exists(Path.Combine(dir, CaptureFileNames.Thumbnail)), "thumb.jpg missing");
            Assert.True(File.Exists(Path.Combine(dir, CaptureFileNames.Meta)), "meta.json missing");
            Assert.True(File.Exists(paths.IndexFile), "index.json missing");

            Assert.Same(record, queue.Records[0]);
            Assert.Equal(40, record.Width);
            Assert.Equal(30, record.Height);
            Assert.Equal(1.5, record.DpiScale);
            Assert.False(record.HasAnnotations);
            Assert.True(record.TotalBytes > 0);

            // The index survives a reload.
            CaptureQueue reloaded = NewQueue(paths, settings);
            reloaded.Load();
            Assert.Single(reloaded.Records);
            Assert.Equal(record.Id, reloaded.Records[0].Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void Finalize_FlattensRewritesLayersAndUpdatesByteCount() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var settings = new QueueSettings();
            CaptureQueue queue = NewQueue(paths, settings);
            CapturePersistenceService persistence = NewPersistence(queue, paths, settings);

            BitmapSource original = SolidBitmap(64, 48, 0xFF, 0xFF, 0xFF);
            CaptureRecord record = persistence.PersistOriginal(original, 1.0, string.Empty, string.Empty);
            long originalBytes = record.TotalBytes;

            var document = AnnotationDocument.CreateFor(64, 48);
            document.Add(new RectangleAnnotation
            {
                Rect = new RectD(4, 4, 40, 30),
                Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44),
                StrokeThickness = 3,
            });

            var store = AnnotationImageStore.FromDecoded(new Dictionary<string, BitmapSource>());
            var renderer = new AnnotationRenderer(store);
            BitmapSource flattened = AnnotationFlattener.Flatten(original, document, renderer);

            Assert.Equal(64, flattened.PixelWidth);
            Assert.Equal(48, flattened.PixelHeight);

            persistence.Finalize(record, flattened, document, new Dictionary<string, BitmapSource>());

            string dir = queue.GetDirectory(record);
            string layersJson = File.ReadAllText(Path.Combine(dir, CaptureFileNames.Layers));
            AnnotationDocument? reloadedDoc = AnnotationDocument.TryFromJson(layersJson);

            Assert.NotNull(reloadedDoc);
            Assert.Single(reloadedDoc!.Items);
            Assert.True(record.HasAnnotations);

            // Byte total was updated to reflect the finalised files, not left at the original.
            Assert.Equal(record.TotalBytes, queue.TotalBytes);
            Assert.NotEqual(originalBytes, record.TotalBytes);
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void Finalize_CanonicalisesImageSidecarAndSurvivesSourceDeletion() => RunSta(() =>
    {
        string root = NewRoot();
        string sourcePath = Path.Combine(Path.GetTempPath(), $"mycapture-src-{Guid.NewGuid():N}.png");
        try
        {
            // Write a real PNG source, decode it into a store as if inserted, then delete it.
            BitmapSource asset = SolidBitmap(20, 20, 0x10, 0x90, 0x30);
            File.WriteAllBytes(sourcePath, MyCapture.Platform.Imaging.ImageCodec.EncodePng(asset));

            var store = new AnnotationImageStore();
            (BitmapSource Bitmap, string AssetFileName)? loaded = store.LoadFromFile(sourcePath);
            Assert.NotNull(loaded);
            string sessionAsset = loaded!.Value.AssetFileName;

            // The source file is gone before we persist — the in-memory bitmap must carry it.
            File.Delete(sourcePath);
            Assert.False(File.Exists(sourcePath));

            AppPaths paths = AppPaths.CreateForRoot(root);
            var settings = new QueueSettings();
            CaptureQueue queue = NewQueue(paths, settings);
            CapturePersistenceService persistence = NewPersistence(queue, paths, settings);

            BitmapSource original = SolidBitmap(64, 48, 0xFF, 0xFF, 0xFF);
            CaptureRecord record = persistence.PersistOriginal(original, 1.0, string.Empty, string.Empty);

            var document = AnnotationDocument.CreateFor(64, 48);
            document.Add(new ImageAnnotation
            {
                AssetFileName = sessionAsset,
                SourceWidth = 20,
                SourceHeight = 20,
                Rect = new RectD(8, 8, 20, 20),
            });

            IReadOnlyDictionary<string, BitmapSource> decoded = store.DecodedFor([sessionAsset]);
            var flatRenderer = new AnnotationRenderer(AnnotationImageStore.FromDecoded(decoded));
            BitmapSource flattened = AnnotationFlattener.Flatten(original, document, flatRenderer);

            persistence.Finalize(record, flattened, document, decoded);

            string dir = queue.GetDirectory(record);
            string canonical = Path.Combine(dir, $"{CaptureFileNames.AssetPrefix}01.png");

            // The sidecar was canonicalised to asset-01.png and the document now points at it.
            Assert.True(File.Exists(canonical), "asset-01.png sidecar missing");
            var image = Assert.IsType<ImageAnnotation>(document.Items[0]);
            Assert.Equal($"{CaptureFileNames.AssetPrefix}01.png", image.AssetFileName);
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }

            DeleteRoot(root);
        }
    });

    [Fact]
    public void Flatten_ProducesBitmapMatchingBaseResolution() => RunSta(() =>
    {
        BitmapSource original = SolidBitmap(123, 77, 0x30, 0x30, 0x30);
        var document = AnnotationDocument.CreateFor(123, 77);
        var renderer = new AnnotationRenderer(AnnotationImageStore.FromDecoded(new Dictionary<string, BitmapSource>()));

        BitmapSource flattened = AnnotationFlattener.Flatten(original, document, renderer);

        Assert.Equal(123, flattened.PixelWidth);
        Assert.Equal(77, flattened.PixelHeight);
        Assert.True(flattened.IsFrozen);
    });
}

/// <summary>
/// The four editor commit actions, exercised through <see cref="CaptureCommitService"/>
/// with the Save As dialog stubbed out.
/// </summary>
public sealed class CaptureCommitServiceTests
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
        string root = Path.Combine(Path.GetTempPath(), "mycapture-commit-tests", Guid.NewGuid().ToString("N"));
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

    private static BitmapSource Solid(int w, int h)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        byte[] px = new byte[w * h * 4];
        Array.Fill(px, (byte)0xC0);
        bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    private static AnnotationEditingResult MakeResult(EditorCommitAction action)
    {
        BitmapSource selected = Solid(48, 32);
        var frame = new MyCapture.Platform.Capture.FrozenFrame(selected, new RectD(0, 0, 48, 32), null, 0);
        var document = AnnotationDocument.CreateFor(48, 32);
        return new AnnotationEditingResult(
            frame,
            new RectD(0, 0, 48, 32),
            selected,
            document,
            action,
            new Dictionary<string, BitmapSource>(),
            new Dictionary<string, string>());
    }

    private static (CaptureCommitService Commit, CaptureQueue Queue, CaptureRecord Record, AppSettings Settings, AppPaths Paths)
        Build(string root)
    {
        AppPaths paths = AppPaths.CreateForRoot(root);
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var persistence = new CapturePersistenceService(
            queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(
            persistence, () => settings, () => paths, NullLogger<CaptureCommitService>.Instance);

        BitmapSource original = Solid(48, 32);
        CaptureRecord record = persistence.PersistOriginal(original, 1.0, string.Empty, string.Empty);
        return (commit, queue, record, settings, paths);
    }

    [Fact]
    public void Done_PersistsAndCloses_WithoutExport() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            var (commit, _, record, settings, _) = Build(root);
            bool close = commit.Commit(record, MakeResult(EditorCommitAction.Done));

            Assert.True(close);
            // No quick-save file was written for a plain Done.
            Assert.False(Directory.Exists(settings.Export.QuickSaveDirectoryOverride is { Length: > 0 } d ? d : Path.Combine(root, "quicksave")));
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void QuickSave_WritesPngToConfiguredDirectoryAndCloses() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            var (commit, _, record, settings, _) = Build(root);
            string quickSaveDir = Path.Combine(root, "exports");
            settings.Export.QuickSaveDirectoryOverride = quickSaveDir;

            bool close = commit.Commit(record, MakeResult(EditorCommitAction.QuickSave));

            Assert.True(close);
            Assert.True(Directory.Exists(quickSaveDir));
            string[] pngs = Directory.GetFiles(quickSaveDir, "*.png");
            Assert.Single(pngs);
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void SaveAs_CancelledKeepsEditorOpenAndWritesNothing() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            var (commit, _, record, settings, _) = Build(root);
            string quickSaveDir = Path.Combine(root, "exports");
            settings.Export.QuickSaveDirectoryOverride = quickSaveDir;

            // Stub the dialog to "cancel".
            commit.SaveAsPrompt = _ => null;

            bool close = commit.Commit(record, MakeResult(EditorCommitAction.SaveAs));

            Assert.False(close);
            Assert.False(Directory.Exists(quickSaveDir), "Cancelled Save As must not write anything");
        }
        finally
        {
            DeleteRoot(root);
        }
    });

    [Fact]
    public void SaveAs_AcceptedSavesToChosenPathAndCloses() => RunSta(() =>
    {
        string root = NewRoot();
        try
        {
            var (commit, _, record, _, _) = Build(root);
            string chosen = Path.Combine(root, "chosen.png");
            commit.SaveAsPrompt = _ => chosen;

            bool close = commit.Commit(record, MakeResult(EditorCommitAction.SaveAs));

            Assert.True(close);
            Assert.True(File.Exists(chosen));
        }
        finally
        {
            DeleteRoot(root);
        }
    });
}
