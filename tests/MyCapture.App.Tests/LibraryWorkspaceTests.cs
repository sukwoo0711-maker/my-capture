using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mycapture-workspace-tests", Guid.NewGuid().ToString("N"));
    public LibraryWorkspaceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData(null, "")]
    [InlineData(" #Client, client; 개발，review", "Client, 개발, review")]
    [InlineData(" , #, ; ", "")]
    public void Tags_NormalizeAndDeduplicate(string? input, string expected) =>
        Assert.Equal(expected, CaptureOrganization.NormalizeTags(input));

    [Fact]
    public void Organization_RejectsOversizedOrControlInput()
    {
        Assert.Throws<ArgumentException>(() => CaptureOrganization.NormalizeTitle(new string('x', 141)));
        Assert.Throws<ArgumentException>(() => CaptureOrganization.NormalizeTitle("bad\0label"));
        Assert.Throws<ArgumentException>(() => CaptureOrganization.NormalizeTags(new string('x', 33)));
        Assert.Throws<ArgumentException>(() => CaptureOrganization.NormalizeTags(string.Join(',', Enumerable.Range(0, 13))));
        Assert.Equal("hello world", CaptureOrganization.NormalizeTitle(" hello\nworld "));
    }

    [Fact]
    public async Task Organization_PersistsAcrossRestartAndOcrWithoutChangingPixelsRevision()
    {
        AppPaths paths = AppPaths.CreateForRoot(_root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var record = new CaptureRecord { Title = "before", Width = 1, Height = 1, ContentRevision = 7,
            OcrText = "unchanged OCR", OcrContentRevision = 7 };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        Directory.CreateDirectory(queue.GetDirectory(record));
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Original), [1, 2, 3]);
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Rendered), [4, 5, 6]);
        queue.Add(record);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        Assert.True(await controller.UpdateOrganizationAsync(record.Id, "  계약 검토  ", "#Client, client; alpha"));
        Assert.Equal(7, record.ContentRevision);
        Assert.Equal("unchanged OCR", record.OcrText);
        Assert.Single(controller.BuildGroups("계약", DateTimeOffset.Now));
        Assert.True(await controller.CacheOcrAsync(record.Id, "new OCR", "en", 7));
        var reloaded = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        CaptureRecord saved = Assert.Single(reloaded.Records);
        Assert.Equal("계약 검토", saved.Title);
        Assert.Equal("Client, alpha", saved.Tags);
        Assert.Equal(7, saved.ContentRevision);
        Assert.Equal("new OCR", saved.OcrText);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.Rendered)));
    }

    [Fact]
    public async Task Organization_MissingAndCancelledDoNotCreateRecords()
    {
        var queue = new CaptureQueue(AppPaths.CreateForRoot(_root), new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        Assert.False(await controller.UpdateOrganizationAsync(Guid.NewGuid(), "name", "tag"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.UpdateOrganizationAsync(Guid.NewGuid(), "a", "b", cancellation.Token));
        Assert.Empty(queue.Records);
    }

    [Fact]
    public void MetadataNotificationsRefreshCaptionAndTagsWithoutLoadingThumbnail()
    {
        var record = new CaptureRecord();
        var tile = new GalleryItemViewModel(record, _ => throw new InvalidOperationException("Must not decode"), 160);
        var changed = new List<string?>();
        tile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        record.Title = "renamed"; record.Tags = "work"; tile.RaiseMetaChanged();
        Assert.Equal("renamed", tile.Caption); Assert.Equal("work", tile.TagsCaption);
        Assert.Contains(nameof(tile.Caption), changed); Assert.Contains(nameof(tile.TagsCaption), changed);
    }

    [Fact]
    public async Task Archive_ContainsOnlyMediaWithGenericUniqueNames()
    {
        string image = Source("private-title.png", [1, 2, 3]);
        string video = Source("private-title.mp4", [4, 5, 6]);
        string destination = Path.Combine(_root, "share.zip");
        await GalleryArchiveExporter.ExportNewAsync([image, video], destination);
        using ZipArchive zip = ZipFile.OpenRead(destination);
        Assert.Equal(new[] { "MyCapture-001.png", "MyCapture-002.mp4" }, zip.Entries.Select(e => e.FullName));
        using var content = new MemoryStream(); await zip.Entries[0].Open().CopyToAsync(content);
        Assert.Equal(new byte[] { 1, 2, 3 }, content.ToArray());
        Assert.True(File.Exists(image)); Assert.True(File.Exists(video));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Fact]
    public async Task Archive_RejectsMetadataAndEmptySelection()
    {
        string file = Source("index.json", [1]);
        await Assert.ThrowsAsync<ArgumentException>(() => GalleryArchiveExporter.ExportNewAsync([file], Path.Combine(_root, "no.zip")));
        await Assert.ThrowsAsync<ArgumentException>(() => GalleryArchiveExporter.ExportNewAsync([], Path.Combine(_root, "empty.zip")));
        Assert.Empty(Directory.GetFiles(_root, "*.zip"));
    }

    [Fact]
    public async Task Archive_NeverOverwritesExistingDestination()
    {
        string source = Source("capture.png", [1]);
        string destination = Source("existing.zip", [9, 8, 7]);
        await Assert.ThrowsAsync<IOException>(() => GalleryArchiveExporter.ExportNewAsync([source], destination));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Fact]
    public async Task Archive_PublicationRacePreservesOtherWritersFile()
    {
        string source = Source("capture.png", [1]);
        string destination = Path.Combine(_root, "raced.zip");
        await Assert.ThrowsAsync<IOException>(() => GalleryArchiveExporter.ExportNewAsync([source], destination,
            new InlineProgress(_ => File.WriteAllBytes(destination, [9]))));
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(destination));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Fact]
    public async Task Archive_CancellationAfterFirstItemPublishesNothing()
    {
        string first = Source("first.png", [1]); string second = Source("second.mp4", [2]);
        using var cancellation = new CancellationTokenSource();
        string destination = Path.Combine(_root, "cancelled.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GalleryArchiveExporter.ExportNewAsync([first, second], destination,
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
        Assert.False(File.Exists(destination)); Assert.True(File.Exists(first));
        Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Fact]
    public async Task Archive_MissingMediaLeavesNoPartialOutput()
    {
        string destination = Path.Combine(_root, "missing.zip");
        await Assert.ThrowsAsync<FileNotFoundException>(() => GalleryArchiveExporter.ExportNewAsync([Path.Combine(_root, "missing.png")], destination));
        Assert.False(File.Exists(destination)); Assert.Empty(Directory.GetFiles(_root, "*.partial"));
    }

    [Fact]
    public async Task Tags_AreSearchableTogetherWithTitle()
    {
        var queue = new CaptureQueue(AppPaths.CreateForRoot(_root), new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var record = new CaptureRecord { Title = "계약 검토", Width = 1, Height = 1 };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        Directory.CreateDirectory(queue.GetDirectory(record)); queue.Add(record);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        Assert.True(await controller.UpdateOrganizationAsync(record.Id, "계약 검토", "Client, alpha"));
        // Retain the failed release requirement as an independent test, not a skipped assertion.
        Assert.Single(controller.BuildGroups("계약 alpha", DateTimeOffset.Now));
    }
    private string Source(string name, byte[] bytes)
    {
        string path = Path.Combine(_root, name); File.WriteAllBytes(path, bytes); return path;
    }
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    { public void Report(int value) => report(value); }
}