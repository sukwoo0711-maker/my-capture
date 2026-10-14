using System.IO;
using System.Windows;
using MyCapture.Core.Export;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private async void OnContactSheetClick(object sender, RoutedEventArgs e)
    {
        CaptureRecord[] selected = _viewModel.SelectedTiles.Select(tile => tile.Record).ToArray();
        if (selected.Length == 0) return;
        if (selected.Length > ContactSheetLayout.MaximumItems)
        {
            LibraryActionStatus.Text = UiText.Get("ContactSheet.Limit");
            return;
        }
        CaptureRecord[] images = selected.Where(record => record.IsImage).ToArray();
        if (images.Length == 0)
        {
            LibraryActionStatus.Text = UiText.Get("ContactSheet.NoImages");
            return;
        }
        if (images.Any(record => !EnsureRecordReady(record.Id))) return;
        long[] revisions = images.Select(record => record.ContentRevision).ToArray();
        var leases = new List<IDisposable>();
        try
        {
            foreach (CaptureRecord record in images) leases.Add(_queue.AcquireEvictionLease(record.Id));
            var items = images.Select(record => new GalleryContactSheetItem(
                _queue.GetFilePath(record, CaptureFileNames.Rendered), record.Title, record.CreatedAt,
                record.SourceDetails.AppName, record.SourceWindowTitle)).ToArray();
            var dialog = new GalleryContactSheetDialog(items, selected.Length - images.Length,
                () => images.Select((record, index) => _queue.Find(record.Id) == record
                    && record.ContentRevision == revisions[index]).All(current => current), _queue.Activities) { Owner = this };
            dialog.ShowDialog();
            await dialog.PendingPreview;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            LibraryActionStatus.Text = UiText.Get("ContactSheet.Failed");
        }
        finally { foreach (IDisposable lease in leases) lease.Dispose(); }
    }
}
