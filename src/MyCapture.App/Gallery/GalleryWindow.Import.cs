using MyCapture.App.Themes;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private CancellationTokenSource? _importCancellation;
    private CaptureImportService? _importService;

    private bool IsOverImportArea(DragEventArgs e)
    {
        Point point = e.GetPosition(RowsList);
        return point.X >= 0 && point.Y >= 0 && point.X < RowsList.ActualWidth && point.Y < RowsList.ActualHeight;
    }

    internal static string[] ExternalImportPaths(IDataObject data)
    {
        // A private payload always belongs to internal move handling, including other windows.
        if (data.GetDataPresent(GalleryFolderDragPayload.Format, false)
            || !data.GetDataPresent(DataFormats.FileDrop, false)) return [];
        return data.GetData(DataFormats.FileDrop, false) is string[] { Length: > 0 } paths
            && paths.All(CaptureImportService.IsSupported) ? paths : [];
    }

    private bool PreviewExternalImport(DragEventArgs e)
    {
        bool external = !e.Data.GetDataPresent(GalleryFolderDragPayload.Format, false);
        bool allowed = external && _importCancellation is null && IsOverImportArea(e)
            && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            && ExternalImportPaths(e.Data).Length > 0;
        if (_importCancellation is null)
            ImportOverlay.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        if (!external) return false;
        ClearDropHighlight();
        e.Effects = allowed && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        return true;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_importCancellation is not null) return;
        var picker = new OpenFileDialog
        {
            Title = UiText.Get("Library.Import.Command"), Filter = UiText.Get("Library.Import.Filter"),
            Multiselect = true, CheckFileExists = true,
        };
        if (picker.ShowDialog(this) == true) await ImportFilesAsync(picker.FileNames);
    }

    private void OnCancelImportClick(object sender, RoutedEventArgs e) => _importCancellation?.Cancel();

    private async Task ImportFilesAsync(string[] paths)
    {
        if (_importCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        using var activity = _queue.Activities.Start(LibraryActivityKind.Import, cancellation.Cancel, paths.Length);
        Guid? folder = _viewModel.SelectedScope.FolderId;
        ImportMenu.IsEnabled = false;
        ImportOverlay.IsHitTestVisible = false;
        ImportOverlay.Visibility = Visibility.Collapsed;
        try
        {
            _importService ??= GalleryImportService.Create(_queue, _log);
            var progress = new Progress<CaptureImportProgress>(value =>
            {
                if (!ReferenceEquals(_importCancellation, cancellation)) return;
                activity.Report(value.Completed, value.Total);
            });
            CaptureImportResult result = await _importService.ImportAsync(paths, folder, progress, cancellation.Token);
            KoreanWordWrap.Show(LibraryActionStatus, UiText.Format("Library.Import.Summary", result.Imported, result.Duplicates, result.Failed)
                + (result.Cancelled ? " · " + UiText.Get("Library.Import.Cancelled") : string.Empty)
                + (result.TooLarge > 0 ? " · " + UiText.Get("Library.Import.TooLarge") : string.Empty));
            _viewModel.Refresh();
            RaiseCaptureChanged();
            if (result.Imported > 0) _ = IndexImportedImagesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { KoreanWordWrap.Show(LibraryActionStatus, UiText.Get("Library.Import.Failed")); }
        finally
        {
            _importCancellation = null;
            ImportMenu.IsEnabled = true;
            ImportOverlay.IsHitTestVisible = false;
            ImportOverlay.Visibility = Visibility.Collapsed;
            ImportProgress.Visibility = Visibility.Collapsed;
            ImportCancelButton.Visibility = Visibility.Collapsed;
            KoreanWordWrap.Show(ImportOverlayTitle, UiText.Get("Library.Import.DropTitle"));
        }
    }

    private async Task IndexImportedImagesAsync()
    {
        try { await _ocrIndexing.IndexMissingAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        RefreshOcrCoverageBanner();
        _viewModel.Refresh();
    }
}
