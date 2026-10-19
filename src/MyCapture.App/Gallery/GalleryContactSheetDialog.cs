using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MyCapture.App.Themes;
using MyCapture.Core.Export;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed class GalleryContactSheetDialog : Window
{
    private readonly IReadOnlyList<GalleryContactSheetItem> _items;
    private readonly Func<bool> _isCurrent;
    private readonly CultureInfo _culture = UiText.Culture;
    private readonly ComboBox _pageSize = new();
    private readonly ComboBox _layout = new();
    private readonly ComboBox _caption = new();
    private readonly CheckBox _source = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(16) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Visibility = Visibility.Collapsed };
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly StackPanel _settings = new();
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _exportCancellation;
    private Task _pendingPreview = Task.CompletedTask;
    private bool _closed;
    private readonly LibraryActivityTracker? _activities;
    private int _generation;

    internal GalleryContactSheetDialog(IReadOnlyList<GalleryContactSheetItem> items, int skippedVideos, Func<bool> isCurrent,
        LibraryActivityTracker? activities = null)
    {
        _items = items;
        _activities = activities;
        _isCurrent = isCurrent;
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("ContactSheet.Action");
        Width = 860; Height = 680; MinWidth = 620; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var root = new DockPanel { Margin = new Thickness(20) };
        _save = ThemedDialog.CreateActionButton(UiText.Get("ContactSheet.Save"), primary: true);
        _save.IsDefault = true;
        _save.IsEnabled = false;
        _cancel = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        _cancel.IsCancel = true;
        var footer = new StackPanel();
        _progress.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        _progress.SetResourceReference(Control.BackgroundProperty, "Surface.Sunken");
        footer.Children.Add(_progress);
        footer.Children.Add(_status);
        footer.Children.Add(ThemedDialog.CreateActionRow(_save, _cancel));
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        AddChoice("ContactSheet.PageSize", _pageSize, ["ContactSheet.A4Portrait", "ContactSheet.A4Landscape", "ContactSheet.LetterPortrait"], 0);
        AddChoice("ContactSheet.Layout", _layout, ["ContactSheet.OneByOne", "ContactSheet.OneByTwo", "ContactSheet.TwoByTwo", "ContactSheet.TwoByThree", "ContactSheet.ThreeByFour"], 2);
        AddChoice("ContactSheet.Caption", _caption, ["ContactSheet.Title", "ContactSheet.CaptureTime", "ContactSheet.TitleAndCaptureTime", "ContactSheet.None"], 0);
        _source.Content = UiText.Get("ContactSheet.Source");
        AutomationProperties.SetName(_source, UiText.Get("ContactSheet.Source"));
        _settings.Children.Add(_source);
        if (skippedVideos > 0) _settings.Children.Add(new TextBlock
        {
            Text = UiText.Format("ContactSheet.SkippedVideos", skippedVideos), TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var previewPanel = new DockPanel();
        var previewLabel = new TextBlock { Text = UiText.Get("ContactSheet.Preview"), Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(previewLabel, Dock.Top);
        previewPanel.Children.Add(previewLabel);
        previewPanel.Children.Add(_preview);
        Grid.SetColumn(previewPanel, 1);
        body.Children.Add(previewPanel);
        root.Children.Add(body);
        Content = root;
        AutomationProperties.SetName(_preview, UiText.Get("ContactSheet.Preview"));
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, UiText.Get("ContactSheet.Saving"));
        _pageSize.SelectionChanged += (_, _) => RequestPreview();
        _layout.SelectionChanged += (_, _) => RequestPreview();
        _caption.SelectionChanged += (_, _) => RequestPreview();
        _source.Checked += (_, _) => RequestPreview();
        _source.Unchecked += (_, _) => RequestPreview();
        Loaded += (_, _) => { RequestPreview(); _pageSize.Focus(); };
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => _exportCancellation?.Cancel();
        Closing += (_, e) =>
        {
            if (_exportCancellation is not null) { _exportCancellation.Cancel(); e.Cancel = true; }
        };
        Closed += (_, _) => { _closed = true; _generation++; _previewCancellation?.Cancel(); _preview.Source = null; };
    }

    internal Task PendingPreview => _pendingPreview;

    private void AddChoice(string labelKey, ComboBox choice, string[] keys, int selected)
    {
        var label = new Label { Content = UiText.Get(labelKey), Target = choice, Padding = new Thickness(0, 10, 0, 5) };
        label.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        _settings.Children.Add(label);
        foreach (string key in keys) choice.Items.Add(UiText.Get(key));
        choice.SelectedIndex = selected;
        choice.MinHeight = 32;
        AutomationProperties.SetName(choice, UiText.Get(labelKey));
        _settings.Children.Add(choice);
    }

    private ContactSheetOptions Options => new((ContactSheetPageSize)_pageSize.SelectedIndex,
        (ContactSheetGrid)_layout.SelectedIndex, (ContactSheetCaption)_caption.SelectedIndex, _source.IsChecked == true);

    private void RequestPreview()
    {
        if (!IsLoaded || _closed || _exportCancellation is not null) return;
        _previewCancellation?.Cancel();
        _pendingPreview = Task.WhenAll(_pendingPreview, UpdatePreviewAsync(++_generation, Options));
    }

    private async Task UpdatePreviewAsync(int generation, ContactSheetOptions options)
    {
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _save.IsEnabled = false;
        _preview.Source = null;
        try
        {
            int pages = new ContactSheetLayout(options).PageCount(_items.Count);
            _status.Text = UiText.Get("ContactSheet.Preparing");
            await Task.Delay(150, cancellation.Token);
            var preview = await GalleryContactSheetRenderer.RunAsync(
                () => GalleryContactSheetRenderer.Preview(_items, options, _culture, cancellation.Token), cancellation.Token);
            if (_closed || generation != _generation) return;
            _preview.Source = preview;
            _status.Text = UiText.Format("ContactSheet.PageCount", _items.Count, pages);
            _save.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (ArgumentOutOfRangeException)
        {
            if (!_closed && generation == _generation) _status.Text = UiText.Get("ContactSheet.Limit");
        }
        catch (Exception ex) when (IsExportError(ex))
        {
            if (!_closed && generation == _generation) _status.Text = UiText.Get("ContactSheet.PreviewFailed");
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
        }
    }

    private async Task SaveAsync()
    {
        if (_exportCancellation is not null || !_save.IsEnabled) return;
        var picker = new SaveFileDialog
        {
            Title = UiText.Get("ContactSheet.Save"), Filter = UiText.Get("ContactSheet.Filter"),
            DefaultExt = ".pdf", AddExtension = true, CheckPathExists = true, OverwritePrompt = false,
            FileName = $"MyCapture-{DateTime.Now:yyyyMMdd-HHmmss}.pdf",
        };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName)) { _status.Text = UiText.Get("ContactSheet.Exists"); return; }
        ContactSheetOptions options = Options;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        using var activity = _activities?.Start(LibraryActivityKind.Pdf, cancellation.Cancel, _items.Count);
        _settings.IsEnabled = _save.IsEnabled = false;
        _progress.Maximum = _items.Count;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        _status.Text = UiText.Get("ContactSheet.Saving");
        string stage = Path.Combine(Path.GetDirectoryName(picker.FileName)!, $".MyCapture-{Guid.NewGuid():N}.pdf.partial");
        bool created = false;
        bool acceptingProgress = true;
        try
        {
            if (!_isCurrent()) throw new InvalidOperationException("A capture changed before export.");
            var progress = new Progress<int>(done =>
            {
                if (!acceptingProgress || !ReferenceEquals(_exportCancellation, cancellation)) return;
                activity?.Report(done, _items.Count);
                _progress.Value = done;
                _status.Text = UiText.Format("ContactSheet.Progress", done, _items.Count);
            });
            await GalleryContactSheetRenderer.RunAsync(() =>
            {
                var layout = new ContactSheetLayout(options);
                using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                ContactSheetPdfWriter.Write(output, options, _items.Count,
                    index => GalleryContactSheetRenderer.EncodeCell(_items[index], options,
                        layout.Cell(index % layout.CellsPerPage), _culture, cancellation.Token), progress, cancellation.Token);
                output.Flush(flushToDisk: true);
                return true;
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_isCurrent()) throw new InvalidOperationException("A capture changed during export.");
            // Claim the destination only after a complete PDF exists; an existing file always survives.
            File.Move(stage, picker.FileName, overwrite: false);
            created = false;
            _status.Text = UiText.Get("ContactSheet.Saved");
        }
        catch (OperationCanceledException) { _status.Text = UiText.Get("ContactSheet.Cancelled"); }
        catch (Exception ex) when (IsExportError(ex)) { _status.Text = UiText.Get("ContactSheet.Failed"); }
        finally
        {
            acceptingProgress = false;
            if (created)
            {
                try { await Task.Run(() => File.Delete(stage)); }
                catch (Exception ex) when (IsExportError(ex))
                { _status.Text = UiText.Format("ContactSheet.StageRetained", stage); }
            }
            _exportCancellation = null;
            _settings.IsEnabled = _save.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsExportError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException;
}
