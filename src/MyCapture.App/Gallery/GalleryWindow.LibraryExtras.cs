using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private int _activityRefreshPending;

    private void InitializeLibraryExtras()
    {
        SavedSearchList.ItemsSource = _queue.SavedSearches;
        SearchBox.TextChanged += (_, _) => RefreshSaveSearchCommand();
        RefreshSaveSearchCommand();
        _queue.Activities.Changed += OnLibraryActivitiesChanged;
        _queue.LibraryUndo.Recorded += OnLibraryUndoRecorded;
        Closed += (_, _) =>
        {
            _queue.Activities.Changed -= OnLibraryActivitiesChanged;
            _queue.LibraryUndo.Recorded -= OnLibraryUndoRecorded;
        };
        RefreshLibraryActivities();
    }

    /// <summary>Saving needs a query, so the command shows only while one is typed.</summary>
    private void RefreshSaveSearchCommand()
    {
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SaveSearchButton.IsEnabled = hasQuery;
        SaveSearchButton.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSaveSearchClick(object sender, RoutedEventArgs e) => EditSavedSearch(null);
    private void OnRenameSavedSearchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CaptureSavedSearch search) EditSavedSearch(search);
    }

    private void EditSavedSearch(CaptureSavedSearch? search)
    {
        string query = search?.Query ?? SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query)) return;
        var dialog = new GallerySavedSearchDialog(search?.Name ?? string.Empty, async name =>
        {
            if (await _queue.SaveSearchAsync(name, query, search?.Id) is null)
                throw new InvalidOperationException("The saved search no longer exists.");
        }) { Owner = this };
        if (dialog.ShowDialog() == true) ShowActionStatus(UiText.Get("Library.SavedSearch.Saved"));
    }

    private async void OnApplySavedSearchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CaptureSavedSearch search) return;
        await ShowLibraryViewAsync(GalleryFilter.All);
        SearchBox.Text = search.Query;
        _viewModel.SearchQuery = search.Query;
    }

    private async void OnDeleteSavedSearchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CaptureSavedSearch search) return;
        try
        {
            await _queue.DeleteSavedSearchAsync(search.Id);
            ShowActionStatus(UiText.Get("Library.SavedSearch.Deleted"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ShowActionStatus(UiText.Get("Library.SavedSearch.Failed")); }
    }

    private void OnLibraryUndoRecorded(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLibraryUndoRecorded(sender, e)));
            return;
        }
        if (!_queue.LibraryUndo.CanUndo
            || _queue.LibraryUndo.NextKind == LibraryUndoKind.Delete) return;
        string previousStatus = KoreanWordWrap.Strip(LibraryActionStatus.Text);
        // Let the command finish publishing its own result (including partial batch counts).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_queue.LibraryUndo.CanUndo) return;
            string status = string.IsNullOrWhiteSpace(KoreanWordWrap.Strip(LibraryActionStatus.Text)) || KoreanWordWrap.Strip(LibraryActionStatus.Text) == previousStatus
                ? UiText.Get("Library.Undo.Changed") : KoreanWordWrap.Strip(LibraryActionStatus.Text);
            ShowActionStatus(status, UiText.Get("LibraryUndo_Action"), UiText.Get("LibraryUndo_ActionName"), UndoLibraryAsync);
        }));
    }

    internal async Task UndoLibraryAsync()
    {
        if (_deleteInProgress || _foldersBusy || !_queue.LibraryUndo.CanUndo) return;
        try
        {
            LibraryUndoKind? requested = _queue.LibraryUndo.NextKind;
            LibraryUndoKind? kind = await _queue.LibraryUndo.UndoAsync();
            _viewModel.Refresh();
            RaiseCaptureChanged();
            if (requested == LibraryUndoKind.Delete || kind == LibraryUndoKind.Delete)
            {
                // Recovery reports exact restored/failed counts, including partial restores.
                if (_queue.LibraryUndo.CanUndo)
                    ShowActionStatus(KoreanWordWrap.Strip(LibraryActionStatus.Text), UiText.Get("LibraryUndo_Action"),
                        UiText.Get("LibraryUndo_ActionName"), UndoLibraryAsync);
                return;
            }
            string status = LibraryUndoStatus(kind);
            ShowActionStatus(status, UiText.Get("LibraryUndo_Action"), UiText.Get("LibraryUndo_ActionName"),
                _queue.LibraryUndo.CanUndo ? UndoLibraryAsync : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            ShowActionStatus(UiText.Get("Library.Undo.Failed"), UiText.Get("LibraryUndo_Action"),
                UiText.Get("LibraryUndo_ActionName"), UndoLibraryAsync);
        }
    }

    internal static string LibraryUndoStatus(LibraryUndoKind? kind) => kind switch
    {
        LibraryUndoKind.Move => UiText.Get("Library.Undo.Move"),
        LibraryUndoKind.Tags => UiText.Get("Library.Undo.Tags"),
        LibraryUndoKind.Pin => UiText.Get("Library.Undo.Pin"),
        LibraryUndoKind.Title => UiText.Get("Library.Undo.Title"),
        _ => UiText.Get("Library.Undo.Unavailable"),
    };

    internal static string LibraryActivityLabel(LibraryActivityKind kind) => kind switch
    {
        LibraryActivityKind.Import => UiText.Get("Library.Activity.Import"),
        LibraryActivityKind.ZipExport => UiText.Get("Library.Activity.ZipExport"),
        LibraryActivityKind.FolderExport => UiText.Get("Library.Activity.FolderExport"),
        LibraryActivityKind.Pdf => UiText.Get("Library.Activity.Pdf"),
        LibraryActivityKind.Ocr => UiText.Get("Library.Activity.Ocr"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void OnLibraryActivitiesChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _activityRefreshPending, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _activityRefreshPending, 0);
            RefreshLibraryActivities();
        }));
    }

    private void RefreshLibraryActivities()
    {
        IReadOnlyList<LibraryActivity> running = _queue.Activities.Running;
        ActivityButton.Visibility = running.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ActivityButton.Content = UiText.Format("Library.Activity.Count", running.Count);
        AutomationProperties.SetName(ActivityButton, UiText.Format("Library.Activity.Count", running.Count));
        if (ActivityPopup.IsOpen) PopulateLibraryActivities(running);
    }

    private void OnActivityClick(object sender, RoutedEventArgs e)
    {
        ActivityPopup.IsOpen = !ActivityPopup.IsOpen;
        if (ActivityPopup.IsOpen) PopulateLibraryActivities(_queue.Activities.Running);
    }

    private void PopulateLibraryActivities(IReadOnlyList<LibraryActivity> running)
    {
        Guid? focused = (Keyboard.FocusedElement as FrameworkElement)?.Tag as Guid?;
        Button? restoreFocus = null;
        ActivityRows.Children.Clear();
        if (running.Count == 0) { ActivityPopup.IsOpen = false; return; }
        foreach (LibraryActivity activity in running)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var cancel = ThemedDialog.CreateActionButton(UiText.Get(activity.CancellationRequested
                ? "Library.Activity.Cancelling" : "Library.Action.Cancel"), primary: false);
            cancel.IsEnabled = activity.CanCancel;
            cancel.Tag = activity.Id;
            if (focused == activity.Id && activity.CanCancel) restoreFocus = cancel;
            cancel.Visibility = activity.CanCancel || activity.CancellationRequested ? Visibility.Visible : Visibility.Collapsed;
            cancel.Click += (_, _) => _queue.Activities.Cancel(activity.Id);
            DockPanel.SetDock(cancel, Dock.Right);
            row.Children.Add(cancel);
            var details = new StackPanel { MinWidth = 200, Margin = new Thickness(0, 0, 12, 0) };
            string label = LibraryActivityLabel(activity.Kind);
            var text = new TextBlock { Text = activity.Total is { } total
                ? UiText.Format("Library.Activity.Progress", label, activity.Completed, total) : label };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            details.Children.Add(text);
            var progress = new ProgressBar { Height = 4, Margin = new Thickness(0, 6, 0, 0),
                IsIndeterminate = activity.Total is null, Maximum = Math.Max(1, activity.Total ?? 1), Value = activity.Completed };
            AutomationProperties.SetName(progress, text.Text);
            details.Children.Add(progress);
            row.Children.Add(details);
            ActivityRows.Children.Add(row);
        }
        if (restoreFocus is not null) restoreFocus.Focus();
        else if (focused is not null) ActivityButton.Focus();
    }
}

