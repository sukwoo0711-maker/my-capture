using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureImportTests
{
    [Theory]
    [InlineData("photo.PNG", true)]
    [InlineData("photo.JpEg", true)]
    [InlineData("movie.MP4", true)]
    [InlineData("photo.jpg.exe", false)]
    [InlineData("photo.gif", false)]
    [InlineData("folder", false)]
    public void SupportedTypesAreExplicit(string path, bool expected) =>
        Assert.Equal(expected, CaptureImportService.IsSupported(path));

    [Fact]
    public async Task ImportCopiesOriginalAndPersistsMetadataAndHash()
    {
        var fixture = new Fixture();
        string path = fixture.Source("external.png", [1, 2, 3]);
        DateTimeOffset start = DateTimeOffset.Now;
        CaptureImportResult result = await fixture.Service.ImportAsync([path]);
        Assert.Equal(new CaptureImportResult(1, 0, 0, 0, false), result);
        CaptureRecord record = Assert.Single(fixture.Queue.Records);
        Assert.Equal("external", record.Title);
        Assert.Equal("external.png", record.ImportedFileName);
        Assert.Null(record.SourceDocumentPath);
        Assert.InRange(record.CreatedAt, start, DateTimeOffset.Now);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), record.OriginalContentHash);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(fixture.Queue.GetFilePath(record, CaptureFileNames.Original)));
        Assert.False(record.HasCurrentOcrIndex);
        Assert.True(record.TotalBytes > 0);
        CaptureRecord loaded = Assert.Single(fixture.Reload().Records);
        Assert.Equal(record.OriginalContentHash, loaded.OriginalContentHash);
        Assert.Equal(record.ImportedFileName, loaded.ImportedFileName);
        Assert.Equal(record.Width, loaded.Width);
    }

    [Fact]
    public async Task HashDedupesRenamedFilesAndAcrossBatches()
    {
        var fixture = new Fixture();
        string first = fixture.Source("first.jpg", [5, 6, 7]);
        string renamed = fixture.Source("renamed.jpeg", [5, 6, 7]);
        string different = fixture.Source("different.jpg", [5, 6, 8]);
        CaptureImportResult result = await fixture.Service.ImportAsync([first, renamed, different]);
        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(1, (await fixture.Service.ImportAsync([renamed])).Duplicates);
        Assert.Equal(2, fixture.Queue.Count);
        Assert.All(fixture.Queue.Records, record => Assert.True(File.Exists(
            fixture.Queue.GetFilePath(record, CaptureFileNames.ImportedJpeg))));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyIndexMigratesAndHashesOriginalOnFirstMatchingImport(int schema)
    {
        var fixture = new Fixture();
        string source = fixture.Source("legacy.png", [8, 9]);
        await fixture.Service.ImportAsync([source]);
        JsonNode index = JsonNode.Parse(File.ReadAllText(fixture.Paths.IndexFile))!;
        index["schemaVersion"] = schema;
        var stored = index["records"]![0]!.AsObject();
        stored.Remove("originalContentHash");
        stored.Remove("importedFileName");
        File.WriteAllText(fixture.Paths.IndexFile, index.ToJsonString());
        CaptureQueue queue = fixture.Reload();
        Assert.Null(Assert.Single(queue.Records).OriginalContentHash);
        var service = new CaptureImportService(queue, Fixture.Prepare);
        Assert.Equal(1, (await service.ImportAsync([source])).Duplicates);
        Assert.NotNull(Assert.Single(queue.Records).OriginalContentHash);
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(fixture.Paths.IndexFile))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExplicitFolderWinsAndLibraryRootDoesNotUseCaptureDefault()
    {
        var fixture = new Fixture();
        CaptureFolder folder = await fixture.Queue.CreateFolderAsync("Imports");
        await fixture.Queue.SetNewCaptureFolderAsync(folder.Id, true);
        Assert.Equal(1, (await fixture.Service.ImportAsync([fixture.Source("folder.png", [1])], folder.Id)).Imported);
        CaptureRecord filed = Assert.Single(fixture.Queue.Records);
        Assert.Equal(folder.Id, filed.FolderId);
        Assert.True(filed.IsPinned);
        Assert.True(filed.FolderSequence > 0);
        await fixture.Service.ImportAsync([fixture.Source("root.png", [2])]);
        Assert.Null(fixture.Queue.Records[0].FolderId);
        Assert.Equal(folder.Id, fixture.Reload().Find(filed.Id)!.FolderId);
    }

    [Fact]
    public async Task UnsupportedAndMissingFilesFailWithoutStoppingBatch()
    {
        var fixture = new Fixture();
        CaptureImportResult result = await fixture.Service.ImportAsync([
            fixture.Source("unsupported.gif", [1]), Path.Combine(fixture.Root, "missing.png"),
            fixture.Source("valid.png", [2])]);
        Assert.Equal(2, result.Failed);
        Assert.Equal(1, result.Imported);
    }

    [Fact]
    public async Task CancellationBeforeWorkDoesNotPublish()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CaptureImportResult result = await fixture.Service.ImportAsync([fixture.Source("cancel.png", [1])],
            cancellationToken: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(fixture.Queue.Records);
    }

    [Fact]
    public async Task CancellationDuringPreparationKeepsCompletedFilesAndSources()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        int prepared = 0;
        var service = new CaptureImportService(fixture.Queue, async (record, directory, token) =>
        {
            await Fixture.Prepare(record, directory, token);
            if (++prepared == 2) cancellation.Cancel();
        });
        string first = fixture.Source("first.png", [1]);
        string second = fixture.Source("second.png", [2]);
        CaptureImportResult result = await service.ImportAsync([first, second], cancellationToken: cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(1, result.Imported);
        Assert.Single(fixture.Queue.Records);
        Assert.Single(fixture.Reload().Records);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task MissingTargetFolderFailsWithoutPublishingElsewhere()
    {
        var fixture = new Fixture();
        CaptureImportResult result = await fixture.Service.ImportAsync([fixture.Source("image.png", [1])], Guid.NewGuid());
        Assert.Equal(1, result.Failed);
        Assert.Empty(fixture.Queue.Records);
    }

    [Fact]
    public async Task InvalidDimensionsAreRejectedBeforeIndexPublication()
    {
        var fixture = new Fixture();
        var service = new CaptureImportService(fixture.Queue, (record, _, _) =>
        {
            record.Width = 70_000;
            record.Height = 1;
            return Task.CompletedTask;
        });
        Assert.Equal(1, (await service.ImportAsync([fixture.Source("skinny.png", [1])])).Failed);
        Assert.Empty(fixture.Queue.Records);
        Assert.Empty(fixture.Reload().Records);
    }

    [Fact]
    public async Task FailedPublicationRollsBackIndexAndRequestsManagedCleanup()
    {
        var fixture = new Fixture();
        string? cleanup = null;
        int writes = 0;
        fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path == fixture.Paths.IndexFile && Interlocked.Increment(ref writes) == 1)
                throw new IOException("injected publication failure");
        };
        var service = new CaptureImportService(fixture.Queue, Fixture.Prepare,
            directory => { cleanup = directory; return Task.CompletedTask; });
        Assert.Equal(1, (await service.ImportAsync([fixture.Source("failure.png", [1])])).Failed);
        Assert.NotNull(cleanup);
        Assert.Empty(fixture.Queue.Records);
        Assert.Empty(fixture.Reload().Records);
    }

    [Fact]
    public async Task FailedRollbackPreservesPayloadForRecovery()
    {
        var fixture = new Fixture();
        bool cleanupRequested = false;
        fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (path == fixture.Paths.IndexFile) throw new IOException("injected disk failure");
        };
        var service = new CaptureImportService(fixture.Queue, Fixture.Prepare,
            _ => { cleanupRequested = true; return Task.CompletedTask; });
        Assert.Equal(1, (await service.ImportAsync([fixture.Source("failure.png", [1])])).Failed);
        Assert.False(cleanupRequested);
        Assert.Single(Directory.EnumerateFiles(fixture.Paths.CapturesRoot, CaptureFileNames.Original, SearchOption.AllDirectories));
    }

    // Keep owned fixtures in this worktree. No permanent fixture cleanup, user profile or clipboard.
    private sealed class Fixture
    {
        public Fixture()
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
            Root = Path.Combine(root!.FullName, "artifacts", "import-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Paths = AppPaths.CreateForRoot(Path.Combine(Root, "library"));
            Queue = Reload();
            Service = new CaptureImportService(Queue, Prepare);
        }
        public string Root { get; }
        public AppPaths Paths { get; }
        public CaptureQueue Queue { get; }
        public CaptureImportService Service { get; }
        public CaptureQueue Reload()
        {
            var queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            queue.Load();
            return queue;
        }
        public string Source(string name, byte[] bytes)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public static Task Prepare(CaptureRecord record, string directory, CancellationToken token)
        {
            record.Width = 40;
            record.Height = 30;
            if (CaptureImportService.OriginalFileName(record) == CaptureFileNames.ImportedJpeg)
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), [0]);
            return Task.CompletedTask;
        }
    }
}
