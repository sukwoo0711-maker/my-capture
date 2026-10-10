using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    /// <summary>Settles keyboard arrowing through the option lists before a new sample starts.</summary>
    internal static readonly TimeSpan SampleDebounce = TimeSpan.FromMilliseconds(300);

    private readonly RecordingResult _recording;
    private readonly VideoEditDocument _document;
    private readonly ILoggerFactory _logs;
    // Reuses decoded/encoded GIF frames while only preset or speed changes in this dialog.
    private readonly GifExportSession _gifSession;
    private readonly ComboBox _format = new() { MinHeight = 36 };
    private readonly Slider _target = new() { Minimum = 0, Maximum = 90, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _targetLabel = new();
    private readonly ComboBox _quality = OptionList();
    private readonly ComboBox _speed = OptionList();
    private readonly ComboBox _shareTarget = OptionList();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly Image _preview = new() { Height = 150, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly GifPreviewPanel _samplePanel;
    private readonly StackPanel _samplePositionRow = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _samplePositionLabel = new() { Margin = new Thickness(0, 8, 0, 4) };
    private readonly Slider _samplePosition = new() { Minimum = 0, Maximum = 100, Value = GifPreviewSampling.CentrePosition * 100, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly TextBlock _estimate = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border _adviceCard = new() { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _advice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _applyAdvice;
    private readonly TextBlock _applyAdviceText;
    private readonly StackPanel _mp4 = new();
    private readonly StackPanel _gif = new();
    private readonly StackPanel _settings = new();
    private readonly Button _calculate;
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly DispatcherTimer _sampleTimer;
    private readonly Dictionary<GifExportQuality, GifPresetMeasurement> _measurements = [];
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _sampleCancellation;
    private Task _sampleTask = Task.CompletedTask;
    private GifPreviewSample? _sample;
    private GifRecommendation? _recommendation;
    private VideoExportCalculation? _result;
    private bool _working;
    private bool _closeRequested;
    private bool _closed;
    private int _settingsRevision;
    private int _sampleRequest;
    private double _sampleMotion;

    internal VideoExportDialog(RecordingResult recording, VideoEditDocument document, ILoggerFactory logs, bool gif)
    {
        _recording = recording;
        // The owner is modal/paused; take a detached document so all worker inputs stay stable.
        _document = document.Clone();
        _logs = logs;
        _gifSession = new GifExportSession(recording, _document, logs.CreateLogger("GifExport"));
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("MediaExport_Title");
        Width = 640;
        MinWidth = 460;
        MinHeight = 400;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 24);
        // The GIF form carries the animated sample; open tall enough to show it unscrolled.
        Height = Math.Min(MaxHeight, gif ? 900 : 520);
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
        foreach (GifExportQuality quality in GifSharePolicy.Presets)
            _quality.Items.Add(new OptionItem(QualityText(quality), quality));
        _quality.SelectedIndex = 0;
        foreach (double speed in GifSharePolicy.Speeds)
            _speed.Items.Add(new OptionItem(SpeedText(speed), speed));
        _speed.SelectedIndex = 1;
        foreach (GifShareTarget shareTarget in GifSharePolicy.Targets)
            _shareTarget.Items.Add(new OptionItem(UiText.Get(shareTarget.LabelKey), shareTarget));
        _shareTarget.SelectedIndex = GifSharePolicy.DefaultTargetIndex;
        var options = new Grid();
        AddOption(options, 0, 5, "MediaExport_GifPreset", _quality);
        AddOption(options, 1, 3, "MediaExport_Speed", _speed);
        AddOption(options, 2, 5, "GifPreview_Target", _shareTarget);
        _gif.Children.Add(options);
        // Longer ranges show a 3 s window; the user can move it to the moment that matters.
        _samplePositionRow.Children.Add(_samplePositionLabel);
        _samplePositionRow.Children.Add(_samplePosition);
        AutomationProperties.SetName(_samplePosition, UiText.Get("GifPreview_SamplePosition"));
        _samplePositionRow.Visibility = SpanMs > GifPreviewSampling.MaximumSourceMs ? Visibility.Visible : Visibility.Collapsed;
        _gif.Children.Add(_samplePositionRow);
        _samplePanel = new GifPreviewPanel(RenderOriginalAsync);
        _gif.Children.Add(_samplePanel);
        _estimate.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_estimate, AutomationLiveSetting.Polite);
        _gif.Children.Add(_estimate);
        _adviceCard.SetResourceReference(StyleProperty, "Card.Accent");
        _adviceCard.Padding = new Thickness(12, 10, 12, 10);
        var adviceBody = new StackPanel();
        _advice.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_advice, AutomationLiveSetting.Polite);
        adviceBody.Children.Add(_advice);
        _applyAdvice = MediaExportVisuals.Button(this, UiText.Get("GifPreview_ApplyAdvice"));
        _applyAdviceText = ((StackPanel)_applyAdvice.Content).Children.OfType<TextBlock>().Single();
        _applyAdvice.HorizontalAlignment = HorizontalAlignment.Left;
        _applyAdvice.Margin = new Thickness(0, 8, 0, 0);
        adviceBody.Children.Add(_applyAdvice);
        _adviceCard.Child = adviceBody;
        _gif.Children.Add(_adviceCard);
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
        _sampleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SampleDebounce };
        _sampleTimer.Tick += (_, _) => { _sampleTimer.Stop(); StartSample(); };
        _format.SelectionChanged += (_, _) => Changed();
        _target.ValueChanged += (_, _) => Changed();
        _quality.SelectionChanged += (_, _) => Changed();
        _speed.SelectionChanged += (_, _) => Changed();
        _shareTarget.SelectionChanged += (_, _) => UpdateGuidance();
        _samplePosition.ValueChanged += (_, _) =>
        {
            UpdateSamplePositionLabel();
            if (IsLoaded && IsGif) RequestSample(immediately: false);
        };
        UpdateSamplePositionLabel();
        _applyAdvice.Click += (_, _) => ApplyRecommendation();
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
        Closed += (_, _) =>
        {
            _closed = true;
            _sampleTimer.Stop();
            _cancellation?.Cancel();
            _sampleCancellation?.Cancel();
            _samplePanel.Stop();
            _sample?.Dispose(); _sample = null;
            _result?.Dispose(); _result = null;
            _gifSession.Dispose();
        };
        // GIF is the common share target: render the preview sample the moment the dialog is
        // shown, so quality, speed and an estimated size appear without pressing 결과 계산.
        // Loaded (not the ctor) so headless construction in tests never starts a render.
        Loaded += (_, _) =>
        {
            if (_format.SelectedIndex == 1 && _sample is null) RequestSample(immediately: true);
        };
        Changed();
    }

    private static void AddText(Panel panel, string text) => panel.Children.Add(new TextBlock
    { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) });

    private static void AddOption(Grid grid, int column, double weight, string labelKey, ComboBox combo)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
        var cell = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0) };
        var label = new TextBlock { Text = UiText.Get(labelKey), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6) };
        cell.Children.Add(label);
        cell.Children.Add(combo);
        AutomationProperties.SetName(combo, label.Text);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>A combo box whose item captions can change (the 권장 mark) while selected.</summary>
    private static ComboBox OptionList()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(OptionItem.Text)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new ComboBox { MinHeight = 36, ItemTemplate = new DataTemplate { VisualTree = text } };
    }

    private static string QualityText(GifExportQuality quality) => $"{quality.LongEdge}px · {quality.FramesPerSecond} fps";

    /// <summary>
    /// Explains the trade the recommended preset actually makes. The motion class only orders
    /// the candidates; once the size target forces a smaller or slower preset, the sentence has
    /// to describe that preset rather than the preference it started from.
    /// </summary>
    private string RecommendationReasonKey(GifExportQuality recommended)
    {
        bool fullSize = recommended.LongEdge >= AnimatedGifExporter.MaximumLongEdge;
        bool fullRate = recommended.FramesPerSecond >= AnimatedGifExporter.FramesPerSecond;
        return (fullSize, fullRate) switch
        {
            (true, true) => GifSharePolicy.Classify(_sampleMotion) switch
            {
                GifMotionClass.Static => "GifPreview_MotionStatic",
                GifMotionClass.Moderate => "GifPreview_MotionModerate",
                _ => "GifPreview_MotionHigh",
            },
            (true, false) => "GifPreview_ReasonKeptSharpness",
            (false, true) => "GifPreview_ReasonKeptSmoothness",
            _ => "GifPreview_ReasonBothReduced",
        };
    }

    private static string SpeedText(double speed) => string.Create(CultureInfo.InvariantCulture, $"{speed:0.0}×");

    private GifExportQuality SelectedQuality => (GifExportQuality)((OptionItem)_quality.SelectedItem).Value;

    private double SelectedSpeed => (double)((OptionItem)_speed.SelectedItem).Value;

    private GifShareTarget SelectedShareTarget => (GifShareTarget)((OptionItem)_shareTarget.SelectedItem).Value;

    private double SpanMs => KeptRange.OutMs - KeptRange.InMs;

    /// <summary>The trim clamped to the recording, as the exporter normalizes it.</summary>
    private (double InMs, double OutMs) KeptRange
    {
        get
        {
            double duration = Math.Max(1, _recording.DurationMs);
            double trimIn = Math.Clamp(_document.TrimInMs, 0, duration);
            double trimOut = Math.Clamp(_document.TrimOutMs, 0, duration);
            return trimOut > trimIn ? (trimIn, trimOut) : (0, duration);
        }
    }

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
        // No hard cap and no fixed-length warning: the sample measures this clip, and the
        // estimate and recommendation below compare it with the chosen share limit.
        _status.Text = UiText.Get("MediaExport_CalculateHint");
        UpdateSamplePositionLabel();
        DiscardSample();
        if (gif && IsLoaded) RequestSample(immediately: false);
        UpdateGuidance();
    }

    internal int TargetReductionPercent
    {
        get => (int)_target.Value;
        set => _target.Value = value;
    }

    internal bool CanExport => _save.IsEnabled;

    /// <summary>Completes when the latest requested preview sample has been published or abandoned.</summary>
    internal Task SampleCompletion => _sampleTask;

    internal GifPreviewPanel SamplePanel => _samplePanel;

    internal string EstimateText => _estimate.Text;

    internal string AdviceText => _adviceCard.Visibility == Visibility.Visible ? _advice.Text : string.Empty;

    internal GifRecommendation? Recommendation => _recommendation;

    internal int QualityIndex
    {
        get => _quality.SelectedIndex;
        set => _quality.SelectedIndex = value;
    }

    internal int ShareTargetIndex
    {
        get => _shareTarget.SelectedIndex;
        set => _shareTarget.SelectedIndex = value;
    }

    internal bool IsGif => _format.SelectedIndex == 1;

    private void DiscardSample()
    {
        _sampleTimer.Stop();
        _sampleCancellation?.Cancel();
        _samplePanel.ShowPreparing();
        _sample?.Dispose();
        _sample = null;
    }

    private void RequestSample(bool immediately)
    {
        DiscardSample();
        if (immediately) StartSample();
        else _sampleTimer.Start();
    }

    private void UpdateSamplePositionLabel()
    {
        GifSampleWindow window = GifPreviewSampling.Plan(KeptRange.InMs, KeptRange.OutMs, SelectedSpeed, _samplePosition.Value / 100);
        _samplePositionLabel.Text = UiText.Format("GifPreview_SamplePositionValue", window.StartMs / 1000);
    }

    private void StartSample()
    {
        if (_closed || !IsGif || _working) return;
        var cancellation = new CancellationTokenSource();
        _sampleCancellation = cancellation;
        Task previous = _sampleTask;
        _sampleTask = RenderSampleAsync(previous, cancellation, _settingsRevision, ++_sampleRequest,
            SelectedQuality, SelectedSpeed, _samplePosition.Value / 100);
    }

    private bool IsCurrentSample(int revision, int request, CancellationTokenSource cancellation) =>
        !_closed && revision == _settingsRevision && request == _sampleRequest && !cancellation.IsCancellationRequested;

    private async Task RenderSampleAsync(Task previous, CancellationTokenSource cancellation, int revision, int request,
        GifExportQuality quality, double speed, double position)
    {
        GifPreviewSample? sample = null;
        try
        {
            // One render worker at a time: the cancelled one exits at its next frame.
            await previous;
            cancellation.Token.ThrowIfCancellationRequested();
            (double trimIn, double trimOut) = KeptRange;
            GifSampleWindow window = GifPreviewSampling.Plan(trimIn, trimOut, speed, position);
            VideoEditDocument sampleDocument = GifPreviewSampling.CreateSampleDocument(_document, window);
            CancellationToken token = cancellation.Token;
            sample = await StaThreadTask.RunAsync(
                () => GifPreviewSample.Render(_recording, sampleDocument, window, quality, speed, token),
                "MyCapture GIF preview sample");
            if (!IsCurrentSample(revision, request, cancellation)) return;
            _sample = sample;
            sample = null;
            _sampleMotion = _sample.MotionFraction;
            GifPresetMeasurement measurement = _sample.MeasurementFrom(trimIn);
            Remember(measurement);
            // A sample of the whole kept range is the exact export: make it saveable at once.
            VideoExportCalculation? whole = _working || _result is not null ? null : _sample.TakeWholeRangeResult();
            if (whole is not null)
            {
                _result = whole;
                _save.IsEnabled = true;
                _status.Text = UiText.Format("MediaExport_GifResult", _result.ResultBytes) + Environment.NewLine + UiText.Get("MediaExport_Ready");
            }

            _samplePanel.ShowSample(_sample, autoplay: FluidMotion.AnimationsEnabled);
            UpdateGuidance();

            // Estimate-only start and end segments refine the size once the preview is visible.
            foreach (GifSampleWindow segment in GifPreviewSampling.PlanRefinement(trimIn, trimOut, window))
            {
                VideoEditDocument segmentDocument = GifPreviewSampling.CreateSampleDocument(_document, segment);
                GifSegmentSize segmentSize = await StaThreadTask.RunAsync(
                    () => GifPreviewSample.MeasureSegment(_recording, segmentDocument, quality, speed, token),
                    "MyCapture GIF estimate segment");
                if (!IsCurrentSample(revision, request, cancellation)
                    || !_measurements.TryGetValue(quality, out GifPresetMeasurement? known)
                    || !ReferenceEquals(known, measurement))
                {
                    return; // Superseded, or replaced by an exact full calculation.
                }

                measurement = measurement.Including(segmentSize, segment.DurationMs, Math.Abs(segment.StartMs - trimIn) < 0.5);
                _measurements[quality] = measurement;
                UpdateGuidance();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _logs.CreateLogger("VideoExport").LogWarning(error, "GIF preview sample failed");
            if (IsCurrentSample(revision, request, cancellation) && _sample is null)
                _samplePanel.ShowFailure(error.Message);
        }
        finally
        {
            sample?.Dispose();
            if (ReferenceEquals(_sampleCancellation, cancellation)) _sampleCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>Keeps a whole-range measurement of a preset over a later partial sample.</summary>
    private void Remember(GifPresetMeasurement measurement)
    {
        bool keepExact = _measurements.TryGetValue(measurement.Quality, out GifPresetMeasurement? known)
            && known.SourceMs >= SpanMs - 0.5 && measurement.SourceMs < SpanMs - 0.5;
        if (!keepExact) _measurements[measurement.Quality] = measurement;
    }

    private Task<BitmapSource> RenderOriginalAsync(GifPreviewSample sample, int frameIndex, CancellationToken cancellationToken)
    {
        double sourceTime = sample.SourceTimeOf(frameIndex);
        string path = _recording.OutputPath;
        int width = _recording.Width, height = _recording.Height;
        List<TimedTextOverlay> overlays = _document.TextOverlays.Select(overlay => overlay.Clone()).ToList();
        List<FrameEditLayer> layers = _document.FrameEditLayers.Select(layer => layer.Clone()).ToList();
        return StaThreadTask.RunAsync(
            () => VideoFrameRenderPipeline.RenderSingleFrame(path, sourceTime, width, height, overlays, layers, cancellationToken),
            "MyCapture GIF preview original");
    }

    /// <summary>Text and image layer intervals inside the kept range, relative to its start.</summary>
    private List<(double StartMs, double EndMs)> LayerIntervals
    {
        get
        {
            (double trimIn, double trimOut) = KeptRange;
            return _document.TextOverlays.Select(overlay => (overlay.StartMs, overlay.EndMs))
                .Concat(_document.FrameEditLayers.Select(layer => (layer.StartMs, layer.EndMs)))
                .Where(interval => interval.EndMs > trimIn && interval.StartMs < trimOut)
                .Select(interval => (Math.Max(interval.StartMs, trimIn) - trimIn, Math.Min(interval.EndMs, trimOut) - trimIn))
                .ToList();
        }
    }

    /// <summary>Refreshes the estimate line, the recommendation and the 권장 mark.</summary>
    private void UpdateGuidance()
    {
        _recommendation = null;
        if (!IsGif || _measurements.Count == 0)
        {
            _estimate.Text = string.Empty;
            _adviceCard.Visibility = Visibility.Collapsed;
            MarkRecommended(null);
            return;
        }

        GifExportQuality quality = SelectedQuality;
        double speed = SelectedSpeed;
        double span = SpanMs;
        List<(double StartMs, double EndMs)> layers = LayerIntervals;
        double playbackSeconds = span / speed / 1000;
        int frames = GifPreviewSampling.EstimateFrameCount(span, quality.FramesPerSecond, layers);
        if (_measurements.TryGetValue(quality, out GifPresetMeasurement? measured))
        {
            GifSizeEstimate own = GifSizeEstimator.FromMeasurement(measured, span);
            _estimate.Text = own.IsExact
                ? UiText.Format("GifPreview_Exact", FormatSize(own.ExpectedBytes), frames, playbackSeconds)
                : UiText.Format("GifPreview_Estimate", FormatRange(own), frames, playbackSeconds,
                    Math.Max(1, GifSizeEstimator.EstimateCalculationMs(measured, span) / 1000));
        }
        else
        {
            _estimate.Text = string.Empty;
        }

        GifShareTarget shareTarget = SelectedShareTarget;
        GifRecommendation recommendation = GifSharePolicy.Recommend(
            span, _recording.Width, _recording.Height, _measurements, quality, speed, shareTarget, _sampleMotion, layers);
        _recommendation = recommendation;
        string limit = shareTarget.Bytes is long bytes ? FormatSize(bytes) : string.Empty;
        string size = FormatRange(recommendation.Estimate);
        var lines = new List<string>();
        switch (recommendation.Kind)
        {
            case GifRecommendationKind.UseMp4:
                int smallestFrames = GifPreviewSampling.EstimateFrameCount(span, recommendation.Quality.FramesPerSecond, layers);
                double maximumSeconds = recommendation.MaximumGifSourceMs / 1000;
                lines.Add(recommendation.Reason switch
                {
                    GifLimitReason.DestinationFrames => UiText.Format("GifPreview_AdviceMp4Frames", shareTarget.MaximumFrames ?? 0, smallestFrames, maximumSeconds),
                    GifLimitReason.SafetyFrames => UiText.Format("GifPreview_AdviceMp4Safety", smallestFrames, maximumSeconds),
                    _ => UiText.Format("GifPreview_AdviceMp4", size, limit, maximumSeconds),
                });
                break;
            case GifRecommendationKind.CurrentFits:
                lines.Add(shareTarget.Bytes is null
                    ? UiText.Format("GifPreview_AdviceCurrentNoLimit", size)
                    : UiText.Format("GifPreview_AdviceCurrent", size, limit));
                break;
            default:
                lines.Add(shareTarget.Bytes is null
                    ? UiText.Format("GifPreview_AdviceSwitchNoLimit", QualityText(recommendation.Quality), size)
                    : UiText.Format("GifPreview_AdviceSwitch", QualityText(recommendation.Quality), limit, size));
                lines.Add(UiText.Get(RecommendationReasonKey(recommendation.Quality)));
                GifSizeEstimate current = GifSharePolicy.EstimateFor(quality, span, _recording.Width, _recording.Height, _measurements);
                if (shareTarget.Bytes is long target && current.HighBytes > target)
                    lines.Add(UiText.Format("GifPreview_AdviceCurrentOver", FormatRange(current), limit));
                break;
        }

        // Speed is only ever suggested: it shortens playback, not the file.
        if (recommendation.Speed is double faster)
            lines.Add(UiText.Format("GifPreview_AdviceSpeed", playbackSeconds, faster, span / faster / 1000));
        _advice.Text = string.Join(Environment.NewLine, lines);
        _adviceCard.Visibility = Visibility.Visible;
        string action = UiText.Get(recommendation.Kind == GifRecommendationKind.UseMp4 ? "GifPreview_SwitchToMp4" : "GifPreview_ApplyAdvice");
        _applyAdviceText.Text = action;
        AutomationProperties.SetName(_applyAdvice, action);
        _applyAdvice.ToolTip = action;
        _applyAdvice.Visibility = recommendation.Kind == GifRecommendationKind.CurrentFits ? Visibility.Collapsed : Visibility.Visible;
        MarkRecommended(recommendation.Kind == GifRecommendationKind.UseMp4 ? null : recommendation.Quality);
    }

    private void MarkRecommended(GifExportQuality? quality)
    {
        string suffix = " · " + UiText.Get("GifPreview_Recommended");
        foreach (OptionItem item in _quality.Items)
            item.Text = QualityText((GifExportQuality)item.Value) + (Equals(item.Value, quality) ? suffix : string.Empty);
    }

    private void ApplyRecommendation()
    {
        GifRecommendation? recommendation = _recommendation;
        if (recommendation is null || _working) return;
        if (recommendation.Kind == GifRecommendationKind.UseMp4)
            _format.SelectedIndex = 0;
        else
            _quality.SelectedItem = _quality.Items.OfType<OptionItem>().First(item => Equals(item.Value, recommendation.Quality));
    }

    /// <summary>One size when measured, otherwise the estimated range.</summary>
    internal static string FormatRange(GifSizeEstimate estimate) => estimate.IsExact || estimate.LowBytes == estimate.HighBytes
        ? FormatSize(estimate.ExpectedBytes)
        : UiText.Format("GifPreview_SizeRange", FormatSize(estimate.LowBytes), FormatSize(estimate.HighBytes));

    /// <summary>Decimal units, matching how share sites state their upload limits.</summary>
    internal static string FormatSize(long bytes) => bytes >= 1_000_000
        ? UiText.Format("GifPreview_SizeMb", bytes / 1_000_000d)
        : UiText.Format("GifPreview_SizeKb", Math.Max(1, bytes / 1_000d));

    internal async Task CalculateAsync()
    {
        if (_working || _closed) return;
        _result?.Dispose(); _result = null;
        _preview.Source = null; _preview.Visibility = Visibility.Collapsed;
        bool gif = _format.SelectedIndex == 1;
        int target = (int)_target.Value;
        GifExportQuality quality = SelectedQuality;
        double speed = SelectedSpeed;
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
            if (gif)
            {
                // The full render needs the decoder to itself; a pending sample yields.
                _sampleTimer.Stop();
                _sampleCancellation?.Cancel();
                await _sampleTask;
            }

            var stopwatch = Stopwatch.StartNew();
            calculated = await StaThreadTask.RunAsync(() => gif
                ? VideoExportCalculation.CalculateGif(path => _gifSession.Export(path, quality, speed, gifProgress, cancellation.Token), cancellation.Token)
                : VideoExportCalculation.Calculate(target, _document.TrimOutMs - _document.TrimInMs,
                    VideoEncoderOptions.DeriveBitrate(_recording.Width, _recording.Height, _recording.Fps),
                    (path, bitrate, baseline, token) => TrimReencoder.Reencode(_recording.OutputPath, path,
                        _document.TrimInMs, _document.TrimOutMs, _recording,
                        options => new MediaFoundationVideoEncoder(options, _logs.CreateLogger<MediaFoundationVideoEncoder>()),
                        _logs.CreateLogger("VideoExport"), _document.TextOverlays, _document.FrameEditLayers, baseline ? baselineProgress : reducedProgress, token, bitrate),
                    cancellation.Token), "MyCapture export calculation");
            double elapsed = stopwatch.Elapsed.TotalMilliseconds;
            cancellation.Token.ThrowIfCancellationRequested();
            string resultPath = calculated.ResultPath;
            GifSegmentSize? gifSize = gif ? await Task.Run(() => GifSegmentSize.Measure(resultPath)) : null;
            BitmapSource? preview = gif ? null : await StaThreadTask.RunAsync(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                double scale = Math.Min(1, 480d / Math.Max(_recording.Width, _recording.Height));
                BitmapSource frame = VideoFrameRenderPipeline.RenderSingleFrame(calculated.ResultPath,
                    (_document.TrimOutMs - _document.TrimInMs) / 2,
                    Math.Max(1, (int)(_recording.Width * scale)), Math.Max(1, (int)(_recording.Height * scale)));
                frame.Freeze();
                cancellation.Token.ThrowIfCancellationRequested();
                return frame;
            }, "MyCapture export result preview");
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed || revision != _settingsRevision) return;
            _result = calculated;
            calculated = null; // Publish only the complete result for the current settings.
            if (gif)
            {
                // The animated sample above is the visual check; the measured file now
                // replaces the sample-based estimate for this preset.
                Remember(gifSize is { } size
                    ? GifPresetMeasurement.Of(quality, size, SpanMs, elapsed / SpanMs, startsRange: true)
                    : new GifPresetMeasurement(quality, _result.ResultBytes, SpanMs, elapsed / SpanMs));
                UpdateGuidance();
            }
            else
            {
                _preview.Source = preview;
                _preview.Visibility = Visibility.Visible;
            }

            _status.Text = gif ? UiText.Format("MediaExport_GifResult", _result.ResultBytes) + Environment.NewLine + UiText.Get("MediaExport_Ready")
                : UiText.Format("MediaExport_Result", _result.BaselineBytes, _result.ResultBytes, _result.ActualReduction)
                    + Environment.NewLine + UiText.Get(_result.TargetReached ? "MediaExport_Ready" : "MediaExport_Unmet");
            VideoExportCalculation ready = _result;
            FrameworkElement shown = gif ? _status : _preview;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_closed && !_working && ReferenceEquals(_result, ready)) shown.BringIntoView();
            }));
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
            else if (gif && !_closed && revision == _settingsRevision && _sample is null) RequestSample(immediately: true);
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
        _status.Text = UiText.Format("MediaExport_SavingCopy", 0);
        var copyProgress = new Progress<double>(percent =>
        {
            if (!_closed && revision == _settingsRevision && ReferenceEquals(_cancellation, cancellation))
            {
                _status.Text = UiText.Format("MediaExport_SavingCopy", (int)Math.Clamp(percent, 0, 100));
            }
        });
        try
        {
            await Task.Run(() => result.SaveCopy(dialog.FileName, _recording.OutputPath, cancellation.Token, copyProgress));
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

    internal static BitmapSource CreateGifPreview(string path)
    {
        using var stream = File.OpenRead(path);
        // OnLoad caches every GIF frame. Decode only the first frame on demand and
        // copy its pixels into a standalone bitmap; a returned BitmapFrameDecode
        // would retain the decoder and all of its frame wrappers and native state.
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
        BitmapSource first = decoder.Frames[0];
        if (first.Format != PixelFormats.Bgra32)
            first = new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0);
        int stride = checked(first.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * first.PixelHeight)];
        first.CopyPixels(pixels, stride, 0);
        BitmapSource result = BitmapSource.Create(first.PixelWidth, first.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Combo-box entry whose caption can gain or lose the 권장 mark in place.</summary>
    private sealed class OptionItem(string text, object value) : INotifyPropertyChanged
    {
        private string _text = text;

        public event PropertyChangedEventHandler? PropertyChanged;

        public object Value { get; } = value;

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public override string ToString() => _text;
    }
}
