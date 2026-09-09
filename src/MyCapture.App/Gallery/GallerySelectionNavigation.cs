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
}
