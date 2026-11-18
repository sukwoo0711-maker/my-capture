using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.Core.Export;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;
using static MyCapture.App.Tests.FreeformTransparencyTests;

namespace MyCapture.App.Tests;

/// <summary>Uses owned, isolated library/export roots, never the user's profile or clipboard.</summary>
public sealed class FreeformTransparencyStorageTests
{
    [Fact]
    public void LibraryOriginalAndRenderedPngKeepAlphaAndThumbnailUsesWhite() => WithLibrary((root, queue, persistence, settings, paths) =>
    {
        BitmapSource image = Image(320, 192);
        CaptureRecord record = persistence.PersistOriginal(image, 1, "synthetic", "synthetic");
        foreach (string file in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered })
            Assert.Equal(Pixels(image), Pixels(Load(queue.GetFilePath(record, file))));
        BitmapSource thumbnail = Load(queue.GetFilePath(record, CaptureFileNames.Thumbnail));
        Assert.All(Pixel(thumbnail, 16, thumbnail.PixelHeight - 20), channel => Assert.InRange(channel, (byte)245, (byte)255));
        Assert.Single(queue.Records);
        Assert.Equal((320, 192), (record.Width, record.Height));
    });

    [Fact]
    public void CardDragOutAndQuickSaveProducePngWithTheSameAlpha() => WithLibrary((root, queue, persistence, settings, paths) =>
    {
        BitmapSource image = Image();
        CaptureRecord record = persistence.PersistOriginal(image, 1, "synthetic", "synthetic");
        string staging = Path.Combine(root, "drag");
        Directory.CreateDirectory(staging);
        var exports = new GalleryDragExportService(queue, staging);
        string libraryDrag = exports.PrepareExport(record);
        Assert.Equal(Pixels(image), Pixels(Load(libraryDrag)));
        string savePath = Path.Combine(root, "saved.png");
        BitmapSource? copied = null, pinned = null;
        var actions = new QuickAccessImageActions(() => Task.FromResult(image),
            value => { copied = value; return Task.FromResult(true); }, value => pinned = value,
            value => { ImageCodec.SavePngExport(value, savePath); return Task.FromResult(true); }, exportService: exports);
        Assert.True(Wait(actions.CopyAsync()));
        Wait(actions.PinAsync());
        Assert.True(Wait(actions.SaveAsync()));
        string cardDrag = Wait(actions.ExportAsync());
        Assert.Same(image, copied);
        Assert.Same(image, pinned);
        Assert.Equal(Pixels(image), Pixels(Load(savePath)));
        Assert.Equal(Pixels(image), Pixels(Load(cardDrag)));
        IDataObject drop = GalleryDragExportService.CreateFileDropData(cardDrag);
        Assert.Equal(new[] { cardDrag }, Assert.IsType<string[]>(drop.GetData(DataFormats.FileDrop)));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditorCommitSavesAnnotatedAlphaAndCopiesTheSamePng(bool quickSave) => WithLibrary((root, queue, persistence, settings, paths) =>
    {
        BitmapSource image = Image();
        CaptureRecord record = persistence.PersistOriginal(image, 1, "synthetic", "synthetic");
        BitmapSource reopened = Load(queue.GetFilePath(record, CaptureFileNames.Original));
        Assert.Equal(Pixels(image), Pixels(reopened));
        settings.Export.ShowSourceWindowTitle = false;
        settings.Export.QuickSaveDirectoryOverride = Path.Combine(root, "quick-save");
        byte[]? copied = null;
        var commits = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance,
            (bitmap, url) => ClipboardImageService.CopyImageAsync(bitmap, url, data =>
            {
                using var bytes = new MemoryStream();
                Assert.IsAssignableFrom<Stream>(data.GetData("PNG", false)).CopyTo(bytes);
                copied = bytes.ToArray();
            }));
        string? exportPath = null;
        commits.QuickSaved += path => exportPath = path;
        var region = new RectD(0, 0, 64, 48);
        var result = new AnnotationEditingResult(new FrozenFrame(reopened, region, null, 0), region, reopened,
            Document(), quickSave ? EditorCommitAction.QuickSave : EditorCommitAction.Done,
            new Dictionary<string, BitmapSource>(), new Dictionary<string, string>(), showSourceWindowTitle: false);
        Assert.True(Wait(commits.CommitAsync(record, result)));
        Assert.True(result.SaveCompleted);
        Assert.False(result.ClipboardCopyFailed);
        BitmapSource rendered = Load(queue.GetFilePath(record, CaptureFileNames.Rendered));
        Assert.Equal(0, Pixel(rendered, 2, 45)[3]);
        Assert.Equal(new byte[] { 0, 220, 0, 255 }, Pixel(rendered, 30, 12));
        Assert.Equal(Pixels(rendered), Pixels(Decode(Assert.IsType<byte[]>(copied))));
        Assert.Equal(Pixels(image), Pixels(Load(queue.GetFilePath(record, CaptureFileNames.Original))));
        if (quickSave) Assert.Equal(Pixels(rendered), Pixels(Load(Assert.IsType<string>(exportPath))));
        else Assert.Null(exportPath);
    });

    [Fact]
    public void PdfCellReadsTheStoredPngAndEmitsAnOpaqueWhiteJpeg() => WithLibrary((root, queue, persistence, settings, paths) =>
    {
        CaptureRecord record = persistence.PersistOriginal(Image(), 1, "synthetic", "synthetic");
        var item = new GalleryContactSheetItem(queue.GetFilePath(record, CaptureFileNames.Rendered), "", DateTimeOffset.UnixEpoch, null, "");
        ContactSheetJpeg jpeg = GalleryContactSheetRenderer.EncodeCell(item, new(Caption: ContactSheetCaption.None), new ContactSheetRect(0, 0, 72, 56),
            CultureInfo.InvariantCulture, CancellationToken.None);
        BitmapSource decoded = Decode(jpeg.Bytes);
        Assert.Equal(jpeg.Width, decoded.PixelWidth);
        Assert.Equal(jpeg.Height, decoded.PixelHeight);
        Assert.All(Pixel(decoded, 16, decoded.PixelHeight - 18), channel => Assert.InRange(channel, (byte)245, (byte)255));
    });

    private static BitmapSource Load(string path) => Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryLoad(path));

    private static void WithLibrary(Action<string, CaptureQueue, CapturePersistenceService, AppSettings, AppPaths> action) => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("freeform-library-");
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            action(root, queue, persistence, settings, paths);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            OwnedTestDirectory.Delete(root);
        }
    });

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Wait(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Assert.True(task.IsCompleted, "The isolated capture operation did not complete.");
        task.GetAwaiter().GetResult();
    }
}
