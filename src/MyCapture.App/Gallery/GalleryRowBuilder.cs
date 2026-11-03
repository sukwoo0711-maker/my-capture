using MyCapture.App.Themes;
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
/// A tile row holds at most <see cref="ColumnCountForWidth"/> tiles: as many minimum tracks as
/// fit, two even at the minimum window with the details pane open (a search that lists filed captures
/// may use one wide column there, so each card keeps its folder name and match reason readable). The tracks are fluid:
/// <see cref="GalleryCardRowPanel"/> divides the row's whole width between them, so the cards grow
/// until another column fits instead of leaving an empty band. Each date group emits one header
/// row followed by however many tile rows its tiles fill; a partial final row keeps its remaining
/// tiles rather than padding, so selection and keyboard order stay dense.
/// </para>
/// </remarks>
public static class GalleryRowBuilder
{
    /// <summary>
    /// The narrowest track of a full card: a 208-DIP card plus the 16 DIPs around it (a tile's
    /// 6-DIP margins and 2-DIP selection outline, or a folder card's 8-DIP margins). The column
    /// count is the number of these that fit; the cards then share the leftover width.
    /// </summary>
    /// <remarks>
    /// 208 still fits the card's two lines (the title and the time; the 24-DIP source glyph, a status badge and
    /// the three 28-DIP commands, or a readable search reason in place of status badges), and two tracks fit the library at the window's minimum width with the
    /// details pane open. With two or more columns a card therefore stays between 208 and 320 DIPs wide before
    /// a further column is added. <see cref="CardTrackFor"/> raises the track when the time or the text needs more.
    /// </remarks>
    public const double MinTileTrackWidth = 224.0;

    /// <summary>
    /// The narrowest track a card needs for the capture time in the UI language (the widest of the day, in DIPs):
    /// the title line needs a readable title and the time; the second line keeps the source glyph, a caption
    /// at least four ems wide and the three commands. Kept/edited badges yield to a nonempty caption; a filed
    /// search result also reserves its readable folder name. The time
    /// and commands share a column, so its larger width must fit on both lines.
    /// </summary>
    public static double CardTrackFor(double timeWidth, bool compact, double captionFontSize = TypeScale.Caption, bool withFolderReason = false)
    {
        // The track's 16-DIP inset, the card edge (2) and the text margins (8 and 6).
        const double chrome = 16 + 2 + 14;
        const double minimumTitle = 64, titleGap = 8, source = 24 + 4, commands = 3 * 28, folderGlyph = 20;
        double firstLine = chrome + minimumTitle + titleGap + timeWidth;
        double minimumCaption = Math.Max(48, captionFontSize * 4);
        double secondLine = chrome + source + minimumCaption + Math.Max(commands, timeWidth)
            + (withFolderReason ? SearchFolderBadgeWidthFor(captionFontSize) + 4 : 0);
        return Math.Max(firstLine, secondLine) + (compact || withFolderReason ? 0 : folderGlyph);
    }

    /// <summary>The search folder badge keeps its icon/padding and at least four ems of readable name.</summary>
    public static double SearchFolderBadgeWidthFor(double captionFontSize) => Math.Max(96, captionFontSize * 8);

    /// <summary>
    /// One card per row when two do not fit, so a narrow window never clips a card or its commands.
    /// </summary>
    public const int MinColumns = 1;

    /// <summary>Caps ultra-wide layouts; beyond it the cards keep growing.</summary>
    public const int MaxColumns = 10;

    /// <summary>The narrowest track of a compact card inside an opened folder.</summary>
    /// <remarks>
    /// A compact card is at least 176 DIPs wide plus the same 16 DIPs around it, so a folder can show more
    /// captures per row than the library root. This keeps a 48-DIP search reason beside the source glyph
    /// and three commands; <see cref="CardTrackFor"/> raises it for larger text or a wider localized time.
    /// </remarks>
    public const double MinCompactTileTrackWidth = 192.0;

    /// <summary>The compact grid's cap: about as wide as <see cref="MaxColumns"/> full tiles.</summary>
    public const int CompactMaxColumns = 15;

    /// <summary>Space around a card inside its track, both sides together.</summary>
    public const double CardInset = 16.0;

    /// <summary>
    /// Chooses the column count for a gallery content width: as many minimum tracks as fit,
    /// clamped to <see cref="MinColumns"/>..<see cref="MaxColumns"/>, or to
    /// <see cref="CompactMaxColumns"/> for the compact cards of an opened folder.
    /// </summary>
    /// <param name="availableWidth">
    /// The width available to the tile rows, in device-independent pixels. A non-positive
    /// width (before first layout) yields <see cref="MinColumns"/>.
    /// </param>
    /// <param name="compact">True for the smaller cards shown inside a folder.</param>
    /// <param name="minimumTrackWidth">Width needed for localized metadata and enlarged text.</param>
    public static int ColumnCountForWidth(double availableWidth, bool compact = false, double minimumTrackWidth = 0)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return MinColumns;
        }

        double track = Math.Max(compact ? MinCompactTileTrackWidth : MinTileTrackWidth, minimumTrackWidth);
        int fit = (int)Math.Floor(availableWidth / track);
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
                rows.Add(new GalleryFolderRow(folders.Skip(start).Take(perRow).ToArray(), compact, perRow));
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

                rows.Add(new GalleryTileRow(slice, compact, perRow));
            }
        }

        return rows;
    }
}
