namespace MyCapture.App.Gallery;

/// <summary>Keyboard movement follows the actual tile rows, including short date-group rows.</summary>
internal static class GallerySelectionNavigation
{
    internal static GalleryItemViewModel? FindNeighbor(IEnumerable<GalleryRow> rows,
        GalleryItemViewModel current, bool vertical, int direction)
    {
        GalleryTileRow[] tileRows = rows.OfType<GalleryTileRow>().Where(row => row.Tiles.Count > 0).ToArray();
        int rowIndex = Array.FindIndex(tileRows, row => row.Tiles.Contains(current));
        if (rowIndex < 0) return null;
        int column = tileRows[rowIndex].Tiles.IndexOf(current);
        if (vertical)
        {
            GalleryTileRow next = tileRows[Math.Clamp(rowIndex + Math.Sign(direction), 0, tileRows.Length - 1)];
            return next.Tiles[Math.Min(column, next.Tiles.Count - 1)];
        }
        GalleryItemViewModel[] visible = tileRows.SelectMany(row => row.Tiles).ToArray();
        int index = Array.IndexOf(visible, current);
        return visible[Math.Clamp(index + Math.Sign(direction), 0, visible.Length - 1)];
    }

    /// <summary>
    /// The folder card or tile in the next folder/tile row above or below, skipping headings and
    /// keeping the column. <paramref name="current"/> is a tile or a folder card. Returns
    /// <see langword="null"/> at the first or last row.
    /// </summary>
    internal static object? FindVertical(IReadOnlyList<GalleryRow> rows, object current, int direction)
    {
        int step = Math.Sign(direction);
        if (step == 0) return null;
        int rowIndex = -1, column = -1;
        for (int index = 0; index < rows.Count && rowIndex < 0; index++)
        {
            column = Items(rows[index]).IndexOf(current);
            if (column >= 0) rowIndex = index;
        }
        if (rowIndex < 0) return null;
        for (int index = rowIndex + step; index >= 0 && index < rows.Count; index += step)
        {
            IList<object> items = Items(rows[index]);
            if (items.Count > 0) return items[Math.Min(column, items.Count - 1)];
        }
        return null;
    }

    /// <summary>Left/right movement through every visible folder card, clamped at either end.</summary>
    internal static GalleryFolderCardViewModel? FindFolderNeighbor(IEnumerable<GalleryRow> rows,
        GalleryFolderCardViewModel current, int direction)
    {
        GalleryFolderCardViewModel[] cards = rows.OfType<GalleryFolderRow>().SelectMany(row => row.Folders).ToArray();
        int index = Array.IndexOf(cards, current);
        if (index < 0) return null;
        return cards[Math.Clamp(index + Math.Sign(direction), 0, cards.Length - 1)];
    }

    private static IList<object> Items(GalleryRow row) => row switch
    {
        GalleryFolderRow folders => folders.Folders.Cast<object>().ToList(),
        GalleryTileRow tiles => tiles.Tiles.Cast<object>().ToList(),
        _ => [],
    };
}
