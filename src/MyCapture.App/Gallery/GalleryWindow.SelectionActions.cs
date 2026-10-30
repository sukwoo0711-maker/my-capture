using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private bool _keepingSelection;
    private Window? _imagePreview;

    // A click and UI Automation's Toggle both reach the box, by different routes: a click raises Click (after the
    // box toggled itself, which raises Checked or Unchecked first), Toggle raises only Checked or Unchecked. The
    // selection changes once either way.
    private bool _checkToggleDone;

    private void OnTileCheckToggled(object sender, RoutedEventArgs e)
    {
        // The model's own changes also toggle the box; they already agree with the card.
        if (sender is not CheckBox { DataContext: GalleryItemViewModel tile } box || (box.IsChecked == true) == tile.IsSelected) return;
        _viewModel.Select(tile.Id, control: true, shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _checkToggleDone = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _checkToggleDone = false));
    }

    private void OnTileCheckClick(object sender, RoutedEventArgs e)
    {
        if (!_checkToggleDone && sender is FrameworkElement { DataContext: GalleryItemViewModel tile })
            _viewModel.Select(tile.Id, control: true, shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        _checkToggleDone = false;
        e.Handled = true;
    }

    private void OnPreviewMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is { } tile) ShowPreview(tile);
    }

    private void OnPlayMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is { IsVideo: true } tile) PrepareVideoPlayer(tile, autoPlay: true);
    }

    private void OnSourceDetailsMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is { HasSourceBadge: true } tile) ShowSourceDetails(tile);
    }

    private void OnSameSourceMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is { HasSourceBadge: true } tile && tile.SourceSearchQuery.Length > 0)
            SearchSource(tile.SourceSearchQuery);
    }

    internal void ShowSourceDetails(GalleryItemViewModel tile)
    {
        _viewModel.Select(tile.Id);
        SetDetailsPaneOpen(true);
    }

    private void OnPreviewSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SingleSelectedTile is { } tile) ShowPreview(tile);
    }

    private void ShowPreview(GalleryItemViewModel tile)
    {
        if (tile.IsVideo) { PrepareVideoPlayer(tile, autoPlay: false); return; }
        if (!EnsureRecordReady(tile.Id) || _controller.Find(tile.Id) is not { } record) return;
        var bitmap = ImageCodec.TryLoad(_queue.GetFilePath(record, CaptureFileNames.Rendered))
            ?? ImageCodec.TryLoad(_queue.GetFilePath(record, CaptureFileNames.Original));
        if (bitmap is null) { ShowStatus(UiText.Get("Gallery.FloatFailed")); return; }
        _imagePreview?.Close();
        Window preview = CreateImagePreviewWindow(bitmap, tile.Caption, tile.AccessibleName);
        preview.Owner = this;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_imagePreview, preview)) _imagePreview = null;
            if (IsVisible) { Activate(); FocusTile(RowsList, tile); }
        };
        _imagePreview = preview;
        preview.ContentRendered += (_, _) => PracticeSignals.PreviewOpened(tile.Id);
        preview.Show();
        preview.Activate();
    }

    internal static Window CreateImagePreviewWindow(BitmapSource bitmap, string caption, string accessibleName)
    {
        return new GalleryImagePreviewWindow(bitmap, caption, accessibleName);
    }

    /// <summary>The toolbar's keep works like the K key and the menu: the whole selection, kept or released together.</summary>
    private async void OnKeepSelectedClick(object sender, RoutedEventArgs e) =>
        // Started inside a dispatcher operation, so every continuation returns to the window's thread.
        await Dispatcher.InvokeAsync(async () =>
        {
            KeepSelectedButton.SetCurrentValue(IsEnabledProperty, false);
            try { await ToggleKeepAsync(_viewModel.SelectedTiles); }
            finally
            {
                KeepSelectedButton.SetCurrentValue(IsEnabledProperty, _viewModel.HasSelection);
                FitSelectionCommands();
            }
        }).Task.Unwrap();
}
