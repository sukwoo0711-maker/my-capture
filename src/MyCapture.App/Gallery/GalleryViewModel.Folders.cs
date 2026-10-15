using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

public enum GallerySortOrder { Newest, Oldest, Name }

public sealed class GalleryFolderNode : INotifyPropertyChanged
{
    internal GalleryFolderNode(CaptureFolderScope scope) { Scope = scope; }
    public CaptureFolderScope Scope { get; }
    public Guid? FolderId => Scope.FolderId;
    public bool IsFolder => FolderId is not null;
    public ObservableCollection<GalleryFolderNode> Children { get; } = [];
    private string _name = string.Empty;
    public string Name { get => _name; internal set { if (_name != value) { _name = value; Raise(); Raise(nameof(Caption)); } } }
    private int _count;
    public int Count { get => _count; internal set { if (_count != value) { _count = value; Raise(); Raise(nameof(Caption)); } } }
    public string DeleteLabel => UiText.Get(IsFolder ? "Folder410.Delete" : "Folder410.DeleteDate");
    public string Caption => UiText.Format("Folder410.Count", Name, Count);
    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded != value) { _isExpanded = value; Raise(); } } }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; Raise(); } } }
    private bool _isDropTarget;
    /// <summary>True while captures dragged over this folder or date would move into it.</summary>
    public bool IsDropTarget { get => _isDropTarget; internal set { if (_isDropTarget != value) { _isDropTarget = value; Raise(); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed partial class GalleryViewModel
{
    public string FolderStorageText => UiText.Format("Folder410.Storage", _controller.Queue.Folders.Count,
        FormatBytes(_controller.Queue.FolderBytes));
    public bool ShowFolderStorageInfo => _controller.Queue.IsFolderStorageLarge;
    public ObservableCollection<GalleryFolderNode> FolderNodes { get; } = [];
    private CaptureFolderScope _scope;
    private Guid? _revealedFolderId;
    internal Guid? RevealedFolderId => _revealedFolderId;
    public CaptureFolderScope SelectedScope => _scope;
    public bool HasFolderScope => _scope.FolderId is not null;
    public bool CanSetNewCaptureFolder => _scope.FolderId is { } id && _controller.Queue.FindFolder(id) is not null;
    public bool CanChangeNewCaptureFolder => CanSetNewCaptureFolder || SavesNewCapturesToFolder;

    /// <summary>The saved "keep after closing the library" option.</summary>
    public bool UseCurrentFolderForNewCaptures => _controller.Queue.UseCurrentFolderForNewCaptures;

    /// <summary>The folder the next capture joins, or <see langword="null"/> for the date library.</summary>
    public Guid? NewCaptureFolderId => _controller.Queue.ResolveNewCaptureFolderId();
    public bool SavesNewCapturesToFolder => NewCaptureFolderId is not null;
    public string NewCaptureDestinationText => UiText.Format("Folder410.DestinationHeader",
        NewCaptureFolderId is { } id ? _controller.Queue.GetFolderDisplayPath(id) : UiText.Get("Folder410.DefaultDestination"));

    private bool _libraryOpen;
    private bool _folderCaptureOptOut;
    private Guid? _publishedBrowsing;
    private readonly Dictionary<Guid, GalleryFolderCardViewModel> _folderCards = [];

    /// <summary>
    /// The library is open while its window is visible or hidden only for a capture it started.
    /// While open, new captures join the folder it shows. Closing it ends that session, including
    /// a session-only opt-out from the destination check box.
    /// </summary>
    internal void SetLibraryOpen(bool open)
    {
        if (_libraryOpen == open) return;
        _libraryOpen = open;
        if (!open) _folderCaptureOptOut = false;
        SyncBrowsingCaptureFolder();
        RaiseNewCaptureDestination();
    }

    /// <summary>
    /// Applies the destination check box. Clearing it saves to the date library for the rest of
    /// this library session and also turns off the saved keep-after-close folder. Checking it
    /// again uses the shown folder for this session only.
    /// </summary>
    internal async Task SetFolderCaptureEnabledAsync(bool enabled)
    {
        _folderCaptureOptOut = !enabled;
        SyncBrowsingCaptureFolder();
        try
        {
            if (!enabled && _controller.Queue.UseCurrentFolderForNewCaptures)
                await _controller.Queue.SetNewCaptureFolderAsync(null, false);
        }
        finally { Refresh(); }
    }

    internal GalleryFolderCardViewModel? FindFolderCard(Guid id) =>
        _folderCards.TryGetValue(id, out GalleryFolderCardViewModel? card) ? card : null;

    private void SyncBrowsingCaptureFolder()
    {
        CaptureQueue queue = _controller.Queue;
        Guid? browsing = _libraryOpen && !_folderCaptureOptOut && _scope.FolderId is { } id
            && queue.FindFolder(id) is not null ? id : null;
        if (browsing == _publishedBrowsing) return;
        _publishedBrowsing = browsing;
        queue.SetBrowsingCaptureFolder(browsing);
    }

    private void RaiseNewCaptureDestination()
    {
        Raise(nameof(UseCurrentFolderForNewCaptures)); Raise(nameof(SavesNewCapturesToFolder));
        Raise(nameof(CanChangeNewCaptureFolder)); Raise(nameof(NewCaptureDestinationText));
        Raise(nameof(EmptyStateText)); Raise(nameof(SavesNewCapturesHere)); Raise(nameof(SaveHereToolTip));
        Raise(nameof(ShowNewCaptureDestination));
    }

    private GalleryFolderCardViewModel GetOrUpdateFolderCard(CaptureFolder folder, FolderCardIndex index,
        HashSet<Guid> seen)
    {
        if (!_folderCards.TryGetValue(folder.Id, out GalleryFolderCardViewModel? card))
            _folderCards[folder.Id] = card = new GalleryFolderCardViewModel(folder.Id);
        seen.Add(folder.Id);
        (int captures, CaptureRecord? newest) = index.Subtree(folder.Id);
        card.Update(folder.Name, captures, index.ChildCount(folder.Id), newest is null ? null : GetOrCreateTile(newest));
        card.IsSelected = folder.Id == _revealedFolderId;
        return card;
    }

    /// <summary>One pass over the queue for every folder card in a refresh.</summary>
    private sealed class FolderCardIndex
    {
        private readonly ILookup<Guid?, CaptureFolder> _children;
        private readonly Dictionary<Guid, (int Count, CaptureRecord Newest)> _direct = [];
        private readonly Dictionary<Guid, (int Count, CaptureRecord? Newest)> _subtrees = [];

        internal FolderCardIndex(CaptureQueue queue)
        {
            _children = queue.Folders.ToLookup(folder => folder.ParentId);
            foreach (CaptureRecord record in queue.Records)
            {
                if (record.FolderId is not { } id) continue;
                _direct[id] = _direct.TryGetValue(id, out var current)
                    ? (current.Count + 1, Newer(record, current.Newest)!) : (1, record);
            }
        }

        internal int ChildCount(Guid id) => _children[id].Count();

        internal (int Count, CaptureRecord? Newest) Subtree(Guid id) => Subtree(id, 1);

        private (int Count, CaptureRecord? Newest) Subtree(Guid id, int depth)
        {
            if (_subtrees.TryGetValue(id, out var cached)) return cached;
            int count = 0;
            CaptureRecord? newest = null;
            if (_direct.TryGetValue(id, out var direct)) (count, newest) = (direct.Count, direct.Newest);
            // Persisted trees are validated, but never let a damaged parent chain recurse forever.
            if (depth < CaptureFolderNaming.MaximumDepth)
            {
                foreach (CaptureFolder child in _children[id])
                {
                    (int childCount, CaptureRecord? childNewest) = Subtree(child.Id, depth + 1);
                    count += childCount;
                    newest = Newer(childNewest, newest);
                }
            }
            return _subtrees[id] = (count, newest);
        }

        // Same order as the newest-first grid: later first, then the smaller id.
        private static CaptureRecord? Newer(CaptureRecord? first, CaptureRecord? second) =>
            first is null ? second : second is null ? first
            : first.CreatedAt > second.CreatedAt || (first.CreatedAt == second.CreatedAt && first.Id.CompareTo(second.Id) < 0)
                ? first : second;
    }
    public string SearchPlaceholder => _scope.FolderId is { } id
        ? UiText.Format("Folder410.SearchInFolder", _controller.Queue.GetFolderDisplayPath(id))
        : UiText.Get("LibraryUx_SearchPlaceholder");

    /// <summary>The search shortcut lives in the tooltip, not in the placeholder.</summary>
    public string SearchToolTip => UiText.Format("LibraryUx_CommandShortcut", UiText.Get("Library.Search.Syntax"), "Ctrl+F");

    private GallerySortOrder _sortOrder;
    public int SortIndex
    {
        get => (int)_sortOrder;
        set
        {
            if (value is < 0 or > 2 || value == (int)_sortOrder) return;
            _sortOrder = (GallerySortOrder)value;
            Raise(); Refresh();
        }
    }

    public void SelectScope(CaptureFolderScope scope)
    {
        ChangeLocation(new(scope, _filter, _selectedTag));
    }

    internal async Task<CaptureFolder> CreateFolderAsync(string name, CaptureFolderScope scope)
    {
        CaptureFolder folder = await _controller.Queue.CreateFolderInScopeAsync(name, scope);
        _revealedFolderId = folder.Id;
        Refresh();
        foreach (CaptureFolder ancestor in _controller.Queue.GetFolderPath(folder.Id))
            if (Flatten(FolderRoots).FirstOrDefault(node => node.FolderId == ancestor.Id) is { } node)
                node.IsExpanded = true;
        return folder;
    }

    private void RefreshFolderTree()
    {
        CaptureQueue queue = _controller.Queue;
        if (_revealedFolderId is { } revealed && queue.FindFolder(revealed) is null) _revealedFolderId = null;
        if (_scope.FolderId is { } selected && queue.FindFolder(selected) is null) _scope = CaptureFolderScope.All;
        var previous = Flatten(FolderNodes).ToDictionary(node => node.Scope);
        GalleryFolderNode Node(CaptureFolderScope scope, string name)
        {
            if (!previous.TryGetValue(scope, out var node)) node = new GalleryFolderNode(scope);
            node.Name = name;
            node.IsSelected = _revealedFolderId is { } id ? scope.FolderId == id : scope == _scope;
            return node;
        }
        var children = queue.Folders.ToLookup(folder => folder.ParentId);
        var counts = queue.Records.Where(record => record.FolderId is not null)
            .GroupBy(record => record.FolderId!.Value).ToDictionary(group => group.Key, group => group.Count());
        GalleryFolderNode BuildFolder(CaptureFolder folder)
        {
            var node = Node(CaptureFolderScope.ForFolder(folder.Id), folder.Name);
            GalleryFolderNode[] nested = children[folder.Id].OrderBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(BuildFolder).ToArray();
            Replace(node.Children, nested);
            node.Count = counts.GetValueOrDefault(folder.Id) + nested.Sum(child => child.Count);
            return node;
        }
        var loose = queue.Records.Where(record => record.FolderId is null)
            .GroupBy(record => DateOnly.FromDateTime(record.CreatedAt.DateTime))
            .ToDictionary(group => group.Key, group => group.Count());
        var roots = children[null].ToLookup(folder => folder.CreatedDate);
        var dates = loose.Keys.Concat(roots.Select(group => group.Key)).Distinct().OrderDescending();
        var nodes = new List<GalleryFolderNode>();
        var rootFolders = new List<(GalleryFolderNode Node, DateOnly Date)>();
        foreach (DateOnly date in dates)
        {
            var node = Node(CaptureFolderScope.ForDate(date), date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            GalleryFolderNode[] nested = roots[date].OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(BuildFolder).ToArray();
            Replace(node.Children, nested);
            node.Count = loose.GetValueOrDefault(date) + nested.Sum(child => child.Count);
            nodes.Add(node);
            rootFolders.AddRange(nested.Select(folder => (folder, date)));
        }
        Replace(FolderNodes, nodes);
        // The navigation pane lists folders on their own, by name across dates; newer first on a tie.
        Replace(FolderRoots, rootFolders.OrderBy(root => root.Node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(root => root.Date).Select(root => root.Node));
        foreach (GalleryFolderCardViewModel card in _folderCards.Values)
            card.IsSelected = card.FolderId == _revealedFolderId;
        SyncBrowsingCaptureFolder();
        Raise(nameof(FolderStorageText)); Raise(nameof(ShowFolderStorageInfo));
        Raise(nameof(SelectedScope)); Raise(nameof(HasFolderScope)); Raise(nameof(CanSetNewCaptureFolder));
        RaiseNewCaptureDestination(); Raise(nameof(SearchPlaceholder));
    }

    private static IEnumerable<GalleryFolderNode> Flatten(IEnumerable<GalleryFolderNode> nodes)
    {
        foreach (GalleryFolderNode node in nodes)
        {
            yield return node;
            foreach (GalleryFolderNode child in Flatten(node.Children)) yield return child;
        }
    }

    private static void Replace(ObservableCollection<GalleryFolderNode> target, IEnumerable<GalleryFolderNode> values)
    {
        GalleryFolderNode[] next = values.ToArray();
        if (target.SequenceEqual(next)) return;
        target.Clear();
        foreach (GalleryFolderNode node in next) target.Add(node);
    }
}
