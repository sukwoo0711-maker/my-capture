using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>
/// The gallery's presentation state: grouped, filtered tiles plus the summary line.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter over <see cref="GalleryController"/>. It owns the observable collections
/// the WPF grid binds to and the derived text (count, storage, empty-state), but every
/// mutation (pin, delete, refresh) is delegated to the controller so the queue rules stay in
/// one testable place. The view model itself carries no WPF type, so its grouping/search
/// behaviour is unit-testable without a window.
/// </para>
/// <para>
/// Tiles are cached by id across a rebuild so a re-decode is only paid once per capture and a
/// search that removes then restores an item keeps its already-decoded thumbnail.
/// </para>
/// </remarks>
public sealed class GalleryViewModel : INotifyPropertyChanged
{
    private readonly GalleryController _controller;
    private readonly Func<CaptureRecord, string> _thumbnailPathResolver;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _decodePixelWidth;
    private readonly Dictionary<Guid, GalleryItemViewModel> _tileCache = [];

    private string _searchQuery = string.Empty;
    private int _columnCount = GalleryRowBuilder.MinColumns;

    public GalleryViewModel(
        GalleryController controller,
        Func<CaptureRecord, string> thumbnailPathResolver,
        int decodePixelWidth,
        Func<DateTimeOffset>? clock = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _thumbnailPathResolver = thumbnailPathResolver ?? throw new ArgumentNullException(nameof(thumbnailPathResolver));
        _decodePixelWidth = Math.Max(1, decodePixelWidth);
        _clock = clock ?? (() => DateTimeOffset.Now);

        Groups = new ReadOnlyObservableCollection<GalleryGroupViewModel>(_groups);
        Rows = new ReadOnlyObservableCollection<GalleryRow>(_rows);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly ObservableCollection<GalleryGroupViewModel> _groups = [];
    private readonly ObservableCollection<GalleryRow> _rows = [];

    /// <summary>Grouped tiles, newest day first, newest tile first within a day.</summary>
    public ReadOnlyObservableCollection<GalleryGroupViewModel> Groups { get; }

    /// <summary>
    /// The flattened header/tile-row sequence the outer virtualizing list binds to. Rebuilt
    /// whenever the groups change (search, pin, delete, re-edit) or the column count changes.
    /// </summary>
    public ReadOnlyObservableCollection<GalleryRow> Rows { get; }

    /// <summary>Tiles per row, as last resolved from the gallery width.</summary>
    public int ColumnCount => _columnCount;

    /// <summary>Live search text; setting it re-filters and re-groups.</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            string next = value ?? string.Empty;
            if (_searchQuery == next)
            {
                return;
            }

            _searchQuery = next;
            Raise();
            Refresh();
        }
    }

    /// <summary>True when the filtered view has no tiles.</summary>
    public bool IsEmpty => _groups.Count == 0;

    /// <summary>True when the queue itself is empty (as opposed to a search with no hits).</summary>
    public bool QueueIsEmpty => _controller.Count == 0;

    /// <summary>Empty-state message, tailored to whether a search is active.</summary>
    public string EmptyStateText =>
        QueueIsEmpty
            ? "아직 이미지나 동영상이 없습니다. 캡처하거나 녹화하면 여기에 표시됩니다."
            : "검색 결과가 없습니다.";

    /// <summary>Count and storage summary, for example "캡처 12개 · 34.5 MB".</summary>
    public string SummaryText
    {
        get
        {
            string count = $"항목 {_controller.Count:N0}개";
            string storage = FormatBytes(_controller.TotalBytes);
            string pins = _controller.IsOverCapacityDueToPins
                ? " · 고정 항목 보호로 저장 한도를 일시 초과했습니다"
                : string.Empty;
            return $"{count} · {storage}{pins}";
        }
    }

    /// <summary>Rebuilds the grouped, filtered view from the current queue state.</summary>
    public void Refresh()
    {
        IReadOnlyList<GalleryGroupedRecords> grouped = _controller.BuildGroups(_searchQuery, _clock());

        var seenTiles = new HashSet<Guid>();

        _groups.Clear();
        foreach (GalleryGroupedRecords group in grouped)
        {
            var tiles = new List<GalleryItemViewModel>(group.Records.Count);
            foreach (CaptureRecord record in group.Records)
            {
                tiles.Add(GetOrCreateTile(record));
                seenTiles.Add(record.Id);
            }

            _groups.Add(new GalleryGroupViewModel(group.Group.Heading, tiles));
        }

        // Drop cached tiles for records that no longer exist so decoded bitmaps are released.
        foreach (Guid stale in _tileCache.Keys.Where(id => !seenTiles.Contains(id)).ToList())
        {
            // Keep a tile whose record still lives in the queue but is filtered out by search,
            // so restoring the search term does not re-decode it.
            if (_controller.Find(stale) is null)
            {
                _tileCache.Remove(stale);
            }
        }

        RebuildRows();

        Raise(nameof(IsEmpty));
        Raise(nameof(QueueIsEmpty));
        Raise(nameof(EmptyStateText));
        Raise(nameof(SummaryText));
    }

    /// <summary>
    /// Updates the tiles-per-row from the current gallery width and, only when that count
    /// actually changes, rebuilds the flat <see cref="Rows"/> list. Called on meaningful
    /// gallery width changes so a resize that does not cross a column breakpoint costs nothing.
    /// </summary>
    /// <param name="availableWidth">Content width available to the tile rows, in DIPs.</param>
    /// <returns><see langword="true"/> when the column count changed and rows were rebuilt.</returns>
    public bool SetColumnCountForWidth(double availableWidth)
    {
        int next = GalleryRowBuilder.ColumnCountForWidth(availableWidth);
        if (next == _columnCount)
        {
            return false;
        }

        _columnCount = next;
        Raise(nameof(ColumnCount));
        RebuildRows();
        return true;
    }

    private void RebuildRows()
    {
        IReadOnlyList<GalleryRow> built = GalleryRowBuilder.Build(_groups, _columnCount);

        _rows.Clear();
        foreach (GalleryRow row in built)
        {
            _rows.Add(row);
        }
    }

    public GalleryItemViewModel? FindTile(Guid id) =>
        _tileCache.TryGetValue(id, out GalleryItemViewModel? tile) ? tile : null;

    private GalleryItemViewModel GetOrCreateTile(CaptureRecord record)
    {
        if (_tileCache.TryGetValue(record.Id, out GalleryItemViewModel? existing))
        {
            return existing;
        }

        var tile = new GalleryItemViewModel(record, _thumbnailPathResolver, _decodePixelWidth);
        _tileCache[record.Id] = tile;
        return tile;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 MB";
        }

        double mb = bytes / 1024.0 / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:0.0} MB";
        }

        double gb = mb / 1024.0;
        return $"{gb:0.00} GB";
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A date group with its heading and tiles, for the grouped item control.</summary>
public sealed class GalleryGroupViewModel
{
    public GalleryGroupViewModel(string heading, IReadOnlyList<GalleryItemViewModel> items)
    {
        Heading = heading;
        Items = items;
    }

    public string Heading { get; }

    public IReadOnlyList<GalleryItemViewModel> Items { get; }
}
