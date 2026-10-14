using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryImportTests
{
    [Fact]
    public async Task ExternalDropAcceptsOnlySupportedFileBatchesAndNeverPrivatePayloads()
    {
        await StaThreadTask.RunAsync(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { @"C:\fixture\a.png", @"C:\fixture\b.MP4" });
            Assert.Equal(2, GalleryWindow.ExternalImportPaths(data).Length);
            data.SetData(GalleryFolderDragPayload.Format,
                new GalleryFolderDragPayload(Guid.NewGuid(), [Guid.NewGuid()], null), false);
            Assert.Empty(GalleryWindow.ExternalImportPaths(data));
            var unsupported = new DataObject();
            unsupported.SetData(DataFormats.FileDrop, new[] { @"C:\fixture\a.png", @"C:\fixture\b.exe" });
            Assert.Empty(GalleryWindow.ExternalImportPaths(unsupported));
            Assert.Empty(GalleryWindow.ExternalImportPaths(new DataObject()));
            return true;
        });
    }

    [Theory]
    [InlineData(CaptureMediaKind.Image)]
    [InlineData(CaptureMediaKind.Video)]
    public void ImportedSourceAppearsInDetailsWithoutAnExternalPath(CaptureMediaKind kind)
    {
        var record = new CaptureRecord { ImportedFileName = "example.mp4", MediaKind = kind };
        var tile = new GalleryItemViewModel(record, _ => string.Empty, 320);
        Assert.True(tile.HasSource);
        Assert.Equal(UiText.Get("Library.Import.Source"), tile.SourceAppName);
        Assert.Equal("example.mp4", tile.SourceWindowTitle);
        Assert.False(tile.HasSourceDocumentPath);
        Assert.Empty(tile.SourceAppPath);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpeg")]
    public async Task ImageImportProducesEditablePngAndThumbnailOffCallerThread(string extension)
    {
        string root = NewRoot();
        string source = Path.Combine(root, "image" + extension);
        await StaThreadTask.RunAsync(() =>
        {
            var pixels = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, new byte[640 * 360 * 4], 640 * 4);
            if (extension == ".png") ImageCodec.SavePng(pixels, source);
            else ImageCodec.SaveJpeg(pixels, source, 90);
            return true;
        });
        byte[] original = File.ReadAllBytes(source);
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(root, "library"));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync);
        CaptureImportResult result = await service.ImportAsync([source]);
        Assert.Equal(1, result.Imported);
        CaptureRecord record = Assert.Single(queue.Records);
        Assert.Equal((640, 360), (record.Width, record.Height));
        Assert.Equal(original, File.ReadAllBytes(queue.GetFilePath(record, CaptureImportService.OriginalFileName(record))));
        Assert.Equal(original, File.ReadAllBytes(source));
        await StaThreadTask.RunAsync(() =>
        {
            using var imageStream = File.OpenRead(queue.GetFilePath(record, CaptureFileNames.Original));
            var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.IsType<PngBitmapDecoder>(decoder);
            using var thumbStream = File.OpenRead(queue.GetFilePath(record, CaptureFileNames.Thumbnail));
            var thumbnail = BitmapDecoder.Create(thumbStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(320, thumbnail.Frames[0].PixelWidth);
            return true;
        });
    }

    [Fact]
    public async Task CorruptImageDoesNotPublishAndRequestsOnlyManagedCopyCleanup()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "broken.png");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var queue = new CaptureQueue(AppPaths.CreateForRoot(Path.Combine(root, "library")),
            new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        string? cleanup = null;
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync,
            directory => { cleanup = directory; return Task.CompletedTask; });
        CaptureImportResult result = await service.ImportAsync([source]);
        Assert.Equal(1, result.Failed);
        Assert.Empty(queue.Records);
        Assert.True(File.Exists(source));
        Assert.NotNull(cleanup);
        Assert.StartsWith(Path.Combine(root, "library"), cleanup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FourKVideoImportCopiesBytesProbesMediaAndCreatesThumbnail()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "clip.mp4");
        await Task.Run(() => FrameIndexClip.Encode(source, 3840, 2160, 10, FrameIndexClip.Constant(10, 5)));
        var paths = AppPaths.CreateForRoot(Path.Combine(root, "library"));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var service = new CaptureImportService(queue, GalleryImportService.PrepareAsync);
        CaptureImportResult result = await service.ImportAsync([source, source]);
        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Duplicates);
        CaptureRecord record = Assert.Single(queue.Records);
        Assert.True(record.IsVideo);
        Assert.Equal((3840, 2160), (record.Width, record.Height));
        Assert.InRange(record.DurationMs, 400, 700);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.VideoSource)));
        Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Thumbnail)));
        var reloaded = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        Assert.True(Assert.Single(reloaded.Records).IsVideo);
    }

    [Fact]
    public async Task ImportedRecordsCombineWithTagAndFieldSearchAndRetainDeduplicationAfterReload()
    {
        string root = NewRoot();
        string source = Path.Combine(root, "report.png");
        File.WriteAllBytes(source, [1, 2, 3]);
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(root, "library"));
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var model = new GalleryViewModel(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            _ => string.Empty, 320);
        model.SetThumbnailLoadingEnabled(false);
        model.ShowTag("work");
        model.SearchQuery = "title:report -tag:draft";
        // Exercise publication and filtering without depending on media decoding in this integration test.
        var service = new CaptureImportService(queue, (record, _, _) =>
        {
            record.Width = 40;
            record.Height = 30;
            return Task.CompletedTask;
        });
        Assert.Equal(1, (await service.ImportAsync([source])).Imported);
        CaptureRecord imported = Assert.Single(queue.Records);
        model.Refresh();
        Assert.True(model.IsEmpty);
        Assert.Equal(0, model.SearchHiddenCount);
        Assert.Equal("work", model.SelectedTag);

        await queue.UpdateTagsAsync([imported.Id], [new("work", true), new("draft", true)]);
        model.Refresh();
        Assert.True(model.IsEmpty);
        Assert.Equal(1, model.SearchHiddenCount);
        await queue.UpdateTagsAsync([imported.Id], [new("draft", false)]);
        model.Refresh();
        GalleryItemViewModel tile = Assert.Single(model.Groups.SelectMany(group => group.Items));
        Assert.Equal(imported.Id, tile.Id);
        Assert.Equal(GallerySearchMatchText.Describe(CaptureMatchField.Title), tile.SearchMatchText);
        Assert.Equal("report.png", tile.SourceWindowTitle);
        Assert.Equal(UiText.Get("Library.Import.Source"), tile.SourceAppName);
        Assert.Equal(0, model.SearchHiddenCount);
        Assert.Equal(2, model.SearchConditions.Count);
        Assert.Equal(1, Assert.Single(model.VisibleTags).Count);

        var reloaded = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        CaptureRecord restored = Assert.Single(reloaded.Records);
        Assert.Equal("work", restored.Tags);
        Assert.Equal(imported.ImportedFileName, restored.ImportedFileName);
        Assert.Equal(imported.OriginalContentHash, restored.OriginalContentHash);
        Assert.True(CaptureTextSearch.IsMatch(restored, "title:report tag:work -tag:draft"));
        var reimport = new CaptureImportService(reloaded,
            (_, _, _) => throw new InvalidOperationException("Duplicates must skip media preparation."));
        Assert.Equal(1, (await reimport.ImportAsync([source])).Duplicates);
        Assert.Single(reloaded.Records);
    }

    private static string NewRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
        string result = Path.Combine(root!.FullName, "artifacts", "import-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(result);
        return result;
    }
}
