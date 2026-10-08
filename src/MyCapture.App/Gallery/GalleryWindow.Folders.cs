using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed record GalleryFolderDragPayload(Guid Owner, Guid[] Captures, Guid? FolderId)
{
    internal const string Format = "MyCapture.Folder410.Internal";
}

internal sealed partial class GalleryWindow
{
    private readonly Guid _folderDragOwner = Guid.NewGuid();
    private bool _foldersBusy;
    private bool _folderNavigationUpdating;
    private GalleryFolderNode? _folderDragNode;
    private Point _folderDragStart;
    private CancellationTokenSource? _folderDragCancellation;

    private void InitializeFolderActions()
    {
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnNewFolderClick(this, new RoutedEventArgs())),
            new KeyGesture(Key.N, ModifierKeys.Control | ModifierKeys.Shift)));
        PreviewMouseLeftButtonUp += (_, _) => CancelFolderDragPreparation();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelFolderDragPreparation(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CancelFolderDragPreparation(); };
    }

    private void RenameSelectedFolderOrCapture()
    {
        if (FolderTree.IsKeyboardFocusWithin)
        {
            if (_viewModel.HasFolderScope) OnRenameFolderClick(this, new RoutedEventArgs());
        }
        else OnOrganizeClick(this, new RoutedEventArgs());
    }

    private async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_folderNavigationUpdating || e.NewValue is not GalleryFolderNode node) return;
        await SelectFolderScopeAsync(node.Scope);
    }

    private async Task SelectFolderScopeAsync(CaptureFolderScope scope)
    {
        if (_viewModel.SelectedScope == scope) return;
        _folderNavigationUpdating = true;
        try { CloseInlinePlayer(); _viewModel.SelectScope(scope); }
        finally { _folderNavigationUpdating = false; }
        if (_queue.UseCurrentFolderForNewCaptures && _queue.DefaultCaptureFolderId != scope.FolderId)
        {
            try { await _queue.SetNewCaptureFolderAsync(scope.FolderId, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or AggregateException)
            { LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed"); }
            _viewModel.Refresh();
        }
    }

    private async void OnAllFolderDatesClick(object sender, RoutedEventArgs e) =>
        await SelectFolderScopeAsync(CaptureFolderScope.All);

    private async void OnFolderCaptureTargetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool enabled = CurrentFolderCaptureCheck.IsChecked == true;
            await _queue.SetNewCaptureFolderAsync(enabled ? _viewModel.SelectedScope.FolderId : null, enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or AggregateException)
        { LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed"); }
        finally { _viewModel.Refresh(); }
    }

    private CaptureFolderScope FolderScopeFromSender(object sender) =>
        sender is MenuItem { CommandParameter: GalleryFolderNode menuNode } ? menuNode.Scope
        : sender is FrameworkElement { DataContext: GalleryFolderNode node } ? node.Scope
        : _viewModel.SelectedScope;

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (_foldersBusy) return;
        Guid? parent = FolderScopeFromSender(sender).FolderId;
        CaptureFolder? created = null;
        _foldersBusy = true;
        bool saved;
        try
        {
            var dialog = GalleryFolderEditDialog.ForName(string.Empty, true, async name =>
            { created = await _queue.CreateFolderAsync(name, parent); });
            dialog.Owner = this;
            saved = dialog.ShowDialog() == true;
        }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
        if (saved && created is not null)
        {
            await SelectFolderScopeAsync(CaptureFolderScope.ForFolder(created.Id));
            RaiseCaptureChanged();
        }
    }

    private void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        Guid? id = FolderScopeFromSender(sender).FolderId;
        if (_foldersBusy || id is null || _queue.FindFolder(id.Value) is not { } folder) return;
        _foldersBusy = true;
        try
        {
            var dialog = GalleryFolderEditDialog.ForName(folder.Name, false, async name =>
            { await _queue.RenameFolderAsync(folder.Id, name); });
            dialog.Owner = this;
            if (dialog.ShowDialog() == true) RaiseCaptureChanged();
        }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
    }

    private void OnMoveFolderClick(object sender, RoutedEventArgs e)
    {
        Guid? id = FolderScopeFromSender(sender).FolderId;
        if (_foldersBusy || id is null || _queue.FindFolder(id.Value) is not { } folder) return;
        _foldersBusy = true;
        try
        {
            var dialog = GalleryFolderEditDialog.ForMove(_queue, folder.Id, folder.ParentId, async parent =>
            { await _queue.MoveFolderAsync(folder.Id, parent); });
            dialog.Owner = this;
            if (dialog.ShowDialog() == true) RaiseCaptureChanged();
        }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
    }

    private void OnMoveCapturesClick(object sender, RoutedEventArgs e)
    {
        if (_foldersBusy) return;
        GalleryItemViewModel? targeted = sender is MenuItem { CommandParameter: GalleryItemViewModel menuTile }
            ? menuTile : null;
        Guid[] ids = targeted is not null && !_viewModel.Selection.Contains(targeted.Id)
            ? [targeted.Id] : _viewModel.Selection.SelectedIds.ToArray();
        if (ids.Length == 0) return;
        _foldersBusy = true;
        try
        {
            var dialog = GalleryFolderEditDialog.ForMove(_queue, null, _viewModel.SelectedScope.FolderId, async destination =>
            {
                int moved = await _queue.MoveCapturesAsync(ids, destination);
                LibraryActionStatus.Text = UiText.Format("Folder410.MoveDone", moved);
            });
            dialog.Owner = this;
            if (dialog.ShowDialog() == true) RaiseCaptureChanged();
        }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
    }

    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        CaptureFolderScope scope = FolderScopeFromSender(sender);
        if (_deleteInProgress || _foldersBusy || scope.IsAll) return;
        if (scope.FolderId is { } staleId && _queue.FindFolder(staleId) is null)
        {
            _viewModel.Refresh();
            LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed");
            return;
        }
        Guid[] captureIds = _queue.RecordsInScope(scope).Select(record => record.Id).ToArray();
        Guid[] folderIds = _queue.FoldersInScope(scope).Select(folder => folder.Id).ToArray();
        CaptureFolderDeletionPreview preview = _queue.PreviewFolderDeletion(scope);
        string name = scope.FolderId is { } id
            ? _queue.GetFolderDate(id).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                + " / " + _queue.GetFolderDisplayPath(id)
            : scope.Date!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var dialog = new GalleryFolderDeletionDialog(name, preview) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _deleteInProgress = _foldersBusy = true;
        try
        {
            CloseInlinePlayer();
            foreach (Guid captureId in captureIds) _viewModel.FindTile(captureId)?.SetThumbnailLoadingEnabled(false);
            CaptureFolderDeletionResult result = await _queue.DeleteFolderScopeAsync(scope,
                captureId => !_commitService.IsRecordBusy(captureId) && !_videoLibrary.IsBusy(captureId),
                expectedCaptureIds: captureIds, expectedFolderIds: folderIds);
            string status = UiText.Format("Folder410.DeleteResult", result.RemovedFolders,
                result.RemovedCaptures, result.RemainingCaptures);
            if (result.CleanupPendingCount > 0)
                status += "\n" + UiText.Format("LibrarySelection_CleanupPending", result.CleanupPendingCount);
            LibraryActionStatus.Text = status;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not complete folder deletion");
            LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed");
        }
        finally
        {
            _deleteInProgress = _foldersBusy = false;
            _viewModel.Refresh();
            _viewModel.SetThumbnailLoadingEnabled(IsVisible);
            RaiseCaptureChanged();
        }
    }

    private void OnFolderTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Delete) { OnDeleteFolderClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.M && _viewModel.HasFolderScope)
        { OnMoveFolderClick(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private async void OnExportFolderClick(object sender, RoutedEventArgs e) =>
        await ExportFolderScopeAsync(FolderScopeFromSender(sender), zip: false);

    private async void OnExportFolderZipClick(object sender, RoutedEventArgs e) =>
        await ExportFolderScopeAsync(FolderScopeFromSender(sender), zip: true);

    private async Task ExportFolderScopeAsync(CaptureFolderScope scope, bool zip)
    {
        if (_archiveCancellation is not null) return;
        var exporter = new GalleryFolderExportService(_queue);
        GalleryFolderExportPlan plan;
        try { plan = exporter.BuildPlan(scope); }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "The selected folder export scope is no longer available");
            _viewModel.Refresh();
            LibraryActionStatus.Text = UiText.Get("Folder410.ExportFailed");
            return;
        }
        if (plan.RootNames.Count == 0 || plan.Entries.Any(entry => !EnsureRecordReady(entry.Record.Id))) return;
        string destination;
        if (zip)
        {
            string name = scope.FolderId is { } id ? _queue.FindFolder(id)!.Name
                : scope.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "MyCapture";
            var dialog = new SaveFileDialog { Title = UiText.Get("Folder410.ZipExport"), Filter = "ZIP (*.zip)|*.zip",
                DefaultExt = ".zip", AddExtension = true, CheckPathExists = true, OverwritePrompt = false,
                FileName = CaptureFolderNaming.SafeStem(name) + ".zip" };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = UiText.Get("Folder410.ExportDestination"), Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FolderName;
        }
        using var cancellation = new CancellationTokenSource();
        _archiveCancellation = cancellation;
        ArchiveExportButton.IsEnabled = false; ArchiveCancelButton.Visibility = Visibility.Visible;
        LibraryActionStatus.Text = UiText.Get("Folder410.ExportPreparing");
        try
        {
            using var prepared = await exporter.PrepareAsync(plan, cancellation.Token);
            var progress = new Progress<int>(done =>
            {
                if (ReferenceEquals(_archiveCancellation, cancellation))
                    LibraryActionStatus.Text = UiText.Format("Folder410.ExportProgress", done, plan.Entries.Count);
            });
            if (zip) await GalleryFolderExportService.ExportZipAsync(prepared, destination, progress, cancellation.Token);
            else await GalleryFolderExportService.ExportDirectoryAsync(prepared, destination, progress, cancellation.Token);
            LibraryActionStatus.Text = UiText.Format("Folder410.ExportDone", plan.Entries.Count);
        }
        catch (OperationCanceledException) { LibraryActionStatus.Text = UiText.Get("Library.Archive.Cancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not export a virtual folder tree");
            LibraryActionStatus.Text = UiText.Get("Folder410.ExportFailed");
        }
        finally
        {
            _archiveCancellation = null; ArchiveExportButton.IsEnabled = true;
            ArchiveCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private static GalleryFolderNode? NodeAt(object source)
    {
        for (DependencyObject? current = source as DependencyObject; current is not null;)
        {
            if (current is FrameworkElement { DataContext: GalleryFolderNode node }) return node;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnFolderPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_folderDragCancellation is not null || IsInsideButton(e.OriginalSource)) return;
        _folderDragNode = NodeAt(e.OriginalSource);
        _folderDragStart = e.GetPosition(this);
    }

    private async void OnFolderPointerMove(object sender, MouseEventArgs e)
    {
        if (_folderDragNode is not { } node || _folderDragCancellation is not null
            || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(this);
        if (Math.Abs(point.X - _folderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _folderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        using var cancellation = new CancellationTokenSource();
        _folderDragCancellation = cancellation;
        e.Handled = true;
        try
        {
            var exporter = new GalleryFolderExportService(_queue);
            GalleryFolderExportPlan plan = exporter.BuildPlan(node.Scope);
            if (plan.Entries.Any(entry => !EnsureRecordReady(entry.Record.Id))) return;
            using var prepared = await exporter.PrepareAsync(plan, cancellation.Token);
            if (!IsVisible || Mouse.LeftButton != MouseButtonState.Pressed || _folderDragNode != node) return;
            DataObject data = GalleryDragExportService.CreateFileDropData(prepared.Paths);
            if (node.FolderId is { } id)
                data.SetData(GalleryFolderDragPayload.Format, new GalleryFolderDragPayload(_folderDragOwner, [], id), false);
            prepared.MarkPublished();
            _ = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { LibraryActionStatus.Text = UiText.Get("Folder410.ExportFailed"); }
        finally { _folderDragCancellation = null; _folderDragNode = null; }
    }

    private void CancelFolderDragPreparation()
    {
        _folderDragNode = null;
        _folderDragCancellation?.Cancel();
    }

    private bool TryFolderDrop(DragEventArgs e, out GalleryFolderDragPayload? payload, out CaptureFolderScope target)
    {
        payload = e.Data.GetDataPresent(GalleryFolderDragPayload.Format, false)
            ? e.Data.GetData(GalleryFolderDragPayload.Format, false) as GalleryFolderDragPayload : null;
        target = NodeAt(e.OriginalSource)?.Scope ?? CaptureFolderScope.All;
        return !_foldersBusy && payload is not null && payload.Owner == _folderDragOwner && CanDropFolderPayload(_queue, payload, target);
    }

    internal static bool CanDropFolderPayload(CaptureQueue queue, GalleryFolderDragPayload payload, CaptureFolderScope target)
    {
        if (target.FolderId is { } targetId && queue.FindFolder(targetId) is null) return false;
        if (payload.FolderId is { } sourceId)
        {
            CaptureFolder? source = queue.FindFolder(sourceId);
            if (source is null) return false;
            if (target.FolderId is { } parent)
                return !queue.GetFolderPath(parent).Any(folder => folder.Id == sourceId);
            return target.Date is null || target.Date == source.CreatedDate;
        }
        if (payload.Captures.Length == 0) return false;
        foreach (Guid id in payload.Captures)
        {
            CaptureRecord? record = queue.Find(id);
            if (record is null || (target.Date is { } date
                && DateOnly.FromDateTime(record.CreatedAt.DateTime) != date)) return false;
        }
        return true;
    }

    // The OLE payload exposes rendered copies only. Internal drops move metadata
    // explicitly, while negotiating Copy keeps external receivers from consuming staging.
    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryFolderDrop(e, out _, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        bool allowed = TryFolderDrop(e, out var payload, out var target);
        e.Handled = true;
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        if (!allowed || payload is null) return;
        _foldersBusy = true;
        try
        {
            if (payload.FolderId is { } id) await _queue.MoveFolderAsync(id, target.FolderId);
            else
            {
                int moved = await _queue.MoveCapturesAsync(payload.Captures, target.FolderId);
                LibraryActionStatus.Text = UiText.Format("Folder410.MoveDone", moved);
            }
            RaiseCaptureChanged();
        }
        catch (ArgumentException) { LibraryActionStatus.Text = UiText.Get("Folder410.InvalidMove"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
        { LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed"); }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
    }
}
