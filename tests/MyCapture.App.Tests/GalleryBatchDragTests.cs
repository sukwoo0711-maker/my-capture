using System.ComponentModel;
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
            string staging = Path.Combine(root, "staging");
            var service = new GalleryDragExportService(queue, staging);
            Assert.Equal(staging, service.StagingRoot);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareBatchAsync([record], new CancellationToken(true)));
            Assert.False(Directory.Exists(staging));
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
            string staging = Path.Combine(root, "staging");
            var service = new GalleryDragExportService(queue, staging)
            { StagedFileForTest = count => { if (count == 1) cancellation.Cancel(); } };
            Assert.Equal(staging, service.StagingRoot);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareBatchAsync(records, cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(staging));
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
            string staging = Path.Combine(root, "staging");
            var service = new GalleryDragExportService(queue, staging);
            Assert.Equal(staging, service.StagingRoot);
            using (GalleryDragExportService.PreparedDrag unused = await service.PrepareBatchAsync([record], CancellationToken.None))
                Assert.Single(unused.Paths);
            Assert.Empty(Directory.EnumerateFiles(staging));
            using (GalleryDragExportService.PreparedDrag published = await service.PrepareBatchAsync([record], CancellationToken.None))
                published.MarkPublished();
            string retained = Assert.Single(Directory.EnumerateFiles(staging));
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
            string staging = Path.Combine(root, "staging");
            var service = new GalleryDragExportService(queue, staging);
            Assert.Equal(staging, service.StagingRoot);
            await Assert.ThrowsAsync<IOException>(() => service.PrepareBatchAsync([record], CancellationToken.None));
            Assert.Equal("unrelated file", File.ReadAllText(target));
            Assert.Empty(Directory.EnumerateFiles(staging));
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
            string staging = Path.Combine(root, "staging");
            var service = new GalleryDragExportService(queue, staging);
            Assert.Equal(staging, service.StagingRoot);
            await Assert.ThrowsAsync<FileNotFoundException>(() => service.PrepareBatchAsync([record], CancellationToken.None));
            Assert.Equal("unrelated", File.ReadAllText(keep));
            Assert.False(Directory.Exists(staging));
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
            string staging = Path.Combine(root, "staging");
            var service = new GalleryDragExportService(queue, staging)
            {
                StagedFileForTest = count =>
                {
                    if (count != 1) return;
                    Directory.Move(sourceParent, sourceParent + "-original");
                    UpdatePathsTests.CreateJunction(sourceParent, targetRoot);
                },
            };
            Assert.Equal(staging, service.StagingRoot);
            await Assert.ThrowsAsync<IOException>(() => service.PrepareBatchAsync(records, CancellationToken.None));
            Assert.Empty(Directory.EnumerateFiles(staging));
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
    public void ExportPathBoundaries_RejectTraversalSiblingPrefixAndAmbiguousChildNames()
    {
        string root = OwnedTestDirectory.Create("MyCapture-drag-path-boundary-");
        try
        {
            Assert.Throws<IOException>(() => GalleryExportFiles.WithinRoot(root, root + "-sibling\\capture.png"));
            Assert.Throws<IOException>(() => GalleryExportFiles.WithinRoot(root, Path.Combine(root, "..", "capture.png")));
            Assert.Throws<IOException>(() => GalleryExportFiles.WithinRoot(root, root));
            string extendedRoot = @"\\?\" + root;
            Assert.Throws<IOException>(() => GalleryExportFiles.WithinRoot(extendedRoot, extendedRoot + @"\..\capture.png"));
            foreach (string name in new[] { "..", "../capture.png", "sub\\capture.png", "C:\\capture.png", "capture.png:stream", "capture.png.", "capture.png " })
                Assert.Throws<IOException>(() => GalleryExportFiles.Child(root, name));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Theory]
    [InlineData(@"C:\exports", @"C:\exports\capture.png")]
    [InlineData(@"\\server\share\exports", @"\\server\share\exports\capture.png")]
    [InlineData(@"\\?\C:\exports", @"\\?\C:\exports\capture.png")]
    [InlineData(@"\\?\UNC\server\share\exports", @"\\?\UNC\server\share\exports\capture.png")]
    public void ExportChild_PreservesLocalUncAndExtendedPathForms(string root, string expected)
    {
        // A path-format contract only; no UNC network resource is contacted.
        Assert.Equal(expected, GalleryExportFiles.Child(root, "capture.png"));
    }

    [Fact]
    public void DirectoryLease_BlocksInPlaceReparseWrites_WhileChildCopyAndCleanupStillWork()
    {
        string root = OwnedTestDirectory.Create("MyCapture-drag-directory-pin-");
        string targetRoot = OwnedTestDirectory.Create("MyCapture-drag-directory-target-");
        string staging = Path.Combine(root, "staging");
        try
        {
            Directory.CreateDirectory(staging);
            string source = Path.Combine(root, "source.png");
            string copy = Path.Combine(staging, "MyCapture_copy.png");
            File.WriteAllText(source, "original bytes");
            using (GalleryExportFiles.DirectoryLease lease = GalleryExportFiles.DirectoryLease.Open(staging, create: false))
            {
                // Pinning a directory must not deny ordinary writes to its children.
                GalleryExportFiles.Copy(source, copy, CancellationToken.None);
                Assert.Equal("original bytes", File.ReadAllText(copy));
                GalleryExportFiles.DeleteBestEffort(copy);
                Assert.Empty(Directory.EnumerateFileSystemEntries(staging));

                // This native helper opens the existing empty directory with GENERIC_WRITE,
                // then issues FSCTL_SET_REPARSE_POINT. No rename or directory swap is used.
                Win32Exception denied = Assert.Throws<Win32Exception>(() => UpdatePathsTests.CreateJunction(staging, targetRoot));
                Assert.Equal(32 /* ERROR_SHARING_VIOLATION */, denied.NativeErrorCode);
                Assert.False(File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint));
            }
            // Prove the same filesystem and native request permit the operation after release.
            UpdatePathsTests.CreateJunction(staging, targetRoot);
            Assert.True(File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal("original bytes", File.ReadAllText(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(targetRoot));
        }
        finally
        {
            if (Directory.Exists(staging) && File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(staging);
            OwnedTestDirectory.Delete(root); OwnedTestDirectory.Delete(targetRoot);
        }
    }

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
