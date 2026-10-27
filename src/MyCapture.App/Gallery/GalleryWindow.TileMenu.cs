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
    internal ContextMenu TileMenu => _tileMenu ??= (ContextMenu)FindResource("Gallery.TileContextMenu");

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

    /// <summary>Opens the card menu on <paramref name="target"/>, a card or its More button.</summary>
    internal void OpenTileMenu(FrameworkElement target, PlacementMode placement)
    {
        ContextMenu menu = TileMenu;
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }
}
