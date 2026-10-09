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
    public CaptureFolderScope SelectedScope => _scope;
    public bool HasFolderScope => _scope.FolderId is not null;
    public bool CanSetNewCaptureFolder => _scope.FolderId is { } id && _controller.Queue.FindFolder(id) is not null;
    public bool CanChangeNewCaptureFolder => CanSetNewCaptureFolder || UseCurrentFolderForNewCaptures;
    public bool UseCurrentFolderForNewCaptures => _controller.Queue.UseCurrentFolderForNewCaptures;
    public string NewCaptureDestinationText => UiText.Format("Folder410.DestinationHeader",
        _controller.Queue.UseCurrentFolderForNewCaptures && _controller.Queue.DefaultCaptureFolderId is { } id
            && _controller.Queue.FindFolder(id) is not null
            ? _controller.Queue.GetFolderDisplayPath(id) : UiText.Get("Folder410.DefaultDestination"));
    public string SearchPlaceholder => _scope.FolderId is { } id
        ? UiText.Format("Folder410.SearchInFolder", _controller.Queue.GetFolderDisplayPath(id))
        : UiText.Get("Text_59208AC2B48E");

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
        if (scope == _scope) return;
        _scope = scope;
        Refresh();
    }

    private void RefreshFolderTree()
    {
        CaptureQueue queue = _controller.Queue;
        if (_scope.FolderId is { } selected && queue.FindFolder(selected) is null) _scope = CaptureFolderScope.All;
        var previous = Flatten(FolderNodes).ToDictionary(node => node.Scope);
        GalleryFolderNode Node(CaptureFolderScope scope, string name)
        {
            if (!previous.TryGetValue(scope, out var node)) node = new GalleryFolderNode(scope);
            node.Name = name; node.IsSelected = scope == _scope;
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
        foreach (DateOnly date in dates)
        {
            var node = Node(CaptureFolderScope.ForDate(date), date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            GalleryFolderNode[] nested = roots[date].OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(BuildFolder).ToArray();
            Replace(node.Children, nested);
            node.Count = loose.GetValueOrDefault(date) + nested.Sum(child => child.Count);
            nodes.Add(node);
        }
        Replace(FolderNodes, nodes);
        Raise(nameof(FolderStorageText)); Raise(nameof(ShowFolderStorageInfo));
        Raise(nameof(SelectedScope)); Raise(nameof(HasFolderScope)); Raise(nameof(CanSetNewCaptureFolder));
        Raise(nameof(UseCurrentFolderForNewCaptures)); Raise(nameof(CanChangeNewCaptureFolder)); Raise(nameof(NewCaptureDestinationText)); Raise(nameof(SearchPlaceholder));
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
