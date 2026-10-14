using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool _folderDragging;
    private CancellationTokenSource? _folderDragCancellation;

    private void InitializeFolderActions()
    {
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnNewFolderClick(this, new RoutedEventArgs())),
            new KeyGesture(Key.N, ModifierKeys.Control | ModifierKeys.Shift)));
        PreviewMouseLeftButtonUp += OnFolderPointerUp;
        PreviewMouseMove += OnFolderPointerMove;
        PreviewKeyDown += OnNavigationKeyDown;
        PreviewMouseDown += OnNavigationMouseDown;
        IsVisibleChanged += (_, _) => { if (!IsVisible) CancelFolderDragPreparation(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CancelFolderDragPreparation(); };
    }

    private void RenameSelectedFolderOrCapture()
    {
        if (FolderTree.IsKeyboardFocusWithin)
        {
            OnRenameFolderClick(this, new RoutedEventArgs());
        }
        else if (Keyboard.FocusedElement is FrameworkElement { DataContext: GalleryFolderCardViewModel card })
            OnRenameFolderClick(card, new RoutedEventArgs());
        else OnOrganizeClick(this, new RoutedEventArgs());
    }

    internal static bool IsNavigationTextInput(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
            if (current is TextBoxBase or PasswordBox or ComboBox { IsEditable: true }) return true;
        return false;
    }

    internal static bool? NavigationKeyDirection(Key key, ModifierKeys modifiers, bool textInput) =>
        (key, modifiers, textInput) switch
        {
            (Key.Back, ModifierKeys.None, false) => false,
            (Key.Left, ModifierKeys.Alt, _) => false,
            (Key.Right, ModifierKeys.Alt, _) => true,
            _ => null,
        };

    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (e.Handled || _foldersBusy || NavigationKeyDirection(key, Keyboard.Modifiers,
            IsNavigationTextInput(Keyboard.FocusedElement as DependencyObject)) is not { } forward) return;
        if (!(forward ? _viewModel.CanNavigateForward : _viewModel.CanNavigateParent)) return;
        e.Handled = true;
        await NavigateLibraryAsync(forward);
    }

    private async void OnNavigationMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _foldersBusy || e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        bool forward = e.ChangedButton == MouseButton.XButton2;
        if (!(forward ? _viewModel.CanNavigateForward : _viewModel.CanNavigateParent)) return;
        e.Handled = true;
        await NavigateLibraryAsync(forward);
    }

    private async Task<bool> NavigateLibraryAsync(bool forward)
    {
        bool navigated;
        _folderNavigationUpdating = true;
        try
        {
            navigated = forward ? _viewModel.NavigateForward() : _viewModel.NavigateParent();
            if (navigated) CloseInlinePlayer();
        }
        finally { _folderNavigationUpdating = false; }
        if (navigated) await FollowSavedDestinationAsync(_viewModel.SelectedScope);
        return navigated;
    }

    private async void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_folderNavigationUpdating || _viewModel.IsRefreshingFolderTree
            || e.NewValue is not GalleryFolderNode node
            || node.FolderId is { } id && id == _viewModel.RevealedFolderId) return;
        await SelectFolderScopeAsync(node.Scope);
    }

    private async void OnDateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_folderNavigationUpdating || _viewModel.IsRefreshingFolderTree
            || e.AddedItems is not [GalleryFolderNode node]) return;
        await SelectFolderScopeAsync(node.Scope);
    }

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryBreadcrumb { IsLink: true } step })
            await SelectFolderScopeAsync(step.Scope);
    }

    private async Task SelectFolderScopeAsync(CaptureFolderScope scope)
    {
        if (_viewModel.SelectedScope == scope && _viewModel.RevealedFolderId is null) return;
        ShowScope(scope);
        await FollowSavedDestinationAsync(scope);
    }

    /// <summary>A navigation pane view: shows the whole library in one step, then follows the saved destination.</summary>
    private async Task ShowLibraryViewAsync(GalleryFilter filter)
    {
        bool leftLocation = !_viewModel.SelectedScope.IsAll;
        _folderNavigationUpdating = true;
        try { CloseInlinePlayer(); _viewModel.ShowView(filter); }
        finally { _folderNavigationUpdating = false; }
        if (leftLocation) await FollowSavedDestinationAsync(CaptureFolderScope.All);
    }

    /// <summary>The saved keep-after-close destination follows the location the library shows.</summary>
    private async Task FollowSavedDestinationAsync(CaptureFolderScope scope)
    {
        if (_queue.UseCurrentFolderForNewCaptures && _queue.DefaultCaptureFolderId != scope.FolderId)
        {
            try { await _queue.SetNewCaptureFolderAsync(scope.FolderId, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or AggregateException)
            { LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed"); }
            _viewModel.Refresh();
        }
    }

    /// <summary>
    /// Shows a scope without changing the saved keep-after-close destination. A folder or date
    /// opens as a location without a media view, like any other navigation pane selection.
    /// </summary>
    private void ShowScope(CaptureFolderScope scope)
    {
        if (_viewModel.SelectedScope == scope && _viewModel.SelectedTag is null
            && _viewModel.RevealedFolderId is null && (scope.IsAll || _viewModel.Filter == GalleryFilter.All)) return;
        _folderNavigationUpdating = true;
        try { CloseInlinePlayer(); _viewModel.NavigateTo(scope); }
        finally { _folderNavigationUpdating = false; }
    }

    private async void OnFolderCaptureTargetClick(object sender, RoutedEventArgs e)
    {
        try { await _viewModel.SetFolderCaptureEnabledAsync(CurrentFolderCaptureCheck.IsChecked == true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or AggregateException)
        { LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed"); }
    }

    private async void OnFolderCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryFolderCardViewModel card })
            await OpenFolderCardAsync(card, fromKeyboard: false);
    }

    private async void OnFolderCardKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GalleryFolderCardViewModel card }
            || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                e.Handled = true;
                await OpenFolderCardAsync(card, fromKeyboard: true);
                break;
            case Key.Left:
            case Key.Right:
                e.Handled = true;
                if (GallerySelectionNavigation.FindFolderNeighbor(_viewModel.Rows, card, e.Key == Key.Left ? -1 : 1)
                    is { } neighbor) FocusFolderCard(neighbor);
                break;
            case Key.Up:
            case Key.Down:
                e.Handled = true;
                object? target = GallerySelectionNavigation.FindVertical(_viewModel.Rows, card, e.Key == Key.Up ? -1 : 1);
                if (target is GalleryFolderCardViewModel above) FocusFolderCard(above);
                else if (target is GalleryItemViewModel tile)
                {
                    _viewModel.Select(tile.Id);
                    GalleryTileRow? row = _viewModel.Rows.OfType<GalleryTileRow>().FirstOrDefault(r => r.Tiles.Contains(tile));
                    if (row is not null) RowsList.ScrollIntoView(row);
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusTile(RowsList, tile)));
                }
                break;
        }
    }

    private async Task OpenFolderCardAsync(GalleryFolderCardViewModel card, bool fromKeyboard)
    {
        if (_queue.FindFolder(card.FolderId) is null) { _viewModel.Refresh(); return; }
        await SelectFolderScopeAsync(card.Scope);
        if (!fromKeyboard) return;
        // Keep keyboard users in the opened folder: its first card or capture, else the tree.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            GalleryRow? first = _viewModel.Rows.FirstOrDefault(row => row is GalleryFolderRow or GalleryTileRow);
            if (first is GalleryFolderRow { Folders.Count: > 0 } folders) FocusFolderCard(folders.Folders[0]);
            else if (first is GalleryTileRow { Tiles.Count: > 0 } tiles)
            {
                _viewModel.Select(tiles.Tiles[0].Id);
                RowsList.ScrollIntoView(tiles);
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusTile(RowsList, tiles.Tiles[0])));
            }
            else _ = FolderTree.Focus();
        }));
    }

    private void FocusFolderCard(GalleryFolderCardViewModel card)
    {
        GalleryFolderRow? row = _viewModel.Rows.OfType<GalleryFolderRow>().FirstOrDefault(r => r.Folders.Contains(card));
        if (row is null) return;
        RowsList.ScrollIntoView(row);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusFolderCard(RowsList, card)));
    }

    private static bool FocusFolderCard(DependencyObject parent, GalleryFolderCardViewModel card)
    {
        if (parent is Button { DataContext: GalleryFolderCardViewModel candidate } button && candidate == card)
            return button.Focus();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FocusFolderCard(VisualTreeHelper.GetChild(parent, i), card)) return true;
        return false;
    }

    private CaptureFolderScope FolderScopeFromSender(object sender) =>
        sender is GalleryFolderCardViewModel card ? card.Scope
        : sender is MenuItem { CommandParameter: GalleryFolderNode menuNode } ? menuNode.Scope
        : sender is FrameworkElement { DataContext: GalleryFolderNode node } ? node.Scope
        : FolderTree.IsKeyboardFocusWithin && FolderTree.SelectedItem is GalleryFolderNode selected ? selected.Scope
        : _viewModel.SelectedScope;

    private void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (_foldersBusy) return;
        CaptureFolderScope scope = sender is MenuItem or FrameworkElement { DataContext: GalleryFolderNode }
            ? FolderScopeFromSender(sender) : _viewModel.SelectedScope;
        CaptureFolder? created = null;
        _foldersBusy = true;
        bool saved;
        try
        {
            var dialog = GalleryFolderEditDialog.ForName(string.Empty, true, async name =>
            { created = await _viewModel.CreateFolderAsync(name, scope); });
            dialog.Owner = this;
            saved = dialog.ShowDialog() == true;
        }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
        if (saved && created is not null)
        {
            RaiseCaptureChanged();
            RevealCreatedFolder(created.Id);
        }
    }

    internal void RevealCreatedFolder(Guid id)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            if (!await RevealFolderNodeAsync(id)) return;
            if (_viewModel.FindFolderCard(id) is { } card) FocusFolderCard(card);
        }));
    }

    private async Task<bool> RevealFolderNodeAsync(Guid id)
    {
        ItemsControl parent = FolderTree;
        foreach (CaptureFolder folder in _queue.GetFolderPath(id))
        {
            if (!IsVisible || _viewModel.RevealedFolderId != id) return false;
            GalleryFolderNode? node = parent.Items.OfType<GalleryFolderNode>()
                .FirstOrDefault(candidate => candidate.FolderId == folder.Id);
            if (node is null) return false;
            parent.UpdateLayout();
            // Realize only this ancestor path, including nodes outside the recycled viewport.
            if (FindVisualChild<ItemsPresenter>(parent) is { } presenter
                && VisualTreeHelper.GetChildrenCount(presenter) > 0
                && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingPanel panel)
                panel.BringIndexIntoViewPublic(parent.Items.IndexOf(node));
            await Dispatcher.InvokeAsync(parent.UpdateLayout, DispatcherPriority.Loaded);
            if (!IsVisible || _viewModel.RevealedFolderId != id) return false;
            if (parent.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem item) return false;
            if (node.FolderId == id)
            {
                item.BringIntoView();
                item.Focus();
                return true;
            }
            item.IsExpanded = true;
            parent = item;
        }
        return false;
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
                LibraryActionStatus.Text = UiText.Format("Folder410.MoveDone", moved)
                    + (destination is null ? " " + UiText.Get("Folder410.RetentionRestarted") : string.Empty);
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
        var dialog = new GalleryFolderDeletionDialog(name, preview, scope.FolderId is null) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _deleteInProgress = _foldersBusy = true;
        try
        {
            CloseInlinePlayer();
            foreach (Guid captureId in captureIds) _viewModel.FindTile(captureId)?.SetThumbnailLoadingEnabled(false);
            Func<Guid, bool> canRemove = captureId => !_commitService.IsRecordBusy(captureId) && !_videoLibrary.IsBusy(captureId);
            CaptureFolderDeletionResult result = await _queue.DeleteFolderScopeAsync(scope, canRemove,
                expectedCaptureIds: captureIds, expectedFolderIds: folderIds);
            IReadOnlyList<Guid> unavailable = result.RecycleUnavailableIds;
            CaptureFolderDeletionResult? permanent = await LibraryPermanentDeletion.ResolveAsync(unavailable,
                count => LibraryPermanentDeletion.Confirm(this, count),
                ids => _queue.DeleteFolderScopeAsync(scope, captureId => ids.Contains(captureId) && canRemove(captureId),
                    permanent: true));
            int cleanupPending = result.CleanupPendingCount + (permanent?.CleanupPendingCount ?? 0);
            string status = UiText.Format("Folder410.DeleteResult", result.RemovedFolders + (permanent?.RemovedFolders ?? 0),
                result.RemovedCaptures + (permanent?.RemovedCaptures ?? 0), permanent?.RemainingCaptures ?? result.RemainingCaptures);
            if (cleanupPending > 0)
                status += "\n" + UiText.Format("LibrarySelection_CleanupPending", cleanupPending);
            if (unavailable.Count > 0 && permanent is null) status += "\n" + LibraryPermanentDeletion.KeptStatus(unavailable.Count);
            LibraryActionStatus.Text = status;
        }
        catch (CaptureFolderChangedException)
        {
            LibraryActionStatus.Text = UiText.Get("Folder410.DeleteChanged");
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

    private async void OnFolderTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Enter && FolderTree.SelectedItem is GalleryFolderNode selected)
        { e.Handled = true; await SelectFolderScopeAsync(selected.Scope); }
        else if (e.Key == Key.Delete) { OnDeleteFolderClick(this, new RoutedEventArgs()); e.Handled = true; }
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
            LibraryActionStatus.Text = ExportFailureMessage(ex);
            return;
        }
        if (plan.RootNames.Count == 0 || plan.Entries.Any(entry => !EnsureRecordReady(entry.Record.Id))) return;
        var options = MyCapture.App.Editing.ImageExportOptionsPanel.ShowCopyOptions(this, _commitService.LastExportOptions);
        if (options is null) return;
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
            if (zip) destination = UniqueArchivePath(destination);
            using var prepared = await exporter.PrepareAsync(plan, cancellation.Token);
            var progress = new Progress<int>(done =>
            {
                if (ReferenceEquals(_archiveCancellation, cancellation))
                    LibraryActionStatus.Text = UiText.Format("Folder410.ExportProgress", done, plan.Entries.Count);
            });
            if (zip) await GalleryFolderExportService.ExportZipAsync(prepared, destination, progress, cancellation.Token, options);
            else await GalleryFolderExportService.ExportDirectoryAsync(prepared, destination, progress, cancellation.Token, options);
            _commitService.RememberExportOptions(options);
            LibraryActionStatus.Text = UiText.Format("Folder410.ExportDone", plan.Entries.Count)
                + (zip ? " · " + Path.GetFileName(destination) : string.Empty);
        }
        catch (OperationCanceledException) { LibraryActionStatus.Text = UiText.Get("Library.Archive.Cancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not export a virtual folder tree");
            LibraryActionStatus.Text = ExportFailureMessage(ex);
        }
        finally
        {
            _archiveCancellation = null; ArchiveExportButton.IsEnabled = true;
            ArchiveCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    internal static string UniqueArchivePath(string requested)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(requested))!;
        string stem = Path.GetFileNameWithoutExtension(requested);
        if (!string.Equals(Path.GetExtension(requested), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a ZIP filename.");
        string candidate = GalleryExportFiles.Child(parent, Path.GetFileName(requested));
        for (int suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            if (suffix > 10_000) throw new IOException("No available archive name.");
            candidate = GalleryExportFiles.Child(parent, stem + "~" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".zip");
        }
        return candidate;
    }

    private static string ExportFailureMessage(Exception ex)
    {
        int code = ex.InnerException is System.ComponentModel.Win32Exception native
            ? native.NativeErrorCode : ex.HResult & 0xffff;
        string key = ex switch
        {
            PathTooLongException => "Folder410.ExportPathTooLong",
            UnauthorizedAccessException => "Dialog.Access",
            FileNotFoundException or DirectoryNotFoundException => "Folder410.ExportMissing",
            InvalidOperationException => "Folder410.ExportChanged",
            _ when code is 80 or 183 => "Folder410.ExportNameExists",
            _ when code is 32 or 33 => "Folder410.ExportLocked",
            _ when code is 39 or 112 => "Folder410.ExportDiskFull",
            _ => "Dialog.Storage",
        };
        return UiText.Get("Folder410.ExportFailed") + " " + UiText.Get(key);
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

    private async void OnFolderPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_folderDragCancellation is not null || IsInsideButton(e.OriginalSource)) return;
        if (_foldersBusy) return;
        _folderDragging = false;
        _folderDragNode = NodeAt(e.OriginalSource);
        _folderDragStart = e.GetPosition(this);
        if (_folderDragNode?.FolderId is { } id && id == _viewModel.RevealedFolderId)
            await SelectFolderScopeAsync(CaptureFolderScope.ForFolder(id));
    }

    private async void OnFolderPointerMove(object sender, MouseEventArgs e)
    {
        if (_folderDragNode is not null && e.LeftButton != MouseButtonState.Pressed)
        { CancelFolderDragPreparation(); return; }
        if (_folderDragNode is not { } node || _folderDragCancellation is not null
            || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(this);
        if (Math.Abs(point.X - _folderDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _folderDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _folderDragging = true;
        if (!IsMouseCaptured) CaptureMouse();
        e.Handled = true;
        if (IsInsideFolderDragWindow(point)) return; // Internal moves never stage media.
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
            ReleaseMouseCapture();
            DataObject data = GalleryDragExportService.CreateFileDropData(prepared.Paths);
            if (node.FolderId is { } id)
                data.SetData(GalleryFolderDragPayload.Format, new GalleryFolderDragPayload(_folderDragOwner, [], id), false);
            _ = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { LibraryActionStatus.Text = ExportFailureMessage(ex); }
        finally { _folderDragCancellation = null; CancelFolderDragPreparation(); }
    }

    private bool IsInsideFolderDragWindow(Point point) =>
        new Rect(0, 0, ActualWidth, ActualHeight).Contains(point);

    private async void OnFolderPointerUp(object sender, MouseButtonEventArgs e)
    {
        GalleryFolderNode? source = _folderDragNode;
        bool internalMove = _folderDragging && _folderDragCancellation is null && IsInsideFolderDragWindow(e.GetPosition(this));
        var hit = InputHitTest(e.GetPosition(this));
        CaptureFolderScope target = NodeAt(hit!)?.Scope ?? CaptureFolderScope.All;
        bool treeTarget = hit is DependencyObject obj && IsFolderTreeTarget(obj);
        CancelFolderDragPreparation();
        if (!internalMove || source?.FolderId is not { } id || !treeTarget) return;
        e.Handled = true;
        var payload = new GalleryFolderDragPayload(_folderDragOwner, [], id);
        if (CanDropFolderPayload(_queue, payload, target)) await ApplyFolderDropAsync(payload, target);
        else LibraryActionStatus.Text = UiText.Get("Folder410.InvalidMove");
    }

    private bool IsFolderTreeTarget(DependencyObject current)
    {
        while (true)
        {
            if (ReferenceEquals(current, FolderTree) || ReferenceEquals(current, DateList)) return true;
            DependencyObject? parent = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            if (parent is null) return false;
            current = parent;
        }
    }

    private void CancelFolderDragPreparation()
    {
        _folderDragNode = null;
        _folderDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _folderDragCancellation?.Cancel();
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

    // ---- Dropping captures on folders --------------------------------------------------
    //
    // One handler on the library root serves folder cards, the folder tree, dates and "라이브러리".
    // The OLE payload exposes rendered copies only. Internal drops move metadata explicitly,
    // while negotiating Copy keeps external receivers from consuming staging.

    private object? _dropHighlight;

    internal GalleryFolderDragPayload CreateCaptureDragPayload(IEnumerable<Guid> captureIds) =>
        new(_folderDragOwner, captureIds.Distinct().ToArray(), null);

    /// <summary>
    /// The data a capture drag carries: the staged files every external target (desktop, File
    /// Explorer) receives, plus this window's private payload that only the library reads.
    /// </summary>
    internal static DataObject CreateCaptureDragData(IReadOnlyList<string> stagedPaths, GalleryFolderDragPayload payload)
    {
        DataObject data = GalleryDragExportService.CreateFileDropData(stagedPaths);
        data.SetData(GalleryFolderDragPayload.Format, payload, false);
        return data;
    }

    private static GalleryFolderDragPayload? ReadDragPayload(IDataObject data) =>
        data.GetDataPresent(GalleryFolderDragPayload.Format, false)
            ? data.GetData(GalleryFolderDragPayload.Format, false) as GalleryFolderDragPayload : null;

    private void OnLibraryDragOver(object sender, DragEventArgs e)
    {
        if (PreviewExternalImport(e)) return;
        GalleryDropPlan plan = PreviewFolderDrop(e.OriginalSource as DependencyObject, ReadDragPayload(e.Data),
            e.GetPosition(DragHintLayer));
        e.Effects = plan.CanDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // Leaving one element for another inside the library is followed by DragOver; only
    // leaving the window clears the highlight and hint.
    private void OnLibraryDragLeave(object sender, DragEventArgs e)
    {
        if (_importCancellation is null) ImportOverlay.Visibility = Visibility.Collapsed;
        Point point = e.GetPosition(LibraryRoot);
        if (point.X < 0 || point.Y < 0 || point.X >= LibraryRoot.ActualWidth || point.Y >= LibraryRoot.ActualHeight)
            ClearDropHighlight();
    }

    private async void OnLibraryDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        GalleryFolderDragPayload? payload = ReadDragPayload(e.Data);
        if (!e.Data.GetDataPresent(GalleryFolderDragPayload.Format, false))
        {
            string[] paths = ExternalImportPaths(e.Data);
            bool import = paths.Length > 0 && IsOverImportArea(e) && _importCancellation is null
                && e.AllowedEffects.HasFlag(DragDropEffects.Copy);
            if (_importCancellation is null) ImportOverlay.Visibility = Visibility.Collapsed;
            e.Effects = import ? DragDropEffects.Copy : DragDropEffects.None;
            if (import) await ImportFilesAsync(paths);
            return;
        }
        bool dropped = await DropAsync(e.OriginalSource as DependencyObject, payload);
        e.Effects = dropped ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// Resolves the target under the pointer, highlights it and shows the pointer-side hint.
    /// Also the test seam for the drag-over path.
    /// </summary>
    internal GalleryDropPlan PreviewFolderDrop(DependencyObject? over, GalleryFolderDragPayload? payload, Point hintPosition)
    {
        GalleryDropPlan plan = PlanDrop(over, payload, out object? highlight);
        SetDropHighlight(plan.CanDrop ? highlight : null);
        if (plan.CanDrop) ShowDragHint(plan.Hint, hintPosition);
        else DragHint.Visibility = Visibility.Collapsed;
        return plan;
    }

    /// <summary>Moves the payload into the target under <paramref name="over"/>; false when nothing moved.</summary>
    internal async Task<bool> DropAsync(DependencyObject? over, GalleryFolderDragPayload? payload)
    {
        GalleryDropPlan plan = PlanDrop(over, payload, out _);
        ClearDropFeedback();
        if (!plan.CanDrop || payload is null) return false;
        await ApplyFolderDropAsync(payload, plan.Target);
        return true;
    }

    private GalleryDropPlan PlanDrop(DependencyObject? over, GalleryFolderDragPayload? payload, out object? highlight)
    {
        (CaptureFolderScope? target, highlight, bool folderCard) = ResolveDropTarget(over);
        return _foldersBusy ? default
            : GalleryDropPlanner.Plan(_queue, _folderDragOwner, payload, target, folderCard);
    }

    private (CaptureFolderScope? Target, object? Highlight, bool FolderCard) ResolveDropTarget(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is FrameworkElement { DataContext: GalleryFolderCardViewModel card }) return (card.Scope, card, true);
            if (current is FrameworkElement { DataContext: GalleryFolderNode node }) return (node.Scope, node, false);
            if (ReferenceEquals(current, AllLibraryFilter)) return (CaptureFolderScope.All, AllLibraryFilter, false);
            // Empty space in the folder list keeps its earlier meaning: the top level, out of folders.
            if (ReferenceEquals(current, FolderTree) || ReferenceEquals(current, DateList)) return (CaptureFolderScope.All, null, false);
            if (ReferenceEquals(current, this)) break;
        }
        return (null, null, false);
    }

    private void SetDropHighlight(object? target)
    {
        if (ReferenceEquals(_dropHighlight, target)) return;
        switch (_dropHighlight)
        {
            case GalleryFolderCardViewModel card: card.IsDropTarget = false; break;
            case GalleryFolderNode node: node.IsDropTarget = false; break;
            case RadioButton: _viewModel.IsAllItemsDropTarget = false; break;
        }
        _dropHighlight = target;
        switch (target)
        {
            case GalleryFolderCardViewModel card: card.IsDropTarget = true; break;
            case GalleryFolderNode node: node.IsDropTarget = true; break;
            case RadioButton: _viewModel.IsAllItemsDropTarget = true; break;
        }
    }

    private void ShowDragHint(string text, Point position)
    {
        DragHintText.Text = text;
        DragHint.Visibility = Visibility.Visible;
        DragHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Below and right of the pointer, kept inside the window.
        double left = Math.Clamp(position.X + 16, 0, Math.Max(0, DragHintLayer.ActualWidth - DragHint.DesiredSize.Width));
        double top = Math.Clamp(position.Y + 22, 0, Math.Max(0, DragHintLayer.ActualHeight - DragHint.DesiredSize.Height));
        Canvas.SetLeft(DragHint, left);
        Canvas.SetTop(DragHint, top);
    }

    private void ClearDropHighlight()
    {
        SetDropHighlight(null);
        DragHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ends all drag feedback: highlight, hint and the folders marked as drop targets.</summary>
    private void ClearDropFeedback()
    {
        ClearDropHighlight();
        _viewModel.EndCaptureDrag();
    }

    private async Task ApplyFolderDropAsync(GalleryFolderDragPayload payload, CaptureFolderScope target)
    {
        _foldersBusy = true;
        try
        {
            if (payload.FolderId is { } id) await _queue.MoveFolderAsync(id, target.FolderId);
            else
            {
                int moved = await _queue.MoveCapturesAsync(payload.Captures, target.FolderId);
                LibraryActionStatus.Text = target.FolderId is { } folderId && _queue.FindFolder(folderId) is { } folder
                    ? UiText.Format("LibraryUx_MovedTo", folder.Name, moved)
                    : UiText.Format("Folder410.MoveDone", moved) + " " + UiText.Get("Folder410.RetentionRestarted");
            }
            RaiseCaptureChanged();
        }
        catch (ArgumentException) { LibraryActionStatus.Text = UiText.Get("Folder410.InvalidMove"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
        { LibraryActionStatus.Text = UiText.Get("Folder410.OperationFailed"); }
        finally { _foldersBusy = false; _viewModel.Refresh(); }
    }
}
