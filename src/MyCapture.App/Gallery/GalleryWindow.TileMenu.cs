using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Data;

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
            var info = new MenuItem { Tag = "MediaInfo", Header = UiText.Get("MediaInfo.Title") };
            info.SetBinding(MenuItem.CommandParameterProperty, new Binding());
            AutomationProperties.SetName(info, UiText.Get("MediaInfo.Title"));
            info.Click += OnMediaDetailsClick;
            int index = _tileMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == "Preview") is { } preview
                ? _tileMenu.Items.IndexOf(preview) + 1 : 0;
            _tileMenu.Items.Insert(index, info);
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
        if (target.DataContext is GalleryItemViewModel opened) PrepareTileMenu(menu, opened);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Brings the shared menu up to date for the card it opens on: the keep label (it keeps or releases the whole
    /// selection the card belongs to), whether compare is possible (exactly two images selected, this card one of
    /// them) and which groups show. Every open does it; nothing from the last open is kept.
    /// </summary>
    internal void PrepareTileMenu(ContextMenu menu, GalleryItemViewModel tile)
    {
        IReadOnlyList<GalleryItemViewModel> targets = CommandTargets(tile);
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "Keep":
                    item.Header = UiText.Get(targets.Count > 0 && targets.All(target => target.IsPinned) ? "LibraryUx_Unkeep" : "LibraryUx_Keep");
                    break;
                case "Compare":
                    item.Visibility = _viewModel.CanCompareSelected && _viewModel.Selection.Contains(tile.Id)
                        ? Visibility.Visible : Visibility.Collapsed;
                    break;
            }
        }

        // The media group is empty for a video, so its separator goes with it.
        foreach (Separator separator in menu.Items.OfType<Separator>().Where(candidate => candidate.Tag as string == "SeparatorMedia"))
            separator.Visibility = tile.IsImage ? Visibility.Visible : Visibility.Collapsed;
    }
}
