using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private bool _keepingSelection;
    private Window? _imagePreview;

    private void OnTileCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryItemViewModel tile })
            _viewModel.Select(tile.Id, control: true, shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
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
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(16) };
        System.Windows.Automation.AutomationProperties.SetName(image, tile.AccessibleName);
        var preview = new Window
        {
            Title = tile.Caption,
            Owner = this,
            Width = Math.Min(960, SystemParameters.WorkArea.Width - 48),
            Height = Math.Min(700, SystemParameters.WorkArea.Height - 48),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = image,
            ShowInTaskbar = false,
        };
        StandardWindowTheme.Apply(preview);
        preview.SetResourceReference(BackgroundProperty, "Surface.Base");
        preview.PreviewKeyDown += (_, key) =>
        {
            if (key.Key is Key.Escape or Key.Space)
            {
                key.Handled = true;
                preview.Close();
            }
        };
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_imagePreview, preview)) _imagePreview = null;
            if (IsVisible) { Activate(); FocusTile(RowsList, tile); }
        };
        _imagePreview = preview;
        preview.Show();
        preview.Activate();
    }

    private async void OnKeepSelectedClick(object sender, RoutedEventArgs e) =>
        await Dispatcher.InvokeAsync(KeepSelectedAsync).Task.Unwrap();

    private async Task KeepSelectedAsync()
    {
        if (_keepingSelection) return;
        GalleryItemViewModel[] targets = _viewModel.SelectedTiles.Where(tile => !tile.IsPinned).ToArray();
        if (targets.Any(tile => !EnsureRecordReady(tile.Id))) return;
        _keepingSelection = true;
        KeepSelectedButton.SetCurrentValue(IsEnabledProperty, false);
        try
        {
            foreach (GalleryItemViewModel tile in targets)
            {
                if (_controller.Find(tile.Id) is { IsManuallyPinned: false })
                    await _queue.ToggleLibraryPinAsync(tile.Id);
                tile.RaiseMetaChanged();
            }
            _viewModel.Refresh();
            RaiseCaptureChanged();
        }
        catch (Exception)
        {
            ShowActionStatus(UiText.Get("Library.Organize.Failed"));
        }
        finally
        {
            _keepingSelection = false;
            KeepSelectedButton.SetCurrentValue(IsEnabledProperty, _viewModel.HasSelection);
        }
    }
}
