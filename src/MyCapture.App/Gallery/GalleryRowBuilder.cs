namespace MyCapture.App.Gallery;

/// <summary>
/// Pure rules for turning date-grouped tiles into a flat header/tile-row sequence and for
/// choosing how many tiles sit in a row at a given gallery width.
/// </summary>
/// <remarks>
/// <para>
/// Split out from the view model so both behaviours are unit-testable without a window: the
/// column count is a deterministic function of width, and row building is a deterministic
/// function of groups plus that column count. The gallery rebuilds rows only when the column
/// count actually changes (a "meaningful" width change), not on every pixel of resize.
/// </para>
/// <para>
/// A tile row holds at most <see cref="ColumnCountForWidth"/> tiles: one where two cards would
/// not fit (the minimum window with the details pane open) and up to 6 on very wide desktops.
/// Each date group emits one header row followed by however many tile rows its tiles fill; a
/// partial final row keeps its remaining tiles rather than padding, so selection and keyboard
/// order stay dense.
/// </para>
/// </remarks>
public static class GalleryRowBuilder
{
    /// <summary>The horizontal track of a full card, used to derive the column count from width.</summary>
    /// <remarks>
    /// A tile is 244 wide with a 6-DIP margin and the 2-DIP selection outline on each side (see
    /// <c>Gallery.TileTemplate</c>); a folder card is the same width with 8-DIP margins. Both
    /// therefore take 260 DIPs, so folder and capture rows keep one column grid for arrow keys,
    /// and a row of N cards never needs more than N tracks.
    /// </remarks>
    public const double TileTrackWidth = 260.0;

    /// <summary>
    /// One card per row when two do not fit, so a narrow window never clips a card or its commands.
    /// </summary>
    public const int MinColumns = 1;

    /// <summary>Caps ultra-wide layouts while avoiding the previous large empty right gutter.</summary>
    public const int MaxColumns = 6;

    /// <summary>The horizontal track of a compact card inside an opened folder.</summary>
    /// <remarks>
    /// A compact tile is 148x180 (about 60% of the 244x292 tile, see
    /// <c>Gallery.CompactTileTemplate</c>) with 6-DIP margins and the 2-DIP selection outline on
    /// each side; a compact folder card is the same size with 8-DIP margins. Both therefore take
    /// 164 DIPs, so folder and capture rows keep one column grid for arrow keys.
    /// </remarks>
    public const double CompactTileTrackWidth = 164.0;

    /// <summary>The compact grid's cap: about as wide as <see cref="MaxColumns"/> full tiles.</summary>
    public const int CompactMaxColumns = 10;

    /// <summary>
    /// Chooses the column count for a gallery content width: as many whole tracks as fit,
    /// clamped to <see cref="MinColumns"/>..<see cref="MaxColumns"/>, or to
    /// <see cref="CompactMaxColumns"/> for the compact cards of an opened folder.
    /// </summary>
    /// <param name="availableWidth">
    /// The width available to the tile rows, in device-independent pixels. A non-positive
    /// width (before first layout) yields <see cref="MinColumns"/>.
    /// </param>
    /// <param name="compact">True for the smaller cards shown inside a folder.</param>
    public static int ColumnCountForWidth(double availableWidth, bool compact = false)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return MinColumns;
        }

        int fit = (int)Math.Floor(availableWidth / (compact ? CompactTileTrackWidth : TileTrackWidth));
        return Math.Clamp(fit, MinColumns, compact ? CompactMaxColumns : MaxColumns);
    }

    /// <summary>
    /// Flattens <paramref name="groups"/> into a header/tile-row sequence: for each group, a
    /// <see cref="GalleryHeaderRow"/>, then its folder cards chunked into
    /// <see cref="GalleryFolderRow"/>s, then its tiles chunked into
    /// <see cref="GalleryTileRow"/>s, each row holding at most <paramref name="columns"/> items.
    /// </summary>
    /// <param name="groups">The date groups, already ordered newest-first.</param>
    /// <param name="columns">Tiles per row; values below <see cref="MinColumns"/> are raised.</param>
    /// <param name="compact">Marks folder and tile rows for the smaller cards of an opened folder.</param>
    public static IReadOnlyList<GalleryRow> Build(
        IReadOnlyList<GalleryGroupViewModel> groups,
        int columns,
        bool compact = false)
    {
        int perRow = Math.Max(MinColumns, columns);
        var rows = new List<GalleryRow>();

        foreach (GalleryGroupViewModel group in groups)
        {
            rows.Add(new GalleryHeaderRow(group.Heading));

            IReadOnlyList<GalleryFolderCardViewModel> folders = group.Folders;
            for (int start = 0; start < folders.Count; start += perRow)
            {
                rows.Add(new GalleryFolderRow(folders.Skip(start).Take(perRow).ToArray(), compact));
            }

            IReadOnlyList<GalleryItemViewModel> tiles = group.Items;
            for (int start = 0; start < tiles.Count; start += perRow)
            {
                int count = Math.Min(perRow, tiles.Count - start);
                var slice = new List<GalleryItemViewModel>(count);
                for (int i = 0; i < count; i++)
                {
                    slice.Add(tiles[start + i]);
                }

                rows.Add(new GalleryTileRow(slice, compact));
            }
        }

        return rows;
    }
}
