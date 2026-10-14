using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

public sealed record GalleryTagItem(string Name, int Count, bool IsSelected)
{
    public string AccessibleName => UiText.Format("LibraryUx_NavItemName", Name, Count);
}

public sealed partial class GalleryViewModel
{
    private string? _selectedTag;
    private bool _showAllTags;
    private IReadOnlyList<GalleryTagItem> _tags = [];

    public string? SelectedTag => _selectedTag;
    public IReadOnlyList<GalleryTagItem> VisibleTags => _showAllTags ? _tags : _tags.Take(8).ToArray();
    public bool HasMoreTags => !_showAllTags && _tags.Count > 8;

    private bool MatchesTagFilter(CaptureRecord record) => _selectedTag is null
        || CaptureOrganization.SplitTags(record.Tags).Contains(_selectedTag, StringComparer.OrdinalIgnoreCase);

    public void ShowTag(string tag)
    {
        if (IsRefreshingFolderTree) return;
        ChangeLocation(new(CaptureFolderScope.All, GalleryFilter.All,
            string.Equals(_selectedTag, tag, StringComparison.OrdinalIgnoreCase) ? null : tag));
    }

    public void ShowAllTags()
    {
        _showAllTags = true;
        Raise(nameof(VisibleTags)); Raise(nameof(HasMoreTags));
    }

    private void RefreshTags()
    {
        var counts = CaptureOrganization.CountTags(_controller.Records.Select(record => record.Tags)).ToList();
        // Keep an emptied active filter available to clear after the last matching capture is edited.
        if (_selectedTag is not null && !counts.Any(tag => string.Equals(tag.Name, _selectedTag, StringComparison.OrdinalIgnoreCase)))
            counts.Add(new CaptureTagCount(_selectedTag, 0));
        _tags = counts
            .Select(tag => new GalleryTagItem(tag.Name, tag.Count,
                string.Equals(tag.Name, _selectedTag, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (_tags.Skip(8).Any(tag => tag.IsSelected)) _showAllTags = true;
        Raise(nameof(SelectedTag)); Raise(nameof(VisibleTags)); Raise(nameof(HasMoreTags));
    }
}