internal sealed class GallerySavedSearchDialog : Window
{
    private bool _saving;
    internal GallerySavedSearchDialog(string name, Func<string, Task> save)
    {
        StandardWindowTheme.Apply(this);
        Title = UiText.Get(name.Length == 0 ? "Library.SavedSearch.Save" : "Library.SavedSearch.Rename");
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var content = new StackPanel { Margin = new Thickness(24) };
        var input = new TextBox { Text = name, MaxLength = 80, MinHeight = 36 };
        content.Children.Add(new Label { Content = UiText.Get("Library.SavedSearch.Name"), Target = input });
        AutomationProperties.SetName(input, UiText.Get("Library.SavedSearch.Name"));
        content.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        content.Children.Add(error);
        var apply = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Save"), primary: true);
        var cancel = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        apply.IsDefault = true; cancel.IsCancel = true;
        content.Children.Add(ThemedDialog.CreateActionRow(apply, cancel));
        Content = content;
        Closing += (_, e) => e.Cancel = _saving;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        apply.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; apply.IsEnabled = cancel.IsEnabled = input.IsEnabled = false;
            bool saved = false;
            try { await save(input.Text); saved = true; }
            catch (ArgumentException) { KoreanWordWrap.Show(error, UiText.Get("Library.SavedSearch.Invalid")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { KoreanWordWrap.Show(error, UiText.Get("Library.SavedSearch.Failed")); }
            finally { _saving = false; apply.IsEnabled = cancel.IsEnabled = input.IsEnabled = true; }
            if (saved) DialogResult = true;
        };
    }
}
