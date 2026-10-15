using System.Windows;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private void OnTagNavigationSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TagSection is not null) TagSection.MaxHeight = Math.Max(72, e.NewSize.Height - 500);
    }

    private async void OnTagClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GalleryTagItem tag) return;
        bool leftLocation = !_viewModel.SelectedScope.IsAll;
        _folderNavigationUpdating = true;
        try { CloseInlinePlayer(); _viewModel.ShowTag(tag.Name); }
        finally { _folderNavigationUpdating = false; }
        if (leftLocation) await FollowSavedDestinationAsync(CaptureFolderScope.All);
    }

    private void OnMoreTagsClick(object sender, RoutedEventArgs e) => _viewModel.ShowAllTags();

    private void OnBatchTagsClick(object sender, RoutedEventArgs e)
    {
        GalleryItemViewModel? target = ResolveTileFromCommand(sender);
        if (target is not null && !_viewModel.Selection.Contains(target.Id)) _viewModel.Select(target.Id);
        CaptureRecord[] records = _viewModel.SelectedTiles.Select(tile => tile.Record).ToArray();
        if (records.Length == 0) return;
        Guid[] ids = records.Select(record => record.Id).ToArray();
        var dialog = new GalleryBatchTagsDialog(records, changes => _controller.UpdateTagsAsync(ids, changes)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        _viewModel.Refresh();
        CaptureChanged?.Invoke(this, EventArgs.Empty);
        ShowActionStatus(UiText.Format("Library.Tags.Saved", result.UpdatedCount, result.LimitReachedCount, result.MissingCount));
    }
}
