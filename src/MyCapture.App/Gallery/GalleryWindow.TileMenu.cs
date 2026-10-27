using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MyCapture.App.Gallery;

/// <summary>
/// The capture card's menu. The library has one, built the first time any menu opens; a card, its
/// More button and the Menu key all open that instance with themselves as its placement target.
/// </summary>
internal sealed partial class GalleryWindow
{
    private ContextMenu? _tileMenu;

    /// <summary>The one card menu (the <c>Gallery.TileContextMenu</c> resource), created on first use.</summary>
    internal ContextMenu TileMenu
    {
        get
        {
            if (_tileMenu is not null) return _tileMenu;
            _tileMenu = (ContextMenu)FindResource("Gallery.TileContextMenu");
            // A closed menu keeps no card: recycled cards are never held through the shared instance.
            _tileMenu.Closed += (_, _) => _tileMenu.ClearValue(ContextMenu.PlacementTargetProperty);
            return _tileMenu;
        }
    }

    /// <summary>
    /// Opens the card menu for a right click (or the Menu key) anywhere on a capture card. Blank
    /// space, headers and folder cards keep their own menus.
    /// </summary>
    private void OnTileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FindLibraryCard(e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: GalleryItemViewModel } card) return;
        // A negative cursor position means the keyboard asked for the menu.
        OpenTileMenu(card, e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint);
        e.Handled = true;
    }

    /// <summary>
    /// Opens the card menu on <paramref name="target"/>, a card or its More button. The same rule applies however
    /// the menu was asked for (right click, More, Shift+F10 or the Menu key): a card outside the selection becomes
    /// the selection first, so the menu's commands work on the whole selection when the card is part of it.
    /// </summary>
    internal void OpenTileMenu(FrameworkElement target, PlacementMode placement)
    {
        if (target.DataContext is GalleryItemViewModel tile && !_viewModel.Selection.Contains(tile.Id))
            _viewModel.Select(tile.Id);
        ContextMenu menu = TileMenu;
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }
}
