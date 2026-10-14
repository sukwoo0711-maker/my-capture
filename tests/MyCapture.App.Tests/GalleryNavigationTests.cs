using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryNavigationTests
{
    [Theory]
    [InlineData(Key.Back, ModifierKeys.None, false, false)]
    [InlineData(Key.Back, ModifierKeys.None, true, null)]
    [InlineData(Key.Back, ModifierKeys.Control, false, null)]
    [InlineData(Key.Left, ModifierKeys.Alt, false, false)]
    [InlineData(Key.Left, ModifierKeys.Alt, true, false)]
    [InlineData(Key.Right, ModifierKeys.Alt, false, true)]
    [InlineData(Key.Right, ModifierKeys.Alt, true, true)]
    [InlineData(Key.Left, ModifierKeys.None, false, null)]
    [InlineData(Key.Right, ModifierKeys.Control, true, null)]
    public void NavigationShortcutsPreserveTextEditing(Key key, ModifierKeys modifiers, bool textInput, bool? direction)
    {
        Assert.Equal(direction, GalleryWindow.NavigationKeyDirection(key, modifiers, textInput));
    }

    [Fact]
    public async Task ParentAndForwardTraverseHierarchyInsteadOfLastVisitedSibling()
    {
        var (queue, vm) = Create();
        CaptureFolder parent = await queue.CreateFolderAsync("Parent");
        CaptureFolder child = await queue.CreateFolderAsync("Child", parent.Id);
        CaptureFolder sibling = await queue.CreateFolderAsync("Sibling");
        vm.NavigateTo(CaptureFolderScope.ForFolder(sibling.Id));
        vm.NavigateTo(CaptureFolderScope.ForFolder(child.Id));

        Assert.True(vm.NavigateParent());
        Assert.Equal(parent.Id, vm.SelectedScope.FolderId);
        Assert.True(vm.NavigateParent());
        Assert.True(vm.IsAllItemsView);
        Assert.False(vm.NavigateParent());
        Assert.True(vm.NavigateForward());
        Assert.Equal(parent.Id, vm.SelectedScope.FolderId);
        Assert.True(vm.NavigateForward());
        Assert.Equal(child.Id, vm.SelectedScope.FolderId);
        Assert.False(vm.CanNavigateForward);
        Assert.Equal(new[] { CaptureFolderScope.All, CaptureFolderScope.ForFolder(parent.Id),
            CaptureFolderScope.ForFolder(child.Id) }, vm.Breadcrumb.Select(step => step.Scope));
        Assert.All(vm.Breadcrumb.Take(2), step => Assert.True(step.IsLink));
        Assert.False(vm.Breadcrumb[^1].IsLink);
    }

    [Fact]
    public async Task HistoryRestoresViewsAndNewNavigationClearsForward()
    {
        var (queue, vm) = Create();
        CaptureFolder folder = await queue.CreateFolderAsync("Folder");
        vm.ShowView(GalleryFilter.Images);
        vm.ShowTag("work");
        vm.NavigateTo(CaptureFolderScope.ForFolder(folder.Id));
        Assert.True(vm.NavigateBack());
        Assert.Equal("work", vm.SelectedTag);
        Assert.True(vm.NavigateBack());
        Assert.True(vm.IsImagesView);
        Assert.True(vm.NavigateForward());
        Assert.Equal("work", vm.SelectedTag);
        vm.ShowView(GalleryFilter.Videos);
        Assert.False(vm.CanNavigateForward);
        Assert.False(vm.NavigateForward());
        Assert.True(vm.IsVideosView);
    }

    [Theory]
    [InlineData(GalleryFilter.Images, null)]
    [InlineData(GalleryFilter.Videos, null)]
    [InlineData(GalleryFilter.All, "work")]
    public async Task ParentPreservesDistinctMediaAndTagLocationsInHistory(GalleryFilter filter, string? tag)
    {
        var (queue, vm) = Create();
        CaptureFolder folder = await queue.CreateFolderAsync("Folder");
        if (tag is not null) vm.ShowTag(tag);
        else vm.ShowView(filter);
        vm.NavigateTo(CaptureFolderScope.ForFolder(folder.Id));

        Assert.True(vm.NavigateParent());
        Assert.True(vm.IsAllItemsView);
        Assert.True(vm.NavigateBack());
        Assert.Equal(CaptureFolderScope.All, vm.SelectedScope);
        Assert.Equal(filter, vm.Filter);
        Assert.Equal(tag, vm.SelectedTag);
        Assert.True(vm.NavigateForward());
        Assert.True(vm.IsAllItemsView);
        Assert.True(vm.NavigateForward());
        Assert.Equal(folder.Id, vm.SelectedScope.FolderId);
    }

    [Fact]
    public async Task ParentConsumesHistoryEntryWhenTheWholeLocationMatches()
    {
        var (queue, vm) = Create();
        CaptureFolder folder = await queue.CreateFolderAsync("Folder");
        vm.ShowView(GalleryFilter.Images);
        vm.ShowView(GalleryFilter.All);
        vm.NavigateTo(CaptureFolderScope.ForFolder(folder.Id));
        Assert.True(vm.NavigateParent());
        Assert.True(vm.NavigateBack());
        Assert.True(vm.IsImagesView);
        Assert.True(vm.NavigateBack());
        Assert.True(vm.IsAllItemsView);
        Assert.False(vm.NavigateBack());
    }

    [Fact]
    public void DateParentReturnsToLibraryAndForwardRestoresDate()
    {
        var (_, vm) = Create();
        CaptureFolderScope date = CaptureFolderScope.ForDate(new DateOnly(2026, 10, 14));
        vm.NavigateTo(date);
        Assert.True(vm.NavigateParent());
        Assert.True(vm.IsAllItemsView);
        Assert.True(vm.NavigateForward());
        Assert.Equal(date, vm.SelectedScope);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationKeepsLocationAndDestinationAndSelectsNewFolder(bool nested)
    {
        var (queue, vm) = Create();
        CaptureFolder parent = await queue.CreateFolderAsync("Parent");
        CaptureFolderScope location = nested ? CaptureFolderScope.ForFolder(parent.Id) : CaptureFolderScope.All;
        vm.NavigateTo(location);
        vm.SetLibraryOpen(true);
        Guid? destination = vm.NewCaptureFolderId;
        CaptureFolder created = await vm.CreateFolderAsync("New folder", location);

        Assert.Equal(location, vm.SelectedScope);
        Assert.Equal(destination, vm.NewCaptureFolderId);
        Assert.Equal(nested ? parent.Id : (Guid?)null, created.ParentId);
        GalleryFolderNode node = nested
            ? Assert.Single(Assert.Single(vm.FolderRoots).Children)
            : Assert.Single(vm.FolderRoots, item => item.FolderId == created.Id);
        Assert.True(node.IsSelected);
        Assert.True(vm.FindFolderCard(created.Id)!.IsSelected);
        Assert.Contains(vm.Rows.OfType<GalleryFolderRow>().SelectMany(row => row.Folders), card => card.FolderId == created.Id);
        vm.Refresh();
        Assert.True(node.IsSelected);
        Assert.Equal(location, vm.SelectedScope);
        vm.NavigateTo(node.Scope);
        Assert.Null(vm.RevealedFolderId);
        Assert.False(vm.FindFolderCard(created.Id)?.IsSelected ?? false);
    }

    [Fact]
    public async Task CreationPreservesSearchAndMediaFilterWithoutAddingHistory()
    {
        var (_, vm) = Create();
        vm.ShowView(GalleryFilter.Images);
        vm.SearchQuery = "needle";
        await vm.CreateFolderAsync("New folder", vm.SelectedScope);
        Assert.True(vm.IsImagesView);
        Assert.Equal("needle", vm.SearchQuery);
        Assert.True(vm.NavigateBack());
        Assert.True(vm.IsAllItemsView);
        Assert.False(vm.CanNavigateBack);
    }

    [Fact]
    public void CompareOnlyAppearsForExactlyTwoImagesAndPublishesChanges()
    {
        var (queue, vm) = Create();
        var first = new CaptureRecord();
        var second = new CaptureRecord();
        var third = new CaptureRecord();
        var video = new CaptureRecord { MediaKind = CaptureMediaKind.Video };
        foreach (CaptureRecord record in new[] { first, second, third, video }) queue.Add(record);
        vm.Refresh();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.False(vm.CanCompareSelected);
        vm.Select(first.Id);
        Assert.False(vm.CanCompareSelected);
        Assert.True(vm.CanReduceSelected);
        vm.Select(second.Id, control: true);
        Assert.True(vm.CanCompareSelected);
        Assert.False(vm.CanReduceSelected);
        vm.Select(third.Id, control: true);
        Assert.False(vm.CanCompareSelected);
        vm.Select(first.Id);
        vm.Select(video.Id, control: true);
        Assert.False(vm.CanCompareSelected);
        vm.Select(video.Id);
        Assert.True(vm.CanGifSelected);
        vm.ClearSelection();
        Assert.False(vm.CanCompareSelected);
        Assert.Contains(nameof(vm.CanCompareSelected), changes);
    }

    private static (CaptureQueue Queue, GalleryViewModel ViewModel) Create()
    {
        string root = Path.Combine(Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT")
            ?? Path.GetTempPath(), "library-nav-" + Guid.NewGuid().ToString("N"));
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var vm = new GalleryViewModel(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        // Retain the isolated fixture for review; no user profile or cleanup is involved.
        return (queue, vm);
    }
}
