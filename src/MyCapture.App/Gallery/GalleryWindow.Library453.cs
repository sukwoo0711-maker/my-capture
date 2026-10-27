using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private (double Font, string Family, string Culture, double Dpi, TextFormattingMode Mode)? _metadataTextMetrics;

    private void OnMetadataLayoutUpdated(object? sender, EventArgs e)
    {
        double font = TryFindResource("FontSize.Caption") is double size ? size : 12;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var metrics = (font, FontFamily.Source, UiText.Culture.Name, dpi.DpiScaleX, TextOptions.GetTextFormattingMode(this));
        if (_metadataTextMetrics == metrics) return;
        _metadataTextMetrics = metrics;
        var probe = new TextBlock { FontSize = font, FontFamily = FontFamily };
        TextOptions.SetTextFormattingMode(probe, metrics.Item5);
        VisualTreeHelper.SetRootDpi(probe, dpi);
        double timeWidth = 0;
        // Every hour covers single/double digits and both day periods in the UI culture.
        for (int hour = 0; hour < 24; hour++)
        {
            probe.Text = new DateTime(2000, 1, 1, hour, 58, 0).ToString("t", UiText.Culture);
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            timeWidth = Math.Max(timeWidth, probe.DesiredSize.Width);
        }
        // Two 20-DIP status glyph badges, the 4-DIP gap, 16-DIP card text inset,
        // and 16-DIP track inset; full views also reserve a 20-DIP folder glyph.
        // The existing minimum tracks still win at ordinary text sizes.
        _viewModel.SetMetadataMinimumTrackWidths(Math.Ceiling(timeWidth) + 96, Math.Ceiling(timeWidth) + 76);
    }

    private GalleryItemViewModel? _renamingTile;
    private Task<bool>? _renameSave;
    private int _renameRequest;

    private async void OnRenameCaptureClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is not { } tile) return;
        e.Handled = true;
        await BeginCaptureRenameAsync(tile);
    }

    internal async Task BeginCaptureRenameAsync(GalleryItemViewModel tile)
    {
        int request = ++_renameRequest;
        if (_renamingTile != tile && !await CommitCaptureRenameAsync()) return;
        if (request != _renameRequest) return;
        if (_renamingTile != tile)
        {
            _renamingTile = tile;
            tile.RenameDraft = tile.Caption;
            tile.IsRenaming = true;
            _viewModel.Select(tile.Id);
        }
        GalleryTileRow? row = _viewModel.Rows.OfType<GalleryTileRow>().FirstOrDefault(candidate => candidate.Tiles.Contains(tile));
        if (row is null) { CancelCaptureRename(); return; }
        RowsList.ScrollIntoView(row);
        await Dispatcher.InvokeAsync(() =>
        {
            if (_renamingTile != tile) return;
            RowsList.UpdateLayout();
            if (FindRenameInput(RowsList, tile) is not { } input) return;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private static TextBox? FindRenameInput(DependencyObject parent, GalleryItemViewModel tile)
    {
        if (parent is TextBox { Name: "TileRenameInput" } input && ReferenceEquals(input.DataContext, tile)) return input;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindRenameInput(VisualTreeHelper.GetChild(parent, index), tile) is { } found) return found;
        return null;
    }

    internal Task<bool> CommitCaptureRenameAsync()
    {
        if (_renameSave is not null) return _renameSave;
        if (_renamingTile is not { } tile) return Task.FromResult(true);
        _renameSave = SaveCaptureRenameAsync(tile);
        return CompleteCaptureRenameAsync(_renameSave);
    }

    private async Task<bool> CompleteCaptureRenameAsync(Task<bool> save)
    {
        try { return await save; }
        finally { if (ReferenceEquals(_renameSave, save)) _renameSave = null; }
    }

    private async Task<bool> SaveCaptureRenameAsync(GalleryItemViewModel tile)
    {
        tile.IsRenameSaving = true;
        try
        {
            bool saved = await _controller.UpdateOrganizationAsync(tile.Id, tile.RenameDraft, tile.Record.Tags);
            if (!saved)
            {
                _renamingTile = null;
                tile.IsRenaming = false;
                _viewModel.Refresh();
                ShowActionStatus(UiText.Get("Library.Organize.Missing"));
                return false;
            }
            // Clear the draft state before hiding the input, because that raises LostKeyboardFocus.
            _renamingTile = null;
            tile.IsRenaming = false;
            tile.RaiseMetaChanged();
            _viewModel.Refresh();
            CaptureChanged?.Invoke(this, EventArgs.Empty);
            LibraryActionStatus.Text = UiText.Get("Library.Organize.Saved");
            return true;
        }
        catch (ArgumentException)
        {
            ShowActionStatus(UiText.Get("Library.Organize.Invalid"));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            _log.LogWarning(ex, "Could not rename library capture");
            ShowActionStatus(UiText.Get("Library.Organize.Failed"));
            return false;
        }
        finally { tile.IsRenameSaving = false; }
    }

    internal void CancelCaptureRename()
    {
        if (_renameSave is not null || _renamingTile is not { } tile) return;
        _renameRequest++;
        _renamingTile = null;
        tile.IsRenaming = false;
        tile.RenameDraft = string.Empty;
    }

    private async void OnCaptureRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: GalleryItemViewModel tile } input || e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CancelCaptureRename();
            if (!tile.IsRenaming) FocusTile(RowsList, tile);
            return;
        }
        int request = _renameRequest;
        if (!await CommitCaptureRenameAsync()) return;
        if (request == _renameRequest && (ReferenceEquals(Keyboard.FocusedElement, input)
            || FindLibraryCard(Keyboard.FocusedElement as DependencyObject)?.DataContext == tile))
            FocusTile(RowsList, tile);
    }

    private async void OnCaptureRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: GalleryItemViewModel tile } && tile == _renamingTile)
            await CommitCaptureRenameAsync();
    }

    private void OnEmptyContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        MenuItem sort = menu.Items.OfType<MenuItem>().Single(item => item.Items.Count == 3);
        foreach (MenuItem choice in sort.Items)
            choice.IsChecked = int.TryParse(choice.Tag?.ToString(), out int index) && index == _viewModel.SortIndex;
        menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+A").IsEnabled =
            _viewModel.Rows.OfType<GalleryTileRow>().Any(row => row.Tiles.Count > 0);
    }

    private void OnEmptySortClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && int.TryParse(item.Tag?.ToString(), out int sort)) _viewModel.SortIndex = sort;
    }

    private void OnSelectAllVisibleClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAllVisible();
        CloseInlinePlayer();
        e.Handled = true;
    }

    private void OnLibraryItemsRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A blank click makes the canvas the keyboard context for the next menu-key invocation.
        // Child capture and folder menus retain WPF's normal right-click routing.
        FrameworkElement? card = FindLibraryCard(e.OriginalSource as DependencyObject);
        if (card is null) { RowsList.Focus(); return; }
        // As in File Explorer, a right click on a card outside the selection selects only that card.
        if (card.DataContext is GalleryItemViewModel tile && !_viewModel.Selection.Contains(tile.Id))
            _viewModel.Select(tile.Id);
    }

    internal static FrameworkElement? FindLibraryCard(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is ListBoxItem { DataContext: GalleryItemViewModel } tile) return tile;
            if (current is Button { DataContext: GalleryFolderCardViewModel } folder) return folder;
        }
        return null;
    }
}
