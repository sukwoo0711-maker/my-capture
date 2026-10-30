using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyCapture.App.Gallery;

/// <summary>What a key press asks of the library's content area.</summary>
public enum GalleryKeyCommand
{
    None,
    SelectAll,
    ContextMenu,
    Navigate,
    Open,
    Preview,
    Info,
    ToggleSelect,
    Float,
    Keep,
    Move,
    Recognize,
    ExportGif,
    Copy,
    Delete,
}

/// <summary>
/// The library's single-key shortcuts. Every key requires exactly its own modifiers: a plain
/// letter is a command, while Alt+Enter opens information for the selection.
/// </summary>
public static class GalleryKeyMap
{
    public static GalleryKeyCommand Resolve(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Enter when modifiers == ModifierKeys.Alt: return GalleryKeyCommand.Info;
            case Key.A when modifiers == ModifierKeys.Control: return GalleryKeyCommand.SelectAll;
            case Key.C when modifiers == ModifierKeys.Control: return GalleryKeyCommand.Copy;
            case Key.Apps when modifiers == ModifierKeys.None:
            case Key.F10 when modifiers == ModifierKeys.Shift:
                return GalleryKeyCommand.ContextMenu;
            case Key.Left or Key.Right or Key.Up or Key.Down
                when (modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None:
                return GalleryKeyCommand.Navigate;
            case Key.Space when modifiers == ModifierKeys.Control || modifiers == ModifierKeys.Shift
                || modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                return GalleryKeyCommand.ToggleSelect;
        }

        if (modifiers != ModifierKeys.None) return GalleryKeyCommand.None;
        return key switch
        {
            Key.Enter => GalleryKeyCommand.Open,
            Key.Space => GalleryKeyCommand.Preview,
            Key.P => GalleryKeyCommand.Float,
            Key.K => GalleryKeyCommand.Keep,
            Key.M => GalleryKeyCommand.Move,
            Key.T => GalleryKeyCommand.Recognize,
            Key.G => GalleryKeyCommand.ExportGif,
            Key.Delete => GalleryKeyCommand.Delete,
            _ => GalleryKeyCommand.None,
        };
    }
}

internal sealed partial class GalleryWindow
{
    /// <summary>Test seam: sees the command a key resolved to; returning true stops it from running.</summary>
    internal Func<GalleryKeyCommand, bool>? KeyCommandHookForTest { get; set; }
    internal ModifierKeys? KeyModifiersForTest { get; set; }
    internal Action<GalleryItemViewModel>? TileFocusForTest { get; set; }
    private ModifierKeys CurrentKeyModifiers => KeyModifiersForTest ?? Keyboard.Modifiers;

    /// <summary>
    /// The one key handler of the library's content area. Keys follow the selection: the card
    /// that has focus (else the single selected one) takes the one-capture commands, and the
    /// selection takes the commands that work on many (keep, move, delete), as in File Explorer.
    /// A letter is read through <see cref="ShortcutInput.Resolve"/>, so it works in Hangul mode.
    /// </summary>
    /// <remarks>
    /// <para>Order of a key press (WPF runs the preview stage from the window down):</para>
    /// <list type="number">
    /// <item>Window PreviewKeyDown: Esc cancels a drag in preparation; Alt+Left and Backspace go back and Alt+Right
    /// goes forward in the library's history, Alt+Up opens the parent folder (Backspace not while a text box has focus).</item>
    /// <item>This handler, on the content area, before the focused control sees the key. It does nothing while a
    /// text box (the rename box, the search box) or an editable combo has focus, for every key: Del, Ctrl+C,
    /// Ctrl+A, Enter, Esc, F2 and the letters all belong to the text box. It does nothing for keys that start in
    /// the content toolbar, and the card commands only run for keys that start in the card grid; a folder card
    /// has its own handler (open, move, delete). Enter, Space and the arrows are left to a focused button.</item>
    /// <item>Window KeyDown: Esc clears a selection, then dismisses a failure on the status line; the input
    /// bindings Ctrl+F (search), Ctrl+Z (undo), F2 (rename the focused folder or capture) and Ctrl+Shift+N (new
    /// folder) run for anything that did not handle the key. Ctrl with an arrow moves focus only; Shift extends the selection
    /// here (see <see cref="GalleryKeyMap"/>); Space with Ctrl or Shift toggles the card, plain Space previews it.</item>
    /// </list>
    /// </remarks>
    private async void OnLibraryItemsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var source = e.OriginalSource as DependencyObject;
        // The content toolbar shares this surface for layout, but its controls keep their own
        // keyboard handling instead of the card commands.
        if (IsWithin(source, LibraryContentCommandBar)) return;
        if (IsNavigationTextInput(source) || IsNavigationTextInput(Keyboard.FocusedElement as DependencyObject)) return;
        Key key = ShortcutInput.Resolve(e.Key == Key.System ? e.SystemKey : e.Key, e.ImeProcessedKey, textInputFocused: false);
        ModifierKeys modifiers = CurrentKeyModifiers;
        GalleryKeyCommand command = GalleryKeyMap.Resolve(key, modifiers);
        switch (command)
        {
            case GalleryKeyCommand.None:
                return;
            case GalleryKeyCommand.SelectAll:
                OnSelectAllVisibleClick(sender, e);
                return;
            case GalleryKeyCommand.ContextMenu:
                OpenFocusedCardMenu(source);
                e.Handled = true;
                return;
        }

