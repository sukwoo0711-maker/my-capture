using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.App.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Plays a finished recording and lets the user trim it, step it frame by frame, and
/// pull any frame into the existing still-image annotation editor.
/// </summary>
/// <remarks>
/// <para>
/// Playback and seeking use WPF <see cref="MediaElement"/> in manual clock mode, which
/// decodes on demand rather than holding every frame in memory — the property the brief
/// needs for a weak PC. Trimming is non-destructive (a <see cref="TrimSelection"/>); the
/// source MP4 is only re-encoded when the user commits.
/// </para>
/// <para>
/// Arrow-key behaviour is the crux of the request and is delegated wholly to
/// <see cref="FrameStepCalculator"/>: in frame-step mode Left/Right move one frame
/// (Shift = 10); otherwise they move a normal-editor "coarse" step.
/// </para>
/// </remarks>
internal sealed class VideoEditorWindow : Window
{
    private readonly RecordingResult _recording;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VideoEditorWindow> _log;
    private VideoEditDocument _editDocument;
    private VideoEditDocument? _initialDocument;

    private readonly MediaElement _media;
    private readonly TimedTextPreviewView _overlayPreview;
    private readonly MediaElementPreviewEngine _previewEngine;
    private readonly PreviewSeekCoordinator _previewSeeks;
    private readonly TwoLineTimeline _timeline;
    private readonly TextBlock _positionLabel;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _loadingLabel;
    private readonly Border _loadingOverlay;
    private readonly ListBox _overlayList;
    private Button _addTextButton = null!;
    private Button _editTextButton = null!;
    private Button _deleteTextButton = null!;
    private Button _cancelOperationButton = null!;
    private readonly List<Control> _editControls = [];
    private Grid _controlRows = null!;
    private DispatcherTimer? _loadProgressTimer;
    private DispatcherTimer? _openTimeoutTimer;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _operationCts;

    private double _durationMs;
    private bool _mediaReady;
    private bool _mediaFailed;
    private string _mediaFailure = string.Empty;
    private bool _committed;
    private bool _isPlaying;
    private bool _operationRunning;
    private bool _closeRequested;

    // Test hooks so a headless self-test can confirm the editor actually reaches the ready
    // state for a real clip (the field report: a ~2s video failed to load).
    internal bool IsMediaReadyForTest => _mediaReady;

    internal bool HasMediaFailedForTest => _mediaFailed;

    internal string MediaFailureForTest => _mediaFailure;

    internal double DurationMsForTest => _durationMs;

    internal TwoLineTimeline TimelineForTest => _timeline;

    internal PreviewSeekCoordinator PreviewSeekCoordinatorForTest => _previewSeeks;

    internal int ControlRowCountForTest => _controlRows.RowDefinitions.Count;

    internal double ControlAreaWidthForTest => _controlRows.ActualWidth;

    internal double WidestControlRowWidthForTest => _controlRows.Children
        .OfType<FrameworkElement>()
        .Max(child => child.DesiredSize.Width);

    internal double WidestControlRowContentWidthForTest => _controlRows.Children
        .OfType<Panel>()
        .Max(panel => panel.Children
            .OfType<FrameworkElement>()
            .Sum(child => child.ActualWidth + child.Margin.Left + child.Margin.Right));

    internal VideoEditorWindow(
        RecordingResult recording,
        AppPaths paths,
        ILoggerFactory loggerFactory,
        VideoEditDocument? editDocument = null)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<VideoEditorWindow>();
        _editDocument = (editDocument ?? VideoEditDocument.CreateFor(
                recording.Width,
                recording.Height,
                recording.DurationMs))
            .NormalizeFor(recording.Width, recording.Height, recording.DurationMs);

        StandardWindowTheme.Apply(this);

        Title = "MyCapture — 녹화 편집";
        Background = TryBrush("Surface.Base", Color.FromRgb(0x1B, 0x17, 0x12));
        Foreground = TryBrush("Text.Primary", Colors.White);
        FontFamily = TryFont("Font.Ui");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        MinWidth = 760;
        MinHeight = 560;

