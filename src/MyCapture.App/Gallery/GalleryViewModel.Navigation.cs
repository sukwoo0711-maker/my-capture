using System.Collections.ObjectModel;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>One step of the location shown above an opened folder or date, for example "모든 항목 › 제품리뷰".</summary>
public sealed record GalleryBreadcrumb(string Name, CaptureFolderScope Scope, bool IsCurrent)
{
    /// <summary>Every step except the current one opens its scope.</summary>
    public bool IsLink => !IsCurrent;
}

/// <summary>
/// Navigation pane, location header, status line and drag state of the library. Like a Windows
/// navigation view, the views (all items, images, videos, kept), folders and dates share one
/// selection: choosing a folder or date shows all of its items, and choosing a view returns to
/// the whole library.
/// </summary>
public sealed partial class GalleryViewModel
{
    private IReadOnlyList<GalleryBreadcrumb> _breadcrumb = [];
    private bool _isAllItemsDropTarget;

    /// <summary>Root folders of every date, in the tree's name order. Subfolders hang off each node.</summary>
    public ObservableCollection<GalleryFolderNode> FolderRoots { get; } = [];

    public int AllItemsCount => _controller.Count;
    public int ImageCount => _controller.Records.Count(record => record.IsImage);
    public int VideoCount => _controller.Records.Count(record => record.IsVideo);
    public int KeptCount => _controller.Records.Count(record => record.IsManuallyPinned);

    public bool IsAllItemsView => _scope.IsAll && _filter == GalleryFilter.All;
    public bool IsImagesView => _scope.IsAll && _filter == GalleryFilter.Images;
    public bool IsVideosView => _scope.IsAll && _filter == GalleryFilter.Videos;
    public bool IsKeptView => _scope.IsAll && _filter == GalleryFilter.Pinned;

    public string AllItemsAccessibleName => NavName("LibraryUx_NavAll", AllItemsCount);
    public string ImagesAccessibleName => NavName("Text_302BAE127938", ImageCount);
    public string VideosAccessibleName => NavName("Text_C11734267E6B", VideoCount);
    public string KeptAccessibleName => NavName("LibraryUx_NavKept", KeptCount);

    /// <summary>Retention rule and folder storage, shown as the folder section's tooltip instead of body text.</summary>
    public string FolderSectionToolTip => UiText.Get("LibraryUx_FoldersHint") + Environment.NewLine + FolderStorageText;

    /// <summary>The location header is shown inside a folder or a date and offers the way back.</summary>
    public bool ShowLocationHeader => !_scope.IsAll;

    public IReadOnlyList<GalleryBreadcrumb> Breadcrumb => _breadcrumb;

    /// <summary>The save-here chip belongs to an opened folder only.</summary>
    public bool ShowSaveHere => HasFolderScope;

    /// <summary>True while new captures join the folder that is open.</summary>
    public bool SavesNewCapturesHere => _scope.FolderId is { } id && NewCaptureFolderId == id;

    public string SaveHereToolTip => _scope.FolderId is { } id && _controller.Queue.FindFolder(id) is not null
        ? UiText.Format(SavesNewCapturesHere ? "LibraryUx_SaveHereOn" : "LibraryUx_SaveHereOff",
            _controller.Queue.GetFolderDisplayPath(id))
        : string.Empty;

    /// <summary>
    /// A saved "keep after closing" folder still receives captures while another view is open;
    /// the status line names it because no folder header shows it then.
    /// </summary>
    public bool ShowNewCaptureDestination => NewCaptureFolderId is { } id && id != _scope.FolderId;

    /// <summary>"전체 71개 중 12개" only when a search, view or location hides part of the library.</summary>
    public bool ShowResultCount => VisibleCount != _controller.Count;

    /// <summary>Highlights "모든 항목" while captures dragged over it would leave their folder.</summary>
    public bool IsAllItemsDropTarget
    {
        get => _isAllItemsDropTarget;
        internal set { if (_isAllItemsDropTarget != value) { _isAllItemsDropTarget = value; Raise(); } }
    }

