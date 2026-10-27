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
using MyCapture.App.Threading;
using MyCapture.Core.Storage;
using MyCapture.Core.Settings;
using System.Windows.Media;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Exports a smaller copy of the edited image. The result follows the target: 250 ms after
/// the last change it is calculated again off the UI thread, and until then the previous
/// preview and sizes stay on screen, dimmed and labelled as the previous setting's result.
/// </summary>
/// <remarks>
/// Only the newest calculation may publish. A target change cancels the running one and
/// bumps a generation number, so a slow, older encode that finishes late never replaces a
/// newer result. Saving and dragging wait for the result of the current target.
/// </remarks>
internal sealed class ImageReductionExportDialog : Window
{
    /// <summary>How long the target has to stay still before the size is calculated again.</summary>
    internal static readonly TimeSpan RecalculateDelay = TimeSpan.FromMilliseconds(250);

    private readonly BitmapSource _image;
    private readonly string _suggestedPath;
    private readonly Slider _target = new() { Minimum = 0, Maximum = 90, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 10 };
    private readonly TextBlock _targetText = new();
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 20, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
    private readonly Image _imagePreview = new() { MaxWidth = 480, Height = 220, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 6, 0, 10) };
    private readonly Button _save;
    private readonly Button _drag;
    private readonly string? _sourcePageUrl;
    private readonly ImageExportOptionsPanel _options;
    private readonly Action<ImageExportOptions>? _remember;
    private readonly Action<string, ImageExportOptions>? _rememberSavedExport;
    private Task<byte[]>? _original;
    private ImageExportResult? _result;
    private IImageExportStage? _stage;
    private CancellationTokenSource? _previewCancellation;
    private IDisposable? _pendingRecalculation;
    private string? _resultSizes;
    private int _generation;
    private bool _closed;
    private bool _saving;
    private Point? _dragStart;

    internal ImageReductionExportDialog(BitmapSource image, string suggestedPngPath, string? sourcePageUrl = null,
        ImageExportOptions? options = null, Action<ImageExportOptions>? remember = null,
        Action<string, ImageExportOptions>? rememberSavedExport = null)
    {
        _image = image.IsFrozen ? image : image.Clone();
        if (!_image.IsFrozen) _image.Freeze();
        _suggestedPath = suggestedPngPath;
        _rememberSavedExport = rememberSavedExport;
        _sourcePageUrl = sourcePageUrl;
        options ??= new();
        _remember = remember;
        _options = new ImageExportOptionsPanel(options, full: true);
        _target.Value = options.ReductionPercent;
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("ExportReduction_Title");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        Width = 560;
        Height = 820;
        MinWidth = 460;
        MinHeight = 400;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _save = MediaExportVisuals.Button(this, UiText.Get("ExportReduction_Save"), "MediaExport_ArrowExport", true);
        _drag = MediaExportVisuals.Button(this, UiText.Get("ExportReduction_Drag"));
        _save.IsEnabled = _drag.IsEnabled = false;
        var root = new Grid { Margin = new Thickness(20, 12, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(MyCapture.App.Themes.KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) },
            UiText.Get("ExportReduction_Description")));
        panel.Children.Add(_options);
        panel.Children.Add(_targetText);
        panel.Children.Add(_target);
        // One line for the calculation, so the preview below never jumps while it runs.
        _state.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetLiveSetting(_state, AutomationLiveSetting.Polite);
        panel.Children.Add(_state);
        AutomationProperties.SetName(_imagePreview, UiText.Get("ExportReduction_PreviewName"));
        panel.Children.Add(_imagePreview);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
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
        foreach (Button button in new[] { _drag, _save, cancel })
        {
            button.Margin = new Thickness(4, 10, 4, 10);
            button.Padding = new Thickness(12, 7, 12, 7);
            button.MinWidth = MediaExportVisuals.DialogActionMinWidth;
        }
        AutomationProperties.SetName(_target, UiText.Get("ExportReduction_TargetLabel"));
        void Changed()
        {
            UpdateTargetLabel();
            // The shown result belongs to the previous target now: it stays for comparison,
            // marked as such, but can no longer be saved. A running calculation is dropped.
            _generation++;
            _previewCancellation?.Cancel();
            _result = null;
            DeleteUnconsumedStage();
            _save.IsEnabled = _drag.IsEnabled = false;
            ShowCalculating();
            _pendingRecalculation?.Dispose();
            _pendingRecalculation = ScheduleRecalculation(RecalculateDelay, () =>
            {
                _pendingRecalculation = null;
                _ = UpdatePreviewAsync();
            });
        }
        _target.ValueChanged += (_, _) => Changed();
        _options.Changed += Changed;
        _options.PresetSelected += preset => _target.Value = preset.ReductionPercent;
        _save.Click += async (_, _) => await SaveAsync();
        _drag.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(_drag);
        _drag.PreviewMouseLeftButtonUp += (_, _) => _dragStart = null;
        _drag.PreviewMouseMove += OnDragMove;
        Loaded += async (_, _) => await UpdatePreviewAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _pendingRecalculation?.Dispose();
            _pendingRecalculation = null;
            _previewCancellation?.Cancel();
            _result = null;
            _original = null;
            _imagePreview.Source = null;
            DeleteUnconsumedStage();
        };
        UpdateTargetLabel();
    }

    /// <summary>
    /// Runs an action once after a delay; disposing the returned handle cancels it. Tests
    /// replace it to see each request and run the calculation when they choose.
    /// </summary>
    internal Func<TimeSpan, Action, IDisposable> ScheduleRecalculation { get; set; } = DispatcherDelay.Schedule;

    /// <summary>The size encoder; tests replace it to control when a calculation finishes.</summary>
    internal Func<BitmapSource, byte[], int, CancellationToken, ImageExportResult>? Encoder { get; set; }

    /// <summary>Prepares a drag file on the preview worker before dragging is enabled.</summary>
    internal Func<ImageExportResult, IImageExportStage> CreateStage { get; set; } = result => OwnedImageExportStage.Create(result);

    /// <summary>
    /// Shows that a calculation is pending or running. A result already on screen stays,
    /// dimmed and named as the previous setting's, so the two can still be compared.
    /// </summary>
    private void ShowCalculating()
    {
        bool previous = _imagePreview.Source is not null;
        _state.Text = UiText.Get(previous ? "ExportReduction_CalculatingPrevious" : "ExportReduction_Working");
        _imagePreview.Opacity = previous ? StaleOpacity : 1;
        _status.Opacity = previous ? StaleOpacity : 1;
        // The previous sizes stay for comparison; its "ready, save it" note does not, since
        // that result can no longer be saved.
        if (previous && _resultSizes is not null) _status.Text = _resultSizes;
    }

    private void ShowCurrent()
    {
        _state.Text = string.Empty;
        _imagePreview.Opacity = 1;
        _status.Opacity = 1;
    }

    private const double StaleOpacity = 0.55;

    internal static bool Show(Window? owner, BitmapSource frozenImage, string suggestedPngPath, string? sourcePageUrl = null,
        ImageExportOptions? options = null, Action<ImageExportOptions>? remember = null,
        Action<string, ImageExportOptions>? rememberSavedExport = null)
    {
        var dialog = new ImageReductionExportDialog(frozenImage, suggestedPngPath, sourcePageUrl, options, remember, rememberSavedExport);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    internal int TargetReductionPercent
    {
        get => (int)_target.Value;
        set => _target.Value = value;
    }

    internal bool CanExport => _save.IsEnabled && _drag.IsEnabled;
    internal bool CanSaveForTest => _save.IsEnabled;
    internal bool CanDragForTest => _drag.IsEnabled;
    internal ImageExportOptionsPanel OptionsForTest => _options;

    /// <summary>Bytes the save button would write. Tests use this to see the prepared PNG.</summary>
    internal byte[]? PreparedExportBytesForTest => _result?.Bytes;

    private void UpdateTargetLabel() => _targetText.Text = _target.Value == 0
        ? UiText.Get("ExportReduction_Original")
        : UiText.Format("ExportReduction_Target", (int)_target.Value);

    /// <summary>A file size people can read, in the same 1024-based KB/MB File Explorer shows.</summary>
    internal static string FormatFileSize(long bytes) => bytes >= 1024 * 1024
        ? UiText.Format("CopyTerms_SizeMb", bytes / (1024d * 1024d))
        : UiText.Format("CopyTerms_SizeKb", Math.Max(1, (long)Math.Ceiling(bytes / 1024d)));

    /// <summary>
    /// Calculates the result for the current target and shows it, unless the target changed
    /// or the dialog closed meanwhile. The encoding runs on a worker thread.
    /// </summary>
    internal async Task UpdatePreviewAsync()
    {
        if (_closed) return;
        _pendingRecalculation?.Dispose();
        _pendingRecalculation = null;
        _previewCancellation?.Cancel();
        int generation = ++_generation;
        _result = null;
        DeleteUnconsumedStage();
        _save.IsEnabled = _drag.IsEnabled = false;
        ShowCalculating();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        int target = (int)_target.Value;
        var encode = Encoder;
        ImageExportOptions options = _options.Read(target);
        var createStage = CreateStage;
        Color accent = (TryFindResource("Accent.Default") as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        bool Current() => !_closed && generation == _generation && !cancellation.IsCancellationRequested;
        IImageExportStage? stage = null;
        try
        {
            // The PNG baseline is encoded once and shared by every later calculation.
            _original ??= Task.Run(() => ImageCodec.EncodePng(_image, _sourcePageUrl));
            byte[] original = await _original;
            cancellation.Token.ThrowIfCancellationRequested();
            ImageExportResult result = await Task.Run(() => encode is null
                ? ImageExportEncoder.Encode(_image, original, options, accent, _sourcePageUrl, cancellation.Token)
                : encode(_image, original, target, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            BitmapSource preview = await Task.Run(() =>
            {
                BitmapSource imagePreview = CreatePreview(result, result.PixelWidth > 0 ? result.PixelWidth : _image.PixelWidth,
                    result.PixelHeight > 0 ? result.PixelHeight : _image.PixelHeight);
                cancellation.Token.ThrowIfCancellationRequested();
                return imagePreview;
            });
            // A newer target, or a closed dialog, owns the screen now: drop this result.
            if (!Current())
            {
                return;
            }
            DeleteUnconsumedStage();
            _result = result;
            _imagePreview.Source = preview;
            _resultSizes = UiText.Format("ExportReduction_Result", FormatFileSize(original.LongLength), FormatFileSize(result.Bytes.LongLength), result.ActualReductionPercent.ToString("F1"), result.Extension);
            _resultSizes += Environment.NewLine + UiText.Format("ExportOptions.Dimensions",
                result.PixelWidth > 0 ? result.PixelWidth : _image.PixelWidth, result.PixelHeight > 0 ? result.PixelHeight : _image.PixelHeight);
            _status.Text = _resultSizes
                + Environment.NewLine + (result.PreservedTransparency ? UiText.Get("ExportReduction_Transparent")
                    : !result.TargetReached ? UiText.Get("ExportReduction_Unmet") : UiText.Get("ExportReduction_Ready"));
            ShowCurrent();
            _save.IsEnabled = !_saving;
            stage = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return createStage(result);
            });
            if (!Current()) return;
            _stage = stage;
            stage = null;
            _drag.IsEnabled = !_saving;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // A failed baseline is encoded again by the next calculation instead of failing it too.
            if (_original is { IsFaulted: true }) _original = null;
            if (Current())
            {
                _state.Text = string.Empty;
                _status.Opacity = 1;
                _status.Text = UiText.Format("ExportReduction_Error", MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
            }
        }
        finally
        {
            // A stage can finish after cancellation or close. It was never published, so
            // only this calculation owns it and its cleanup stays off the dispatcher.
            if (stage is not null) await Task.Run(stage.Dispose);
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>Whether a calculation is waiting for the target to settle.</summary>
    internal bool IsRecalculationPendingForTest => _pendingRecalculation is not null;

    /// <summary>The line above the preview: empty, or what is being calculated.</summary>
    internal string CalculationStateForTest => _state.Text;

    internal string ResultTextForTest => _status.Text;

    internal BitmapSource? PreviewForTest => _imagePreview.Source as BitmapSource;

    internal bool ShowsPreviousResultForTest => _imagePreview.Source is not null && _imagePreview.Opacity < 1;

    /// <summary>The target the prepared export was calculated for.</summary>
    internal int? PreparedTargetForTest => _result?.TargetReductionPercent;

    /// <summary>Publish prepared bytes before remembering either the destination or the options.</summary>
    internal static async Task WritePreparedExportAsync(string path, byte[] bytes, ImageExportOptions options,
        Action<ImageExportOptions>? remember, Action<string, ImageExportOptions>? rememberSavedExport)
    {
        await Task.Run(() => AtomicFile.WriteExportBytes(path, bytes));
        if (rememberSavedExport is not null) rememberSavedExport(path, options);
        else remember?.Invoke(options);
    }

    private async Task SaveAsync()
    {
        ImageExportResult? result = _result;
        if (result is null) return;
        _saving = true;
        _save.IsEnabled = _drag.IsEnabled = _target.IsEnabled = _options.IsEnabled = false;
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
                    Filter = UiText.Get(result.Extension == ".jpg" ? "ExportOptions.JpegFilter" : "Text_4DCACB688866"),
                    AddExtension = true, OverwritePrompt = true,
                };
                dialog.FileOk += (_, e) =>
                {
                    if (!string.Equals(Path.GetExtension(dialog.FileName), result.Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        MyCapture.App.Themes.ThemedDialog.Show(this, UiText.Format("ExportReduction_Extension", result.Extension), Title);
                    }
                };
                if (dialog.ShowDialog(this) != true) return false;
                if (_closed) return false;
                await WritePreparedExportAsync(dialog.FileName, result.Bytes, _options.Read((int)_target.Value),
                    _remember, _rememberSavedExport);
                return true;
            });
            if (saved && !_closed) DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!_closed) _status.Text = UiText.Format("ExportReduction_Error", MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
        }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                _target.IsEnabled = _options.IsEnabled = true;
                _save.IsEnabled = _result is not null;
                _drag.IsEnabled = _stage is not null;
            }
        }
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed || _result is null || _stage is null) return;
        Point current = e.GetPosition(_drag);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        IImageExportStage stage = _stage;
        ImageExportResult? result = _result;
        ImageExportOptions exportedOptions = _options.Read((int)_target.Value);
        // OLE pumps dispatcher messages. Hold ownership locally so a close/target change
        // cannot dispose the file while the shell is reading it or null the post-drop handle.
        _stage = null;
        _drag.IsEnabled = false;
        bool copied = false;
        try
        {
            DragDropEffects effect = DragDrop.DoDragDrop(_drag, GalleryDragExportService.CreateFileDropData(stage.FilePath), DragDropEffects.Copy);
            if (effect == DragDropEffects.Copy)
            {
                // Explorer may finish reading after DoDragDrop returns. Retain successful
                // staging files for the normal two-day shell handoff window.
                stage.RetainForShell();
                _remember?.Invoke(exportedOptions);
                copied = true;
                if (!_closed) DialogResult = true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            if (!_closed) _status.Text = UiText.Format("ExportReduction_Error", MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex));
        }
        finally
        {
            if (!copied)
            {
                if (!_closed && ReferenceEquals(_result, result) && _stage is null) _stage = stage;
                else stage.Dispose();
            }
            if (!_closed) _drag.IsEnabled = _stage is not null;
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
