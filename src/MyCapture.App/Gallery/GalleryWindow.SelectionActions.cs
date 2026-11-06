using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private bool _keepingSelection;
    private Window? _imagePreview;

    // #221: decoding a large rendered PNG on the UI thread held the library for up to ~120 ms (1920x12000).
    // One decode runs on the pool at a time, so a held Space key cannot queue many full-size images: a newer
    // request replaces the one waiting. A decoded image opens only while its request is still the latest one
    // and the library, the selection and the capture are as they were when it was asked for.
    private PreviewRequest? _waitingPreview;
    private bool _decodingPreview;
    private bool _previewVisibilityHooked;
    private int _previewRequests;

    private sealed record PreviewRequest(int Number, GalleryItemViewModel Tile, Guid[] Selection,
        long ContentRevision, string RenderedPath, string OriginalPath);

    /// <summary>Decodes a preview from the rendered and original paths on a pool thread; tests replace it.</summary>
    internal Func<string, string, BitmapSource?> DecodePreviewForTest { get; set; } = DecodePreviewFiles;

    private static BitmapSource? DecodePreviewFiles(string renderedPath, string originalPath) =>
        ImageCodec.TryLoad(renderedPath) ?? ImageCodec.TryLoad(originalPath);

    internal bool IsLoadingPreviewForTest => _decodingPreview || _waitingPreview is not null;

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
        if (!_previewVisibilityHooked)
        {
            _previewVisibilityHooked = true;
            IsVisibleChanged += OnPreviewOwnerVisibilityChanged;
        }

        _waitingPreview = new PreviewRequest(++_previewRequests, tile, [.. _viewModel.Selection.SelectedIds],
            record.ContentRevision, _queue.GetFilePath(record, CaptureFileNames.Rendered),
            _queue.GetFilePath(record, CaptureFileNames.Original));
        if (_decodingPreview) return;
        _decodingPreview = true;
        // Started inside a dispatcher operation, so every continuation returns to the window's thread.
        _ = Dispatcher.InvokeAsync(DecodeWaitingPreviewsAsync).Task.Unwrap();
    }

    private async Task DecodeWaitingPreviewsAsync()
    {
        try
        {
            while (_waitingPreview is { } request)
            {
                _waitingPreview = null;
                Func<string, string, BitmapSource?> decode = DecodePreviewForTest;
                try
                {
                    BitmapSource? bitmap = await Task.Run(() => decode(request.RenderedPath, request.OriginalPath));
                    // A dropped image is no longer referenced here; the GC frees it with its pixels.
                    if (!IsCurrentPreview(request)) continue;
                    if (bitmap is null) ShowStatus(UiText.Get("Gallery.FloatFailed"));
                    else OpenPreview(request.Tile, bitmap);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not show the preview of capture {Id}", request.Tile.Id);
                    if (IsCurrentPreview(request)) ShowStatus(UiText.Get("Gallery.FloatFailed"));
                }
            }
        }
        finally { _decodingPreview = false; }
    }

    private bool IsCurrentPreview(PreviewRequest request) =>
        request.Number == _previewRequests && IsVisible
        && _controller.Find(request.Tile.Id) is { } record && record.ContentRevision == request.ContentRevision
        && _viewModel.Selection.SelectedIds.SequenceEqual(request.Selection);

    private void OnPreviewOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) return;
        // Hiding the library cancels a preview that is still loading.
        _previewRequests++;
        _waitingPreview = null;
    }

    private void OpenPreview(GalleryItemViewModel tile, BitmapSource bitmap)
    {
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
