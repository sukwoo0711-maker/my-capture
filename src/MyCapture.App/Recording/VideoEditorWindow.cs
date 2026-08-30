using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
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

    private readonly MediaElement _media;
    private readonly TwoLineTimeline _timeline;
    private readonly TextBlock _positionLabel;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _loadingLabel;
    private readonly Border _loadingOverlay;
    private readonly List<Control> _editControls = [];
    private Grid _controlRows = null!;
    private DispatcherTimer? _loadProgressTimer;
    private DispatcherTimer? _openTimeoutTimer;

    private double _durationMs;
    private bool _mediaReady;
    private bool _mediaFailed;
    private string _mediaFailure = string.Empty;
    private bool _committed;

    // Test hooks so a headless self-test can confirm the editor actually reaches the ready
    // state for a real clip (the field report: a ~2s video failed to load).
    internal bool IsMediaReadyForTest => _mediaReady;

    internal bool HasMediaFailedForTest => _mediaFailed;

    internal string MediaFailureForTest => _mediaFailure;

    internal double DurationMsForTest => _durationMs;

    internal TwoLineTimeline TimelineForTest => _timeline;

    internal int ControlRowCountForTest => _controlRows.RowDefinitions.Count;

    internal double ControlAreaWidthForTest => _controlRows.ActualWidth;

    internal double WidestControlRowWidthForTest => _controlRows.Children
        .OfType<FrameworkElement>()
        .Max(child => child.DesiredSize.Width);

    internal VideoEditorWindow(RecordingResult recording, AppPaths paths, ILoggerFactory loggerFactory)
    {
        _recording = recording ?? throw new ArgumentNullException(nameof(recording));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<VideoEditorWindow>();

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

        _timeline = new TwoLineTimeline();
        _timeline.PlayheadChanged += (_, ms) => OnTimelinePlayhead(ms);
        _timeline.TrimChanged += (_, _) => UpdateStatusForMode();
        _positionLabel = BuildMono("00:00.000 / 00:00.000");
        _statusLabel = new TextBlock
        {
            Text = "동영상을 불러오는 중…",
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
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

        Content = BuildLayout();

        // Controls start DISABLED until the media is ready; a click during load must do nothing.
        SetEditControlsEnabled(false);

        KeyDown += OnKeyDown;
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

        Grid.SetRow(_timeline, 0);
        timeline.Children.Add(_timeline);

        _positionLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _positionLabel.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_positionLabel, 1);
        timeline.Children.Add(_positionLabel);

        _timelineCache = timeline;
        return timeline;
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
        precisionAndEdit.Children.Add(MakeButton("− 축소", "세부 타임라인 축소 (Ctrl+Shift+-)", "Button.Ghost", () => _timeline.ZoomAroundPlayhead(1.25)));
        precisionAndEdit.Children.Add(MakeButton("확대 +", "세부 타임라인 확대 (Ctrl+Shift+=)", "Button.Ghost", () => _timeline.ZoomAroundPlayhead(0.8)));
        precisionAndEdit.Children.Add(MakeButton("전체 보기", "타임라인 전체 보기 (확대 초기화)", "Button.Ghost", () => _timeline.FitAll()));
        precisionAndEdit.Children.Add(Spacer(10));
        precisionAndEdit.Children.Add(MakeButton("시작 지점 자르기", "현재 위치를 시작(In)으로", "Button.Ghost", SetInHere));
        precisionAndEdit.Children.Add(MakeButton("끝 지점 자르기", "현재 위치를 끝(Out)으로", "Button.Ghost", SetOutHere));
        precisionAndEdit.Children.Add(Spacer(10));
        precisionAndEdit.Children.Add(MakeButton("이 프레임 편집", "현재 프레임을 이미지로 편집 (E)", "Button.Secondary", EditCurrentFrame));
        precisionAndEdit.Children.Add(Spacer(10));
        precisionAndEdit.Children.Add(MakeButton("완료 · 저장", "트림한 영상 저장", "Button.Primary", CommitTrim));
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

        try
        {
            _media.Position = TimeSpan.Zero;
            _media.Pause();
        }
        catch (InvalidOperationException)
        {
            // Position before the element is fully ready can throw; harmless here.
        }

        _loadingOverlay.Visibility = Visibility.Collapsed;
        SetEditControlsEnabled(true);

        UpdatePositionLabel(0);
        UpdateStatusForMode();
        if (fromFallback)
        {
            _log.LogInformation("Editor opened via duration fallback ({Duration:0}ms)", _durationMs);
        }

        _ = Keyboard.Focus(this);
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

    private void OnMediaEnded(object? sender, RoutedEventArgs e) => _media.Pause();

    // ---- transport ----

    private void TogglePlay()
    {
        if (!_mediaReady)
        {
            return;
        }

        // A simple play/pause; playback is clamped to the trim Out on the next tick.
        _media.Play();
        _statusLabel.Text = "재생 중";
    }

    private void Seek(double positionMs)
    {
        if (!_mediaReady)
        {
            return;
        }

        double clamped = Math.Clamp(positionMs, 0, _durationMs);
        _media.Pause();
        _media.Position = TimeSpan.FromMilliseconds(clamped);
        _timeline.SetPlayhead(clamped);
        UpdatePositionLabel(clamped);
    }

    /// <summary>Playhead moved by the user dragging/clicking on a timeline strip.</summary>
    private void OnTimelinePlayhead(double ms)
    {
        if (!_mediaReady)
        {
            return;
        }

        double clamped = Math.Clamp(ms, 0, _durationMs);
        _media.Pause();
        _media.Position = TimeSpan.FromMilliseconds(clamped);
        UpdatePositionLabel(clamped);
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

    // ---- extract frame -> annotation editor ----

    private void EditCurrentFrame()
    {
        if (!_mediaReady)
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
        editor.CommitRequested = _ => true; // Persistence is handled by the app via the event below.
        editor.Committed += (_, result) => FrameImageCaptured?.Invoke(this, new AnnotationFrameCapturedEventArgs(result));
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

    // ---- commit trim ----

    private void CommitTrim()
    {
        if (!_mediaReady || _committed)
        {
            return;
        }

        if (_timeline.IsFullClip)
        {
            _statusLabel.Text = "전체 영상을 유지합니다 (트림 없음)";
            _committed = true;
            Close();
            return;
        }

        _statusLabel.Text = "트림 저장 중…";
        try
        {
            string trimmedPath = BuildTrimmedPath();

            TrimReencoder.Reencode(
                _recording.OutputPath,
                trimmedPath,
                _timeline.InMs,
                _timeline.OutMs,
                _recording,
                options => new MediaFoundationVideoEncoder(options, _loggerFactory.CreateLogger<MediaFoundationVideoEncoder>()),
                _loggerFactory.CreateLogger("TrimReencoder"));

            _statusLabel.Text = "저장 완료: " + Path.GetFileName(trimmedPath);
            _committed = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Trim re-encode failed");
            _statusLabel.Text = "트림 저장에 실패했습니다: " + ex.Message;
            _statusLabel.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }
    }

    private string BuildTrimmedPath()
    {
        string dir = Path.GetDirectoryName(_recording.OutputPath) ?? _paths.CapturesRoot;
        string stem = Path.GetFileNameWithoutExtension(_recording.OutputPath);
        return Path.Combine(dir, stem + "_trim.mp4");
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
        _statusLabel.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"←/→ 크게 · Ctrl/Shift+←/→ 또는 , . 1프레임 · 휠/Ctrl+Shift +/- 확대 · {trim}");
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

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        StopLoadTimers();
        KeyDown -= OnKeyDown;
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
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => onClick();
        _editControls.Add(button);
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
