using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

public sealed record GallerySearchCondition(int Index, string Label)
{
    public string RemoveToolTip => UiText.Format("Library.Search.RemoveCondition", Label);
}

public sealed partial class GalleryViewModel
{
    private CaptureSearchQuery _preparedQuery = CaptureSearchQuery.Parse(null);
    private int _searchCandidateCount;
    private bool _folderIsEmpty;

    public IReadOnlyList<GallerySearchCondition> SearchConditions { get; private set; } = [];
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public int SearchHiddenCount => Math.Max(0, _searchCandidateCount - VisibleCount);

    public void RemoveSearchCondition(int index) => SearchQuery = _preparedQuery.RemoveToken(index);

    private void PrepareSearch()
    {
        _preparedQuery = CaptureSearchQuery.Parse(SearchQuery);
        SearchConditions = _preparedQuery.Tokens.Select((token, index) =>
        {
            string label = token.Field == CaptureSearchField.Any ? token.Value
                : UiText.Format("Library.Search.Condition", UiText.Get("Library.Search.Field." + token.Field), token.Value);
            return new GallerySearchCondition(index, token.IsNegated
                ? UiText.Format("Library.Search.Excluded", label) : label);
        }).ToArray();
        Raise(nameof(SearchConditions));
        Raise(nameof(HasSearchQuery));
    }

    private bool MatchesMediaFilter(CaptureRecord record) => _filter switch
    {
        GalleryFilter.Images => record.IsImage,
        GalleryFilter.Videos => record.IsVideo,
        GalleryFilter.Pinned => record.IsManuallyPinned,
        _ => true,
    };
}

internal static class GallerySearchMatchText
{
    internal static string Describe(CaptureMatchField fields)
    {
        string[] names = Enum.GetValues<CaptureMatchField>()
            .Where(field => field != CaptureMatchField.None && (fields & field) != 0)
            .Select(field => UiText.Get("Library.Search.MatchField." + field)).ToArray();
        return names.Length == 0 ? UiText.Get("Library.Search.ExclusionsMatched")
            : UiText.Format("Library.Search.Matched", string.Join(UiText.Get("Library.Search.FieldSeparator"), names));
    }
}
