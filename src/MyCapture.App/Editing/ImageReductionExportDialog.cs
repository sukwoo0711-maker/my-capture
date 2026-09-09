using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyCapture.App.Gallery;
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
    private readonly Button _preview = new() { Content = UiText.Get("ExportReduction_Preview") };
    private readonly Button _save = new() { Content = UiText.Get("ExportReduction_Save"), IsEnabled = false };
    private readonly Button _drag = new() { Content = UiText.Get("ExportReduction_Drag"), IsEnabled = false };
    private byte[]? _original;
    private ImageExportResult? _result;
    private string? _stagedPath;
    private CancellationTokenSource? _previewCancellation;
    private bool _working;
    private bool _closed;
    private Point? _dragStart;

    internal ImageReductionExportDialog(BitmapSource image, string suggestedPngPath)
    {
        _image = image.IsFrozen ? image : image.Clone();
        if (!_image.IsFrozen) _image.Freeze();
        _suggestedPath = suggestedPngPath;
        Title = UiText.Get("ExportReduction_Title");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = UiText.Get("ExportReduction_Description"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(_targetText);
        panel.Children.Add(_target);
        panel.Children.Add(_preview);
        panel.Children.Add(_imagePreview);
        panel.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_drag);
        actions.Children.Add(_save);
        var cancel = new Button { Content = UiText.Get("ExportReduction_Cancel"), IsCancel = true };
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        Content = panel;
        foreach (Button button in new[] { _preview, _drag, _save, cancel })
        {
            button.Margin = new Thickness(4, 10, 4, 10);
            button.Padding = new Thickness(12, 7, 12, 7);
            AutomationProperties.SetName(button, button.Content.ToString());
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
            (string staged, BitmapSource preview) = await Task.Run(() =>
            {
                BitmapSource imagePreview = CreatePreview(result, _image.PixelWidth, _image.PixelHeight);
                cancellation.Token.ThrowIfCancellationRequested();
                return (Stage(result), imagePreview);
            });
            if (_closed || cancellation.IsCancellationRequested)
            {
                TryDelete(staged);
                return;
            }
            DeleteUnconsumedStage();
            _stagedPath = staged;
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
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _stagedPath is null) return;
        Point current = e.GetPosition(_drag);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        try
        {
            DragDropEffects effect = DragDrop.DoDragDrop(_drag, GalleryDragExportService.CreateFileDropData(_stagedPath), DragDropEffects.Copy);
            if (effect == DragDropEffects.Copy)
            {
                // Explorer may finish reading after DoDragDrop returns. Retain successful
                // staging files for the normal two-day shell handoff window.
                _stagedPath = null;
                DialogResult = true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            _status.Text = UiText.Format("ExportReduction_Error", ex.Message);
        }
    }

    private static string Stage(ImageExportResult result)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MyCapture", "ReducedDragExports");
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(directory, "MyCapture_*.*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-2)) TryDelete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string path = Path.Combine(directory, $"MyCapture_{Guid.NewGuid():N}{result.Extension}");
        bool created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            created = true;
            stream.Write(result.Bytes);
            stream.Flush(true);
            return path;
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
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
        if (_stagedPath is not null) TryDelete(_stagedPath);
        _stagedPath = null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
