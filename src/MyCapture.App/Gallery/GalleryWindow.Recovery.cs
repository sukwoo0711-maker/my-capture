using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.App.Gallery;

/// <summary>
/// Deleting, undoing and restoring captures, and the library's one-time notice about the first
/// automatic deletion. Results go to the status line with their follow-up action, never to a
/// dialog: a modal result used to sit over tiles that had already gone blank.
/// </summary>
internal sealed partial class GalleryWindow
{
    /// <summary>Asks before moving captures to the Recycle Bin (count, whole library). Tests answer it.</summary>
    internal Func<int, bool, bool>? ConfirmDelete { get; set; }

    /// <summary>Opens the settings window at the automatic-deletion choice. Tests replace it.</summary>
    internal Action OpenRetentionSettings { get; set; } = OpenRetentionSettingsInApp;

    /// <summary>Opens a folder in File Explorer. Tests replace it so no process starts.</summary>
    internal Action<string> OpenFolderInShell { get; set; } = ExplorerLauncher.OpenFolder;

    /// <summary>The clock relative deletion times are measured from. Tests pin it.</summary>
    internal Func<DateTimeOffset> RecoveryClock { get; set; } = () => DateTimeOffset.Now;

    private Guid? _undoBatch;
    private Func<Task>? _statusAction;
    private string? _statusActionText;

    // The shared confirmation names the real outcome ("휴지통으로 이동") on a danger button.
    private bool ConfirmDeleteWithDialog(int count, bool entireLibrary) =>
        LibraryPermanentDeletion.ConfirmRecycle(this, count, entireLibrary);

    private static void OpenRetentionSettingsInApp()
    {
        // The shell's own settings command keeps its guards (exit, Store installation); the window
        // it shows is then moved to the storage page.
        ShortcutHelpWindow.OpenSettings?.Invoke();
        Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault()?.ShowRetentionSetting();
    }

    private void InitializeRecovery()
    {
        // Ctrl+Z undoes this session's last delete; a focused text box keeps its own undo.
        InputBindings.Add(new KeyBinding(new RelayCommand(() =>
        {
            if (_undoBatch is { } batch) _ = UndoDeleteAsync(batch);
        }), new KeyGesture(Key.Z, ModifierKeys.Control)));
    }

    // ---- Status line action ---------------------------------------------------------------

    /// <summary>Shows a result with a follow-up action beside it; any later line hides the action.</summary>
    private void ShowActionStatus(string text, string? actionLabel = null, string? actionName = null, Func<Task>? action = null)
    {
        _statusAction = action;
        _statusActionText = action is null ? null : text;
        if (action is not null)
        {
            LibraryStatusAction.Content = actionLabel;
            AutomationProperties.SetName(LibraryStatusAction, actionName ?? actionLabel);
        }

        if (LibraryActionStatus.Text == text) LibraryActionStatus.Text = string.Empty;
        LibraryActionStatus.Text = text;
        SyncStatusAction();
    }

    private bool StatusActionShown => _statusAction is not null && !string.IsNullOrEmpty(LibraryActionStatus.Text)
        && LibraryActionStatus.Text == _statusActionText;