        Rect work = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(920, recording.Width + 120), Math.Max(MinWidth, work.Width - 80));
        Height = Math.Min(Math.Max(680, recording.Height + 260), Math.Max(MinHeight, work.Height - 60));

        AutomationProperties.SetName(this, "녹화 편집 창");

        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
        };
        _media.MediaOpened += OnMediaOpened;
        _media.MediaFailed += OnMediaFailed;
        _media.MediaEnded += OnMediaEnded;
        _overlayPreview = new TimedTextPreviewView();
        _overlayPreview.SetCanvas(recording.Width, recording.Height);
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _previewEngine = new MediaElementPreviewEngine(_media);
        _previewSeeks = new PreviewSeekCoordinator(_previewEngine, recording.Fps);
        _previewSeeks.PreviewPresented += OnPreviewPresented;
        _previewSeeks.SeekFailed += OnPreviewSeekFailed;

        _timeline = new TwoLineTimeline();
        _timeline.PlayheadChanged += OnTimelinePlayhead;
        _timeline.PlayheadInteractionCompleted += OnTimelinePlayheadInteractionCompleted;
        _timeline.TrimChanged += (_, _) => UpdateStatusForMode();
        _positionLabel = BuildMono("00:00.000 / 00:00.000");
        _statusLabel = new TextBlock
        {
            Text = "동영상을 불러오는 중…",
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_statusLabel, AutomationLiveSetting.Polite);
        _loadingLabel = new TextBlock
        {
            Text = "동영상을 불러오는 중… 0%",
            Foreground = TryBrush("Text.Primary", Colors.White),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetLiveSetting(_loadingLabel, AutomationLiveSetting.Polite);
        _loadingOverlay = new Border
        {
            Background = TryBrush("Surface.Scrim", Color.FromArgb(0xC8, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(12),
            Child = _loadingLabel,
        };

        _overlayList = new ListBox
        {
            MinHeight = 38,
            MaxHeight = 120,
            MinWidth = 280,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_overlayList, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_overlayList, ScrollBarVisibility.Auto);
        _overlayList.SelectionChanged += OnOverlaySelectionChanged;
        _overlayList.MouseDoubleClick += (_, _) => EditSelectedOverlay();
        AutomationProperties.SetName(_overlayList, "시간 텍스트 목록");

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playbackTimer.Tick += OnPlaybackTick;

        Content = BuildLayout();

        // Controls start DISABLED until the media is ready; a click during load must do nothing.
        SetEditControlsEnabled(false);

        KeyDown += OnKeyDown;
        Closing += OnClosingInternal;
        Loaded += OnLoadedInternal;
        Closed += OnClosedInternal;
    }

    private void OnLoadedInternal(object? sender, RoutedEventArgs e)
    {
        // Set the source only after the window (and the MediaElement's visual tree) is loaded.
        // A MediaElement asked to open before it is connected to a rendered tree can silently
        // never raise MediaOpened for a short clip — the reported "2s video won't load" case.
        try
        {
            _media.Source = new Uri(_recording.OutputPath, UriKind.Absolute);
            _media.Play();   // Manual mode: Play kicks decoding so MediaOpened fires reliably…
            _media.Pause();  // …then immediately pause so we sit on frame 0.
        }
        catch (Exception ex)
        {
            OnMediaFailed(this, null!);
            _log.LogError(ex, "Could not set the media source");
        }

        StartLoadProgress();
    }

    private void StartLoadProgress()
    {
        // Local files have no real download percentage, so animate an indeterminate-but-honest
        // percentage from MediaElement.DownloadProgress/BufferingProgress, and guarantee the UI
        // never gets stuck on "loading": if MediaOpened has not fired within a timeout, fall
        // back to the known recording duration and open anyway.
        _loadProgressTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(80) };
        int synthetic = 5;
        _loadProgressTimer.Tick += (_, _) =>
        {
            if (_mediaReady || _mediaFailed)
            {
                return;
            }

            double dl = _media.DownloadProgress;      // 0..1, jumps to 1 for local files
            double buf = _media.BufferingProgress;     // 0..1
            int pct = (int)Math.Round(Math.Max(dl, buf) * 100.0);
            if (pct <= 0)
            {
                // No real signal for a local file yet: creep a synthetic value so the user sees motion.
                synthetic = Math.Min(90, synthetic + 5);
                pct = synthetic;
            }

            _loadingLabel.Text = string.Create(CultureInfo.CurrentCulture, $"동영상을 불러오는 중… {pct}%");
        };
        _loadProgressTimer.Start();

        _openTimeoutTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(5) };
        _openTimeoutTimer.Tick += (_, _) =>
        {
            _openTimeoutTimer?.Stop();
            if (!_mediaReady && !_mediaFailed)
            {
                _log.LogWarning("MediaOpened did not fire within 5s; opening with the recorded duration fallback");
                CompleteOpen(_recording.DurationMs, fromFallback: true);
            }
        };
        _openTimeoutTimer.Start();
    }

    /// <summary>Raised when the user commits a still-image edit taken from a frame.</summary>
    internal event EventHandler<AnnotationFrameCapturedEventArgs>? FrameImageCaptured;

    internal Func<FrameImageCommitSession>? FrameImageCommitHandlerFactory { get; set; }

    /// <summary>Allocates a private, same-directory MP4 stage for a queue-backed editor.</summary>
    internal Func<string>? RenderStagingPathFactory { get; set; }

    /// <summary>Atomically commits a completed stage and non-destructive edit document.</summary>
    internal Func<VideoEditDocument, string, CancellationToken, Task>? VideoCommitHandler { get; set; }

    /// <summary>Set by the gallery's GIF command so export opens as soon as media is ready.</summary>
    internal bool ExportGifWhenReady { get; set; }

    internal event EventHandler? VideoCommitted;

    // ---- layout ----

    private Grid BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // preview
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // timeline
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // controls
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status

        var previewStack = new Grid();
        previewStack.Children.Add(_media);
        previewStack.Children.Add(_overlayPreview);
        previewStack.Children.Add(_loadingOverlay);

        var preview = new Border
        {
            Background = TryBrush("Surface.Canvas", Colors.Black),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x40, 0x38, 0x2C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = previewStack,
        };
        Grid.SetRow(preview, 0);
        root.Children.Add(preview);

        Grid timeline = BuildTimeline();
        Grid.SetRow(timeline, 1);
        root.Children.Add(timeline);

        Grid controls = BuildControlRow();
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);

        var statusBar = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Child = _statusLabel,
        };
        Grid.SetRow(statusBar, 3);
        root.Children.Add(statusBar);

        return root;
    }

    private Grid _timelineCache = null!;

    private Grid BuildTimeline()
    {
        if (_timelineCache is not null)
        {
            return _timelineCache;
        }

        var timeline = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // two-line timeline
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // position label
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // timed text lane

        Grid.SetRow(_timeline, 0);
        timeline.Children.Add(_timeline);

        _positionLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _positionLabel.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_positionLabel, 1);
        timeline.Children.Add(_positionLabel);

        FrameworkElement overlayLane = BuildOverlayLane();
        Grid.SetRow(overlayLane, 2);
        timeline.Children.Add(overlayLane);
        RefreshOverlayList();

        _timelineCache = timeline;
        return timeline;
    }

    private FrameworkElement BuildOverlayLane()
    {
        var lane = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        lane.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lane.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lane.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "시간 텍스트",
            FontWeight = FontWeights.SemiBold,
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(label, 0);
        lane.Children.Add(label);

        Grid.SetColumn(_overlayList, 1);
        lane.Children.Add(_overlayList);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _addTextButton = MakeButton("+ 텍스트", "현재 위치에 시간 텍스트 추가 (Ctrl+T)", "Button.Secondary", AddTextOverlay);
        _editTextButton = MakeButton("편집", "선택한 시간 텍스트 편집 (F2)", "Button.Ghost", EditSelectedOverlay);
        _deleteTextButton = MakeButton("삭제", "선택한 시간 텍스트 삭제 (Delete)", "Button.Ghost", DeleteSelectedOverlay);
        actions.Children.Add(_addTextButton);
        actions.Children.Add(_editTextButton);
        actions.Children.Add(_deleteTextButton);
        actions.Children.Add(MakeButton("GIF", "선택 구간을 GIF로 내보내기 (G)", "Button.Ghost", ExportGif));
        Grid.SetColumn(actions, 2);
        lane.Children.Add(actions);

        AutomationProperties.SetName(lane, "시간 텍스트 및 GIF 도구");
        return lane;
    }

    private Grid BuildControlRow()
    {
        var controls = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetName(controls, "편집 도구 2행");

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        transport.Children.Add(MakeButton("⏮ 처음", "처음으로", "Button.Ghost", () => Seek(0)));
        transport.Children.Add(MakeButton("◀◀ 크게", "뒤로 크게 이동", "Button.Ghost", () => StepCoarse(-1)));
        transport.Children.Add(MakeButton("재생/일시정지", "재생 또는 일시정지", "Button.Secondary", TogglePlay));
        transport.Children.Add(MakeButton("크게 ▶▶", "앞으로 크게 이동", "Button.Ghost", () => StepCoarse(1)));
        transport.Children.Add(MakeButton("끝 ⏭", "끝으로", "Button.Ghost", () => Seek(_durationMs)));
        transport.Children.Add(Spacer(10));
        transport.Children.Add(MakeButton("◀ 프레임", "이전 프레임 (Ctrl/Shift+← 또는 ,)", "Button.Ghost", () => StepFrames(-1)));
        transport.Children.Add(MakeButton("프레임 ▶", "다음 프레임 (Ctrl/Shift+→ 또는 .)", "Button.Ghost", () => StepFrames(1)));
        Grid.SetRow(transport, 0);
        controls.Children.Add(transport);

        var precisionAndEdit = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        precisionAndEdit.Children.Add(MakeCompactButton("축소 −", "세부 타임라인 축소 (Ctrl+Shift+-)", "Button.Ghost", () => _timeline.ZoomAroundPlayhead(1.25)));
        precisionAndEdit.Children.Add(MakeCompactButton("확대 +", "세부 타임라인 확대 (Ctrl+Shift+=)", "Button.Ghost", () => _timeline.ZoomAroundPlayhead(0.8)));
        precisionAndEdit.Children.Add(MakeCompactButton("전체", "타임라인 전체 보기 (확대 초기화)", "Button.Ghost", () => _timeline.FitAll()));
        precisionAndEdit.Children.Add(Spacer(6));
        precisionAndEdit.Children.Add(MakeCompactButton("시작 자르기", "현재 위치를 시작(In)으로", "Button.Ghost", SetInHere));
        precisionAndEdit.Children.Add(MakeCompactButton("끝 자르기", "현재 위치를 끝(Out)으로", "Button.Ghost", SetOutHere));
        precisionAndEdit.Children.Add(Spacer(6));
        precisionAndEdit.Children.Add(MakeCompactButton("프레임 편집", "현재 프레임을 이미지로 편집 (E)", "Button.Secondary", EditCurrentFrame));
        precisionAndEdit.Children.Add(Spacer(6));
        precisionAndEdit.Children.Add(MakeCompactButton("저장", "트림한 영상 저장", "Button.Primary", CommitTrim));
        _cancelOperationButton = new Button
        {
            Content = "작업 취소",
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 84,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Danger"),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_cancelOperationButton, "진행 중인 영상 저장 또는 GIF 내보내기 취소");
        AutomationProperties.SetHelpText(_cancelOperationButton, "작업을 안전하게 중단하고 임시 파일을 정리합니다.");
        _cancelOperationButton.Click += (_, _) => _operationCts?.Cancel();
        precisionAndEdit.Children.Add(_cancelOperationButton);
        Grid.SetRow(precisionAndEdit, 1);
        controls.Children.Add(precisionAndEdit);

        _controlRows = controls;
        return controls;
    }

    private static Border Spacer(double width) => new() { Width = width };

    private void SetEditControlsEnabled(bool enabled)
    {
        _timeline.IsEnabled = enabled;
        foreach (Control c in _editControls)
        {
            c.IsEnabled = enabled;
        }

        UpdateOverlayActionStates();
    }

    // ---- media lifecycle ----

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        double dur = _media.NaturalDuration.HasTimeSpan
            ? _media.NaturalDuration.TimeSpan.TotalMilliseconds
            : _recording.DurationMs;
        CompleteOpen(dur, fromFallback: false);
    }

    /// <summary>
    /// Finalises the ready state — from either MediaOpened or the open-timeout fallback — so
    /// the editor is never stuck on the loading overlay for a clip that decodes slowly or whose
    /// MediaOpened never arrives (the reported ~2s-clip case).
    /// </summary>
    private void CompleteOpen(double durationMs, bool fromFallback)
    {
        if (_mediaReady)
        {
            return;
        }

        StopLoadTimers();

        _durationMs = durationMs > 0 ? durationMs : Math.Max(1, _recording.DurationMs);
        _mediaReady = true;

        _timeline.Initialize(_durationMs, _recording.Fps);
        _editDocument = _editDocument.NormalizeFor(
            _recording.Width,
            _recording.Height,
            _durationMs);
        _initialDocument = _editDocument.Clone();
        _timeline.SetIn(_editDocument.TrimInMs);
        _timeline.SetOut(_editDocument.TrimOutMs);
        _timeline.SetPlayhead(_editDocument.TrimInMs);
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetSourceTime(_editDocument.TrimInMs);
        RefreshOverlayList();

        try
        {
            _media.Position = TimeSpan.FromMilliseconds(_editDocument.TrimInMs);
            _media.Pause();
        }
        catch (InvalidOperationException)
        {
            // Position before the element is fully ready can throw; harmless here.
        }

        _loadingOverlay.Visibility = Visibility.Collapsed;
        SetEditControlsEnabled(true);

        UpdatePositionLabel(_editDocument.TrimInMs);
        UpdateStatusForMode();
        if (fromFallback)
        {
            _log.LogInformation("Editor opened via duration fallback ({Duration:0}ms)", _durationMs);
        }

        _ = Keyboard.Focus(this);
        if (ExportGifWhenReady)
        {
            ExportGifWhenReady = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ExportGif));
        }
    }

    private void StopLoadTimers()
    {
        _loadProgressTimer?.Stop();
        _loadProgressTimer = null;
        _openTimeoutTimer?.Stop();
        _openTimeoutTimer = null;
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        StopLoadTimers();
        _mediaFailed = true;
        _mediaFailure = e?.ErrorException?.Message ?? "알 수 없는 오류";
        _log.LogError(e?.ErrorException, "Playback of {Path} failed", _recording.OutputPath);
        _loadingLabel.Text = "동영상을 불러올 수 없습니다";
        _loadingLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        _statusLabel.Text = "동영상을 재생할 수 없습니다: " + _mediaFailure;
        _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
    }

    private void OnMediaEnded(object? sender, RoutedEventArgs e)
    {
        _media.Pause();
        _isPlaying = false;
        _playbackTimer.Stop();
    }

    // ---- transport ----

    private void TogglePlay()
    {
        if (!_mediaReady)
        {
            return;
        }

        if (_isPlaying)
        {
            _media.Pause();
            _isPlaying = false;
            _playbackTimer.Stop();
            UpdateStatusForMode();
            return;
        }

        if (_media.Position.TotalMilliseconds < _timeline.InMs
            || _media.Position.TotalMilliseconds >= _timeline.OutMs - 0.5)
        {
            Seek(_timeline.InMs);
        }

        _media.Play();
        _isPlaying = true;
        _playbackTimer.Start();
        _statusLabel.Text = "재생 중 · 텍스트 미리보기 활성";
    }

    private void Seek(double positionMs)
    {
        if (!_mediaReady)
        {
            return;
        }

        double clamped = Math.Clamp(positionMs, 0, _durationMs);
        _previewSeeks.RequestExact(clamped);
        _timeline.SetPlayhead(clamped);
        UpdatePositionLabel(clamped);
    }

    /// <summary>Playhead visual intent moved during pointer interaction.</summary>
    private void OnTimelinePlayhead(object? sender, double ms)
    {
        if (!_mediaReady)
        {
            return;
        }

        double clamped = Math.Clamp(ms, 0, _durationMs);
        UpdatePositionLabel(clamped);
        _previewSeeks.RequestPreview(clamped);
    }

    /// <summary>Pointer release requests one priority exact reconciliation seek.</summary>
    private void OnTimelinePlayheadInteractionCompleted(object? sender, double ms)
    {
        if (_mediaReady)
        {
            _previewSeeks.RequestExact(Math.Clamp(ms, 0, _durationMs));
        }
    }

    private void OnPreviewSeekFailed(Exception exception)
    {
        _log.LogWarning(exception, "Preview seek failed");
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnPreviewSeekFailed(exception)),
                DispatcherPriority.Background);
            return;
        }

        if (IsLoaded)
        {
            _statusLabel.Text = "미리보기 위치 이동에 실패했습니다: " + exception.Message;
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
    }

    private void OnPreviewPresented(PresentedPreviewFrame frame)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => OnPreviewPresented(frame)),
                DispatcherPriority.Render);
            return;
        }

        if (IsLoaded)
        {
            _overlayPreview.SetSourceTime(Math.Clamp(frame.PresentedPositionMs, 0, _durationMs));
        }
    }

    private void StepFrames(int frames)
    {
        double next = FrameStepCalculator.StepByFrames(CurrentMs(), frames, _recording.Fps, _durationMs);
        Seek(next);
        int total = TotalFrameCount();
        int frame = Math.Min(total, FrameStepCalculator.FrameIndexAt(next, _recording.Fps, _durationMs) + 1);
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"프레임 {frame}/{total} · {FormatMs(next)}");
    }

    private void StepCoarse(int direction)
    {
        double next = FrameStepCalculator.StepCoarse(CurrentMs(), direction, _durationMs, 5.0);
        Seek(next);
    }

    private double CurrentMs() => _timeline.PlayheadMs;

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || !_mediaReady)
        {
            return;
        }

        double position = _media.Position.TotalMilliseconds;
        if (position >= _timeline.OutMs - 0.5)
        {
            _media.Pause();
            _isPlaying = false;
            _playbackTimer.Stop();
            position = _timeline.OutMs;
            UpdateStatusForMode();
        }

        double clamped = Math.Clamp(position, 0, _durationMs);
        _timeline.SetPlayhead(clamped, ensureVisible: false);
        _overlayPreview.SetSourceTime(clamped);
        UpdatePositionLabel(clamped);
    }

    // ---- trim ----

    private void SetInHere()
    {
        _timeline.SetIn(CurrentMs());
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"시작 지점 설정: {FormatMs(_timeline.InMs)}");
    }

    private void SetOutHere()
    {
        _timeline.SetOut(CurrentMs());
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"끝 지점 설정: {FormatMs(_timeline.OutMs)}");
    }

    // ---- timed text notes ----

    private void AddTextOverlay()
    {
        if (!_mediaReady || _operationRunning)
        {
            return;
        }

        if (_editDocument.TextOverlays.Count >= VideoEditDocument.MaximumOverlayCount)
        {
            _statusLabel.Text = $"시간 텍스트는 최대 {VideoEditDocument.MaximumOverlayCount}개까지 추가할 수 있습니다.";
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            UpdateOverlayActionStates();
            return;
        }

        var dialog = new TimedTextOverlayDialog(_durationMs, CurrentMs()) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } overlay)
        {
            _editDocument.TextOverlays.Add(overlay);
            RefreshOverlayList(overlay.Id);
            RefreshTextPreview();
            Seek(overlay.StartMs);
            _statusLabel.Text = $"텍스트 추가: {FormatMs(overlay.StartMs)}–{FormatMs(overlay.EndMs)}";
        }
    }

    private void EditSelectedOverlay()
    {
        if (!_mediaReady
            || _operationRunning
            || SelectedOverlay() is not { } selected)
        {
            return;
        }

        var dialog = new TimedTextOverlayDialog(_durationMs, selected.StartMs, selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
        {
            return;
        }

        int index = _editDocument.TextOverlays.FindIndex(item => item.Id == selected.Id);
        if (index >= 0)
        {
            _editDocument.TextOverlays[index] = edited;
        }

        RefreshOverlayList(edited.Id);
        RefreshTextPreview();
        Seek(edited.StartMs);
        _statusLabel.Text = $"텍스트 수정: {FormatMs(edited.StartMs)}–{FormatMs(edited.EndMs)}";
    }

    private void DeleteSelectedOverlay()
    {
        if (_operationRunning || SelectedOverlay() is not { } selected)
        {
            return;
        }

        _editDocument.TextOverlays.RemoveAll(item => item.Id == selected.Id);
        RefreshOverlayList();
        RefreshTextPreview();
        _statusLabel.Text = "선택한 시간 텍스트를 삭제했습니다";
    }

    private TimedTextOverlay? SelectedOverlay() =>
        (_overlayList.SelectedItem as ListBoxItem)?.Tag as TimedTextOverlay;

    private void OnOverlaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOverlayActionStates();
        if (_mediaReady && SelectedOverlay() is { } selected)
        {
            Seek(selected.StartMs);
        }
    }

    private void RefreshOverlayList(Guid? selectedId = null)
    {
        Guid? keep = selectedId ?? SelectedOverlay()?.Id;
        _overlayList.Items.Clear();
        foreach (TimedTextOverlay overlay in _editDocument.TextOverlays.OrderBy(item => item.StartMs))
        {
            string oneLine = overlay.Text.Replace('\r', ' ').Replace('\n', ' ');
            if (oneLine.Length > 38)
            {
                oneLine = oneLine[..38] + "…";
            }

            var item = new ListBoxItem
            {
                Content = $"{FormatMs(overlay.StartMs)}–{FormatMs(overlay.EndMs)}  {oneLine}",
                Tag = overlay,
                ToolTip = overlay.Text,
                Padding = new Thickness(8, 4, 8, 4),
            };
            AutomationProperties.SetName(item, $"{FormatMs(overlay.StartMs)}부터 {FormatMs(overlay.EndMs)}까지 {oneLine}");
            _overlayList.Items.Add(item);
            if (keep == overlay.Id)
            {
                _overlayList.SelectedItem = item;
            }
        }

        AutomationProperties.SetHelpText(
            _overlayList,
            $"시간 텍스트 {_editDocument.TextOverlays.Count}개, 최대 {VideoEditDocument.MaximumOverlayCount}개");
        UpdateOverlayActionStates();
    }

    private void UpdateOverlayActionStates()
    {
        if (_addTextButton is null || _editTextButton is null || _deleteTextButton is null)
        {
            return;
        }

        bool interactive = _mediaReady && !_operationRunning;
        _addTextButton.IsEnabled = interactive
            && _editDocument.TextOverlays.Count < VideoEditDocument.MaximumOverlayCount;
        bool selected = SelectedOverlay() is not null;
        _editTextButton.IsEnabled = interactive && selected;
        _deleteTextButton.IsEnabled = interactive && selected;
    }

    private void RefreshTextPreview()
    {
        _overlayPreview.SetOverlays(_editDocument.TextOverlays);
        _overlayPreview.SetSourceTime(CurrentMs());
    }

    private VideoEditDocument BuildCurrentDocument()
    {
        VideoEditDocument current = _editDocument.Clone();
        current.SourceDurationMs = _durationMs;
        current.CanvasWidth = _recording.Width;
        current.CanvasHeight = _recording.Height;
        current.TrimInMs = _timeline.InMs;
        current.TrimOutMs = _timeline.OutMs;
        return current.NormalizeFor(_recording.Width, _recording.Height, _durationMs);
    }

    // ---- extract frame -> annotation editor ----

    private async void EditCurrentFrame()
    {
        if (!_mediaReady)
        {
            return;
        }

        try
        {
            _statusLabel.Text = "정확한 프레임을 준비하는 중…";
            await _previewSeeks.RequestExactAsync(CurrentMs());
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Exact seek before frame edit failed");
            _statusLabel.Text = "현재 프레임을 준비할 수 없습니다: " + ex.Message;
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        BitmapSource? frame = TryRenderCurrentFrame();
        if (frame is null)
        {
            _statusLabel.Text = "현재 프레임을 가져올 수 없습니다";
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }

        // Wrap the extracted frame as a FrozenFrame so the existing capture editor opens
        // unchanged — image editing from a video frame is identical to editing a capture.
        var region = new RectD(0, 0, frame.PixelWidth, frame.PixelHeight);
        var frozen = new FrozenFrame(frame, region, null, 0);

        var editor = new AnnotationEditorWindow(
            frozen,
            region,
            frame,
            title: "MyCapture — 프레임 이미지 편집");
        FrameImageCommitSession? commitSession = FrameImageCommitHandlerFactory?.Invoke();
        editor.CommitRequested = commitSession?.CommitAsync ?? (_ => Task.FromResult(false));
        editor.Committed += (_, result) => FrameImageCaptured?.Invoke(this, new AnnotationFrameCapturedEventArgs(result));
        editor.Closed += (_, _) => commitSession?.Dispose();
        editor.Owner = this;
        editor.Show();
        _ = editor.Activate();
    }

    private BitmapSource? TryRenderCurrentFrame()
    {
        try
        {
            int w = _recording.Width;
            int h = _recording.Height;
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // A VisualBrush of the MediaElement captures the frame currently shown. The
                // element is in manual/scrubbing mode, so whatever Position it is parked at
                // is the frame we paint into the render target.
                var brush = new VisualBrush(_media) { Stretch = Stretch.Fill };
                dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
                TimedTextOverlayRenderer.Draw(
                    dc,
                    _editDocument.TextOverlays,
                    CurrentMs(),
                    w,
                    h,
                    pixelsPerDip: 1.0);
            }

            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rendering the current video frame failed");
            return null;
        }
    }

    // ---- commit video / GIF ----

    private async void CommitTrim()
    {
        if (!_mediaReady || _committed || _operationRunning)
        {
            return;
        }

        VideoEditDocument document = BuildCurrentDocument();
        if (_initialDocument is not null && DocumentsEquivalent(_initialDocument, document))
        {
            _statusLabel.Text = "변경 사항이 없어 현재 영상을 유지합니다";
            _committed = true;
            Close();
            return;
        }

        string outputPath = RenderStagingPathFactory?.Invoke() ?? BuildTrimmedPath();
        _operationCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCts.Token;
        SetOperationRunning(true);
        var progress = new Progress<VideoFrameRenderProgress>(value =>
        {
            int percent = value.TotalFrames <= 0
                ? 0
                : (int)Math.Round(value.CompletedFrames * 100.0 / value.TotalFrames);
            _statusLabel.Text = $"영상 저장 중… {percent}% ({value.CompletedFrames}/{value.TotalFrames})";
        });

        try
        {
            int emitted = await StaThreadTask.RunAsync(
                () => TrimReencoder.Reencode(
                    _recording.OutputPath,
                    outputPath,
                    document.TrimInMs,
                    document.TrimOutMs,
                    _recording,
                    options => new MediaFoundationVideoEncoder(
                        options,
                        _loggerFactory.CreateLogger<MediaFoundationVideoEncoder>()),
                    _loggerFactory.CreateLogger("TrimReencoder"),
                    document.TextOverlays,
                    progress,
                    cancellationToken),
                "MyCapture video compositor");

            if (VideoCommitHandler is not null)
            {
                await VideoCommitHandler(document, outputPath, cancellationToken);
            }

            _editDocument = document;
            _statusLabel.Text = $"저장 완료 · {emitted}프레임";
            _committed = true;
            VideoCommitted?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (OperationCanceledException)
        {
            DeletePrivateRenderStage(outputPath);
            _statusLabel.Text = "영상 저장을 취소했습니다";
        }
        catch (Exception ex)
        {
            DeletePrivateRenderStage(outputPath);
            _log.LogError(ex, "Video re-render failed");
            _statusLabel.Text = "영상 저장에 실패했습니다: " + ex.Message;
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
        finally
        {
            bool closeAfterCancellation = _closeRequested;
            _operationCts?.Dispose();
            _operationCts = null;
            if (IsLoaded && !_committed)
            {
                SetOperationRunning(false);
                if (closeAfterCancellation)
                {
                    _closeRequested = false;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
                }
            }
        }
    }

    private string BuildTrimmedPath()
    {
        string dir = Path.GetDirectoryName(_recording.OutputPath) ?? _paths.CapturesRoot;
        string stem = Path.GetFileNameWithoutExtension(_recording.OutputPath);
        return Path.Combine(dir, stem + "_edited.mp4");
    }

    private async void ExportGif()
    {
        if (!_mediaReady || _operationRunning)
        {
            return;
        }

        VideoEditDocument document = BuildCurrentDocument();
        if (document.TrimOutMs - document.TrimInMs > AnimatedGifExporter.MaximumDurationMs + 0.5)
        {
            _statusLabel.Text = "GIF는 최대 20초입니다. 시작/끝 지점을 줄여 주세요.";
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "애니메이션 GIF로 내보내기",
            Filter = "애니메이션 GIF (*.gif)|*.gif",
            DefaultExt = ".gif",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"MyCapture_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.gif",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _operationCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _operationCts.Token;
        SetOperationRunning(true);
        var progress = new Progress<VideoFrameRenderProgress>(value =>
        {
            int percent = value.TotalFrames <= 0
                ? 0
                : (int)Math.Round(value.CompletedFrames * 100.0 / value.TotalFrames);
            _statusLabel.Text = $"GIF 만드는 중… {percent}% ({value.CompletedFrames}/{value.TotalFrames})";
        });

        try
        {
            int frames = await StaThreadTask.RunAsync(
                () => AnimatedGifExporter.Export(
                    _recording,
                    document,
                    dialog.FileName,
                    progress,
                    cancellationToken),
                "MyCapture GIF exporter");
            _statusLabel.Text = $"GIF 저장 완료 · {frames}프레임 · {Path.GetFileName(dialog.FileName)}";
            _statusLabel.Foreground = TryBrush("State.Success", Colors.LightGreen);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "GIF 내보내기를 취소했습니다";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "GIF export failed");
            _statusLabel.Text = "GIF 내보내기에 실패했습니다: " + ex.Message;
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
        finally
        {
            bool closeAfterCancellation = _closeRequested;
            _operationCts?.Dispose();
            _operationCts = null;
            if (IsLoaded)
            {
                SetOperationRunning(false);
                if (closeAfterCancellation)
                {
                    _closeRequested = false;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
                }
            }
        }
    }

    private void SetOperationRunning(bool running)
    {
        _operationRunning = running;
        SetEditControlsEnabled(!running && _mediaReady);
        _overlayList.IsEnabled = !running && _mediaReady;
        _cancelOperationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _cancelOperationButton.IsEnabled = running;
    }

    internal static bool DocumentsEquivalent(VideoEditDocument left, VideoEditDocument right)
    {
        const double tolerance = 0.001;
        if (Math.Abs(left.TrimInMs - right.TrimInMs) > tolerance
            || Math.Abs(left.TrimOutMs - right.TrimOutMs) > tolerance
            || left.TextOverlays.Count != right.TextOverlays.Count)
        {
            return false;
        }

        for (int index = 0; index < left.TextOverlays.Count; index++)
        {
            TimedTextOverlay a = left.TextOverlays[index];
            TimedTextOverlay b = right.TextOverlays[index];
            if (a.Id != b.Id
                || !string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                || Math.Abs(a.StartMs - b.StartMs) > tolerance
                || Math.Abs(a.EndMs - b.EndMs) > tolerance
                || a.Placement != b.Placement)
            {
                return false;
            }
        }

        return true;
    }

    private void DeletePrivateRenderStage(string path)
    {
        if (RenderStagingPathFactory is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- keyboard ----

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_mediaReady)
        {
            return;
        }

        // New mapping (per user request): plain Left/Right jump in LARGE steps; Ctrl or Shift
        // + Left/Right nudge by a SINGLE frame. This removes the old frame-step toggle.
        bool fine = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        switch (e.Key)
        {
            case Key.Escape when _operationRunning:
                e.Handled = true;
                _operationCts?.Cancel();
                break;
            case Key.T when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                e.Handled = true;
                AddTextOverlay();
                break;
            case Key.G:
                e.Handled = true;
                ExportGif();
                break;
            case Key.F2 when _overlayList.SelectedItem is not null:
                e.Handled = true;
                EditSelectedOverlay();
                break;
            case Key.Delete when _overlayList.IsKeyboardFocusWithin:
                e.Handled = true;
                DeleteSelectedOverlay();
                break;
            case Key.Left:
                e.Handled = true;
                if (fine) { StepFrames(-1); } else { StepCoarse(-1); }
                break;
            case Key.Right:
                e.Handled = true;
                if (fine) { StepFrames(1); } else { StepCoarse(1); }
                break;
            case Key.Space:
                e.Handled = true;
                TogglePlay();
                break;
            case Key.I:
                e.Handled = true;
                SetInHere();
                break;
            case Key.O:
                e.Handled = true;
                SetOutHere();
                break;
            case Key.E:
                e.Handled = true;
                EditCurrentFrame();
                break;
            case Key.Home:
                e.Handled = true;
                Seek(0);
                break;
            case Key.End:
                e.Handled = true;
                Seek(_durationMs);
                break;
            case Key.OemPlus when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                      (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.Add when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                  (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                _timeline.ZoomAroundPlayhead(0.8);
                break;
            case Key.OemMinus when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                       (ModifierKeys.Control | ModifierKeys.Shift):
            case Key.Subtract when (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) ==
                                   (ModifierKeys.Control | ModifierKeys.Shift):
                e.Handled = true;
                _timeline.ZoomAroundPlayhead(1.25);
                break;
            case Key.OemComma:   // ',' previous frame (Camtasia/ScreenToGif convention)
                e.Handled = true;
                StepFrames(-1);
                break;
            case Key.OemPeriod:  // '.' next frame
                e.Handled = true;
                StepFrames(1);
                break;
        }
    }

    private void UpdateStatusForMode()
    {
        string trim = _timeline.IsFullClip
            ? "전체 길이"
            : string.Create(CultureInfo.CurrentCulture, $"선택 {FormatMs(_timeline.SelectedDurationMs)}");
        string recordingHealth = _recording.DroppedFrames == 0
            ? "녹화 드롭 없음"
            : string.Create(
                CultureInfo.CurrentCulture,
                $"녹화 드롭 {_recording.DroppedFrames} ({_recording.DropRate:P1})");
        string notes = _editDocument.TextOverlays.Count == 0
            ? "텍스트 없음"
            : $"시간 텍스트 {_editDocument.TextOverlays.Count}개";
        _statusLabel.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"←/→ 크게 · Ctrl/Shift+←/→ 1프레임 · Ctrl+T 텍스트 · G GIF · {trim} · {notes} · {recordingHealth}");
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void UpdatePositionLabel(double positionMs)
    {
        int total = TotalFrameCount();
        int frame = Math.Min(total, FrameStepCalculator.FrameIndexAt(positionMs, _recording.Fps, _durationMs) + 1);
        _positionLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatMs(positionMs)} / {FormatMs(_durationMs)}  ·  프레임 {frame}/{total}");
    }

    private int TotalFrameCount()
    {
        double frameMs = FrameStepCalculator.FrameDurationMs(_recording.Fps);
        return frameMs > 0
            ? Math.Max(1, (int)Math.Ceiling(_durationMs / frameMs))
            : 1;
    }

    private static string FormatMs(double ms)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private void OnClosingInternal(object? sender, CancelEventArgs e)
    {
        if (!_operationRunning || _committed)
        {
            return;
        }

        e.Cancel = true;
        _closeRequested = true;
        _operationCts?.Cancel();
        _statusLabel.Text = "작업을 취소하고 임시 파일을 정리하는 중…";
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        _operationCts?.Cancel();
        _playbackTimer.Stop();
        _playbackTimer.Tick -= OnPlaybackTick;
        _overlayList.SelectionChanged -= OnOverlaySelectionChanged;
        StopLoadTimers();
        _timeline.PlayheadChanged -= OnTimelinePlayhead;
        _timeline.PlayheadInteractionCompleted -= OnTimelinePlayheadInteractionCompleted;
        _previewSeeks.PreviewPresented -= OnPreviewPresented;
        _previewSeeks.SeekFailed -= OnPreviewSeekFailed;
        _previewSeeks.Dispose();
        _previewEngine.Dispose();
        _timeline.Dispose();
        KeyDown -= OnKeyDown;
        Closing -= OnClosingInternal;
        Loaded -= OnLoadedInternal;
        Closed -= OnClosedInternal;
        _media.MediaOpened -= OnMediaOpened;
        _media.MediaFailed -= OnMediaFailed;
        _media.MediaEnded -= OnMediaEnded;
        try
        {
            _media.Close();
        }
        catch (InvalidOperationException)
        {
            // Closing an already-released media element is harmless.
        }
    }

    // ---- small view helpers (kept local so the editor matches the 0.4.0 token system) ----

    private Button MakeButton(string content, string automationName, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle(styleKey),
            ToolTip = automationName,
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => onClick();
        _editControls.Add(button);
        return button;
    }

    private Button MakeCompactButton(string content, string automationName, string styleKey, Action onClick)
    {
        Button button = MakeButton(content, automationName, styleKey, onClick);
        button.Margin = new Thickness(0, 0, 4, 0);
        button.MinWidth = 52;
        return button;
    }

    private TextBlock BuildMono(string text) => new()
    {
        Text = text,
        Foreground = TryBrush("Text.Primary", Colors.White),
        FontFamily = TryFont("Font.Mono"),
        FontSize = 13,
    };

    private static Brush TryBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily TryFont(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

    private static Style? TryStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;
}
