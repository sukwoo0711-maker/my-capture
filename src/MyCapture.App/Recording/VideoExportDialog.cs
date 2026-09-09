using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Themes;
using MyCapture.App.Threading;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>Explicit calculation and staged Save As; never commits edits to the library.</summary>
internal sealed class VideoExportDialog : Window
{
    private readonly RecordingResult _recording;
    private readonly VideoEditDocument _document;
    private readonly ILoggerFactory _logs;
    private readonly ComboBox _format = new() { MinHeight = 36 };
    private readonly Slider _target = new() { Minimum = 0, Maximum = 90, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _targetLabel = new();
    private readonly ComboBox _quality = new() { MinHeight = 36 };
    private readonly ComboBox _speed = new() { MinHeight = 36 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly Image _preview = new() { Height = 150, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _mp4 = new();
    private readonly StackPanel _gif = new();
    private readonly StackPanel _settings = new();
    private readonly Button _calculate;
    private readonly Button _save;
    private readonly Button _cancel;
    private CancellationTokenSource? _cancellation;
    private VideoExportCalculation? _result;
    private bool _working;
    private bool _closeRequested;
    private bool _closed;
    private int _settingsRevision;

    internal VideoExportDialog(RecordingResult recording, VideoEditDocument document, ILoggerFactory logs, bool gif)
    {
        _recording = recording;
        // The owner is modal/paused; take a detached document so all worker inputs stay stable.
        _document = document.Clone();
        _logs = logs;
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("MediaExport_Title");
        Width = 580;
        Height = 520;
        MinWidth = 460;
        MinHeight = 400;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        var root = new Grid { Margin = new Thickness(20, 12, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);
        AddText(_settings, UiText.Format("MediaExport_Summary", recording.Width, recording.Height, recording.Fps,
            (_document.TrimOutMs - _document.TrimInMs) / 1000));
        AddText(_settings, UiText.Get("MediaExport_Format"));
        _format.Items.Add("MP4"); _format.Items.Add("GIF");
        _format.SelectedIndex = gif ? 1 : 0;
        _settings.Children.Add(_format);
        AutomationProperties.SetName(_format, UiText.Get("MediaExport_Format"));

        AddText(_mp4, UiText.Get("MediaExport_Baseline"));
        _targetLabel.Margin = new Thickness(0, 12, 0, 4);
        _mp4.Children.Add(_targetLabel);
        _mp4.Children.Add(_target);
        AutomationProperties.SetName(_target, UiText.Get("MediaExport_TargetLabel"));
        AddText(_mp4, UiText.Get("MediaExport_DimensionsKept"));
        _settings.Children.Add(_mp4);
        AddText(_gif, UiText.Get("MediaExport_GifLimits"));
        foreach (GifExportQuality quality in new[] { GifExportQuality.Standard, GifExportQuality.Compact, GifExportQuality.Smallest })
            _quality.Items.Add(new ComboBoxItem { Content = $"{quality.LongEdge}px · {quality.FramesPerSecond} fps", Tag = quality });
        _quality.SelectedIndex = 0;
        AutomationProperties.SetName(_quality, UiText.Get("MediaExport_GifPreset"));
        _gif.Children.Add(_quality);
        AddText(_gif, UiText.Get("MediaExport_Speed"));
        foreach (double speed in new[] { 0.5, 1, 1.5, 2, 3, 4 })
            _speed.Items.Add(new ComboBoxItem { Content = $"{speed:0.0}×", Tag = speed });
        _speed.SelectedIndex = 1;
        AutomationProperties.SetName(_speed, UiText.Get("MediaExport_Speed"));
        _gif.Children.Add(_speed);
        _settings.Children.Add(_gif);
        _calculate = MediaExportVisuals.Button(this, UiText.Get("MediaExport_Calculate"), "MediaExport_Options");
        _calculate.HorizontalAlignment = HorizontalAlignment.Left;
        _calculate.Margin = new Thickness(0, 16, 0, 12);
        _settings.Children.Add(_calculate);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _settings.Children.Add(_status);
        AutomationProperties.SetName(_preview, UiText.Get("MediaExport_ResultPreview"));
        _settings.Children.Add(_preview);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _cancel = MediaExportVisuals.Button(this, UiText.Get("MediaExport_Close"));
        _save = MediaExportVisuals.Button(this, UiText.Get("MediaExport_SaveAs"), "MediaExport_ArrowExport", true);
        _save.IsEnabled = false;
        footer.Children.Add(_cancel); footer.Children.Add(_save);
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        Content = root;
        _format.SelectionChanged += (_, _) => Changed();
        _target.ValueChanged += (_, _) => Changed();
        _quality.SelectionChanged += (_, _) => Changed();
        _speed.SelectionChanged += (_, _) => Changed();
        _calculate.Click += async (_, _) => await CalculateAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => { if (_working) _cancellation?.Cancel(); else Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_working) _cancellation?.Cancel(); else Close();
            e.Handled = true;
        };
        Closing += (_, e) => { if (_working) { e.Cancel = true; _closeRequested = true; _cancellation?.Cancel(); } };
        Closed += (_, _) => { _closed = true; _cancellation?.Cancel(); _result?.Dispose(); _result = null; };
        Changed();
    }

    private static void AddText(Panel panel, string text) => panel.Children.Add(new TextBlock
    { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });

    private void Changed()
    {
        _settingsRevision++;
        _cancellation?.Cancel();
        _result?.Dispose(); _result = null; _save.IsEnabled = false;
        _preview.Source = null; _preview.Visibility = Visibility.Collapsed;
        bool gif = _format.SelectedIndex == 1;
        _mp4.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        _gif.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        _targetLabel.Text = UiText.Format("MediaExport_Target", (int)_target.Value);
        bool tooLong = gif && _document.TrimOutMs - _document.TrimInMs > AnimatedGifExporter.MaximumDurationMs + 0.5;
        _calculate.IsEnabled = !tooLong;
        _status.Text = UiText.Get(tooLong ? "MediaExport_TrimGif" : "MediaExport_CalculateHint");
    }

    internal int TargetReductionPercent
    {
        get => (int)_target.Value;
        set => _target.Value = value;
    }

    internal bool CanExport => _save.IsEnabled;

    internal async Task CalculateAsync()
    {
        if (_working || _closed) return;
        _result?.Dispose(); _result = null;
        _preview.Source = null; _preview.Visibility = Visibility.Collapsed;
        bool gif = _format.SelectedIndex == 1;
        int target = (int)_target.Value;
        var quality = (GifExportQuality)((ComboBoxItem)_quality.SelectedItem).Tag;
        double speed = (double)((ComboBoxItem)_speed.SelectedItem).Tag;
        int revision = _settingsRevision;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetWorking(true);
        _status.Text = UiText.Get("MediaExport_Working");
        IProgress<VideoFrameRenderProgress> ProgressFor(string phase) => new Progress<VideoFrameRenderProgress>(p =>
        {
            if (!_closed && _working && revision == _settingsRevision && ReferenceEquals(_cancellation, cancellation))
                _status.Text = UiText.Format("MediaExport_Progress", UiText.Get(phase), p.CompletedFrames, p.TotalFrames);
        });
        var baselineProgress = ProgressFor("MediaExport_BaselinePhase");
        var reducedProgress = ProgressFor("MediaExport_ReducedPhase");
        var gifProgress = ProgressFor("MediaExport_GifPhase");
        VideoExportCalculation? calculated = null;
        try
        {
            calculated = await StaThreadTask.RunAsync(() => gif
                ? VideoExportCalculation.CalculateGif(path => AnimatedGifExporter.Export(_recording, _document, path, gifProgress, cancellation.Token, speed, quality), cancellation.Token)
                : VideoExportCalculation.Calculate(target, _document.TrimOutMs - _document.TrimInMs,
                    VideoEncoderOptions.DeriveBitrate(_recording.Width, _recording.Height, _recording.Fps),
                    (path, bitrate, baseline, token) => TrimReencoder.Reencode(_recording.OutputPath, path,
                        _document.TrimInMs, _document.TrimOutMs, _recording,
                        options => new MediaFoundationVideoEncoder(options, _logs.CreateLogger<MediaFoundationVideoEncoder>()),
                        _logs.CreateLogger("VideoExport"), _document.TextOverlays, _document.FrameEditLayers, baseline ? baselineProgress : reducedProgress, token, bitrate),
                    cancellation.Token), "MyCapture export calculation");
            cancellation.Token.ThrowIfCancellationRequested();
            BitmapSource preview = await StaThreadTask.RunAsync(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                BitmapSource frame;
                if (gif)
                {
                    using var stream = File.OpenRead(calculated.ResultPath);
                    var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    frame = decoder.Frames[0];
                }
                else
                {
                    double scale = Math.Min(1, 480d / Math.Max(_recording.Width, _recording.Height));
                    frame = VideoFrameRenderPipeline.RenderSingleFrame(calculated.ResultPath,
                        (_document.TrimOutMs - _document.TrimInMs) / 2,
                        Math.Max(1, (int)(_recording.Width * scale)), Math.Max(1, (int)(_recording.Height * scale)));
                }
                frame.Freeze();
                cancellation.Token.ThrowIfCancellationRequested();
                return frame;
            }, "MyCapture export result preview");
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed || revision != _settingsRevision) return;
            _result = calculated;
            calculated = null; // Publish only the complete result for the current settings.
            _preview.Source = preview;
            _preview.Visibility = Visibility.Visible;
            _status.Text = gif ? UiText.Format("MediaExport_GifResult", _result.ResultBytes)
                : UiText.Format("MediaExport_Result", _result.BaselineBytes, _result.ResultBytes, _result.ActualReduction)
                    + Environment.NewLine + UiText.Get(_result.TargetReached ? "MediaExport_Ready" : "MediaExport_Unmet");
        }
        catch (OperationCanceledException)
        {
            if (!_closed && revision == _settingsRevision) _status.Text = UiText.Get("MediaExport_Cancelled");
        }
        catch (Exception error)
        {
            if (!_closed && revision == _settingsRevision) _status.Text = UiText.Format("MediaExport_Error", error.Message);
        }
        finally
        {
            calculated?.Dispose();
            _cancellation = null;
            SetWorking(false);
            if (_closeRequested) Close();
        }
    }

    private async Task SaveAsync()
    {
        if (_working || _result is null) return;
        VideoExportCalculation result = _result;
        int revision = _settingsRevision;
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("MediaExport_SaveAs"), DefaultExt = result.Extension,
            Filter = result.Extension == ".gif" ? "GIF (*.gif)|*.gif" : "MP4 (*.mp4)|*.mp4",
            AddExtension = true, OverwritePrompt = true,
            FileName = $"MyCapture_{DateTime.Now:yyyyMMdd_HHmmss}{result.Extension}",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (_closed || revision != _settingsRevision || !ReferenceEquals(result, _result)) return;
        using var cancellation = new CancellationTokenSource();
        _result = null; // The operation holds the stage until its read and atomic commit end.
        _cancellation = cancellation; SetWorking(true);
        try
        {
            await Task.Run(() => result.SaveCopy(dialog.FileName, _recording.OutputPath, cancellation.Token));
            if (!_closed && revision == _settingsRevision)
                _status.Text = UiText.Format("MediaExport_Saved", Path.GetFileName(dialog.FileName));
        }
        catch (OperationCanceledException)
        {
            if (!_closed && revision == _settingsRevision) _status.Text = UiText.Get("MediaExport_Cancelled");
        }
        catch (Exception error)
        {
            if (!_closed && revision == _settingsRevision) _status.Text = UiText.Format("MediaExport_Error", error.Message);
        }
        finally
        {
            if (!_closed && revision == _settingsRevision) _result = result;
            else result.Dispose();
            _cancellation = null;
            SetWorking(false);
            if (_closeRequested) Close();
        }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        _settings.IsEnabled = !working;
        _save.IsEnabled = !working && _result is not null;
        _cancel.Content = UiText.Get(working ? "MediaExport_Cancel" : "MediaExport_Close");
        AutomationProperties.SetName(_cancel, (string)_cancel.Content);
        _cancel.ToolTip = _cancel.Content;
    }
}
