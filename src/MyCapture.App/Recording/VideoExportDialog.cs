using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

/// <summary>
/// One-step export: choose a format and size, press 내보내기, pick the destination, and the
/// dialog encodes once straight into that folder. Never commits edits to the library.
/// </summary>
internal sealed class VideoExportDialog : Window
{
    /// <summary>Settles keyboard arrowing through the option lists before a new sample starts.</summary>
    internal static readonly TimeSpan SampleDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>Target bitrate reductions behind 원본 화질 · 작게 · 더 작게.</summary>
    internal static readonly int[] SizePresets = [0, 40, 70];

    /// <summary>Folder of the last export in this session; the next save dialog opens there.</summary>
    private static string? s_lastFolder;

    private readonly RecordingResult _recording;
    private readonly VideoEditDocument _document;
    private readonly ILoggerFactory _logs;
    // Reuses decoded/encoded GIF frames while only preset or speed changes in this dialog.
    private readonly GifExportSession _gifSession;
    private readonly SegmentedChoice _format;
    private readonly SegmentedChoice _sizePreset;
    private readonly Slider _target = new() { Minimum = 0, Maximum = 90, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _targetLabel = new();
    private readonly ComboBox _quality = OptionList();
    private readonly ComboBox _speed = OptionList();
    private readonly ComboBox _shareTarget = OptionList();
    private readonly TextBlock _estimate = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Image _preview = new() { Height = 150, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly GifPreviewPanel _samplePanel;
    private readonly StackPanel _samplePositionRow = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _samplePositionLabel = new() { Margin = new Thickness(0, 8, 0, 4) };
    private readonly Slider _samplePosition = new() { Minimum = 0, Maximum = 100, Value = GifPreviewSampling.CentrePosition * 100, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly Grid _adviceRow = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _advice = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _applyAdvice;
    private readonly TextBlock _applyAdviceText;
    private readonly StackPanel _mp4 = new();
    private readonly StackPanel _gif = new();
    private readonly StackPanel _settings = new();
    private readonly Grid _progressRow = new() { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ProgressBar _progressBar = new() { Minimum = 0, Maximum = 100, Height = 6, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _percent = new() { Margin = new Thickness(12, 0, 0, 0), MinWidth = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _remaining = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancelExport;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _openFolder;
    private readonly Button _export;
    private readonly Button _close;
    private readonly DispatcherTimer _sampleTimer;
    private readonly Dictionary<GifExportQuality, GifPresetMeasurement> _measurements = [];
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _sampleCancellation;
    private CancellationTokenSource? _previewCancellation;
    private Task _sampleTask = Task.CompletedTask;
    private Task _previewTask = Task.CompletedTask;
    private GifPreviewSample? _sample;
    private GifRecommendation? _recommendation;
    // A sample that covered the whole kept range already is the exact GIF: exporting copies it.
    private VideoExportCalculation? _result;
    private string? _savedPath;
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
        // Tall enough for every row plus the progress row that appears while exporting, so the
        // form never scrolls; the GIF form also carries the animated sample.
        Height = Math.Min(MaxHeight, gif ? 860 : 610);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        var root = new Grid { Margin = new Thickness(20, 12, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);

        AddText(_settings, UiText.Get("MediaExport_Format"));
        _format = new SegmentedChoice([("MP4", null), ("GIF", null)]);
        AutomationProperties.SetName(_format.Element, UiText.Get("MediaExport_Format"));
        _format.SelectedIndex = gif ? 1 : 0;
        _settings.Children.Add(_format.Element);

        AddText(_mp4, UiText.Get("MediaExport_SizeHeading"));
        _sizePreset = new SegmentedChoice(
        [
            (UiText.Get("MediaExport_SizeOriginal"), UiText.Get("MediaExport_SizeOriginalHelp")),
            (UiText.Get("MediaExport_SizeSmaller"), UiText.Format("MediaExport_SizePresetHelp", SizePresets[1])),
            (UiText.Get("MediaExport_SizeSmallest"), UiText.Format("MediaExport_SizePresetHelp", SizePresets[2])),
        ]);
        AutomationProperties.SetName(_sizePreset.Element, UiText.Get("MediaExport_SizeHeading"));
        _sizePreset.SelectedIndex = 0;
        _mp4.Children.Add(_sizePreset.Element);
        _targetLabel.Margin = new Thickness(0, 8, 0, 4);
        var advancedBody = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        advancedBody.Children.Add(_targetLabel);
        advancedBody.Children.Add(_target);
        AutomationProperties.SetName(_target, UiText.Get("MediaExport_TargetLabel"));
        var advancedHeader = new TextBlock { Text = UiText.Get("MediaExport_Advanced") };
        advancedHeader.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var advanced = new Expander { Header = advancedHeader, Content = advancedBody, IsExpanded = false, Margin = new Thickness(0, 10, 0, 0) };
        advanced.SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(advanced, UiText.Get("MediaExport_Advanced"));
        _mp4.Children.Add(advanced);
        _settings.Children.Add(_mp4);

        foreach (GifExportQuality quality in GifSharePolicy.Presets)
            _quality.Items.Add(new OptionItem(QualityText(quality), quality));
        _quality.SelectedIndex = 0;
        _quality.ToolTip = UiText.Get("MediaExport_GifLimits");
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
        _adviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _adviceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _advice.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_advice, AutomationLiveSetting.Polite);
        _adviceRow.Children.Add(_advice);
        _applyAdvice = MediaExportVisuals.Button(this, UiText.Get("GifPreview_ApplyAdvice"));
        _applyAdviceText = ((StackPanel)_applyAdvice.Content).Children.OfType<TextBlock>().Single();
        _applyAdvice.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_applyAdvice, 1);
        _adviceRow.Children.Add(_applyAdvice);
        _gif.Children.Add(_adviceRow);
        _settings.Children.Add(_gif);

        _estimate.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_estimate, AutomationLiveSetting.Polite);
        _settings.Children.Add(_estimate);
        AutomationProperties.SetName(_preview, UiText.Get("MediaExport_SourcePreview"));
        _settings.Children.Add(_preview);

        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _progressBar.SetResourceReference(ForegroundProperty, "Accent.Default");
        _progressBar.SetResourceReference(BackgroundProperty, "Surface.Sunken");
        AutomationProperties.SetName(_progressBar, UiText.Get("MediaExport_Exporting"));
        _progressRow.Children.Add(_progressBar);
        Grid.SetColumn(_percent, 1);
        _progressRow.Children.Add(_percent);
        _remaining.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Grid.SetColumn(_remaining, 2);
        _progressRow.Children.Add(_remaining);
        _cancelExport = MediaExportVisuals.Button(this, UiText.Get("MediaExport_Cancel"));
        _cancelExport.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_cancelExport, 3);
        _progressRow.Children.Add(_cancelExport);
        Grid.SetRow(_progressRow, 1);
        root.Children.Add(_progressRow);

        var statusRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        statusRow.Children.Add(_status);
        _openFolder = MediaExportVisuals.Button(this, UiText.Get("MediaExport_OpenFolder"));
        _openFolder.Margin = new Thickness(12, 0, 0, 0);
        _openFolder.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_openFolder, 1);
        statusRow.Children.Add(_openFolder);
        Grid.SetRow(statusRow, 2);
        root.Children.Add(statusRow);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _export = MediaExportVisuals.Button(this, UiText.Get("MediaExport_Export"), "MediaExport_ArrowExport", true).AsDialogAction();
        _close = MediaExportVisuals.Button(this, UiText.Get("MediaExport_Close")).AsDialogAction();
        footer.Children.Add(_export); footer.Children.Add(_close);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;

        _sampleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SampleDebounce };
        _sampleTimer.Tick += (_, _) => { _sampleTimer.Stop(); StartSample(); };
        _format.SelectionChanged += Changed;
        // A preset moves the slider; a slider value outside the presets clears the choice.
        _sizePreset.SelectionChanged += () => { if (_sizePreset.SelectedIndex >= 0) _target.Value = SizePresets[_sizePreset.SelectedIndex]; };
        _target.ValueChanged += (_, _) => { _sizePreset.SelectedIndex = Array.IndexOf(SizePresets, (int)_target.Value); Changed(); };
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
        _export.Click += async (_, _) => await ExportAsync();
        _cancelExport.Click += (_, _) => CancelExport();
        _openFolder.Click += (_, _) => OpenFolder();
        _close.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_working) CancelExport(); else Close();
            e.Handled = true;
        };
        // Closing mid-export cancels it, removes the partial file and then closes.
        Closing += (_, e) => { if (_working) { e.Cancel = true; _closeRequested = true; _cancellation?.Cancel(); } };
        Closed += (_, _) =>
        {
            _closed = true;
            _sampleTimer.Stop();
            _cancellation?.Cancel();
            _sampleCancellation?.Cancel();
            _previewCancellation?.Cancel();
            _samplePanel.Stop();
            _sample?.Dispose(); _sample = null;
            _result?.Dispose(); _result = null;
            _gifSession.Dispose();
        };
        // GIF renders its animated sample the moment the dialog is shown; MP4 shows a frame of
        // the kept range. Loaded (not the ctor) so headless construction in tests never renders.
        Loaded += (_, _) =>
        {
            if (IsGif) { if (_sample is null) RequestSample(immediately: true); }
            else RequestSourcePreview();
        };
        Changed();
    }

    /// <summary>Replaces the Save As dialog in tests; returns the destination path or null to cancel.</summary>
    internal Func<string?>? DestinationChooserForTest { get; set; }

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
        bool gif = IsGif;
        _mp4.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        _gif.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        _targetLabel.Text = UiText.Format("MediaExport_Target", (int)_target.Value);
        if (!_working)
        {
            _status.Text = UiText.Get("MediaExport_Hint");
            _openFolder.Visibility = Visibility.Collapsed;
            _savedPath = null;
        }

        // The staged whole-range GIF matches one quality/speed only.
        _result?.Dispose(); _result = null;
        UpdateSamplePositionLabel();
        DiscardSample();
        _preview.Visibility = !gif && _preview.Source is not null ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && !_working)
        {
            if (gif) RequestSample(immediately: false);
            else RequestSourcePreview();
        }

        UpdateGuidance();
    }

    internal int TargetReductionPercent
    {
        get => (int)_target.Value;
        set => _target.Value = value;
    }

    /// <summary>Index into <see cref="SizePresets"/>, or -1 when the slider holds a custom value.</summary>
    internal int SizePresetIndex
    {
        get => _sizePreset.SelectedIndex;
        set => _sizePreset.SelectedIndex = value;
    }

    /// <summary>내보내기 is available: nothing is exporting and the dialog is open.</summary>
    internal bool CanExport => _export.IsEnabled;

    /// <summary>A whole-range GIF sample is staged, so exporting copies instead of encoding.</summary>
    internal bool HasMeasuredResult => _result is not null;

    internal bool IsExporting => _working;

    internal bool IsProgressVisible => _progressRow.Visibility == Visibility.Visible;

    internal string StatusText => _status.Text;

    internal string? SavedPath => _savedPath;

    /// <summary>Completes when the latest requested preview sample has been published or abandoned.</summary>
    internal Task SampleCompletion => _sampleTask;

    internal GifPreviewPanel SamplePanel => _samplePanel;

    internal string EstimateText => _estimate.Text;

    internal string AdviceText => _adviceRow.Visibility == Visibility.Visible ? _advice.Text : string.Empty;

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
            // A sample of the whole kept range is the exact export: 내보내기 copies it.
            VideoExportCalculation? whole = _working || _result is not null ? null : _sample.TakeWholeRangeResult();
            if (whole is not null) _result = whole;

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

    /// <summary>The MP4 preview: the middle frame of the kept range with its text and image layers.</summary>
    private void RequestSourcePreview()
    {
        if (_closed || IsGif || _working || _preview.Source is not null || !_previewTask.IsCompleted) return;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewTask = RenderSourcePreviewAsync(cancellation);
    }

    private async Task RenderSourcePreviewAsync(CancellationTokenSource cancellation)
    {
        try
        {
            (double trimIn, double trimOut) = KeptRange;
            double sourceTime = trimIn + ((trimOut - trimIn) / 2);
            string path = _recording.OutputPath;
            int width = _recording.Width, height = _recording.Height;
            List<TimedTextOverlay> overlays = _document.TextOverlays.Select(overlay => overlay.Clone()).ToList();
            List<FrameEditLayer> layers = _document.FrameEditLayers.Select(layer => layer.Clone()).ToList();
            CancellationToken token = cancellation.Token;
            BitmapSource frame = await StaThreadTask.RunAsync(() =>
            {
                BitmapSource rendered = VideoFrameRenderPipeline.RenderSingleFrame(path, sourceTime, width, height, overlays, layers, token);
                if (rendered.CanFreeze) rendered.Freeze();
                token.ThrowIfCancellationRequested();
                return rendered;
            }, "MyCapture export preview");
            if (_closed || cancellation.IsCancellationRequested) return;
            _preview.Source = frame;
            _preview.Visibility = IsGif ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            // The preview is a courtesy; the export itself reports its own failures.
            _logs.CreateLogger("VideoExport").LogWarning(error, "Export preview frame failed");
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose();
        }
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

    /// <summary>Refreshes the summary line, the one-line recommendation and the 권장 mark.</summary>
    private void UpdateGuidance()
    {
        _recommendation = null;
        double span = SpanMs;
        if (!IsGif)
        {
            long bytes = EstimateMp4Bytes(_recording.Width, _recording.Height, _recording.Fps, span, TargetReductionPercent);
            _estimate.Text = UiText.Format("MediaExport_Mp4Summary", FormatSize(bytes), FormatClock(span), _recording.Width, _recording.Height);
            _adviceRow.Visibility = Visibility.Collapsed;
            MarkRecommended(null);
            return;
        }

        GifExportQuality quality = SelectedQuality;
        double speed = SelectedSpeed;
        List<(double StartMs, double EndMs)> layers = LayerIntervals;
        double playbackSeconds = span / speed / 1000;
        int frames = GifPreviewSampling.EstimateFrameCount(span, quality.FramesPerSecond, layers);
        if (_measurements.TryGetValue(quality, out GifPresetMeasurement? measured))
        {
            GifSizeEstimate own = GifSizeEstimator.FromMeasurement(measured, span);
            _estimate.Text = own.IsExact
                ? UiText.Format("GifPreview_Exact", FormatSize(own.ExpectedBytes), frames, playbackSeconds)
                : UiText.Format("MediaExport_GifSummary", FormatRange(own), frames, playbackSeconds);
        }
        else
        {
            _estimate.Text = UiText.Format("MediaExport_GifSummaryPending", frames, playbackSeconds);
        }

        if (_measurements.Count == 0)
        {
            _adviceRow.Visibility = Visibility.Collapsed;
            MarkRecommended(null);
            return;
        }

        GifShareTarget shareTarget = SelectedShareTarget;
        GifRecommendation recommendation = GifSharePolicy.Recommend(
            span, _recording.Width, _recording.Height, _measurements, quality, speed, shareTarget, _sampleMotion, layers);
        _recommendation = recommendation;
        string limit = shareTarget.Bytes is long bytesLimit ? FormatSize(bytesLimit) : string.Empty;
        string size = FormatRange(recommendation.Estimate);
        // One short line in the form; the full reasoning stays available as its tooltip.
        string headline;
        var detail = new List<string>();
        switch (recommendation.Kind)
        {
            case GifRecommendationKind.UseMp4:
                int smallestFrames = GifPreviewSampling.EstimateFrameCount(span, recommendation.Quality.FramesPerSecond, layers);
                double maximumSeconds = recommendation.MaximumGifSourceMs / 1000;
                bool tooManyFrames = recommendation.Reason is GifLimitReason.DestinationFrames or GifLimitReason.SafetyFrames;
                headline = tooManyFrames ? UiText.Get("MediaExport_AdviceMp4FramesShort") : UiText.Format("MediaExport_AdviceMp4Short", limit);
                detail.Add(recommendation.Reason switch
                {
                    GifLimitReason.DestinationFrames => UiText.Format("GifPreview_AdviceMp4Frames", shareTarget.MaximumFrames ?? 0, smallestFrames, maximumSeconds),
                    GifLimitReason.SafetyFrames => UiText.Format("GifPreview_AdviceMp4Safety", smallestFrames, maximumSeconds),
                    _ => UiText.Format("GifPreview_AdviceMp4", size, limit, maximumSeconds),
                });
                break;
            case GifRecommendationKind.CurrentFits:
                headline = shareTarget.Bytes is null
                    ? UiText.Get("MediaExport_AdviceCurrentNoLimitShort")
                    : UiText.Format("MediaExport_AdviceCurrentShort", limit);
                detail.Add(shareTarget.Bytes is null
                    ? UiText.Format("GifPreview_AdviceCurrentNoLimit", size)
                    : UiText.Format("GifPreview_AdviceCurrent", size, limit));
                break;
            default:
                headline = UiText.Format("MediaExport_AdviceSwitchShort", QualityText(recommendation.Quality), size);
                detail.Add(shareTarget.Bytes is null
                    ? UiText.Format("GifPreview_AdviceSwitchNoLimit", QualityText(recommendation.Quality), size)
                    : UiText.Format("GifPreview_AdviceSwitch", QualityText(recommendation.Quality), limit, size));
                detail.Add(UiText.Get(RecommendationReasonKey(recommendation.Quality)));
                GifSizeEstimate current = GifSharePolicy.EstimateFor(quality, span, _recording.Width, _recording.Height, _measurements);
                if (shareTarget.Bytes is long target && current.HighBytes > target)
                    detail.Add(UiText.Format("GifPreview_AdviceCurrentOver", FormatRange(current), limit));
                break;
        }

        // Speed is only ever suggested: it shortens playback, not the file.
        if (recommendation.Speed is double faster)
        {
            headline += " · " + UiText.Format("MediaExport_AdviceSpeedShort", faster);
            detail.Add(UiText.Format("GifPreview_AdviceSpeed", playbackSeconds, faster, span / faster / 1000));
        }

        _advice.Text = headline;
        _advice.ToolTip = string.Join(Environment.NewLine, detail);
        AutomationProperties.SetHelpText(_advice, (string)_advice.ToolTip);
        _adviceRow.Visibility = Visibility.Visible;
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

    /// <summary>Decimal units, matching how share sites state their upload limits; MB with one decimal.</summary>
    internal static string FormatSize(long bytes) => bytes >= 1_000_000
        ? UiText.Format("MediaExport_SizeMb", bytes / 1_000_000d)
        : UiText.Format("MediaExport_SizeKb", Math.Max(1, bytes / 1_000d));

    /// <summary>The kept range's length in the editor's time format, as the summary line shows it.</summary>
    internal static string FormatClock(double milliseconds) => VideoTimecode.Format(milliseconds);

    /// <summary>
    /// The MP4 size the summary line promises before anything is encoded: the standard bitrate
    /// for the source size over the kept range, less the chosen reduction.
    /// </summary>
    internal static long EstimateMp4Bytes(int width, int height, int fps, double spanMs, int reduction)
    {
        int standard = VideoEncoderOptions.DeriveBitrate(width, height, fps);
        double seconds = Math.Max(0, spanMs) / 1000;
        return (long)Math.Round(standard / 8d * seconds * (100 - Math.Clamp(reduction, 0, 90)) / 100);
    }

    /// <summary>Source file stem plus the localized "_편집" suffix and the export extension.</summary>
    internal static string DefaultExportName(string sourcePath, string extension) =>
        Path.GetFileNameWithoutExtension(sourcePath) + UiText.Get("MediaExport_EditedSuffix") + extension;

    private int BitrateFor(int reduction)
    {
        int standard = VideoEncoderOptions.DeriveBitrate(_recording.Width, _recording.Height, _recording.Fps);
        if (reduction <= 0) return standard;
        // The target-reduction model against the estimated standard size: no baseline encode.
        long baseline = Math.Max(1, EstimateMp4Bytes(_recording.Width, _recording.Height, _recording.Fps, SpanMs, 0));
        return VideoExportCalculation.ReducedBitrate(baseline, SpanMs, reduction, standard);
    }

    private string? ChooseDestination(string extension)
    {
        if (DestinationChooserForTest is Func<string?> chooser) return chooser();
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("MediaExport_Export"), DefaultExt = extension,
            Filter = extension == ".gif" ? "GIF (*.gif)|*.gif" : "MP4 (*.mp4)|*.mp4",
            AddExtension = true, OverwritePrompt = true,
            FileName = DefaultExportName(_recording.OutputPath, extension),
        };
        if (s_lastFolder is not null && Directory.Exists(s_lastFolder)) dialog.InitialDirectory = s_lastFolder;
        if (dialog.ShowDialog(this) != true) return null;
        s_lastFolder = Path.GetDirectoryName(dialog.FileName);
        return dialog.FileName;
    }

    /// <summary>
    /// Asks where to save, then encodes once into a temporary sibling of that file and gives it
    /// the chosen name. A cancelled or failed export leaves nothing behind.
    /// </summary>
    internal async Task ExportAsync()
    {
        if (_working || _closed) return;
        bool gif = IsGif;
        string extension = gif ? ".gif" : ".mp4";
        string? destination = ChooseDestination(extension);
        if (destination is null || _closed || _working) return;
        int target = TargetReductionPercent;
        GifExportQuality quality = SelectedQuality;
        double speed = SelectedSpeed;
        int revision = _settingsRevision;
        VideoExportCalculation? staged = gif ? _result : null;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetWorking(true);
        var stopwatch = Stopwatch.StartNew();
        var progress = new Progress<VideoFrameRenderProgress>(p => ShowProgress(p, stopwatch, cancellation));
        string? temporary = null;
        try
        {
            string full = Path.GetFullPath(destination);
            string source = _recording.OutputPath;
            if (string.Equals(full, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
                throw UserFacingError.Mark(new IOException(UiText.Get("MediaExport_SourceProtected")));
            if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
                throw UserFacingError.Mark(new IOException(UiText.Format("MediaExport_Extension", extension)));
            string folder = Path.GetDirectoryName(full) ?? throw new IOException("Missing export directory.");
            temporary = Path.Combine(folder, ".export-" + Guid.NewGuid().ToString("N") + extension);
            // The encoder needs the decoder to itself; a pending sample or preview yields first.
            _sampleTimer.Stop();
            _sampleCancellation?.Cancel();
            _previewCancellation?.Cancel();
            await _sampleTask;
            await _previewTask;
            cancellation.Token.ThrowIfCancellationRequested();
            (double trimIn, double trimOut) = KeptRange;
            string temp = temporary;
            int bitrate = gif ? 0 : BitrateFor(target);
            List<TimedTextOverlay> overlays = _document.TextOverlays;
            List<FrameEditLayer> layers = _document.FrameEditLayers;
            CancellationToken token = cancellation.Token;
            await StaThreadTask.RunAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (staged is not null)
                {
                    File.Copy(staged.ResultPath, temp);
                    ((IProgress<VideoFrameRenderProgress>)progress).Report(new VideoFrameRenderProgress(1, 1, trimOut));
                }
                else if (gif)
                {
                    _gifSession.Export(temp, quality, speed, progress, token);
                }
                else
                {
                    TrimReencoder.Reencode(source, temp, trimIn, trimOut, _recording,
                        options => new MediaFoundationVideoEncoder(options, _logs.CreateLogger<MediaFoundationVideoEncoder>()),
                        _logs.CreateLogger("VideoExport"), overlays, layers, progress, token, bitrate);
                }

                token.ThrowIfCancellationRequested();
                var file = new FileInfo(temp);
                if (!file.Exists || file.Length <= 0) throw new IOException("The video encoder produced no output.");
                // Hold the source against replacement through an alias while the result takes
                // its final name; the lexical check above cannot see junctions or hard links.
                using var sourceProtection = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                File.Move(temp, full, overwrite: true);
                return file.Length;
            }, "MyCapture export");
            temporary = null;
            if (_closed) return;
            _savedPath = full;
            _status.Text = UiText.Format("MediaExport_Saved", Path.GetFileName(full));
            _openFolder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (!_closed) _status.Text = UiText.Get("MediaExport_Cancelled");
        }
        catch (Exception error)
        {
            _logs.CreateLogger("VideoExport").LogWarning(error, "Video export failed");
            if (!_closed) _status.Text = UiText.Format("MediaExport_Error", ThemedDialog.RecoveryMessage(error));
        }
        finally
        {
            if (temporary is not null) TryDelete(temporary);
            _cancellation = null;
            SetWorking(false);
            if (_closeRequested) Close();
            else if (!_closed && revision == _settingsRevision)
            {
                if (gif && _sample is null) RequestSample(immediately: true);
                else if (!gif) RequestSourcePreview();
            }
        }
    }

    internal void CancelExport() => _cancellation?.Cancel();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ShowProgress(VideoFrameRenderProgress progress, Stopwatch stopwatch, CancellationTokenSource cancellation)
    {
        if (_closed || !_working || !ReferenceEquals(_cancellation, cancellation)) return;
        int total = Math.Max(1, progress.TotalFrames);
        int completed = Math.Clamp(progress.CompletedFrames, 0, total);
        double fraction = (double)completed / total;
        _progressBar.Value = fraction * 100;
        _percent.Text = UiText.Format("MediaExport_Percent", (int)(fraction * 100));
        _status.Text = UiText.Format("MediaExport_Progress", UiText.Get("MediaExport_Exporting"), completed, total);
        // A few frames settle the per-frame pace before the remaining time is worth showing.
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        _remaining.Text = completed >= 5 && completed < total && elapsed >= 0.4
            ? UiText.Format("MediaExport_Remaining", Math.Max(1, Math.Ceiling(elapsed / completed * (total - completed))))
            : string.Empty;
    }

    internal string RemainingText => _remaining.Text;

    private void OpenFolder()
    {
        if (_savedPath is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _savedPath + "\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception error)
        {
            _status.Text = UiText.Format("MediaExport_Error", ThemedDialog.RecoveryMessage(error));
        }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        _settings.IsEnabled = !working;
        _export.IsEnabled = !working;
        _progressRow.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        if (working)
        {
            _progressBar.Value = 0;
            _percent.Text = UiText.Format("MediaExport_Percent", 0);
            _remaining.Text = string.Empty;
            _status.Text = UiText.Get("MediaExport_Exporting");
            _openFolder.Visibility = Visibility.Collapsed;
            _savedPath = null;
        }
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

    /// <summary>A row of toggle buttons of which exactly one (or none) is chosen.</summary>
    private sealed class SegmentedChoice
    {
        private readonly List<ToggleButton> _buttons = [];
        private int _selectedIndex = -1;

        internal SegmentedChoice(IReadOnlyList<(string Caption, string? Help)> choices)
        {
            var row = new UniformGrid { Rows = 1, Columns = choices.Count };
            foreach ((string caption, string? help) in choices)
            {
                int index = _buttons.Count;
                var button = new ToggleButton { Content = caption, MinHeight = 36, MinWidth = 112, Margin = new Thickness(2), ToolTip = help };
                AutomationProperties.SetName(button, caption);
                if (help is not null) AutomationProperties.SetHelpText(button, help);
                // Click runs after the toggle flipped itself; re-assert the single choice.
                button.Click += (_, _) => SelectedIndex = index;
                _buttons.Add(button);
                row.Children.Add(button);
            }

            Element = new Border { Child = row, Padding = new Thickness(2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            Element.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
            Element.SetResourceReference(Border.BorderBrushProperty, "Border.Control");
            Element.SetResourceReference(Border.CornerRadiusProperty, "Radius.Md");
        }

        internal Border Element { get; }

        internal event Action? SelectionChanged;

        internal int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                int next = value >= 0 && value < _buttons.Count ? value : -1;
                for (int i = 0; i < _buttons.Count; i++) _buttons[i].IsChecked = i == next;
                if (next == _selectedIndex) return;
                _selectedIndex = next;
                SelectionChanged?.Invoke();
            }
        }
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
