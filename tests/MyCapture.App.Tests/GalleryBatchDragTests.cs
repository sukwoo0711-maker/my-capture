using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryBatchDragTests
{
    [Fact]
    public async Task MixedBatch_StagesUniqueNamesWithUnchangedSources_AndHoldsAllLeasesUntilDropEnds()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-drag-");
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var records = Enumerable.Range(0, 4).Select(i => new CaptureRecord
            { Id = Guid.NewGuid(), MediaKind = i < 2 ? CaptureMediaKind.Image : CaptureMediaKind.Video }).ToArray();
            var originals = new Dictionary<string, byte[]>();
            foreach (CaptureRecord record in records)
            {
                queue.Add(record);
                Directory.CreateDirectory(queue.GetDirectory(record));
                string path = queue.GetFilePath(record, record.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Rendered);
                byte[] payload = new byte[200_000];
                Random.Shared.NextBytes(payload);
                File.WriteAllBytes(path, payload);
                originals.Add(path, payload);
            }
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"), () => DateTimeOffset.UnixEpoch);
            using (GalleryDragExportService.PreparedDrag batch = await service.PrepareBatchAsync(records, CancellationToken.None))
            {
                Assert.Equal(4, batch.Paths.Select(Path.GetFileName).Distinct().Count());
                Assert.All(records, record => Assert.False(queue.Remove(record.Id)));
                for (int i = 0; i < records.Length; i++)
                {
                    string source = queue.GetFilePath(records[i], records[i].IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Rendered);
                    Assert.NotEqual(source, batch.Paths[i]);
                    Assert.Equal(originals[source], File.ReadAllBytes(batch.Paths[i]));
                    Assert.Equal(originals[source], File.ReadAllBytes(source));
                }
            }
            Assert.All(records, record => Assert.True(queue.Remove(record.Id)));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public async Task CancelledPreparation_ReleasesLeaseAndDoesNotCreateLateExport()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-cancel-");
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            Directory.CreateDirectory(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var record = new CaptureRecord { Id = Guid.NewGuid() };
            queue.Add(record);
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareBatchAsync([record], new CancellationToken(true)));
            Assert.False(Directory.Exists(service.StagingRoot));
            Assert.True(queue.Remove(record.Id));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public async Task CancellationAfterFirstLargeFile_RemovesCompletedExportsAndReleasesEveryLease()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-partial-");
        try
        {
            var queue = NewQueue(root);
            CaptureRecord[] records = [Add(queue, 2_000_000), Add(queue, 20)];
            using var cancellation = new CancellationTokenSource();
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"))
            { StagedFileForTest = count => { if (count == 1) cancellation.Cancel(); } };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareBatchAsync(records, cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(service.StagingRoot));
            Assert.All(records, record => Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Rendered))));
            Assert.All(records, record => Assert.True(queue.Remove(record.Id)));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public async Task UnpublishedGestureIsRemoved_ButPublishedDropRetainsFilesForTheShell()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-published-");
        try
        {
            var queue = NewQueue(root);
            CaptureRecord record = Add(queue);
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"));
            using (GalleryDragExportService.PreparedDrag unused = await service.PrepareBatchAsync([record], CancellationToken.None))
                Assert.Single(unused.Paths);
            Assert.Empty(Directory.EnumerateFiles(service.StagingRoot));
            using (GalleryDragExportService.PreparedDrag published = await service.PrepareBatchAsync([record], CancellationToken.None))
                published.MarkPublished();
            string retained = Assert.Single(Directory.EnumerateFiles(service.StagingRoot));
            service.CleanupExpiredBestEffort(DateTimeOffset.UtcNow.AddDays(-2));
            Assert.True(File.Exists(retained));
            File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-3));
            service.CleanupExpiredBestEffort(DateTimeOffset.UtcNow.AddDays(-2));
            Assert.False(File.Exists(retained));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [WindowsFileSymlinkFact]
    public async Task FinalSourceSymlinkIsRejected_AndExternalTargetIsPreserved()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-source-link-");
        string targetRoot = OwnedTestDirectory.Create("MyCapture-batch-source-target-");
        string? link = null;
        try
        {
            var queue = NewQueue(root);
            CaptureRecord record = Add(queue);
            link = queue.GetFilePath(record, CaptureFileNames.Rendered);
            string target = Path.Combine(targetRoot, "unrelated.png");
            File.WriteAllText(target, "unrelated file");
            File.Delete(link);
            File.CreateSymbolicLink(link, target);
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"));
            await Assert.ThrowsAsync<IOException>(() => service.PrepareBatchAsync([record], CancellationToken.None));
            Assert.Equal("unrelated file", File.ReadAllText(target));
            Assert.Empty(Directory.EnumerateFiles(service.StagingRoot));
            Assert.True(queue.Remove(record.Id));
        }
        finally
        {
            if (link is not null && File.Exists(link)) File.Delete(link);
            OwnedTestDirectory.Delete(root); OwnedTestDirectory.Delete(targetRoot);
        }
    }

    [Fact]
    public async Task FinalSourceJunctionIsRejectedByHandleAndBatch_WithoutOpeningItsTarget()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-leaf-junction-");
        string targetRoot = OwnedTestDirectory.Create("MyCapture-batch-leaf-target-");
        string? link = null;
        try
        {
            var queue = NewQueue(root);
            CaptureRecord record = Add(queue);
            link = queue.GetFilePath(record, CaptureFileNames.Rendered);
            File.Delete(link);
            string keep = Path.Combine(targetRoot, "keep.txt");
            File.WriteAllText(keep, "unrelated");
            UpdatePathsTests.CreateJunction(link, targetRoot);
            Assert.Throws<IOException>(() => GalleryExportFiles.ValidateSource(link));
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"));
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.PrepareBatchAsync([record], CancellationToken.None));
            Assert.Equal("unrelated", File.ReadAllText(keep));
            Assert.False(Directory.Exists(service.StagingRoot));
        }
        finally
        {
            if (link is not null && Directory.Exists(link)) Directory.Delete(link);
            OwnedTestDirectory.Delete(root); OwnedTestDirectory.Delete(targetRoot);
        }
    }

    [Fact]
    public async Task SourceParentReplacedAfterOwnerAdmission_IsRejectedBeforeCopyingExternalBytes()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-parent-link-");
        string targetRoot = OwnedTestDirectory.Create("MyCapture-batch-parent-target-");
        string? link = null;
        try
        {
            var queue = NewQueue(root);
            CaptureRecord[] records = [Add(queue), Add(queue)];
            link = queue.GetDirectory(records[1]);
            string sourceParent = link;
            string target = Path.Combine(targetRoot, CaptureFileNames.Rendered);
            File.WriteAllText(target, "external bytes");
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"))
            {
                StagedFileForTest = count =>
                {
                    if (count != 1) return;
                    Directory.Move(sourceParent, sourceParent + "-original");
                    UpdatePathsTests.CreateJunction(sourceParent, targetRoot);
                },
            };
            await Assert.ThrowsAsync<IOException>(() => service.PrepareBatchAsync(records, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(service.StagingRoot));
            Assert.Equal("external bytes", File.ReadAllText(target));
        }
        finally
        {
            if (link is not null && Directory.Exists(link)
                && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(link);
            OwnedTestDirectory.Delete(root); OwnedTestDirectory.Delete(targetRoot);
        }
    }

    [Fact]
    public async Task StagingRootJunctionIsRejectedForPreparationAndExpiredCleanup()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-stage-link-");
        string targetRoot = OwnedTestDirectory.Create("MyCapture-batch-stage-target-");
        string link = Path.Combine(root, "staging");
        try
        {
            var queue = NewQueue(root);
            CaptureRecord record = Add(queue);
            string keep = Path.Combine(targetRoot, "MyCapture_unrelated.png");
            File.WriteAllText(keep, "keep"); File.SetLastWriteTimeUtc(keep, DateTime.UtcNow.AddDays(-4));
            UpdatePathsTests.CreateJunction(link, targetRoot);
            var service = new GalleryDragExportService(queue, link);
            service.CleanupExpiredBestEffort(DateTimeOffset.UtcNow.AddDays(-2));
            await Assert.ThrowsAsync<IOException>(() => service.PrepareBatchAsync([record], CancellationToken.None));
            Assert.Equal("keep", File.ReadAllText(keep));
            Assert.Single(Directory.EnumerateFileSystemEntries(targetRoot));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            OwnedTestDirectory.Delete(root); OwnedTestDirectory.Delete(targetRoot);
        }
    }

    [Fact]
    public async Task StagingRootReplacedBeforeUnpublishedDisposal_CannotDeleteTargetFiles()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-dispose-link-");
        string targetRoot = OwnedTestDirectory.Create("MyCapture-batch-dispose-target-");
        string link = Path.Combine(root, "staging");
        try
        {
            var queue = NewQueue(root);
            CaptureRecord record = Add(queue);
            var service = new GalleryDragExportService(queue, link);
            using (GalleryDragExportService.PreparedDrag batch = await service.PrepareBatchAsync([record], CancellationToken.None))
            {
                Directory.Move(link, link + "-original");
                string keep = Path.Combine(targetRoot, Path.GetFileName(Assert.Single(batch.Paths)));
                File.WriteAllText(keep, "unrelated matching name");
                UpdatePathsTests.CreateJunction(link, targetRoot);
            }
            Assert.Equal("unrelated matching name", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(targetRoot))));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            OwnedTestDirectory.Delete(root); OwnedTestDirectory.Delete(targetRoot);
        }
    }

    private static CaptureQueue NewQueue(string root) =>
        new(AppPaths.CreateForRoot(root), new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    [Fact]
    public async Task LongStagingPath_CopiesAndCleansWithoutDependingOnProcessLongPathManifest()
    {
        string root = OwnedTestDirectory.Create("MyCapture-batch-long-path-");
        try
        {
            var queue = NewQueue(root);
            CaptureRecord record = Add(queue);
            string staging = Path.Combine(root, new string('a', 90), new string('b', 90), new string('c', 90));
            var service = new GalleryDragExportService(queue, staging);
            string staged;
            using (GalleryDragExportService.PreparedDrag batch = await service.PrepareBatchAsync([record], CancellationToken.None))
            {
                staged = Assert.Single(batch.Paths);
                Assert.True(staged.Length > 320);
                Assert.Equal(File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.Rendered)), File.ReadAllBytes(staged));
                batch.MarkPublished();
            }
            File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddDays(-3));
            service.CleanupExpiredBestEffort(DateTimeOffset.UtcNow.AddDays(-2));
            Assert.False(File.Exists(staged));
            Assert.True(File.Exists(queue.GetFilePath(record, CaptureFileNames.Rendered)));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private static CaptureRecord Add(CaptureQueue queue, int size = 200)
    {
        var record = new CaptureRecord { Id = Guid.NewGuid() };
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Rendered), new byte[size]);
        return record;
    }
}
