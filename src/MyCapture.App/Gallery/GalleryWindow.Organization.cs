using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private CancellationTokenSource? _archiveCancellation;

    private void OnOrganizeClick(object sender, RoutedEventArgs e)
    {
        GalleryItemViewModel? tile = (sender as FrameworkElement)?.DataContext as GalleryItemViewModel
            ?? _viewModel.SingleSelectedTile;
        if (tile is null) return;
        var dialog = new GalleryOrganizationDialog(tile.Record, (title, tags) =>
            _controller.UpdateOrganizationAsync(tile.Id, title, tags), _log) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        tile.RaiseMetaChanged();
        _viewModel.Refresh();
        CaptureChanged?.Invoke(this, EventArgs.Empty);
        KoreanWordWrap.Show(LibraryActionStatus, UiText.Get("Library.Organize.Saved"));
    }

    private async void OnExportArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_archiveCancellation is not null) return;
        CaptureRecord[] records = _viewModel.SelectedTiles.Select(tile => tile.Record).ToArray();
        if (records.Length == 0) return;
        var options = MyCapture.App.Editing.ImageExportOptionsPanel.ShowCopyOptions(this, _commitService.LastExportOptions);
        if (options is null) return;
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Library.Archive.Title"), Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip", AddExtension = true, CheckPathExists = true, OverwritePrompt = false,
            FileName = $"MyCapture-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource();
        _archiveCancellation = cancellation;
        using var activity = _queue.Activities.Start(LibraryActivityKind.ZipExport, cancellation.Cancel);
        FitSelectionCommands();
        ArchiveCancelButton.Visibility = Visibility.Collapsed;
        ShowActionStatus(string.Empty);
        try
        {
            var exporter = new GalleryFolderExportService(_queue);
            using var prepared = await exporter.PrepareAsync(exporter.BuildCapturePlan(records, tree: true), cancellation.Token);
            var progress = new Progress<int>(done =>
            {
                // Ignore dispatcher callbacks queued by a completed or cancelled export.
                if (ReferenceEquals(_archiveCancellation, cancellation))
                    activity.Report(done, records.Length);
            });
            await GalleryFolderExportService.ExportZipAsync(prepared, dialog.FileName, progress, cancellation.Token, options);
            _commitService.RememberExportOptions(options);
            KoreanWordWrap.Show(LibraryActionStatus, UiText.Format("Library.Archive.Done", records.Length));
        }
        catch (OperationCanceledException)
        {
            KoreanWordWrap.Show(LibraryActionStatus, UiText.Get("Library.Archive.Cancelled"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            KoreanWordWrap.Show(LibraryActionStatus, UiText.Get("Library.Archive.Failed"));
        }
        finally
        {
            _archiveCancellation = null;
            FitSelectionCommands();
            ArchiveCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelArchiveClick(object sender, RoutedEventArgs e) => _archiveCancellation?.Cancel();

    private void OnClearLibrarySearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _viewModel.Filter = GalleryFilter.All;
        SearchBox.Focus();
    }
}

/// <summary>Keyboard-accessible, themed metadata editor. Failed saves retain the draft.</summary>
internal sealed class GalleryOrganizationDialog : Window
{
    internal TextBox TitleInput { get; }
    internal TextBox TagsInput { get; }
    internal Button SaveButton { get; }
    internal Button CancelButton { get; }
    private bool _saving;

    internal GalleryOrganizationDialog(CaptureRecord record, Func<string, string, Task<bool>> save, ILogger? log = null)
    {
        Title = UiText.Get("Library.Organize.Title");
        Width = 500; Height = 500; MinWidth = 400; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Window.Standard");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24, 24, 24, 8) };
        content.Children.Add(Label("Library.Organize.Title", TypeScale.Subtitle));
        content.Children.Add(Label("Library.Organize.Hint", TypeScale.Caption));
        content.Children.Add(Label("Library.Organize.Name", TypeScale.Body));
        TitleInput = Input(record.Title, CaptureOrganization.MaximumTitleLength, "Library.Organize.Name");
        content.Children.Add(TitleInput);
        content.Children.Add(Label("Library.Organize.Tags", TypeScale.Body));
        TagsInput = Input(record.Tags ?? string.Empty, 1024, "Library.Organize.Tags");
        content.Children.Add(TagsInput);
        content.Children.Add(Label("Library.Organize.TagsHint", TypeScale.Caption));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        content.Children.Add(error);
        // Save first, then cancel, like every other dialog in the app.
        var apply = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Save"), primary: true);
        apply.IsDefault = true;
        var cancel = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        cancel.IsCancel = true;
        SaveButton = apply;
        CancelButton = cancel;
        WrapPanel buttons = ThemedDialog.CreateActionRow(apply, cancel);
        buttons.Margin = new Thickness(24, 8, 24, 20);
        var shell = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        shell.Children.Add(buttons);
        shell.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = shell;
        Closing += (_, e) => e.Cancel = _saving;
        Loaded += (_, _) => { TitleInput.Focus(); TitleInput.SelectAll(); };
        apply.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; apply.IsEnabled = false; cancel.IsEnabled = false;
            TitleInput.IsEnabled = false; TagsInput.IsEnabled = false;
            bool saved = false;
            try
            {
                saved = await save(TitleInput.Text, TagsInput.Text);
                if (!saved) KoreanWordWrap.Show(error, UiText.Get("Library.Organize.Missing"));
            }
            catch (ArgumentException) { KoreanWordWrap.Show(error, UiText.Get("Library.Organize.Invalid")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
            {
                log?.LogWarning(ex, "Could not save library organization");
                KoreanWordWrap.Show(error, UiText.Get("Library.Organize.Failed"));
            }
            finally
            {
                _saving = false; apply.IsEnabled = true; cancel.IsEnabled = true;
                TitleInput.IsEnabled = true; TagsInput.IsEnabled = true;
            }
            if (saved) DialogResult = true;
        };
    }

    private static TextBlock Label(string key, double size) => KoreanWordWrap.Show(new TextBlock
    {
        FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
    }, UiText.Get(key));

    private static TextBox Input(string value, int maxLength, string label)
    {
        var input = new TextBox { Text = value, MaxLength = maxLength, MinHeight = 36, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(input, UiText.Get(label));
        return input;
    }
}