    /// <summary>Shows one of the navigation pane's views over the whole library.</summary>
    public void ShowView(GalleryFilter filter)
    {
        if (IsRefreshingFolderTree || (_scope.IsAll && _filter == filter)) return;
        bool filterChanged = _filter != filter;
        _filter = filter;
        if (filterChanged) Raise(nameof(Filter));
        if (!_scope.IsAll) SelectScope(CaptureFolderScope.All);
        else Refresh();
    }

    /// <summary>Opens a folder or date with all of its items; the media views apply to the whole library.</summary>
    public void NavigateTo(CaptureFolderScope scope)
    {
        if (IsRefreshingFolderTree) return;
        if (!scope.IsAll && _filter != GalleryFilter.All)
        {
            _filter = GalleryFilter.All;
            Raise(nameof(Filter));
            if (scope == _scope) { Refresh(); return; }
        }
        SelectScope(scope);
    }

    public void ClearSelection()
    {
        if (Selection.SelectedIds.Count == 0) return;
        Selection.Clear();
        PublishSelection();
    }

    /// <summary>Marks the folder cards a capture drag could move into, so they stand out while dragging.</summary>
    internal void BeginCaptureDrag(IReadOnlyCollection<Guid> captureIds)
    {
        CaptureQueue queue = _controller.Queue;
        Guid?[] sources = captureIds.Select(id => queue.Find(id)?.FolderId).ToArray();
        foreach (GalleryFolderCardViewModel card in _folderCards.Values)
            card.IsDropAvailable = sources.Any(source => source != card.FolderId);
    }

    internal void EndCaptureDrag()
    {
        foreach (GalleryFolderCardViewModel card in _folderCards.Values)
        {
            card.IsDropAvailable = false;
            card.IsDropTarget = false;
        }
        foreach (GalleryFolderNode node in Flatten(FolderNodes)) node.IsDropTarget = false;
        IsAllItemsDropTarget = false;
    }

    private static string NavName(string labelKey, int count) =>
        UiText.Format("LibraryUx_NavItemName", UiText.Get(labelKey), count);

    private IReadOnlyList<GalleryBreadcrumb> BuildBreadcrumb()
    {
        if (_scope.IsAll) return [];
        var steps = new List<GalleryBreadcrumb> { new(UiText.Get("LibraryUx_NavAll"), CaptureFolderScope.All, false) };
        if (_scope.FolderId is { } id)
        {
            IReadOnlyList<CaptureFolder> path = _controller.Queue.GetFolderPath(id);
            steps.AddRange(path.Select(folder => new GalleryBreadcrumb(folder.Name, CaptureFolderScope.ForFolder(folder.Id),
                folder.Id == id)));
        }
        else if (_scope.Date is { } date)
        {
            steps.Add(new GalleryBreadcrumb(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                _scope, true));
        }
        return steps;
    }

    private void RefreshNavigation()
    {
        IReadOnlyList<GalleryBreadcrumb> breadcrumb = BuildBreadcrumb();
        if (!breadcrumb.SequenceEqual(_breadcrumb))
        {
            _breadcrumb = breadcrumb;
            Raise(nameof(Breadcrumb));
        }
        foreach (string name in NavigationProperties) Raise(name);
    }

    private static readonly string[] NavigationProperties =
    [
        nameof(AllItemsCount), nameof(ImageCount), nameof(VideoCount), nameof(KeptCount),
        nameof(IsAllItemsView), nameof(IsImagesView), nameof(IsVideosView), nameof(IsKeptView),
        nameof(AllItemsAccessibleName), nameof(ImagesAccessibleName), nameof(VideosAccessibleName),
        nameof(KeptAccessibleName), nameof(FolderSectionToolTip), nameof(ShowLocationHeader), nameof(ShowSaveHere),
        nameof(SavesNewCapturesHere), nameof(SaveHereToolTip), nameof(ShowNewCaptureDestination), nameof(ShowResultCount),
    ];
}
