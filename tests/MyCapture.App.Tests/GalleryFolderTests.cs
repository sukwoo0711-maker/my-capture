using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryFolderTests
{
    [Fact]
    public async Task MovingSelectedFolderIgnoresTransientSelectionDuringTreeRebuild()
    {
        var f = new Fixture();
        var moving = await f.Queue.CreateFolderAsync("A moving");
        var parent = await f.Queue.CreateFolderAsync("B parent");
        var vm = f.ViewModel();
        var selected = CaptureFolderScope.ForFolder(moving.Id);
        vm.SelectScope(selected);
        var date = Assert.Single(vm.FolderNodes);
        var parentNode = Assert.Single(date.Children, n => n.FolderId == parent.Id);
        bool observed = false;
        parentNode.Children.CollectionChanged += (_, _) =>
        {
            if (observed) return;
            observed = true;
            vm.SelectScope(CaptureFolderScope.ForFolder(parent.Id));
            vm.Refresh();
        };

        await f.Queue.MoveFolderAsync(moving.Id, parent.Id);
        vm.Refresh();

        Assert.True(observed);
        Assert.Equal(selected, vm.SelectedScope);
        Assert.Same(parentNode, Assert.Single(date.Children));
        var movedNode = Assert.Single(parentNode.Children);
        Assert.Equal(moving.Id, movedNode.FolderId);
        Assert.True(movedNode.IsSelected);
        vm.Refresh();
        Assert.Same(movedNode, Assert.Single(parentNode.Children));
    }

    [Fact]
    public async Task TreeAndFolderScope_CombineSearchMediaFilterSortAndStableExpansion()
    {
        var f = new Fixture();
        DateTimeOffset date = new(2026, 10, 8, 20, 0, 0, TimeSpan.FromHours(9));
        var root = await f.Queue.CreateFolderAsync("Review", createdAt: date);
        var child = await f.Queue.CreateFolderAsync("Code", root.Id, date.AddDays(1));
        var first = f.Add(date.AddDays(-10), title: "Z needle");
        var second = f.Add(date.AddDays(-9), title: "A needle", video: true);
        f.Add(date.AddDays(1), title: "Other needle");
        await f.Queue.MoveCapturesAsync([first.Id, second.Id], child.Id);
        var vm = f.ViewModel();
        GalleryFolderNode dateNode = Assert.Single(vm.FolderNodes, node => node.Scope.Date == root.CreatedDate);
        Assert.Equal(2, dateNode.Count);
        var rootNode = Assert.Single(dateNode.Children);
        rootNode.IsExpanded = false;
        vm.SelectScope(CaptureFolderScope.ForFolder(root.Id));
        vm.SearchQuery = "needle";
        vm.SortIndex = (int)GallerySortOrder.Name;
        Assert.Equal([second.Id, first.Id], vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));
        vm.Filter = GalleryFilter.Images;
        Assert.Equal(first.Id, Assert.Single(vm.Groups.SelectMany(group => group.Items)).Id);
        Assert.Contains("Review", vm.SearchPlaceholder);
        vm.Refresh();
        Assert.False(rootNode.IsExpanded);
        Assert.Same(rootNode, dateNode.Children[0]);
        vm.SearchQuery = "does not exist";
        Assert.True(vm.IsEmpty);
        Assert.False(vm.QueueIsEmpty);
        Assert.True(vm.CanResetEmptySearch);

        // An empty folder viewed without a search or filter has nothing to reset.
        var empty = await f.Queue.CreateFolderAsync("Empty", createdAt: date);
        vm.SearchQuery = string.Empty;
        vm.Filter = GalleryFilter.All;
        vm.SelectScope(CaptureFolderScope.ForFolder(empty.Id));
        Assert.True(vm.IsEmpty);
        Assert.False(vm.CanResetEmptySearch);
    }

    [Fact]
    public async Task GeneratedNamesAreVisibleSearchableAndKept_AndDefaultHeaderTracksTheQueue()
    {
        var f = new Fixture(new QueueSettings { FolderFilePrefix = "review" });
        var folder = await f.Queue.CreateFolderAsync("Code");
        var capture = f.Add(DateTimeOffset.Now.AddDays(-30));
        await f.Queue.MoveCapturesAsync([capture.Id], folder.Id);
        await f.Queue.SetNewCaptureFolderAsync(folder.Id, true);
        var vm = f.ViewModel();
        vm.SelectScope(CaptureFolderScope.ForFolder(folder.Id));
        vm.SearchQuery = "review01";
        var tile = Assert.Single(vm.Groups.SelectMany(group => group.Items));
        Assert.Equal("review01", tile.Caption);
        Assert.False(tile.IsExpiringSoon);
        Assert.False(tile.IsPinned);
        Assert.True(vm.UseCurrentFolderForNewCaptures);
        Assert.Contains("Code", vm.NewCaptureDestinationText);
        await f.Queue.SetNewCaptureFolderAsync(null, false);
        vm.Refresh();
        Assert.False(vm.UseCurrentFolderForNewCaptures);
    }

    [Fact]
    public async Task DateTreeExport_CopiesRenderedMediaAndEmptyNestedFolders_WithNamesAndCollisions()
    {
        var f = new Fixture();
        DateTimeOffset date = new(2026, 10, 8, 20, 0, 0, TimeSpan.FromHours(9));
        var root = await f.Queue.CreateFolderAsync("Code", createdAt: date);
        var child = await f.Queue.CreateFolderAsync("Nested", root.Id, date);
        await f.Queue.CreateFolderAsync("Empty", child.Id, date);
        var first = f.Add(date.AddDays(-10));
        var duplicate = f.Add(date.AddDays(-9), title: "myCapture01");
        var video = f.Add(date.AddDays(-8), video: true);
        var loose = f.Add(date);
        var other = f.Add(date.AddDays(1));
        await f.Queue.MoveCapturesAsync([first.Id, duplicate.Id], root.Id);
        await f.Queue.MoveCapturesAsync([video.Id], child.Id);
        // This test checks unchanged names. A deep data-root path can legitimately
        // trigger the separate 240-character shortening policy, so stage this fixture
        // under the runner's isolated TEMP and require room for every original name.
        string staging = ValidatedFilePaths.CreateTemporaryDirectory();
        var exporter = new GalleryFolderExportService(f.Queue, staging);
        GalleryFolderExportPlan plan = exporter.BuildPlan(CaptureFolderScope.ForDate(root.CreatedDate));
        int batchPrefixLength = Path.Combine(staging,
            GalleryExportFiles.FolderBatchPrefix + new string('0', 32)).Length;
        Assert.All(plan.Directories.Concat(plan.Entries.Select(entry => entry.RelativePath)), relative =>
            Assert.True(batchPrefixLength + 1 + relative.Length <= GalleryExportPathBudget.MaximumPathLength,
                $"The name-preserving fixture needs a shorter isolated TEMP: {staging}; entry: {relative}"));
        using var prepared = await exporter.PrepareAsync(plan);
        string top = Assert.Single(prepared.Paths);
        Assert.Equal("2026-10-08", Path.GetFileName(top));
        Assert.True(Directory.Exists(Path.Combine(top, "Code", "Nested", "Empty")));
        Assert.Equal(f.RenderedBytes(first), File.ReadAllBytes(Path.Combine(top, "Code", "myCapture01.png")));
        Assert.Equal(f.RenderedBytes(duplicate), File.ReadAllBytes(Path.Combine(top, "Code", "myCapture01-02.png")));
        Assert.Equal(f.RenderedBytes(video), File.ReadAllBytes(Path.Combine(top, "Code", "Nested", "myCapture01.mp4")));
        Assert.Equal(4, Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories).Count());
        Assert.DoesNotContain(plan.Entries, entry => entry.Record.Id == other.Id);
        Assert.Contains(plan.Entries, entry => entry.Record.Id == loose.Id);
        Assert.All(plan.Entries, entry => Assert.False(f.Queue.Remove(entry.Record.Id)));
        RunSta(() =>
        {
            var data = GalleryDragExportService.CreateFileDropData(prepared.Paths);
            Assert.Equal(prepared.Paths, Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop)));
            using var effect = Assert.IsType<MemoryStream>(data.GetData(GalleryDragExportService.PreferredDropEffectFormat));
            Assert.Equal((int)DragDropEffects.Copy, BitConverter.ToInt32(effect.ToArray()));
        });
    }

    [Fact]
    public async Task FolderAndZipExportsPreserveTheSameTree_AndNeverReplaceExistingUserFiles()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Review", createdAt: new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        var capture = f.Add(DateTimeOffset.Now, title: "Decision");
        await f.Queue.MoveCapturesAsync([capture.Id], folder.Id);
        var exporter = f.Exporter();
        using var prepared = await exporter.PrepareAsync(exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id)));
        string destination = ValidatedFilePaths.DirectoryWithinRoot(f.Root, "exports");
        Directory.CreateDirectory(ValidatedFilePaths.DirectoryWithinRoot(destination, "2026-10-08"));
        string keep = ValidatedFilePaths.FileWithinRoot(destination, Path.Combine("2026-10-08", "keep.txt"), ".txt");
        File.WriteAllText(keep, "keep");
        IReadOnlyList<string> paths = await GalleryFolderExportService.ExportDirectoryAsync(prepared, destination);
        Assert.EndsWith("2026-10-08-02", Assert.Single(paths));
        Assert.Equal("keep", File.ReadAllText(keep));
        Assert.Equal(f.RenderedBytes(capture), File.ReadAllBytes(Path.Combine(paths[0], "Review", "Decision.png")));
        string zipPath = ValidatedFilePaths.FileWithinRoot(destination, "review.zip", ".zip");
        await GalleryFolderExportService.ExportZipAsync(prepared, zipPath);
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(archive.GetEntry("2026-10-08/Review/"));
            ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("2026-10-08/Review/Decision.png"));
            using var output = new MemoryStream();
            entry.Open().CopyTo(output);
            Assert.Equal(f.RenderedBytes(capture), output.ToArray());
            Assert.DoesNotContain(archive.Entries, item => item.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }
        byte[] before = File.ReadAllBytes(zipPath);
        await Assert.ThrowsAsync<IOException>(() => GalleryFolderExportService.ExportZipAsync(prepared, zipPath));
        Assert.Equal(before, File.ReadAllBytes(zipPath));
    }

    [Fact]
    public void AtomicArchivePublicationHidesPartialContentAndPreservesConcurrentDestinations()
    {
        var f = new Fixture();
        string target = ValidatedFilePaths.FileWithinRoot(f.Root, "race.zip", ".zip");
        Assert.Throws<IOException>(() => GalleryExportFiles.WriteStreamNew(target, stream =>
        {
            stream.Write([1, 2, 3]);
            Assert.False(File.Exists(target));
            File.WriteAllText(target, "preserve the concurrent destination", Encoding.UTF8);
        }));
        Assert.Equal("preserve the concurrent destination", File.ReadAllText(target, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(f.Root, ".mycapture-folder-*.zip.partial"));

        string cancelled = ValidatedFilePaths.FileWithinRoot(f.Root, "cancelled.zip", ".zip");
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => GalleryExportFiles.WriteStreamNew(cancelled, stream =>
        {
            stream.Write([4, 5, 6]);
            Assert.False(File.Exists(cancelled));
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.False(File.Exists(cancelled));
        Assert.Empty(Directory.EnumerateFiles(f.Root, ".mycapture-folder-*.zip.partial"));
    }

    [Fact]
    public async Task SelectedCaptureTreeReservesFolderPathsBeforeAllocatingFileNames()
    {
        var f = new Fixture();
        DateTimeOffset date = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var loose = f.Add(date, title: "Review");
        var nested = f.Add(date.AddMinutes(1));
        var folder = await f.Queue.CreateFolderAsync("Review.png", createdAt: date);
        await f.Queue.MoveCapturesAsync([nested.Id], folder.Id);
        var exporter = f.Exporter();
        var plan = exporter.BuildCapturePlan([loose, nested], tree: true);
        using var prepared = await exporter.PrepareAsync(plan);
        string top = Assert.Single(prepared.Paths);
        Assert.Equal(f.RenderedBytes(loose), File.ReadAllBytes(Path.Combine(top, "Review-02.png")));
        Assert.Equal(f.RenderedBytes(nested), File.ReadAllBytes(Path.Combine(top, "Review.png", "myCapture01.png")));
        string zipPath = Path.Combine(f.Root, "selection.zip");
        await GalleryFolderExportService.ExportZipAsync(prepared, zipPath);
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.NotNull(archive.GetEntry("2026-10-08/Review-02.png"));
        Assert.NotNull(archive.GetEntry("2026-10-08/Review.png/myCapture01.png"));
    }

    [Fact]
    public async Task NamedCaptureBatchUsesFolderNames_AndDropDisposalRemovesStagingImmediately()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Names");
        var record = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([record.Id], folder.Id);
        var exporter = new GalleryDragExportService(f.Queue, f.Staging);
        string path;
        using (var prepared = await exporter.PrepareBatchAsync([record], CancellationToken.None))
        {
            path = Assert.Single(prepared.Paths);
            Assert.Equal("myCapture01.png", Path.GetFileName(path));
            prepared.MarkPublished();
        }
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
        Assert.True(File.Exists(f.Queue.GetFilePath(record, CaptureFileNames.Rendered)));
    }

    [Fact]
    public async Task EmptyFolderAndVideoFallbackAreExportedWithoutExposingMetadata()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Empty");
        var exporter = f.Exporter();
        using (var empty = await exporter.PrepareAsync(exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id))))
        {
            Assert.Empty(empty.Plan.Entries);
            Assert.True(Directory.Exists(Path.Combine(Assert.Single(empty.Paths), "Empty")));
        }
        var video = f.Add(DateTimeOffset.Now, video: true, hasRendered: false);
        await f.Queue.MoveCapturesAsync([video.Id], folder.Id);
        using var fallback = await exporter.PrepareAsync(exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id)));
        var entry = Assert.Single(fallback.Plan.Entries);
        Assert.Equal(f.SourceBytes(video), File.ReadAllBytes(GalleryExportFiles.RelativeChild(fallback.BatchRoot, entry.RelativePath)));
    }

    [Fact]
    public async Task CancellationAndGenerationChange_DiscardUnpublishedExportsAndReleaseLeases()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Cancel");
        var first = f.Add(DateTimeOffset.Now); var second = f.Add(DateTimeOffset.Now.AddSeconds(1));
        await f.Queue.MoveCapturesAsync([first.Id, second.Id], folder.Id);
        using var cancellation = new CancellationTokenSource();
        var exporter = f.Exporter();
        exporter.StagedFileForTest = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.PrepareAsync(
            exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id)), cancellation.Token));
        Assert.Empty(Directory.EnumerateDirectories(f.Staging));
        exporter.StagedFileForTest = _ => first.ContentRevision++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.PrepareAsync(
            exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id))));
        Assert.Empty(Directory.EnumerateDirectories(f.Staging));
        Assert.True(f.Queue.Remove(first.Id));
        Assert.True(File.Exists(f.Queue.GetFilePath(second, CaptureFileNames.Rendered)));
    }

    [Fact]
    public async Task DestinationJunctionIsRejectedWithoutWritingIntoItsTarget()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Safe");
        var record = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([record.Id], folder.Id);
        using var prepared = await f.Exporter().PrepareAsync(f.Exporter().BuildPlan(CaptureFolderScope.ForFolder(folder.Id)));
        string outside = ValidatedFilePaths.DirectoryWithinRoot(f.Root, "outside");
        Directory.CreateDirectory(outside);
        string sentinel = ValidatedFilePaths.FileWithinRoot(outside, "keep.txt", ".txt");
        File.WriteAllText(sentinel, "keep");
        string link = Path.Combine(f.Root, "destination-link");
        UpdatePathsTests.CreateJunction(link, outside);
        await Assert.ThrowsAsync<IOException>(() => GalleryFolderExportService.ExportDirectoryAsync(prepared, link));
        await Assert.ThrowsAsync<IOException>(() => GalleryFolderExportService.ExportZipAsync(prepared, Path.Combine(link, "capture.zip")));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task StagingReplacementCannotMakeUnpublishedCleanupTraverseAnExternalTree()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Safe");
        var record = f.Add(DateTimeOffset.Now);
        await f.Queue.MoveCapturesAsync([record.Id], folder.Id);
        var exporter = f.Exporter();
        var prepared = await exporter.PrepareAsync(exporter.BuildPlan(CaptureFolderScope.ForFolder(folder.Id)));
        string batchName = Path.GetFileName(prepared.BatchRoot);
        Directory.Move(f.Staging, ValidatedFilePaths.DirectoryWithinRoot(f.Root, "staging-retained"));
        string outside = ValidatedFilePaths.DirectoryWithinRoot(f.Root, "replacement");
        Directory.CreateDirectory(ValidatedFilePaths.DirectoryWithinRoot(outside, batchName));
        string keep = ValidatedFilePaths.FileWithinRoot(outside, Path.Combine(batchName, "keep.png"), ".png");
        File.WriteAllText(keep, "unrelated");
        UpdatePathsTests.CreateJunction(f.Staging, outside);
        prepared.Dispose();
        GalleryExportFiles.CleanupFolderBatches(f.Staging, DateTimeOffset.Now.AddDays(1));
        Assert.Equal("unrelated", File.ReadAllText(keep));
        Assert.True(f.Queue.Remove(record.Id));
    }

    [Fact]
    public void FolderBatchCleanupRemovesOnlyExpiredOwnedBatchesAndPreservesSiblingSentinel()
    {
        var f = new Fixture();
        Directory.CreateDirectory(f.Staging);
        string expired = ValidatedFilePaths.DirectoryWithinRoot(f.Staging, GalleryExportFiles.FolderBatchPrefix + Guid.NewGuid().ToString("N"));
        string recent = ValidatedFilePaths.DirectoryWithinRoot(f.Staging, GalleryExportFiles.FolderBatchPrefix + Guid.NewGuid().ToString("N"));
        string unowned = ValidatedFilePaths.DirectoryWithinRoot(f.Staging, GalleryExportFiles.FolderBatchPrefix + "not-a-guid");
        string sibling = ValidatedFilePaths.DirectoryWithinRoot(f.Root, "staging-sibling");
        string expiredFile = WriteBatchFixture(expired, "expired");
        string recentFile = WriteBatchFixture(recent, "recent");
        string unownedFile = WriteBatchFixture(unowned, "unowned");
        string sentinel = WriteBatchFixture(sibling, "external sentinel");
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-2);
        Directory.SetLastWriteTimeUtc(expired, cutoff.AddDays(-1).UtcDateTime);
        Directory.SetLastWriteTimeUtc(unowned, cutoff.AddDays(-1).UtcDateTime);

        GalleryExportFiles.CleanupFolderBatches(f.Staging, cutoff);

        Assert.False(File.Exists(expiredFile));
        Assert.False(Directory.Exists(expired));
        Assert.Equal("recent", File.ReadAllText(recentFile));
        Assert.Equal("unowned", File.ReadAllText(unownedFile));
        Assert.Equal("external sentinel", File.ReadAllText(sentinel));
        Assert.Equal(2, Directory.EnumerateDirectories(f.Staging).Count());
    }

    private static string WriteBatchFixture(string root, string content)
    {
        root = ValidatedFilePaths.CreateDirectory(root);
        string file = ValidatedFilePaths.FileWithinRoot(root, "keep.png", ".png");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public async Task InternalDropPolicyPreventsCyclesAndWrongDates_AndAcceptsCaptureMoves()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Parent");
        var child = await f.Queue.CreateFolderAsync("Child", folder.Id);
        var record = f.Add(DateTimeOffset.Now);
        var owner = Guid.NewGuid();
        Assert.False(GalleryWindow.CanDropFolderPayload(f.Queue, new(owner, [], folder.Id), CaptureFolderScope.ForFolder(child.Id)));
        Assert.True(GalleryWindow.CanDropFolderPayload(f.Queue, new(owner, [record.Id], null), CaptureFolderScope.ForFolder(child.Id)));
        Assert.False(GalleryWindow.CanDropFolderPayload(f.Queue, new(owner, [Guid.NewGuid()], null), CaptureFolderScope.ForFolder(child.Id)));
        Assert.False(GalleryWindow.CanDropFolderPayload(f.Queue, new(owner, [record.Id], null),
            CaptureFolderScope.ForDate(DateOnly.FromDateTime(record.CreatedAt.DateTime).AddDays(-1))));
        Assert.True(GalleryWindow.CanDropFolderPayload(f.Queue, new(owner, [], child.Id), CaptureFolderScope.All));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("/rooted.png")]
    [InlineData("safe/../escape.png")]
    [InlineData("safe\\escape.png")]
    [InlineData("safe/CON:stream")]
    public void RelativeExportEntriesRejectTraversalAndWindowsStreamSyntax(string relative)
    {
        var f = new Fixture();
        Assert.Throws<IOException>(() => GalleryExportFiles.RelativeChild(f.Root, relative));
    }

    [Fact]
    public void FolderDeleteConfirmationDefaultsToCancel()
    {
        RunSta(() =>
        {
            var dialog = new GalleryFolderDeletionDialog("Review", new(3, 2, 4, 1));
            Assert.True(dialog.CancelButton.IsDefault);
            Assert.True(dialog.CancelButton.IsCancel);
            Assert.False(dialog.DeleteButton.IsDefault);
            Assert.False(dialog.DeleteButton.IsCancel);
            dialog.Close();
        });
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA fixture did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Fixture
    {
        internal string Root { get; }
        internal string Staging { get; }
        internal CaptureQueue Queue { get; }
        internal Fixture(QueueSettings? settings = null)
        {
            string data = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ??
                Path.Combine(Path.GetTempPath(), "MyCapture-Folder410-tests");
            Root = ValidatedFilePaths.DirectoryWithinRoot(data, "app-" + Guid.NewGuid().ToString("N"));
            Staging = ValidatedFilePaths.DirectoryWithinRoot(Root, "staging");
            var paths = AppPaths.CreateForRoot(Root);
            paths.EnsureCreated();
            Queue = new CaptureQueue(paths, settings ?? new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            // Fixtures and injected failure artifacts are deliberately retained.
        }
        internal GalleryViewModel ViewModel() => new(
            new GalleryController(Queue, NullLogger<GalleryController>.Instance),
            record => Queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        internal GalleryFolderExportService Exporter() => new(Queue, Staging);
        internal byte[] RenderedBytes(CaptureRecord record) => record.IsVideo
            ? Encoding.UTF8.GetBytes("rendered-" + record.Id) : ExportPngFixture.Create(record.Id);
        internal byte[] SourceBytes(CaptureRecord record) => Encoding.UTF8.GetBytes("original-" + record.Id);
        internal CaptureRecord Add(DateTimeOffset created, string title = "", bool video = false, bool hasRendered = true)
        {
            var record = new CaptureRecord { CreatedAt = created, UpdatedAt = created, Width = 2, Height = 2,
                Title = title, SourceWindowTitle = "private window title",
                MediaKind = video ? CaptureMediaKind.Video : CaptureMediaKind.Image,
                DurationMs = video ? 1000 : 0, FrameRate = video ? 30 : 0, FrameCount = video ? 30 : 0 };
            Queue.Add(record);
            Directory.CreateDirectory(Queue.GetDirectory(record));
            File.WriteAllBytes(Queue.GetFilePath(record, video ? CaptureFileNames.VideoSource : CaptureFileNames.Original), SourceBytes(record));
            if (hasRendered) File.WriteAllBytes(Queue.GetFilePath(record,
                video ? CaptureFileNames.VideoRendered : CaptureFileNames.Rendered), RenderedBytes(record));
            Queue.SaveRecordMetaOrThrow(record); Queue.Save();
            return record;
        }
    }
}
