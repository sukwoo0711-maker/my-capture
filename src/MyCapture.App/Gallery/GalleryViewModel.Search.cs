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
                : UiText.Format("Library.Search.Condition", SearchFieldLabel(token.Field), token.Value);
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

    private static string SearchFieldLabel(CaptureSearchField field) => field switch
    {
        CaptureSearchField.App => UiText.Get("Library.Search.Field.App"),
        CaptureSearchField.Title => UiText.Get("Library.Search.Field.Title"),
        CaptureSearchField.Tag => UiText.Get("Library.Search.Field.Tag"),
        CaptureSearchField.Text => UiText.Get("Library.Search.Field.Text"),
        CaptureSearchField.Url => UiText.Get("Library.Search.Field.Url"),
        CaptureSearchField.Host => UiText.Get("Library.Search.Field.Host"),
        CaptureSearchField.Folder => UiText.Get("Library.Search.Field.Folder"),
        CaptureSearchField.Type => UiText.Get("Library.Search.Field.Type"),
        CaptureSearchField.Pinned => UiText.Get("Library.Search.Field.Pinned"),
        CaptureSearchField.Date => UiText.Get("Library.Search.Field.Date"),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}

internal static class GallerySearchMatchText
{
    internal static string Describe(CaptureMatchField fields)
    {
        string[] names = Enum.GetValues<CaptureMatchField>()
            .Where(field => field != CaptureMatchField.None && (fields & field) != 0)
            .Select(FieldLabel).ToArray();
        return names.Length == 0 ? UiText.Get("Library.Search.ExclusionsMatched")
            : UiText.Format("Library.Search.Matched", string.Join(UiText.Get("Library.Search.FieldSeparator"), names));
    }

    private static string FieldLabel(CaptureMatchField field) => field switch
    {
        CaptureMatchField.Title => UiText.Get("Library.Search.MatchField.Title"),
        CaptureMatchField.WindowTitle => UiText.Get("Library.Search.MatchField.WindowTitle"),
        CaptureMatchField.OcrText => UiText.Get("Library.Search.MatchField.OcrText"),
        CaptureMatchField.MediaType => UiText.Get("Library.Search.MatchField.MediaType"),
        CaptureMatchField.Tags => UiText.Get("Library.Search.MatchField.Tags"),
        CaptureMatchField.SourcePageUrl => UiText.Get("Library.Search.MatchField.SourcePageUrl"),
        CaptureMatchField.SourceApp => UiText.Get("Library.Search.MatchField.SourceApp"),
        CaptureMatchField.SourceDocumentPath => UiText.Get("Library.Search.MatchField.SourceDocumentPath"),
        CaptureMatchField.Folder => UiText.Get("Library.Search.MatchField.Folder"),
        CaptureMatchField.Pinned => UiText.Get("Library.Search.MatchField.Pinned"),
        CaptureMatchField.Date => UiText.Get("Library.Search.MatchField.Date"),
        CaptureMatchField.ExportFileName => UiText.Get("Library.Search.MatchField.ExportFileName"),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };
}
