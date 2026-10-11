using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Library navigation pane, location header, selection bar state and dragging captures onto
/// folders, without a window.
/// </summary>
public sealed class LibraryUxTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 14, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task NavigationViewsFoldersAndDatesShareOneSelection()
    {
        var f = new Fixture();
        CaptureFolder folder = await f.Queue.CreateFolderAsync("Review", createdAt: Now);
        CaptureRecord image = f.Add(Now.AddMinutes(-1));
        CaptureRecord video = f.Add(Now.AddMinutes(-2), video: true);
        CaptureRecord kept = f.Add(Now.AddDays(-1));
        f.Queue.TogglePin(kept.Id);
        await f.Queue.MoveCapturesAsync([image.Id], folder.Id);
        GalleryViewModel vm = f.ViewModel();

        Assert.Equal((3, 2, 1, 1), (vm.AllItemsCount, vm.ImageCount, vm.VideoCount, vm.KeptCount));
        Assert.True(vm.IsAllItemsView);
        using (UiText.UseLanguage("ko-KR"))
            Assert.Equal("보관함, 1개", vm.KeptAccessibleName);

        vm.NavigateTo(CaptureFolderScope.ForFolder(folder.Id));
        Assert.False(vm.IsAllItemsView || vm.IsImagesView || vm.IsVideosView || vm.IsKeptView);
        Assert.Equal([image.Id], vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));

        // A view always shows the whole library and leaves the open folder.
        vm.ShowView(GalleryFilter.Videos);
        Assert.True(vm.SelectedScope.IsAll);
        Assert.True(vm.IsVideosView);
        Assert.Equal([video.Id], vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));

        // A folder or date always opens with all of its items.
        vm.NavigateTo(CaptureFolderScope.ForDate(DateOnly.FromDateTime(kept.CreatedAt.DateTime)));
        Assert.Equal(GalleryFilter.All, vm.Filter);
        Assert.False(vm.IsVideosView);
        Assert.Equal([kept.Id], vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));

        vm.ShowView(GalleryFilter.Pinned);
        Assert.True(vm.IsKeptView);
        Assert.Equal([kept.Id], vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id));
        vm.ShowView(GalleryFilter.All);
        Assert.True(vm.IsAllItemsView);
        Assert.False(vm.ShowResultCount);
        vm.SearchQuery = "nothing matches this";
        Assert.True(vm.ShowResultCount);
    }

    [Fact]
    public async Task FolderListShowsRootFoldersByNameAcrossDates()
    {
        var f = new Fixture();
        CaptureFolder older = await f.Queue.CreateFolderAsync("Beta", createdAt: Now.AddDays(-4));
        CaptureFolder newer = await f.Queue.CreateFolderAsync("alpha", createdAt: Now);
        CaptureFolder child = await f.Queue.CreateFolderAsync("Child", older.Id);
        GalleryViewModel vm = f.ViewModel();

        Assert.Equal([newer.Id, older.Id], vm.FolderRoots.Select(node => node.FolderId!.Value));
        GalleryFolderNode beta = vm.FolderRoots[1];
        Assert.Equal(child.Id, Assert.Single(beta.Children).FolderId);
        // The same node objects back the date tree, so selection and drop highlights agree.
        Assert.Contains(vm.FolderNodes.SelectMany(date => date.Children), node => ReferenceEquals(node, beta));
        Assert.All(vm.FolderNodes, date => Assert.Null(date.FolderId));
    }

    [Fact]
    public async Task LocationHeaderShowsTheWayBackAndTheSaveHereChip()
    {
        var f = new Fixture();
        CaptureFolder parent = await f.Queue.CreateFolderAsync("Projects", createdAt: Now);
        CaptureFolder child = await f.Queue.CreateFolderAsync("Review", parent.Id);
        GalleryViewModel vm = f.ViewModel();
        using var language = UiText.UseLanguage("ko-KR");
        Assert.False(vm.ShowLocationHeader);
        Assert.Empty(vm.Breadcrumb);

        vm.SetLibraryOpen(true);
        vm.NavigateTo(CaptureFolderScope.ForFolder(child.Id));
        Assert.True(vm.ShowLocationHeader);
        Assert.True(vm.ShowSaveHere);
        Assert.Equal(["모든 항목", "Projects", "Review"], vm.Breadcrumb.Select(step => step.Name));
        Assert.Equal([true, true, false], vm.Breadcrumb.Select(step => step.IsLink));
        Assert.Equal(CaptureFolderScope.ForFolder(parent.Id), vm.Breadcrumb[1].Scope);
        Assert.True(vm.SavesNewCapturesHere);
        Assert.Equal("새 캡처를 Projects / Review 폴더에 저장합니다. 끄면 날짜별 라이브러리에 저장합니다.", vm.SaveHereToolTip);
        Assert.False(vm.ShowNewCaptureDestination);

        await vm.SetFolderCaptureEnabledAsync(false);
        Assert.False(vm.SavesNewCapturesHere);
        Assert.Null(f.Queue.ResolveNewCaptureFolderId());
        Assert.StartsWith("새 캡처를 날짜별 라이브러리에 저장합니다.", vm.SaveHereToolTip, StringComparison.Ordinal);
        await vm.SetFolderCaptureEnabledAsync(true);
        Assert.True(vm.SavesNewCapturesHere);

        // A date shows the way back, but only a folder can be a capture destination.
        vm.NavigateTo(CaptureFolderScope.ForDate(DateOnly.FromDateTime(Now.DateTime)));
        Assert.True(vm.ShowLocationHeader);
        Assert.False(vm.ShowSaveHere);
        Assert.Equal(["모든 항목", Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)],
            vm.Breadcrumb.Select(step => step.Name));

        // A saved keep-after-close folder is named in the status line while another view is open.
        await f.Queue.SetNewCaptureFolderAsync(parent.Id, true);
        vm.NavigateTo(CaptureFolderScope.All);
        Assert.True(vm.ShowNewCaptureDestination);
        Assert.Equal("새 캡처 저장 위치: Projects", vm.NewCaptureDestinationText);
    }

    [Fact]
    public void ClearSelectionReturnsTheCommandBarToTheLibraryCommands()
    {
        var f = new Fixture();
        CaptureRecord first = f.Add(Now);
        CaptureRecord second = f.Add(Now.AddMinutes(-1));
        GalleryViewModel vm = f.ViewModel();
        vm.Select(first.Id);
        vm.Select(second.Id, control: true);
        Assert.True(vm.HasSelection);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ClearSelection();

        Assert.False(vm.HasSelection);
        Assert.Null(vm.SingleSelectedTile);
        Assert.All(vm.Groups.SelectMany(group => group.Items), tile => Assert.False(tile.IsSelected));
        Assert.Contains(nameof(GalleryViewModel.HasSelection), raised);
        // Shift-range selection starts again from the next click, not from the cleared anchor.
        vm.Select(second.Id, shift: true);
        Assert.Equal([second.Id], vm.Selection.SelectedIds);
    }

    [Fact]
    public async Task DroppingCapturesOnAFolderMovesOnlyWhatWouldChange()
    {
        var f = new Fixture();
        CaptureFolder review = await f.Queue.CreateFolderAsync("Review", createdAt: Now);
        CaptureFolder design = await f.Queue.CreateFolderAsync("Design", createdAt: Now);
        CaptureRecord loose = f.Add(Now);
        CaptureRecord second = f.Add(Now.AddMinutes(-1));
        CaptureRecord inReview = f.Add(Now.AddMinutes(-2));
        await f.Queue.MoveCapturesAsync([inReview.Id], review.Id);
        Guid owner = Guid.NewGuid();
        CaptureFolderScope reviewScope = CaptureFolderScope.ForFolder(review.Id);
        using var language = UiText.UseLanguage("ko-KR");

        // One capture onto a folder card.
        GalleryDropPlan single = GalleryDropPlanner.Plan(f.Queue, owner, new(owner, [loose.Id], null), reviewScope, folderCard: true);
        Assert.Equal(GalleryDropAction.MoveCaptures, single.Action);
        Assert.Equal(1, single.Count);
        Assert.Equal("Review 폴더로 이동 · 1개", single.Hint);

        // Several selected captures: one is already there, so two would move.
        GalleryDropPlan several = GalleryDropPlanner.Plan(f.Queue, owner,
            new(owner, [loose.Id, second.Id, inReview.Id], null), reviewScope, folderCard: true);
        Assert.Equal(2, several.Count);
        Assert.Equal("Review 폴더로 이동 · 2개", several.Hint);
        Assert.Equal(2, await f.Queue.MoveCapturesAsync([loose.Id, second.Id, inReview.Id], several.Target.FolderId));
        Assert.All(new[] { loose, second, inReview }, record => Assert.Equal(review.Id, record.FolderId));

        // Nothing would change: no highlight, no hint, no drop.
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, new(owner, [loose.Id], null), reviewScope, true).CanDrop);

        // "모든 항목" and empty folder-list space take captures out of their folder.
        GalleryDropPlan back = GalleryDropPlanner.Plan(f.Queue, owner, new(owner, [loose.Id], null), CaptureFolderScope.All, false);
        Assert.Equal(GalleryDropAction.RemoveFromFolder, back.Action);
        Assert.Equal("폴더에서 빼기 · 1개", back.Hint);

        // Another folder's card accepts them.
        Assert.Equal(GalleryDropAction.MoveCaptures, GalleryDropPlanner.Plan(f.Queue, owner,
            new(owner, [loose.Id], null), CaptureFolderScope.ForFolder(design.Id), true).Action);
    }

    [Fact]
    public async Task DropsFromOutsideTheLibraryOrOfFoldersOnCardsAreRefused()
    {
        var f = new Fixture();
        CaptureFolder review = await f.Queue.CreateFolderAsync("Review", createdAt: Now);
        CaptureFolder other = await f.Queue.CreateFolderAsync("Other", createdAt: Now);
        CaptureRecord loose = f.Add(Now);
        CaptureFolderScope target = CaptureFolderScope.ForFolder(review.Id);
        Guid owner = Guid.NewGuid();

        // Files from File Explorer carry no library payload; another window's payload has another owner.
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, null, target, true).CanDrop);
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, new(Guid.NewGuid(), [loose.Id], null), target, true).CanDrop);
        // No target under the pointer (a capture tile, the details panel).
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, new(owner, [loose.Id], null), null, false).CanDrop);
        // A capture from another day cannot be dropped on a date.
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, new(owner, [loose.Id], null),
            CaptureFolderScope.ForDate(DateOnly.FromDateTime(Now.AddDays(-3).DateTime)), false).CanDrop);

        // Folders are reorganised in the navigation tree, as before; folder cards only take captures.
        var folderPayload = new GalleryFolderDragPayload(owner, [], other.Id);
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, folderPayload, target, folderCard: true).CanDrop);
        GalleryDropPlan intoTree = GalleryDropPlanner.Plan(f.Queue, owner, folderPayload, target, folderCard: false);
        Assert.Equal(GalleryDropAction.MoveFolder, intoTree.Action);
        // Already at the top level: dropping on empty tree space would change nothing.
        Assert.False(GalleryDropPlanner.Plan(f.Queue, owner, folderPayload, CaptureFolderScope.All, false).CanDrop);
    }

    [Fact]
    public async Task DraggingCapturesMarksEveryFolderThatCanTakeThem()
    {
        var f = new Fixture();
        CaptureFolder review = await f.Queue.CreateFolderAsync("Review", createdAt: Now);
        CaptureFolder design = await f.Queue.CreateFolderAsync("Design", createdAt: Now);
        CaptureRecord inReview = f.Add(Now);
        await f.Queue.MoveCapturesAsync([inReview.Id], review.Id);
        GalleryViewModel vm = f.ViewModel();
        GalleryFolderCardViewModel reviewCard = vm.FindFolderCard(review.Id)!;
        GalleryFolderCardViewModel designCard = vm.FindFolderCard(design.Id)!;

        vm.BeginCaptureDrag([inReview.Id]);
        Assert.False(reviewCard.IsDropAvailable);
        Assert.True(designCard.IsDropAvailable);
        designCard.IsDropTarget = true;
        vm.IsAllItemsDropTarget = true;

        vm.EndCaptureDrag();
        Assert.False(designCard.IsDropAvailable || designCard.IsDropTarget || vm.IsAllItemsDropTarget);
    }

    [Fact]
    public void CaptureDragCarriesExportFilesAndTheLibraryPayloadSeparately() => StaTestHost.Run(() =>
    {
        string staged = Path.Combine(Path.GetTempPath(), "mc-library-ux-drag-" + Guid.NewGuid().ToString("N") + ".png");
        var payload = new GalleryFolderDragPayload(Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], null);

        DataObject data = GalleryWindow.CreateCaptureDragData([staged], payload);

        // Outside the app (desktop, File Explorer) the drag is still the staged PNG copies.
        Assert.Equal([Path.GetFullPath(staged)], (string[])data.GetData(DataFormats.FileDrop)!);
        Assert.True(data.GetDataPresent(GalleryDragExportService.PreferredDropEffectFormat));
        // Inside the library the same drag names the captures to move.
        Assert.Same(payload, data.GetData(GalleryFolderDragPayload.Format, false));
    });

    [Fact]
    public void KeepWordingIsSeparateFromPinningToTheScreen()
    {
        var converter = new PinLabelConverter();
        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("보관", converter.Convert(false, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture));
            Assert.Equal("보관 해제", converter.Convert(true, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture));
            Assert.Equal("보관됨", UiText.Get("LibraryUx_KeptBadge"));
            Assert.Equal("보관함", UiText.Get("LibraryUx_NavKept"));
            Assert.Equal("핀으로 띄우기", UiText.Get("Gallery.Float"));
            var tile = new GalleryItemViewModel(new CaptureRecord { Width = 2, Height = 2, CreatedAt = Now, IsPinned = true },
                _ => "missing.png", 96);
            Assert.EndsWith(", 보관됨", tile.AccessibleName, StringComparison.Ordinal);
            Assert.Contains("라이브러리 폴더로 끌어 놓으면", tile.ExportToolTip, StringComparison.Ordinal);
            Assert.Contains("PNG", tile.ExportToolTip, StringComparison.Ordinal);
        }
        using (UiText.UseLanguage("en-US"))
        {
            Assert.Equal("Keep", converter.Convert(false, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture));
            Assert.Equal("Kept", UiText.Get("LibraryUx_KeptBadge"));
        }
    }

    private sealed class Fixture
    {
        internal CaptureQueue Queue { get; }

        internal Fixture()
        {
            string data = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ??
                Path.Combine(Path.GetTempPath(), "MyCapture-LibraryUx-tests");
            AppPaths paths = AppPaths.CreateForRoot(ValidatedFilePaths.DirectoryWithinRoot(data, "app-" + Guid.NewGuid().ToString("N")));
            paths.EnsureCreated();
            Queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        }

        internal GalleryViewModel ViewModel() => new(
            new GalleryController(Queue, NullLogger<GalleryController>.Instance),
            record => Queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96, clock: () => Now);

        internal CaptureRecord Add(DateTimeOffset created, bool video = false)
        {
            var record = new CaptureRecord
            {
                CreatedAt = created, UpdatedAt = created, Width = 2, Height = 2, TotalBytes = 4,
                MediaKind = video ? CaptureMediaKind.Video : CaptureMediaKind.Image,
            };
            Queue.Add(record);
            Directory.CreateDirectory(Queue.GetDirectory(record));
            return record;
        }
    }
}
