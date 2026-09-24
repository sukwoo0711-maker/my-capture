using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
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
            _controller.UpdateOrganizationAsync(tile.Id, title, tags)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        tile.RaiseMetaChanged();
        _viewModel.Refresh();
        CaptureChanged?.Invoke(this, EventArgs.Empty);
        LibraryActionStatus.Text = UiText.Get("Library.Organize.Saved");
    }

    private async void OnExportArchiveClick(object sender, RoutedEventArgs e)
    {
        if (_archiveCancellation is not null) return;
        CaptureRecord[] records = _viewModel.SelectedTiles.Select(tile => tile.Record).ToArray();
        if (records.Length == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Library.Archive.Title"), Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip", AddExtension = true, CheckPathExists = true, OverwritePrompt = false,
            FileName = $"MyCapture-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        using var cancellation = new CancellationTokenSource();
        _archiveCancellation = cancellation;
        ArchiveExportButton.IsEnabled = false;
        ArchiveCancelButton.Visibility = Visibility.Visible;
        LibraryActionStatus.Text = UiText.Get("Library.Archive.Preparing");
        try
        {
            using GalleryDragExportService.PreparedDrag prepared =
                await _dragExport.PrepareBatchAsync(records, cancellation.Token);
            var progress = new Progress<int>(done =>
            {
                // Ignore dispatcher callbacks queued by a completed or cancelled export.
                if (ReferenceEquals(_archiveCancellation, cancellation))
                    LibraryActionStatus.Text = UiText.Format("Library.Archive.Progress", done, records.Length);
            });
            await GalleryArchiveExporter.ExportNewAsync(prepared.Paths, dialog.FileName, progress, cancellation.Token);
            LibraryActionStatus.Text = UiText.Format("Library.Archive.Done", records.Length);
        }
        catch (OperationCanceledException)
        {
            LibraryActionStatus.Text = UiText.Get("Library.Archive.Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            LibraryActionStatus.Text = UiText.Get("Library.Archive.Failed");
        }
        finally
        {
            _archiveCancellation = null;
            ArchiveExportButton.IsEnabled = true;
            ArchiveCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelArchiveClick(object sender, RoutedEventArgs e) => _archiveCancellation?.Cancel();

    private void OnClearLibrarySearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _viewModel.Filter = GalleryFilter.All;
        AllLibraryFilter.IsChecked = true;
        SearchBox.Focus();
    }
}

/// <summary>Keyboard-accessible, themed metadata editor. Failed saves retain the draft.</summary>
internal sealed class GalleryOrganizationDialog : Window
{
    internal TextBox TitleInput { get; }
    internal TextBox TagsInput { get; }
    private bool _saving;

    internal GalleryOrganizationDialog(CaptureRecord record, Func<string, string, Task<bool>> save)
    {
        Title = UiText.Get("Library.Organize.Title");
        Width = 500; Height = 500; MinWidth = 400; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Window.Standard");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var content = new StackPanel { Margin = new Thickness(24, 24, 24, 8) };
        content.Children.Add(Label("Library.Organize.Title", 22));
        content.Children.Add(Label("Library.Organize.Hint", 13));
        content.Children.Add(Label("Library.Organize.Name", 14));
        TitleInput = Input(record.Title, CaptureOrganization.MaximumTitleLength, "Library.Organize.Name");
        content.Children.Add(TitleInput);
        content.Children.Add(Label("Library.Organize.Tags", 14));
        TagsInput = Input(record.Tags ?? string.Empty, 1024, "Library.Organize.Tags");
        content.Children.Add(TagsInput);
        content.Children.Add(Label("Library.Organize.TagsHint", 12));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        content.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 12, 24, 20) };
        var cancel = new Button { Content = UiText.Get("Library.Action.Cancel"), IsCancel = true, MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        var apply = new Button { Content = UiText.Get("Library.Action.Save"), IsDefault = true, MinWidth = 84 };
        apply.SetResourceReference(StyleProperty, "Button.Primary");
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
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
                if (!saved) error.Text = UiText.Get("Library.Organize.Missing");
            }
            catch (ArgumentException) { error.Text = UiText.Get("Library.Organize.Invalid"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { error.Text = UiText.Get("Library.Organize.Failed"); }
            finally
            {
                _saving = false; apply.IsEnabled = true; cancel.IsEnabled = true;
                TitleInput.IsEnabled = true; TagsInput.IsEnabled = true;
            }
            if (saved) DialogResult = true;
        };
    }

    private static TextBlock Label(string key, double size) => new()
    {
        Text = UiText.Get(key), FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
    };

    private static TextBox Input(string value, int maxLength, string label)
    {
        var input = new TextBox { Text = value, MaxLength = maxLength, MinHeight = 36, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(input, UiText.Get(label));
        return input;
    }
}