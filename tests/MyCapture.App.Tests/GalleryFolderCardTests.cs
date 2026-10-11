using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Folder cards in the library body and the open library folder as the capture destination.</summary>
public sealed class GalleryFolderCardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 14, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task TodayShowsNewFolderCardsBeforeCaptures_AndFolderOnlyDaysGetTheirOwnGroup()
    {
        var f = new Fixture();
        var empty = await f.Queue.CreateFolderAsync("Review", createdAt: Now.AddHours(-2));
        var filled = await f.Queue.CreateFolderAsync("Design", createdAt: Now.AddHours(-3));
        var older = await f.Queue.CreateFolderAsync("Archive", createdAt: Now.AddDays(-3));
        var loose = f.Add(Now.AddMinutes(-5));
        var inside = f.Add(Now.AddDays(-10));
        var newest = f.Add(Now.AddDays(-9));
        await f.Queue.MoveCapturesAsync([inside.Id, newest.Id], filled.Id);
        var vm = f.ViewModel();

        GalleryGroupViewModel today = vm.Groups[0];
        Assert.Equal(GalleryDateGrouping.Resolve(Now, Now).Heading, today.Heading);
        Assert.Equal(["Design", "Review"], today.Folders.Select(card => card.Name));
        GalleryFolderCardViewModel emptyCard = today.Folders[1];
        Assert.Equal(empty.Id, emptyCard.FolderId);
        Assert.Equal(CaptureFolderScope.ForFolder(empty.Id), emptyCard.Scope);
        Assert.Equal(0, emptyCard.CaptureCount);
        Assert.False(emptyCard.HasPreview);
        GalleryFolderCardViewModel filledCard = today.Folders[0];
        Assert.Equal(2, filledCard.CaptureCount);
        Assert.True(filledCard.HasPreview);
        Assert.Same(vm.FindTile(newest.Id), filledCard.Preview);
        Assert.Equal([loose.Id], today.Items.Select(tile => tile.Id));

        GalleryGroupViewModel folderOnly = Assert.Single(vm.Groups, group => group.Folders.Any(card => card.FolderId == older.Id));
        Assert.Empty(folderOnly.Items);
        Assert.Equal(GalleryDateGrouping.Resolve(older.CreatedAt, Now).Heading, folderOnly.Heading);
        // Like File Explorer, captures filed in a folder leave the root and its days: the two in
        // Design are reached through its card, so their days have no group of their own.
        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal(1, vm.VisibleCount);
        Assert.Equal(3, vm.ReachableCount);
        Assert.False(vm.ShowResultCount);

        Assert.IsType<GalleryHeaderRow>(vm.Rows[0]);
        Assert.Equal(today.Folders, Assert.IsType<GalleryFolderRow>(vm.Rows[1]).Folders);
        Assert.Equal(loose.Id, Assert.Single(Assert.IsType<GalleryTileRow>(vm.Rows[2]).Tiles).Id);
    }

    [Fact]
    public async Task FolderCardsHideWhileSearchingOrFilteringMedia()
    {
        var f = new Fixture();
        await f.Queue.CreateFolderAsync("Review", createdAt: Now);
        f.Add(Now.AddMinutes(-1), title: "needle");
        var vm = f.ViewModel();
        Assert.Single(vm.Groups[0].Folders);
        foreach (GalleryFilter filter in new[] { GalleryFilter.Images, GalleryFilter.Videos, GalleryFilter.Pinned })
        {
            vm.Filter = filter;
            Assert.All(vm.Groups, group => Assert.Empty(group.Folders));
            Assert.DoesNotContain(vm.Rows, row => row is GalleryFolderRow);
        }
        vm.Filter = GalleryFilter.All;
        vm.SearchQuery = "needle";
        Assert.All(vm.Groups, group => Assert.Empty(group.Folders));
        vm.SearchQuery = "Review";
        Assert.True(vm.IsEmpty);
        vm.SearchQuery = string.Empty;
        Assert.Single(vm.Groups[0].Folders);
    }

    [Fact]
    public async Task FolderScopeShowsDirectChildrenByNameAheadOfItsCaptures_InEverySortOrder()
    {
        var f = new Fixture();
        var root = await f.Queue.CreateFolderAsync("Root", createdAt: Now.AddDays(-1));
        var beta = await f.Queue.CreateFolderAsync("beta", root.Id, Now);
        var alpha = await f.Queue.CreateFolderAsync("Alpha", root.Id, Now);
        var nested = await f.Queue.CreateFolderAsync("Nested", alpha.Id, Now);
        var direct = f.Add(Now.AddHours(-3));
        var deep = f.Add(Now.AddHours(-2));
        await f.Queue.MoveCapturesAsync([direct.Id], root.Id);
        await f.Queue.MoveCapturesAsync([deep.Id], nested.Id);
        var vm = f.ViewModel();
        vm.SelectScope(CaptureFolderScope.ForFolder(root.Id));
        foreach (GallerySortOrder sort in new[] { GallerySortOrder.Oldest, GallerySortOrder.Name, GallerySortOrder.Newest })
        {
            vm.SortIndex = (int)sort;
            GalleryGroupViewModel group = Assert.Single(vm.Groups);
            Assert.Equal([alpha.Id, beta.Id], group.Folders.Select(card => card.FolderId));
            // Only the folder's own capture; the one in Alpha / Nested is behind Alpha's card.
            Assert.Equal([direct.Id], group.Items.Select(tile => tile.Id));
            Assert.IsType<GalleryHeaderRow>(vm.Rows[0]);
            Assert.Equal(group.Folders, Assert.IsType<GalleryFolderRow>(vm.Rows[1]).Folders);
            Assert.All(vm.Rows.Skip(2), row => Assert.IsType<GalleryTileRow>(row));
            // An opened folder uses the compact cards for both subfolders and captures.
            Assert.All(vm.Rows.Skip(1), row => Assert.True(row.IsCompact));
            Assert.False(vm.Rows[0].IsCompact);
        }
        GalleryFolderCardViewModel alphaCard = vm.Groups[0].Folders[0];
        GalleryFolderCardViewModel betaCard = vm.Groups[0].Folders[1];
        Assert.Equal(1, alphaCard.CaptureCount);
        Assert.Equal(1, alphaCard.FolderCount);
        Assert.Same(vm.FindTile(deep.Id), alphaCard.Preview);
        using (UiText.UseLanguage("ko-KR"))
        {
            Assert.Equal("폴더 1개 · 캡처 1개", alphaCard.CountText);
            Assert.Equal("캡처 0개", betaCard.CountText);
            Assert.Equal("폴더 beta, 캡처 0개", betaCard.AccessibleName);
            Assert.Contains("폴더 열기 (Enter)", betaCard.ToolTipText);
        }
        using (UiText.UseLanguage("en-US"))
        {
            Assert.Equal("Folders: 1 · Captures: 1", alphaCard.CountText);
            Assert.Equal("Captures: 0", betaCard.CountText);
            Assert.Equal("Folder beta, Captures: 0", betaCard.AccessibleName);
            Assert.Contains("Open folder (Enter)", betaCard.ToolTipText);
        }

        // An empty folder keeps the existing empty-folder state.
        vm.SelectScope(CaptureFolderScope.ForFolder(beta.Id));
        Assert.True(vm.IsEmpty);
        Assert.Equal(UiText.Get("Folder410.EmptyFolder"), vm.EmptyStateTitle);
    }

    [Fact]
    public void RowBuilderPlacesHeadingThenFolderRowsByColumnsThenTileRows()
    {
        GalleryFolderCardViewModel[] cards = Enumerable.Range(0, 3).Select(_ => new GalleryFolderCardViewModel(Guid.NewGuid())).ToArray();
        GalleryItemViewModel[] tiles = Enumerable.Range(0, 3).Select(_ => Tile()).ToArray();
        IReadOnlyList<GalleryRow> rows = GalleryRowBuilder.Build([new GalleryGroupViewModel("Today", tiles, cards)], columns: 2);
        Assert.Collection(rows,
            row => Assert.Equal("Today", Assert.IsType<GalleryHeaderRow>(row).Heading),
            row => Assert.Equal(cards[..2], Assert.IsType<GalleryFolderRow>(row).Folders),
            row => Assert.Equal([cards[2]], Assert.IsType<GalleryFolderRow>(row).Folders),
            row => Assert.Equal(tiles[..2], Assert.IsType<GalleryTileRow>(row).Tiles),
            row => Assert.Equal([tiles[2]], Assert.IsType<GalleryTileRow>(row).Tiles));
        Assert.Empty(new GalleryGroupViewModel("Legacy", tiles).Folders);
    }

    [Fact]
    public void ArrowKeysMoveBetweenFolderAndTileRowsInTheSameColumn()
    {
        GalleryFolderCardViewModel[] c = Enumerable.Range(0, 4).Select(_ => new GalleryFolderCardViewModel(Guid.NewGuid())).ToArray();
        GalleryItemViewModel[] t = Enumerable.Range(0, 6).Select(_ => Tile()).ToArray();
        GalleryRow[] rows = [new GalleryHeaderRow("Today"), new GalleryFolderRow(c[..3]), new GalleryFolderRow([c[3]]),
            new GalleryTileRow(t[..3]), new GalleryTileRow([t[3]]), new GalleryHeaderRow("Yesterday"), new GalleryTileRow(t[4..])];

        Assert.Same(c[3], GallerySelectionNavigation.FindVertical(rows, t[2], -1));
        Assert.Same(c[3], GallerySelectionNavigation.FindVertical(rows, t[1], -1));
        Assert.Same(c[0], GallerySelectionNavigation.FindVertical(rows, c[3], -1));
        Assert.Same(c[3], GallerySelectionNavigation.FindVertical(rows, c[1], 1));
        Assert.Same(t[0], GallerySelectionNavigation.FindVertical(rows, c[3], 1));
        Assert.Same(t[3], GallerySelectionNavigation.FindVertical(rows, t[2], 1));
        Assert.Same(t[4], GallerySelectionNavigation.FindVertical(rows, t[3], 1));
        Assert.Null(GallerySelectionNavigation.FindVertical(rows, c[0], -1));
        Assert.Null(GallerySelectionNavigation.FindVertical(rows, t[5], 1));

        Assert.Same(c[3], GallerySelectionNavigation.FindFolderNeighbor(rows, c[2], 1));
        Assert.Same(c[3], GallerySelectionNavigation.FindFolderNeighbor(rows, c[3], 1));
        Assert.Same(c[0], GallerySelectionNavigation.FindFolderNeighbor(rows, c[1], -1));
        Assert.Same(c[0], GallerySelectionNavigation.FindFolderNeighbor(rows, c[0], -1));
        // Tile-to-tile movement is unchanged.
        Assert.Same(t[0], GallerySelectionNavigation.FindNeighbor(rows, t[0], true, -1));
    }

    [Fact]
    public async Task FolderCardsAreReusedAcrossRefreshes_FollowTheNewestCapture_AndDropDeletedFolders()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("Reuse", createdAt: Now);
        var first = f.Add(Now.AddHours(-2));
        await f.Queue.MoveCapturesAsync([first.Id], folder.Id);
        var vm = f.ViewModel();
        GalleryFolderCardViewModel card = Assert.Single(vm.Groups[0].Folders);
        Assert.Same(vm.FindTile(first.Id), card.Preview);
        var changes = new List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        var second = f.Add(Now.AddHours(-1));
        await f.Queue.MoveCapturesAsync([second.Id], folder.Id);
        vm.Refresh();
        Assert.Same(card, vm.FindFolderCard(folder.Id));
        Assert.Same(card, Assert.Single(vm.Groups[0].Folders));
        Assert.Equal(2, card.CaptureCount);
        Assert.Same(vm.FindTile(second.Id), card.Preview);
        Assert.Contains(nameof(GalleryFolderCardViewModel.Preview), changes);
        Assert.Contains(nameof(GalleryFolderCardViewModel.CountText), changes);
        Assert.Contains(nameof(GalleryFolderCardViewModel.AccessibleName), changes);

        GalleryRow[] before = vm.Rows.ToArray();
        vm.Refresh();
        Assert.Equal(before, vm.Rows);

        await f.Queue.DeleteFolderScopeAsync(CaptureFolderScope.ForFolder(folder.Id));
        vm.Refresh();
        Assert.Null(vm.FindFolderCard(folder.Id));
        Assert.DoesNotContain(vm.Rows, row => row is GalleryFolderRow);
    }

    [Fact]
    public async Task OpenLibraryFolderIsTheDestination_UntilAllIsShownOrTheLibraryCloses()
    {
        var f = new Fixture();
        var folder = await f.Queue.CreateFolderAsync("제품리뷰", createdAt: Now);
        var vm = f.ViewModel();
        vm.SelectScope(CaptureFolderScope.ForFolder(folder.Id));
        // A view model that is not shown never claims new captures.
        Assert.Null(f.Queue.BrowsingCaptureFolderId);
        Assert.False(vm.SavesNewCapturesToFolder);
        Assert.True(vm.CanChangeNewCaptureFolder);
        using (UiText.UseLanguage("ko-KR")) Assert.Equal("캡처를 끌어 넣거나 새 캡처 저장 위치로 지정하십시오.", vm.EmptyStateText);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SetLibraryOpen(true);
        Assert.Equal(folder.Id, f.Queue.BrowsingCaptureFolderId);
        Assert.True(vm.SavesNewCapturesToFolder);
        Assert.Contains(nameof(GalleryViewModel.SavesNewCapturesToFolder), raised);
        Assert.Contains(nameof(GalleryViewModel.NewCaptureDestinationText), raised);
        using (UiText.UseLanguage("ko-KR")) Assert.Equal("새 캡처를 이 폴더에 저장합니다. 기존 캡처는 끌어 넣을 수 있습니다.", vm.EmptyStateText);
        using (UiText.UseLanguage("en-US")) Assert.Equal("New captures are saved to this folder. You can also drag captures here.", vm.EmptyStateText);
        using (UiText.UseLanguage("ko-KR")) Assert.Equal("새 캡처 저장 위치: 제품리뷰", vm.NewCaptureDestinationText);
        using (UiText.UseLanguage("en-US")) Assert.Equal("New capture destination: 제품리뷰", vm.NewCaptureDestinationText);
        Assert.Equal(folder.Id, f.Add(Now).FolderId);

        vm.SelectScope(CaptureFolderScope.All);
        Assert.Null(f.Queue.BrowsingCaptureFolderId);
        Assert.False(vm.CanChangeNewCaptureFolder);
        Assert.Null(f.Add(Now).FolderId);
        using (UiText.UseLanguage("ko-KR")) Assert.Equal("새 캡처 저장 위치: 날짜별 라이브러리", vm.NewCaptureDestinationText);

        vm.SelectScope(CaptureFolderScope.ForFolder(folder.Id));
        Assert.Equal(folder.Id, f.Queue.BrowsingCaptureFolderId);
        vm.SetLibraryOpen(false);
        Assert.Null(f.Queue.BrowsingCaptureFolderId);
        Assert.False(vm.SavesNewCapturesToFolder);
        Assert.Null(f.Add(Now).FolderId);
        Assert.False(f.Queue.UseCurrentFolderForNewCaptures);
        Assert.Null(f.Reload().BrowsingCaptureFolderId);
    }

    [Fact]
    public async Task ClearingTheCheckBoxUsesDatesForTheSession_AndTurnsOffTheSavedFolder()
    {
        var f = new Fixture();
        var saved = await f.Queue.CreateFolderAsync("Saved", createdAt: Now);
        var other = await f.Queue.CreateFolderAsync("Other", createdAt: Now);
        await f.Queue.SetNewCaptureFolderAsync(saved.Id, true);
        var vm = f.ViewModel();
        vm.SetLibraryOpen(true);
        vm.SelectScope(CaptureFolderScope.ForFolder(other.Id));
        Assert.Equal(other.Id, vm.NewCaptureFolderId);

        await vm.SetFolderCaptureEnabledAsync(false);
        Assert.Null(vm.NewCaptureFolderId);
        Assert.False(vm.SavesNewCapturesToFolder);
        Assert.True(vm.CanChangeNewCaptureFolder);
        Assert.False(f.Queue.UseCurrentFolderForNewCaptures);
        Assert.Null(f.Add(Now).FolderId);
        CaptureQueue reloaded = f.Reload();
        Assert.False(reloaded.UseCurrentFolderForNewCaptures);
        Assert.Null(reloaded.DefaultCaptureFolderId);

        // The choice lasts for this library session, across folders.
        vm.SelectScope(CaptureFolderScope.ForFolder(saved.Id));
        Assert.Null(vm.NewCaptureFolderId);
        await vm.SetFolderCaptureEnabledAsync(true);
        Assert.Equal(saved.Id, vm.NewCaptureFolderId);
        Assert.False(f.Queue.UseCurrentFolderForNewCaptures);

        await vm.SetFolderCaptureEnabledAsync(false);
        vm.SetLibraryOpen(false);
        vm.SetLibraryOpen(true);
        Assert.Equal(saved.Id, vm.NewCaptureFolderId);
    }

    [Fact]
    public async Task SavedKeepAfterCloseFolderStillAppliesAfterTheLibraryCloses()
    {
        var f = new Fixture();
        var saved = await f.Queue.CreateFolderAsync("Saved", createdAt: Now);
        var shown = await f.Queue.CreateFolderAsync("Shown", createdAt: Now);
        await f.Queue.SetNewCaptureFolderAsync(saved.Id, true);
        var vm = f.ViewModel();
        vm.SetLibraryOpen(true);
        vm.SelectScope(CaptureFolderScope.ForFolder(shown.Id));
        Assert.Equal(shown.Id, f.Add(Now).FolderId);
        vm.SetLibraryOpen(false);
        Assert.Equal(saved.Id, f.Add(Now).FolderId);

        vm.SelectScope(CaptureFolderScope.All);
        vm.SetLibraryOpen(true);
        Assert.True(vm.SavesNewCapturesToFolder);
        Assert.True(vm.CanChangeNewCaptureFolder);
        Assert.Contains("Saved", vm.NewCaptureDestinationText);
    }

    [Fact]
    public async Task KeepAfterCloseSettingContinuesFromTheShownFolder()
    {
        var f = new Fixture();
        var shown = await f.Queue.CreateFolderAsync("Shown", createdAt: Now);
        f.Queue.SetBrowsingCaptureFolder(shown.Id);
        var draft = new SettingsDraft(new AppSettings()) { UseCurrentFolderForNewCaptures = true };
        Assert.True(draft.FolderDestinationEdited);
        AppSettings next = draft.ToAppSettings();
        SettingsApplyService.ReconcileFolderDestination(next.Queue, f.Queue, draft.FolderDestinationEdited);
        Assert.Equal(shown.Id, next.Queue.DefaultCaptureFolderId);
        f.Queue.UpdateLimits(next.Queue);
        f.Queue.SetBrowsingCaptureFolder(null);
        Assert.Equal(shown.Id, f.Queue.ResolveNewCaptureFolderId());
        Assert.Equal(shown.Id, f.Reload().ResolveNewCaptureFolderId());
        using (UiText.UseLanguage("ko-KR"))
            Assert.Equal("라이브러리를 닫아도 마지막 폴더에 새 캡처 저장", UiText.Get("LibraryFolder_KeepAfterClose"));
        using (UiText.UseLanguage("en-US"))
            Assert.Equal("Keep saving new captures to the last folder after closing the library", UiText.Get("LibraryFolder_KeepAfterClose"));
    }

    private static GalleryItemViewModel Tile() =>
        new(new CaptureRecord { Width = 2, Height = 2, CreatedAt = Now }, _ => "missing-folder-card-preview.jpg", 96);

    private sealed class Fixture
    {
        private readonly AppPaths _paths;
        internal CaptureQueue Queue { get; }

        internal Fixture()
        {
            string data = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT") ??
                Path.Combine(Path.GetTempPath(), "MyCapture-LibraryFolder-tests");
            _paths = AppPaths.CreateForRoot(ValidatedFilePaths.DirectoryWithinRoot(data, "app-" + Guid.NewGuid().ToString("N")));
            _paths.EnsureCreated();
            Queue = new CaptureQueue(_paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            // Fixtures are deliberately retained for diagnosis, like the other folder tests.
        }

        internal GalleryViewModel ViewModel() => new(
            new GalleryController(Queue, NullLogger<GalleryController>.Instance),
            record => Queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96, clock: () => Now);

        internal CaptureQueue Reload()
        {
            var queue = new CaptureQueue(_paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            queue.Load();
            return queue;
        }

        internal CaptureRecord Add(DateTimeOffset created, string title = "")
        {
            var record = new CaptureRecord
            {
                CreatedAt = created, UpdatedAt = created, Width = 2, Height = 2, Title = title,
                SourceWindowTitle = "private window title", TotalBytes = 4,
            };
            Queue.Add(record);
            Directory.CreateDirectory(Queue.GetDirectory(record));
            File.WriteAllBytes(Queue.GetFilePath(record, CaptureFileNames.Original), [1, 2]);
            File.WriteAllBytes(Queue.GetFilePath(record, CaptureFileNames.Rendered), [3, 4]);
            Queue.SaveRecordMetaOrThrow(record);
            Queue.Save();
            return record;
        }
    }
}