        // The card commands belong to the cards and the grid, not to the location header. A folder
        // card has its own handler (open, move, delete).
        if (!IsWithin(source, RowsList) && !ReferenceEquals(source, LibraryItemsSurface)) return;
        if (FindLibraryCard(source) is Button) return;
        // A focused button inside a card keeps Enter, Space and the arrows for itself.
        if (IsInsideButton(source) && command is GalleryKeyCommand.Open or GalleryKeyCommand.Preview
            or GalleryKeyCommand.ToggleSelect or GalleryKeyCommand.Navigate) return;

        GalleryItemViewModel? focused = ResolveTileFromTree(source) ?? ResolveTileFromTree(Keyboard.FocusedElement);
        GalleryItemViewModel? tile = focused ?? _viewModel.SingleSelectedTile;
        bool control = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift);
        // Tests intercept the commands that open a dialog or another window.
        if (KeyCommandHookForTest?.Invoke(command) == true)
        {
            e.Handled = true;
            return;
        }

        switch (command)
        {
            case GalleryKeyCommand.Navigate when tile is not null:
                NavigateTiles(tile, key, control, shift);
                e.Handled = true;
                break;
            case GalleryKeyCommand.Open when tile is not null:
                if (tile.IsVideo) PrepareVideoPlayer(tile, autoPlay: true); else OpenReedit(tile);
                e.Handled = true;
                break;
            case GalleryKeyCommand.Preview when tile is not null:
                ShowPreview(tile);
                e.Handled = true;
                break;
            case GalleryKeyCommand.Info:
                IReadOnlyList<MyCapture.App.Media.MediaDetailsSource> details = KeyboardDetailsSources(focused);
                if (details.Count == 0) break;
                e.Handled = true;
                await ShowMediaDetailsAsync(details);
                break;
            case GalleryKeyCommand.ToggleSelect when tile is not null:
                _viewModel.Select(tile.Id, control, shift);
                e.Handled = true;
                break;
            case GalleryKeyCommand.Float when tile is { IsImage: true }:
                FloatTile(tile);
                e.Handled = true;
                break;
            case GalleryKeyCommand.Recognize when tile is { IsImage: true }:
                RecognizeText(tile);
                e.Handled = true;
                break;
            case GalleryKeyCommand.ExportGif when tile is { IsVideo: true }:
                OpenVideoEditor(tile, exportGifWhenReady: true);
                e.Handled = true;
                break;
            case GalleryKeyCommand.Copy when tile is { IsImage: true }:
                e.Handled = true;
                if (await CopyRenderedAsync(tile)) ConfirmCopy(source: null);
                break;
            case GalleryKeyCommand.Keep when KeyTargets(focused) is { Count: > 0 } keep:
                e.Handled = true;
                await ToggleKeepAsync(keep);
                break;
            case GalleryKeyCommand.Move when KeyTargets(focused) is { Count: > 0 } move:
                e.Handled = true;
                MoveCaptures(move.Select(target => target.Id).ToArray());
                break;
            case GalleryKeyCommand.Delete when KeyTargets(focused) is { Count: > 0 } delete:
                e.Handled = true;
                ConfirmDeleteMany(delete.Select(target => target.Id).ToArray(), false);
                break;
        }
    }

    /// <summary>
    /// The captures a menu command works on, by the File Explorer rule: the whole selection when
    /// the card it was given on belongs to it, otherwise only that card.
    /// </summary>
    internal IReadOnlyList<GalleryItemViewModel> CommandTargets(GalleryItemViewModel? card)
    {
        IReadOnlyList<GalleryItemViewModel> selected = _viewModel.SelectedTiles;
        if (card is null || selected.Any(tile => tile.Id == card.Id)) return selected;
        return [card];
    }

    /// <summary>
    /// The captures a key works on: the selection, which a card with focus outside it does not
    /// replace. Only with nothing selected does the focused card itself become the target.
    /// </summary>
    private IReadOnlyList<GalleryItemViewModel> KeyTargets(GalleryItemViewModel? focused)
    {
        IReadOnlyList<GalleryItemViewModel> selected = _viewModel.SelectedTiles;
        return selected.Count > 0 || focused is null ? selected : [focused];
    }

    /// <summary>Moves focus to the neighbouring card; Ctrl alone preserves selection and its range anchor.</summary>
    private void NavigateTiles(GalleryItemViewModel tile, Key key, bool control, bool shift)
    {
        // A heading is the nearest target when it directly precedes this row, even if a folder
        // from an earlier day is the next card returned by the card-only vertical navigation.
        if (key == Key.Up && !control && !shift && TryFocusDayHeadingAbove(tile)) return;
        // Plain Up/Down can leave the tiles for the folder cards above them; range
        // selection or focus-only movement with Ctrl or Shift stays on tiles.
        if (key is Key.Up or Key.Down && !control && !shift
            && GallerySelectionNavigation.FindVertical(_viewModel.Rows, tile,
                key == Key.Up ? -1 : 1) is GalleryFolderCardViewModel card)
        {
            FocusFolderCard(card);
            return;
        }

        GalleryItemViewModel? next = GallerySelectionNavigation.FindNeighbor(_viewModel.Rows, tile,
            key is Key.Up or Key.Down, key is Key.Left or Key.Up ? -1 : 1);
        if (next is null) return;
        if (!control || shift) _viewModel.Select(next.Id, control, shift);
        GalleryTileRow? row = _viewModel.Rows.OfType<GalleryTileRow>().FirstOrDefault(r => r.Tiles.Contains(next));
        if (row is null) return;
        RowsList.ScrollIntoView(row);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (TileFocusForTest is { } focus) focus(next);
            else FocusTile(RowsList, next);
        }));
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject container)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
            if (ReferenceEquals(current, container)) return true;
        return false;
    }

    /// <summary>Opens the menu of the card that has focus, else the menu of the empty grid.</summary>
    private void OpenFocusedCardMenu(DependencyObject? source)
    {
        FrameworkElement target = FindLibraryCard(source) ?? FindLibraryCard(Keyboard.FocusedElement as DependencyObject) ?? RowsList;
        if (target is ListBoxItem)
        {
            OpenTileMenu(target, PlacementMode.Bottom);
            return;
        }

        ContextMenu menu = target.ContextMenu ?? LibraryItemsSurface.ContextMenu;
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Keeps the captures that are not kept yet, or releases them all when every one is kept.
    /// One capture simply toggles.
    /// </summary>
    private async Task ToggleKeepAsync(IReadOnlyList<GalleryItemViewModel> targets)
    {
        if (targets.Count == 0 || _keepingSelection) return;
        if (targets.Any(tile => !EnsureRecordReady(tile.Id))) return;
        GalleryItemViewModel[] changing = (targets.All(tile => tile.IsPinned) ? targets : targets.Where(tile => !tile.IsPinned)).ToArray();
        _keepingSelection = true;
        bool changed = false;
        try
        {
            foreach (GalleryItemViewModel tile in changing)
            {
                bool? pinned;
                try { pinned = await _queue.ToggleLibraryPinAsync(tile.Id); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
                {
                    ShowActionStatus(UiText.Get("Library.Organize.Failed"));
                    break;
                }

                if (pinned is null) continue;
                changed = true;
                tile.RaiseMetaChanged();
            }
        }
        finally { _keepingSelection = false; }

        if (!changed) return;
        _viewModel.Refresh();
        RaiseCaptureChanged();
    }
}