    /// <summary>The action belongs to the line it was shown with and disappears with it.</summary>
    private void SyncStatusAction()
    {
        bool shown = StatusActionShown;
        if (!shown && LibraryStatusAction.IsKeyboardFocused) MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        LibraryStatusActionPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnStatusActionClick(object sender, RoutedEventArgs e) => await RunStatusActionAsync();

    private Task RunStatusActionAsync()
    {
        if (!StatusActionShown) return Task.CompletedTask;
        Func<Task> action = _statusAction!;
        _statusAction = null;
        _statusActionText = null;
        SyncStatusAction();
        return action();
    }

    internal string StatusActionLabelForTest =>
        LibraryStatusActionPanel.Visibility == Visibility.Visible ? LibraryStatusAction.Content as string ?? string.Empty : string.Empty;

    internal Task InvokeStatusActionForTest() => RunStatusActionAsync();

    // ---- Delete and undo ------------------------------------------------------------------

    private async void ConfirmDeleteMany(IEnumerable<Guid> ids, bool entireLibrary) =>
        await DeleteCapturesAsync(ids, entireLibrary);

    /// <summary>
    /// Moves the captures to the Recycle Bin as one batch and reports the result on the status
    /// line with Undo. The view is rebuilt before the result appears, so no blank tile is left.
    /// </summary>
    internal async Task DeleteCapturesAsync(IEnumerable<Guid> ids, bool entireLibrary = false)
    {
        if (_deleteInProgress) return;
        Guid[] targets = ids.Distinct().ToArray();
        if (targets.Length == 0) return;
        _deleteInProgress = true;
        string? status = null;
        Guid? undo = null;
        try
        {
            if (!(ConfirmDelete ?? ConfirmDeleteWithDialog)(targets.Length, entireLibrary)) return;
            CloseInlinePlayer(); // MediaElement must release its source before directory cleanup.
            foreach (Guid id in targets) _viewModel.FindTile(id)?.SetThumbnailLoadingEnabled(false);
            Func<Guid, bool> canRemove = id => !_commitService.IsRecordBusy(id) && !_videoLibrary.IsBusy(id);
            Guid batch = Guid.NewGuid();
            CaptureBatchRemovalResult result;
            using (_controller.Recovery?.BeginBatch(batch))
                result = await _queue.RemoveManyAsync(targets, canRemove);
            IReadOnlyList<Guid> unavailable = result.RecycleUnavailableIds;
            CaptureBatchRemovalResult? permanent = await LibraryPermanentDeletion.ResolveAsync(unavailable,
                count => LibraryPermanentDeletion.Confirm(this, count),
                remaining => _queue.RemoveManyPermanentlyAsync(remaining, canRemove));
            status = DeleteStatus(targets.Length, result, permanent);
            if (result.RemovedCount > 0 && _controller.Recovery is not null) undo = batch;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not durably remove selected library entries");
            status = UiText.Get("LibrarySelection_DeleteFailed");
        }
        finally
        {
            _deleteInProgress = false;
            _viewModel.Refresh();
            _viewModel.SetThumbnailLoadingEnabled(IsVisible);
            RaiseCaptureChanged();
        }

        if (status is null) return;
        if (undo is { } restorable)
        {
            _undoBatch = restorable;
            ShowActionStatus(status, UiText.Get("LibraryUndo_Action"), UiText.Get("LibraryUndo_ActionName"),
                () => UndoDeleteAsync(restorable));
        }
        else
        {
            ShowActionStatus(status);
        }
    }

    /// <summary>
    /// "3개를 휴지통으로 옮김", then "· 2개 유지" only when something stayed. Permanent deletions the
    /// user confirmed, unfinished file cleanup and items Windows could not recycle follow it.
    /// </summary>
    internal static string DeleteStatus(int requested, CaptureBatchRemovalResult recycled, CaptureBatchRemovalResult? permanent)
    {
        int unavailable = permanent is null ? recycled.RecycleUnavailableIds.Count : 0;
        int removed = recycled.RemovedCount + (permanent?.RemovedCount ?? 0);
        var parts = new List<string>();
        if (recycled.RemovedCount > 0) parts.Add(UiText.Format("LibraryUndo_Moved", recycled.RemovedCount));
        if (permanent?.RemovedCount > 0) parts.Add(UiText.Format("LibraryUndo_DeletedPermanently", permanent.RemovedCount));
        int kept = requested - removed - unavailable;
        if (kept > 0) parts.Add(UiText.Format("LibraryUndo_Kept", kept));
        var lines = new List<string>();
        if (parts.Count > 0) lines.Add(string.Join(" · ", parts));
        int cleanupPending = recycled.CleanupPendingCount + (permanent?.CleanupPendingCount ?? 0);
        if (cleanupPending > 0) lines.Add(UiText.Format("LibrarySelection_CleanupPending", cleanupPending));
        if (unavailable > 0) lines.Add(LibraryPermanentDeletion.KeptStatus(unavailable));
        return lines.Count > 0 ? string.Join("\n", lines) : UiText.Format("LibraryUndo_Kept", requested);
    }

    /// <summary>Restores one delete command's captures together and reports how many came back.</summary>
    internal async Task UndoDeleteAsync(Guid batch)
    {
        if (_deleteInProgress || _controller.Recovery is not { } recovery) return;
        _deleteInProgress = true;
        if (_undoBatch == batch) _undoBatch = null;
        string status;
        try
        {
            ShowActionStatus(UiText.Get("LibraryUndo_Restoring"));
            LibraryBatchRestoreResult result = await recovery.RestoreBatchAsync(batch);
            status = result switch
            {
                { Restored: 0, Failed: > 0 } => UiText.Get("LibraryRecovery_Failed"),
                { Failed: > 0 } => UiText.Format("LibraryUndo_PartlyRestored", result.Restored, result.Failed),
                _ => UiText.Format("LibraryUndo_Restored", result.Restored),
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restore a deleted library batch");
            status = UiText.Get("LibraryRecovery_Failed");
        }
        finally
        {
            _deleteInProgress = false;
            _viewModel.Refresh();
            RaiseCaptureChanged();
        }

        ShowActionStatus(status);
    }

    // ---- Restore menu ---------------------------------------------------------------------

    private const double RestoreThumbnailSize = 32;

    private async Task PopulateDeletedCapturesAsync()
    {
        RestoreDeletedMenu.Items.Clear();
        RestoreDeletedMenu.Items.Add(new MenuItem { Header = UiText.Get("LibraryRecovery_Loading"), IsEnabled = false });
        DeletedLibraryOverview overview;
        ImageSource?[] thumbnails;
        int pixels = (int)Math.Ceiling(RestoreThumbnailSize * VisualTreeHelper.GetDpi(this).DpiScaleX);
        try
        {
            overview = _controller.Recovery is { } recovery
                ? await recovery.RecentDeletedOverviewAsync(20)
                : new DeletedLibraryOverview([], null, 0);
            thumbnails = await Task.Run(() => overview.Recent.Select(entry => LoadRestoreThumbnail(entry.ThumbnailPath, pixels)).ToArray());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not load recent deleted captures");
            RestoreDeletedMenu.Items.Clear();
            RestoreDeletedMenu.Items.Add(new MenuItem { Header = UiText.Get("LibraryRecovery_Failed"), IsEnabled = false });
            return;
        }

        RestoreDeletedMenu.Items.Clear();
        if (overview.Recent.Count == 0)
        {
            RestoreDeletedMenu.Items.Add(new MenuItem { Header = UiText.Get("LibraryRecovery_Empty"), IsEnabled = false });
            return;
        }

        if (overview.LastBatch is { } last && overview.LastBatchCount > 0)
        {
            // Ctrl+Z undoes this session's last delete; the gesture is shown only when that is this batch.
            var undo = new MenuItem
            {
                Header = UiText.Format("LibraryRestore_Last", overview.LastBatchCount),
                InputGestureText = _undoBatch == last ? "Ctrl+Z" : string.Empty,
            };
            undo.Click += async (_, _) => await UndoDeleteAsync(last);
            RestoreDeletedMenu.Items.Add(undo);
            RestoreDeletedMenu.Items.Add(new Separator());
        }

        DateTimeOffset now = RecoveryClock();
        for (int index = 0; index < overview.Recent.Count; index++)
        {
            DeletedLibraryCapture entry = overview.Recent[index].Capture;
            string label = RestoreEntryLabel(entry, now);
            var item = new MenuItem { Header = RestoreEntryHeader(label, thumbnails[index], entry.Record.IsVideo) };
            AutomationProperties.SetName(item, label);
            item.Click += async (_, _) => await RestoreDeletedCaptureAsync(entry.Token);
            RestoreDeletedMenu.Items.Add(item);
        }
    }

    /// <summary>"제목 · 3분 전 삭제": the title, or the kind when untitled, and when it was deleted.</summary>
    internal static string RestoreEntryLabel(DeletedLibraryCapture entry, DateTimeOffset now)
    {
        string name = string.IsNullOrWhiteSpace(entry.Record.Title)
            ? UiText.Get(entry.Record.IsVideo ? "LibraryRestore_Video" : "LibraryRestore_Image")
            : entry.Record.Title.Trim();
        return UiText.Format("LibraryRestore_Entry", name, RelativeDeletionTime(entry.DeletedAt, now));
    }

    /// <summary>방금, 3분 전, 2시간 전, 어제, 4일 전, then the date.</summary>
    internal static string RelativeDeletionTime(DateTimeOffset deletedAt, DateTimeOffset now)
    {
        TimeSpan elapsed = now - deletedAt;
        if (elapsed < TimeSpan.FromMinutes(1)) return UiText.Get("LibraryRestore_JustNow");
        if (elapsed < TimeSpan.FromHours(1)) return UiText.Format("LibraryRestore_MinutesAgo", (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromDays(1)) return UiText.Format("LibraryRestore_HoursAgo", (int)elapsed.TotalHours);
        int days = (now.ToLocalTime().Date - deletedAt.ToLocalTime().Date).Days;
        if (days <= 1) return UiText.Get("LibraryRestore_Yesterday");
        return days < 7
            ? UiText.Format("LibraryRestore_DaysAgo", days)
            : UiText.Format("LibraryRestore_OnDate", deletedAt.ToLocalTime().DateTime);
    }

    private static ImageSource? LoadRestoreThumbnail(string? path, int pixels)
    {
        if (path is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = pixels;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private object RestoreEntryHeader(string label, ImageSource? thumbnail, bool video)
    {
        var frame = new Border
        {
            Width = RestoreThumbnailSize, Height = RestoreThumbnailSize, CornerRadius = new CornerRadius(4),
            ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center,
        };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
        if (thumbnail is not null)
        {
            frame.Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill };
        }
        else
        {
            var glyph = new SymbolIcon
            {
                Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Data = TryFindResource(video ? "Icon.Record" : "Icon.Capture") as Geometry,
            };
            glyph.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Muted");
            frame.Child = glyph;
        }

        var text = new TextBlock
        {
            Text = label, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(frame);
        row.Children.Add(text);
        return row;
    }

    private async Task RestoreDeletedCaptureAsync(Guid token)
    {
        if (_deleteInProgress || _controller.Recovery is not { } recovery) return;
        _deleteInProgress = true;
        string status;
        try
        {
            await recovery.RestoreAsync(token);
            _viewModel.Refresh();
            RaiseCaptureChanged();
            status = UiText.Get("LibraryRecovery_Restored");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not restore a deleted library capture");
            status = UiText.Get("LibraryRecovery_Failed");
        }
        finally { _deleteInProgress = false; }
        ShowActionStatus(status);
    }

    // ---- Library folder -------------------------------------------------------------------

    private void OnOpenLibraryFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.GetFullPath(_paths.CapturesRoot);
            if (!Directory.Exists(folder))
            {
                ShowActionStatus(UiText.Get("Library.Source.OpenFolderFailed"));
                return;
            }

            OpenFolderInShell(folder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException or ArgumentException or NotSupportedException)
        {
            _log.LogWarning(ex, "Could not open the library folder");
            ShowActionStatus(UiText.Get("Library.Source.OpenFolderFailed"));
        }
    }

    // ---- First automatic deletion -------------------------------------------------------

    /// <summary>
    /// Once per installation, after the retention period first deletes images, the status line
    /// says how many and offers to change the period. Waits until the library is on screen.
    /// </summary>
    private void ShowPendingRetentionNotice()
    {
        if (!IsVisible || _controller.Recovery is not { } recovery || recovery.PendingRetentionNotice is null) return;
        if (recovery.TakeRetentionNotice() is not int count) return;
        ShowActionStatus(UiText.Format("Retention_Notice", count), UiText.Get("Retention_NoticeAction"),
            UiText.Get("Retention_NoticeActionName"), () =>
            {
                OpenRetentionSettings();
                return Task.CompletedTask;
            });
    }
}
