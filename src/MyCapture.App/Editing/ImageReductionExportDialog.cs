using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

internal sealed class ImageReductionExportDialog : Window
{
    private readonly BitmapSource _image;
    private readonly string _suggestedPath;
    private readonly Slider _target = new() { Minimum = 0, Maximum = 90, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _targetText = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
    private readonly Image _imagePreview = new() { MaxWidth = 480, Height = 220, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 10, 0, 10) };
    private readonly Button _preview;
    private readonly Button _save;
    private readonly Button _drag;
    private byte[]? _original;
    private ImageExportResult? _result;
    private OwnedImageExportStage? _stage;
    private CancellationTokenSource? _previewCancellation;
    private bool _working;
    private bool _closed;
    private Point? _dragStart;

    internal ImageReductionExportDialog(BitmapSource image, string suggestedPngPath)
    {
        _image = image.IsFrozen ? image : image.Clone();
        if (!_image.IsFrozen) _image.Freeze();
        _suggestedPath = suggestedPngPath;
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("ExportReduction_Title");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        Width = 560;
        Height = 620;
        MinWidth = 460;
        MinHeight = 400;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _preview = MediaExportVisuals.Button(this, UiText.Get("ExportReduction_Preview"), "MediaExport_Options");
        _save = MediaExportVisuals.Button(this, UiText.Get("ExportReduction_Save"), "MediaExport_ArrowExport", true);
        _drag = MediaExportVisuals.Button(this, UiText.Get("ExportReduction_Drag"));
        _save.IsEnabled = _drag.IsEnabled = false;
        _preview.HorizontalAlignment = HorizontalAlignment.Left;
        var root = new Grid { Margin = new Thickness(20, 12, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(new TextBlock { Text = UiText.Get("ExportReduction_Description"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(_targetText);
        panel.Children.Add(_target);
        panel.Children.Add(_preview);
        panel.Children.Add(_imagePreview);
        panel.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_drag);
        actions.Children.Add(_save);
        var cancel = MediaExportVisuals.Button(this, UiText.Get("ExportReduction_Cancel"));
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        Content = root;
        foreach (Button button in new[] { _preview, _drag, _save, cancel })
        {
            button.Margin = new Thickness(4, 10, 4, 10);
            button.Padding = new Thickness(12, 7, 12, 7);
        }
        AutomationProperties.SetName(_target, UiText.Get("ExportReduction_TargetLabel"));
        _target.ValueChanged += (_, _) =>
        {
            UpdateTargetLabel();
            _previewCancellation?.Cancel();
            _result = null;
            _imagePreview.Source = null;
            DeleteUnconsumedStage();
            _save.IsEnabled = _drag.IsEnabled = false;
            _status.Text = UiText.Get("ExportReduction_UpdateRequired");
        };
        _preview.Click += async (_, _) => await UpdatePreviewAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _drag.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(_drag);
        _drag.PreviewMouseLeftButtonUp += (_, _) => _dragStart = null;
        _drag.PreviewMouseMove += OnDragMove;
        Loaded += async (_, _) => await UpdatePreviewAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _previewCancellation?.Cancel();
            _result = null;
            _original = null;
            _imagePreview.Source = null;
            DeleteUnconsumedStage();
        };
        UpdateTargetLabel();
    }

    internal static bool Show(Window? owner, BitmapSource frozenImage, string suggestedPngPath)
    {
        var dialog = new ImageReductionExportDialog(frozenImage, suggestedPngPath);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    internal int TargetReductionPercent
    {
        get => (int)_target.Value;
        set => _target.Value = value;
    }

    internal bool CanExport => _save.IsEnabled && _drag.IsEnabled;

    private void UpdateTargetLabel() => _targetText.Text = _target.Value == 0
        ? UiText.Get("ExportReduction_Original")
        : UiText.Format("ExportReduction_Target", (int)_target.Value);

    internal async Task UpdatePreviewAsync()
    {
        if (_working || _closed) return;
        _working = true;
        _result = null;
        _imagePreview.Source = null;
        DeleteUnconsumedStage();
        _preview.IsEnabled = _save.IsEnabled = _drag.IsEnabled = false;
        _status.Text = UiText.Get("ExportReduction_Working");
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        int target = (int)_target.Value;
        try
        {
            byte[] original = _original ?? await Task.Run(() => ImageCodec.EncodePng(_image));
            cancellation.Token.ThrowIfCancellationRequested();
            _original = original;
            // A single worker per dialog; changing the slider cancels between encoder calls.
            ImageExportResult result = await Task.Run(() => ImageExportEncoder.Encode(_image, original, target, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            (OwnedImageExportStage staged, BitmapSource preview) = await Task.Run(() =>
            {
                BitmapSource imagePreview = CreatePreview(result, _image.PixelWidth, _image.PixelHeight);
                cancellation.Token.ThrowIfCancellationRequested();
                return (OwnedImageExportStage.Create(result), imagePreview);
            });
            if (_closed || cancellation.IsCancellationRequested)
            {
                staged.Dispose();
                return;
            }
            DeleteUnconsumedStage();
            _stage = staged;
            _result = result;
            _imagePreview.Source = preview;
            _status.Text = UiText.Format("ExportReduction_Result", original.LongLength.ToString("N0"), result.Bytes.LongLength.ToString("N0"), result.ActualReductionPercent.ToString("F1"), result.Extension)
                + Environment.NewLine + (result.PreservedTransparency ? UiText.Get("ExportReduction_Transparent")
                    : !result.TargetReached ? UiText.Get("ExportReduction_Unmet") : UiText.Get("ExportReduction_Ready"));
            _save.IsEnabled = _drag.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            if (!_closed) _status.Text = UiText.Format("ExportReduction_Error", ex.Message);
        }
        finally
        {
            _previewCancellation = null;
            _working = false;
            if (!_closed) _preview.IsEnabled = true;
        }
    }

    private async Task SaveAsync()
    {
        ImageExportResult? result = _result;
        if (result is null) return;
        _save.IsEnabled = _drag.IsEnabled = _preview.IsEnabled = _target.IsEnabled = false;
        try
        {
            bool saved = await ImageExportTransaction.RunAsync(async () =>
            {
                if (_closed) return false;
                var dialog = new SaveFileDialog
                {
                    FileName = Path.GetFileNameWithoutExtension(_suggestedPath) + result.Extension,
                    InitialDirectory = Path.GetDirectoryName(_suggestedPath),
                    DefaultExt = result.Extension,
                    Filter = result.Extension == ".jpg" ? "JPEG (*.jpg)|*.jpg" : "PNG (*.png)|*.png",
                    AddExtension = true, OverwritePrompt = true,
                };
                dialog.FileOk += (_, e) =>
                {
                    if (!string.Equals(Path.GetExtension(dialog.FileName), result.Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        MessageBox.Show(this, UiText.Format("ExportReduction_Extension", result.Extension), Title);
                    }
                };
                if (dialog.ShowDialog(this) != true) return false;
                if (_closed) return false;
                await Task.Run(() => AtomicFile.WriteExportBytes(dialog.FileName, result.Bytes));
                return true;
            });
            if (saved && !_closed) DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_closed) _status.Text = UiText.Format("ExportReduction_Error", ex.Message);
        }
        finally
        {
            if (!_closed) _save.IsEnabled = _drag.IsEnabled = _preview.IsEnabled = _target.IsEnabled = true;
        }
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _stage is null) return;
        Point current = e.GetPosition(_drag);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        OwnedImageExportStage stage = _stage;
        ImageExportResult? result = _result;
        // OLE pumps dispatcher messages. Hold ownership locally so a close/target change
        // cannot dispose the file while the shell is reading it or null the post-drop handle.
        _stage = null;
        bool copied = false;
        try
        {
            DragDropEffects effect = DragDrop.DoDragDrop(_drag, GalleryDragExportService.CreateFileDropData(stage.FilePath), DragDropEffects.Copy);
            if (effect == DragDropEffects.Copy)
            {
                // Explorer may finish reading after DoDragDrop returns. Retain successful
                // staging files for the normal two-day shell handoff window.
                stage.RetainForShell();
                copied = true;
                if (!_closed) DialogResult = true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            if (!_closed) _status.Text = UiText.Format("ExportReduction_Error", ex.Message);
        }
        finally
        {
            if (!copied)
            {
                if (!_closed && ReferenceEquals(_result, result) && _stage is null) _stage = stage;
                else stage.Dispose();
            }
        }
    }

    internal static BitmapSource CreatePreview(ImageExportResult result, int width, int height)
    {
        using var stream = new MemoryStream(result.Bytes, writable: false);
        var preview = new BitmapImage();
        preview.BeginInit();
        preview.CacheOption = BitmapCacheOption.OnLoad;
        if (Math.Max(width, height) > 640)
        {
            if (width >= height) preview.DecodePixelWidth = 640;
            else preview.DecodePixelHeight = 640;
        }
        preview.StreamSource = stream;
        preview.EndInit();
        preview.Freeze();
        return preview;
    }

    private void DeleteUnconsumedStage()
    {
        _stage?.Dispose();
        _stage = null;
    }
}
