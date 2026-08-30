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
/// A tile row holds at most <see cref="ColumnCountForWidth"/> tiles (2 at narrow widths and up
/// to 6 on very wide desktops). Each date group emits one header row followed by however many
/// tile rows its tiles fill; a partial final row keeps its remaining tiles rather than padding,
/// so selection and keyboard order stay dense.
/// </para>
/// </remarks>
public static class GalleryRowBuilder
{
    /// <summary>The tile width plus its margins, used to derive the column count from width.</summary>
    /// <remarks>
    /// A tile is 244 wide with a 6px margin each side (see <c>Gallery.TileTemplate</c>), so it
    /// occupies a 256px horizontal track. This lets four image-first cards fill the default
    /// gallery while preserving two comfortable columns at the minimum window width.
    /// </remarks>
    public const double TileTrackWidth = 256.0;

    /// <summary>Never fewer than two columns, even in the narrowest usable window.</summary>
    public const int MinColumns = 2;

    /// <summary>Caps ultra-wide layouts while avoiding the previous large empty right gutter.</summary>
    public const int MaxColumns = 6;

    /// <summary>
    /// Chooses the column count for a gallery content width, clamped to
    /// <see cref="MinColumns"/>..<see cref="MaxColumns"/>.
    /// </summary>
    /// <param name="availableWidth">
    /// The width available to the tile rows, in device-independent pixels. A non-positive
    /// width (before first layout) yields <see cref="MinColumns"/>.
    /// </param>
    public static int ColumnCountForWidth(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return MinColumns;
        }

        int fit = (int)Math.Floor(availableWidth / TileTrackWidth);
        return Math.Clamp(fit, MinColumns, MaxColumns);
    }

    /// <summary>
    /// Flattens <paramref name="groups"/> into a header/tile-row sequence: for each group, a
    /// <see cref="GalleryHeaderRow"/> followed by its tiles chunked into
    /// <see cref="GalleryTileRow"/>s of at most <paramref name="columns"/> tiles each.
    /// </summary>
    /// <param name="groups">The date groups, already ordered newest-first.</param>
    /// <param name="columns">Tiles per row; values below <see cref="MinColumns"/> are raised.</param>
    public static IReadOnlyList<GalleryRow> Build(
        IReadOnlyList<GalleryGroupViewModel> groups,
        int columns)
    {
        int perRow = Math.Max(MinColumns, columns);
        var rows = new List<GalleryRow>();

        foreach (GalleryGroupViewModel group in groups)
        {
            rows.Add(new GalleryHeaderRow(group.Heading));

            IReadOnlyList<GalleryItemViewModel> tiles = group.Items;
            for (int start = 0; start < tiles.Count; start += perRow)
            {
                int count = Math.Min(perRow, tiles.Count - start);
                var slice = new List<GalleryItemViewModel>(count);
                for (int i = 0; i < count; i++)
                {
                    slice.Add(tiles[start + i]);
                }

                rows.Add(new GalleryTileRow(slice));
            }
        }

        return rows;
    }
}
