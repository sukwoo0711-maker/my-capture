using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private async Task PopulateDeletedCapturesAsync()
    {
        RestoreDeletedMenu.Items.Clear();
        RestoreDeletedMenu.Items.Add(new MenuItem { Header = UiText.Get("LibraryRecovery_Loading"), IsEnabled = false });
        IReadOnlyList<DeletedLibraryCapture> entries;
        try { entries = _controller.Recovery is { } recovery ? await recovery.RecentDeletedAsync(20) : []; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load recent deleted captures");
            RestoreDeletedMenu.Items.Clear();
            RestoreDeletedMenu.Items.Add(new MenuItem { Header = UiText.Get("LibraryRecovery_Failed"), IsEnabled = false });
            return;
        }
        RestoreDeletedMenu.Items.Clear();
        if (entries.Count == 0)
        {
            RestoreDeletedMenu.Items.Add(new MenuItem { Header = UiText.Get("LibraryRecovery_Empty"), IsEnabled = false });
            return;
        }
        foreach (DeletedLibraryCapture entry in entries)
        {
            string name = string.IsNullOrWhiteSpace(entry.Record.Title)
                ? entry.Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : entry.Record.Title;
            var item = new MenuItem { Header = name };
            item.Click += async (_, _) => await RestoreDeletedCaptureAsync(entry.Token);
            RestoreDeletedMenu.Items.Add(item);
        }
    }

    private async Task RestoreDeletedCaptureAsync(Guid token)
    {
        if (_deleteInProgress || _controller.Recovery is not { } recovery) return;
        _deleteInProgress = true;
        try
        {
            await recovery.RestoreAsync(token);
            _viewModel.Refresh();
            RaiseCaptureChanged();
            LibraryActionStatus.Text = UiText.Get("LibraryRecovery_Restored");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restore a deleted library capture");
            LibraryActionStatus.Text = UiText.Get("LibraryRecovery_Failed");
        }
        finally { _deleteInProgress = false; }
    }
}
