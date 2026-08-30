using System.Collections.ObjectModel;

namespace MyCapture.App.Gallery;

/// <summary>
/// One realised row in the gallery's flat, virtualized list: either a date header or a strip
/// of 2-4 capture tiles.
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
}

/// <summary>A date-group heading row, shown once above that day's tile rows.</summary>
public sealed class GalleryHeaderRow : GalleryRow
{
    public GalleryHeaderRow(string heading) => Heading = heading;

    /// <summary>The Korean-friendly heading ("오늘", "어제", or a localised date).</summary>
    public string Heading { get; }
}

/// <summary>
/// A strip of 2-4 tiles from one date group. Because a row holds only a handful of items, its
/// panel can be a non-virtual horizontal stack without cost — the virtualization that matters
/// happens at the row level, in the outer list.
/// </summary>
public sealed class GalleryTileRow : GalleryRow
{
    public GalleryTileRow(IReadOnlyList<GalleryItemViewModel> tiles) =>
        Tiles = new ReadOnlyObservableCollection<GalleryItemViewModel>(
            new ObservableCollection<GalleryItemViewModel>(tiles));

    /// <summary>The 2-4 tiles laid out left-to-right in this row.</summary>
    public ReadOnlyObservableCollection<GalleryItemViewModel> Tiles { get; }
}
