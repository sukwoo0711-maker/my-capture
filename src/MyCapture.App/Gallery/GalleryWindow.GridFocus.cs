using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyCapture.App.Gallery;

/// <summary>
/// The card grid is one Tab stop. The list takes Tab; the cards (and their pin, copy and More buttons, the source
/// glyph and the folder cards) are reached with the arrow keys and act through their keys and Shift+F10.
/// </summary>
/// <remarks>
/// Entering the grid with Tab or Shift+Tab puts focus where the user was: the card that had focus last, else the
/// first selected card, else the first card, else the first folder card. A list with nothing to focus (empty, or
/// still loading) keeps focus on the list itself. After a delete or a move the focus that was in the grid goes to
/// the same entry point, so it never falls to the window.
/// </remarks>
internal sealed partial class GalleryWindow
{
    private Guid? _lastActiveTile;
    private Guid? _lastActiveFolder;

    /// <summary>Remembers the card that takes focus; sends a keyboard entry into the list itself to a card.</summary>
    private void OnRowsListGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ResolveTileFromTree(e.NewFocus) is { } tile)
        {
            _lastActiveTile = tile.Id;
            _lastActiveFolder = null;
        }
        else if (e.NewFocus is FrameworkElement { DataContext: GalleryFolderCardViewModel folder })
        {
            _lastActiveFolder = folder.FolderId;
            _lastActiveTile = null;
        }

        // Tab, Shift+Tab or a programmatic focus after keyboard input lands on the list itself; a click on blank
        // space does too, and stays there.
        if (ReferenceEquals(e.OriginalSource, RowsList) && IsKeyboardEntry()) _ = FocusGridEntry();
    }

    internal void ForgetLastActiveForTest() { _lastActiveTile = null; _lastActiveFolder = null; }

    private static bool IsKeyboardEntry() =>
        InputManager.Current.MostRecentInputDevice is KeyboardDevice && Mouse.LeftButton != MouseButtonState.Pressed;

    /// <summary>Tab leaves the whole grid, including when arrows put focus on a non-Tab-stop card.</summary>
    private void OnRowsListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Tab || ReferenceEquals(e.OriginalSource, RowsList)
            || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
        // Starting reverse traversal from a descendant would visit the list's own Tab stop and
        // immediately re-enter its remembered card. Start from that single boundary instead.
        var direction = Keyboard.Modifiers == ModifierKeys.Shift
            ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
        e.Handled = RowsList.MoveFocus(new TraversalRequest(direction));
    }

    /// <summary>
    /// Puts keyboard focus on the card the user left, or the best card or folder card to start from. False
    /// when the grid has nothing to focus.
    /// </summary>
    internal bool FocusGridEntry()
    {
        GalleryFolderCardViewModel[] folders = _viewModel.Rows.OfType<GalleryFolderRow>().SelectMany(row => row.Folders).ToArray();
        // The remembered kind wins over every fallback. A selected capture must not steal re-entry
        // from the folder card the user actually left in this mixed grid.
        if (_lastActiveFolder is { } lastFolder
            && folders.FirstOrDefault(folder => folder.FolderId == lastFolder) is { } rememberedFolder)
        {
            FocusFolderCard(rememberedFolder);
            return true;
        }

        GalleryTileRow[] tileRows = _viewModel.Rows.OfType<GalleryTileRow>().Where(row => row.Tiles.Count > 0).ToArray();
        IEnumerable<GalleryItemViewModel> tiles = tileRows.SelectMany(row => row.Tiles);
        GalleryItemViewModel? target =
            (_lastActiveTile is { } last ? tiles.FirstOrDefault(tile => tile.Id == last) : null)
            ?? _viewModel.SelectedTiles.FirstOrDefault(tile => tiles.Contains(tile))
            ?? tiles.FirstOrDefault();
        if (target is not null)
        {
            GalleryTileRow row = tileRows.First(candidate => candidate.Tiles.Contains(target));
            if (FocusTile(RowsList, target)) return true;
            RowsList.ScrollIntoView(row);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusTile(RowsList, target)));
            return true;
        }

        GalleryFolderCardViewModel? card = folders.FirstOrDefault();
        if (card is null) return false;
        FocusFolderCard(card);
        return true;
    }

    /// <summary>
    /// A day heading is a button that selects that day's captures. It is not a Tab stop; the arrow keys reach it:
    /// Up from the first row of a day lands on its heading, Down from the heading returns to that day's first row.
    /// Enter and Space activate it like any button.
    /// </summary>
    private void OnDayKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Down || Keyboard.Modifiers != ModifierKeys.None
            || sender is not FrameworkElement { DataContext: GalleryHeaderRow header }) return;
        IReadOnlyList<GalleryRow> rows = _viewModel.Rows;
        int index = rows.ToList().IndexOf(header);
        GalleryRow? next = index >= 0 && index + 1 < rows.Count ? rows[index + 1] : null;
        if (next is GalleryFolderRow { Folders.Count: > 0 } folders) FocusFolderCard(folders.Folders[0]);
        else if (next is GalleryTileRow { Tiles.Count: > 0 } tiles) FocusTileInRow(tiles, tiles.Tiles[0]);
        else return;
        e.Handled = true;
    }

    private void FocusTileInRow(GalleryTileRow row, GalleryItemViewModel tile)
    {
        _viewModel.Select(tile.Id);
        RowsList.ScrollIntoView(row);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusTile(RowsList, tile)));
    }

    /// <summary>The day heading directly above a capture or folder card's row.</summary>
    private bool TryFocusDayHeadingAbove(object card)
    {
        List<GalleryRow> rows = _viewModel.Rows.ToList();
        int index = rows.FindIndex(row => row switch
        {
            GalleryTileRow tiles when card is GalleryItemViewModel tile => tiles.Tiles.Contains(tile),
            GalleryFolderRow folders when card is GalleryFolderCardViewModel folder => folders.Folders.Contains(folder),
            _ => false,
        });
        if (index <= 0 || rows[index - 1] is not GalleryHeaderRow header) return false;
        RowsList.ScrollIntoView(header);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusDayHeading(RowsList, header)));
        return true;
    }

    private static bool FocusDayHeading(DependencyObject parent, GalleryHeaderRow header)
    {
        if (parent is Button { DataContext: GalleryHeaderRow candidate } button && ReferenceEquals(candidate, header)) return button.Focus();
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FocusDayHeading(System.Windows.Media.VisualTreeHelper.GetChild(parent, i), header)) return true;
        return false;
    }

    /// <summary>Called when the grid had keyboard focus before a command rebuilt it: focus goes back into the grid.</summary>
    private void RestoreGridFocus(bool hadFocus)
    {
        if (!hadFocus || RowsList.IsKeyboardFocusWithin || !IsVisible) return;
        // Entry logic needs a keyboard context; the user pressed a key to get here (Del, M, Ctrl+Z).
        if (!FocusGridEntry()) _ = RowsList.Focus();
    }
}
