using System.Collections.ObjectModel;

namespace MyCapture.App.Gallery;

/// <summary>
/// One realised row in the gallery's flat, virtualized list: either a date header or a strip
/// of capture tiles, as many as the gallery width fits (see <see cref="GalleryRowBuilder"/>).
/// </summary>
/// <remarks>
/// <para>
/// The gallery used to nest a per-group <c>ListBox</c> inside a <c>WrapPanel</c> inside an
/// outer <c>ScrollViewer</c>. That arrangement makes every <c>VirtualizingStackPanel</c>
/// setting inert — the outer scroller measures each inner list at its full content height, so
/// all tiles realise and every thumbnail decodes eagerly. Flattening the groups into a single
/// sequence of header/tile rows lets one outer <see cref="System.Windows.Controls.ListBox"/>
/// virtualize the rows for real: only the rows in view are materialised, so an off-screen
/// tile never touches <see cref="GalleryItemViewModel.Thumbnail"/> and never decodes.
/// </para>
/// <para>
/// This is a plain marker base type so the row list is heterogeneous and the XAML can pick a
/// header vs. tile template with a data-type trigger. It carries no WPF type, so row
/// splitting stays unit-testable without a window.
/// </para>
/// </remarks>
public abstract class GalleryRow
{
    /// <summary>
    /// True for the smaller cards shown inside an opened folder. Header rows are never compact;
    /// the flag lives on the base type so the row container can bind it on every row.
    /// </summary>
    public bool IsCompact { get; protected init; }
}

/// <summary>A date-group heading row, shown once above that day's tile rows.</summary>
public sealed class GalleryHeaderRow : GalleryRow
{
    public GalleryHeaderRow(string heading) => Heading = heading;

    /// <summary>The Korean-friendly heading ("오늘", "어제", or a localised date).</summary>
    public string Heading { get; }
}

/// <summary>
/// A strip of tiles from one date group, one up to the column count. Because a row holds only a
/// handful of items, its panel can be a non-virtual horizontal stack without cost — the virtualization that matters
/// happens at the row level, in the outer list.
/// </summary>
public sealed class GalleryTileRow : GalleryRow
{
    public GalleryTileRow(IReadOnlyList<GalleryItemViewModel> tiles, bool compact = false)
    {
        Tiles = new ReadOnlyObservableCollection<GalleryItemViewModel>(
            new ObservableCollection<GalleryItemViewModel>(tiles));
        IsCompact = compact;
    }

    /// <summary>The tiles laid out left-to-right in this row.</summary>
    public ReadOnlyObservableCollection<GalleryItemViewModel> Tiles { get; }
}

/// <summary>
/// A strip of folder cards shown under a group heading and above that group's tiles. Cards use
/// the tile track width, so folder and tile rows share one column grid for keyboard movement.
/// </summary>
public sealed class GalleryFolderRow : GalleryRow
{
    public GalleryFolderRow(IReadOnlyList<GalleryFolderCardViewModel> folders, bool compact = false)
    {
        Folders = new ReadOnlyObservableCollection<GalleryFolderCardViewModel>(
            new ObservableCollection<GalleryFolderCardViewModel>(folders));
        IsCompact = compact;
    }

    /// <summary>The folder cards laid out left-to-right in this row.</summary>
    public ReadOnlyObservableCollection<GalleryFolderCardViewModel> Folders { get; }
}
