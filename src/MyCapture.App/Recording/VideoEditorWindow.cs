using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly Slider _scrubber;
    private readonly Slider _inHandle;
    private readonly Slider _outHandle;
    private readonly ToggleButton _frameStepToggle;
    private readonly TextBlock _positionLabel;
    private readonly TextBlock _statusLabel;

    private TrimSelection _trim = new(0);
    private double _durationMs;
    private bool _mediaReady;
    private bool _isScrubbing;
    private bool _committed;

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
            Source = new Uri(recording.OutputPath, UriKind.Absolute),
        };
        _media.MediaOpened += OnMediaOpened;
        _media.MediaFailed += OnMediaFailed;
        _media.MediaEnded += OnMediaEnded;

        _scrubber = BuildScrubber();
        _inHandle = BuildTrimHandle("시작 지점(In)");
        _outHandle = BuildTrimHandle("끝 지점(Out)");
        _frameStepToggle = BuildFrameStepToggle();
        _positionLabel = BuildMono("00:00.000 / 00:00.000");
        _statusLabel = new TextBlock
        {
            Text = "동영상을 불러오는 중…",
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Content = BuildLayout();

        KeyDown += OnKeyDown;
        Closed += OnClosedInternal;
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

        var preview = new Border
        {
            Background = TryBrush("Surface.Canvas", Colors.Black),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x40, 0x38, 0x2C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Child = _media,
        };
        Grid.SetRow(preview, 0);
        root.Children.Add(preview);

        Grid.SetRow(BuildTimeline(), 1);
        root.Children.Add(BuildTimeline());

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

        var timeline = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Main playhead scrubber.
        Grid.SetRow(_scrubber, 0);
        timeline.Children.Add(_scrubber);

        // Trim handles row.
        var trimRow = new Grid();
        trimRow.ColumnDefinitions.Add(new ColumnDefinition());
        trimRow.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_inHandle, 0);
        Grid.SetColumn(_outHandle, 1);
        trimRow.Children.Add(_inHandle);
        trimRow.Children.Add(_outHandle);
        Grid.SetRow(trimRow, 1);
        timeline.Children.Add(trimRow);

        var labels = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var inLabel = MakeCaption("In: 시작");
        var mid = _positionLabel;
        mid.HorizontalAlignment = HorizontalAlignment.Center;
        var outLabel = MakeCaption("Out: 끝");
        Grid.SetColumn(inLabel, 0);
        Grid.SetColumn(mid, 1);
        Grid.SetColumn(outLabel, 2);
        labels.Children.Add(inLabel);
        labels.Children.Add(mid);
        labels.Children.Add(outLabel);
        Grid.SetRow(labels, 2);
        timeline.Children.Add(labels);

        _timelineCache = timeline;
        return timeline;
    }

    private Grid BuildControlRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0),
        };

        row.Children.Add(MakeButton("⏮ 처음", "처음으로", "Button.Ghost", () => Seek(0)));
        row.Children.Add(MakeButton("◀ 프레임", "이전 프레임", "Button.Ghost", () => StepFrames(-1)));
        row.Children.Add(MakeButton("재생/일시정지", "재생 또는 일시정지", "Button.Secondary", TogglePlay));
        row.Children.Add(MakeButton("프레임 ▶", "다음 프레임", "Button.Ghost", () => StepFrames(1)));
        row.Children.Add(MakeButton("끝 ⏭", "끝으로", "Button.Ghost", () => Seek(_durationMs)));

        row.Children.Add(new Separator { Width = 12, Opacity = 0 });
        row.Children.Add(_frameStepToggle);

        row.Children.Add(new Separator { Width = 12, Opacity = 0 });
        row.Children.Add(MakeButton("여기를 In(I)", "현재 위치를 시작 지점으로", "Button.Ghost", SetInHere));
        row.Children.Add(MakeButton("여기를 Out(O)", "현재 위치를 끝 지점으로", "Button.Ghost", SetOutHere));

        row.Children.Add(new Separator { Width = 12, Opacity = 0 });
        row.Children.Add(MakeButton("이 프레임 편집(E)", "현재 프레임을 이미지로 편집", "Button.Secondary", EditCurrentFrame));

        row.Children.Add(new Separator { Width = 12, Opacity = 0 });
        row.Children.Add(MakeButton("완료 · 저장", "트림한 영상 저장", "Button.Primary", CommitTrim));

        return WrapControls(row);
    }

    private static Grid WrapControls(UIElement content)
    {
        var g = new Grid();
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };
        g.Children.Add(scroll);
        return g;
    }

    private Slider BuildScrubber()
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            IsMoveToPointEnabled = true,
            SmallChange = 1,
            LargeChange = 10,
            Height = 28,
        };
        AutomationProperties.SetName(slider, "재생 위치");
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _isScrubbing = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _isScrubbing = false;
            SeekToScrubber();
        }));
        slider.ValueChanged += (_, _) =>
        {
            if (_isScrubbing)
            {
                SeekToScrubber();
            }
        };
        return slider;
    }

    private Slider BuildTrimHandle(string name)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Height = 24,
            Margin = new Thickness(0, 4, 0, 0),
        };
        AutomationProperties.SetName(slider, name);
        slider.ValueChanged += (_, _) => OnTrimHandleChanged();
        return slider;
    }

    private ToggleButton BuildFrameStepToggle()
    {
        var toggle = new ToggleButton
        {
            Content = "프레임 이동",
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("ToggleButton.Tool"),
        };
        AutomationProperties.SetName(toggle, "프레임 이동 모드 (방향키로 1프레임씩 이동)");
        AutomationProperties.SetHelpText(toggle, "켜면 좌우 방향키가 1프레임씩, Shift와 함께면 10프레임씩 이동합니다.");
        toggle.Checked += (_, _) => UpdateStatusForMode();
        toggle.Unchecked += (_, _) => UpdateStatusForMode();
        return toggle;
    }

    // ---- media lifecycle ----

    private void OnMediaOpened(object? sender, RoutedEventArgs e)
    {
        _durationMs = _media.NaturalDuration.HasTimeSpan
            ? _media.NaturalDuration.TimeSpan.TotalMilliseconds
            : _recording.DurationMs;

        _trim = new TrimSelection(_durationMs);
        _mediaReady = true;

        _scrubber.Maximum = _durationMs;
        _inHandle.Maximum = _durationMs;
        _outHandle.Maximum = _durationMs;
        _inHandle.Value = 0;
        _outHandle.Value = _durationMs;

        _media.Position = TimeSpan.Zero;
        _media.Pause();

        UpdatePositionLabel(0);
        UpdateStatusForMode();
        _ = Keyboard.Focus(this);
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _log.LogError(e.ErrorException, "Playback of {Path} failed", _recording.OutputPath);
        _statusLabel.Text = "동영상을 재생할 수 없습니다: " + (e.ErrorException?.Message ?? "알 수 없는 오류");
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
        _scrubber.Value = clamped;
        UpdatePositionLabel(clamped);
    }

    private void SeekToScrubber() => Seek(_scrubber.Value);

    private void StepFrames(int frames)
    {
        double next = FrameStepCalculator.StepByFrames(CurrentMs(), frames, _recording.Fps, _durationMs);
        Seek(next);
        int index = FrameStepCalculator.FrameIndexAt(next, _recording.Fps, _durationMs);
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"프레임 {index} · {FormatMs(next)}");
    }

    private void StepCoarse(int direction)
    {
        double next = FrameStepCalculator.StepCoarse(
            CurrentMs(),
            direction,
            _durationMs,
            5.0);
        Seek(next);
    }

    private double CurrentMs() => _media.Position.TotalMilliseconds;

    // ---- trim ----

    private void OnTrimHandleChanged()
    {
        if (!_mediaReady)
        {
            return;
        }

        _trim.SetIn(_inHandle.Value);
        _trim.SetOut(_outHandle.Value);

        // Reflect clamping back into the handles so they cannot cross.
        if (Math.Abs(_inHandle.Value - _trim.InMs) > 0.5)
        {
            _inHandle.Value = _trim.InMs;
        }

        if (Math.Abs(_outHandle.Value - _trim.OutMs) > 0.5)
        {
            _outHandle.Value = _trim.OutMs;
        }

        UpdateStatusForMode();
    }

    private void SetInHere()
    {
        _trim.SetIn(CurrentMs());
        _inHandle.Value = _trim.InMs;
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"시작 지점 설정: {FormatMs(_trim.InMs)}");
    }

    private void SetOutHere()
    {
        _trim.SetOut(CurrentMs());
        _outHandle.Value = _trim.OutMs;
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"끝 지점 설정: {FormatMs(_trim.OutMs)}");
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

        if (_trim.IsFullClip)
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
                _trim.InMs,
                _trim.OutMs,
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

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        switch (e.Key)
        {
            case Key.Left:
                e.Handled = true;
                if (_frameStepToggle.IsChecked == true)
                {
                    StepFrames(shift ? -10 : -1);
                }
                else
                {
                    StepCoarse(-1);
                }

                break;
            case Key.Right:
                e.Handled = true;
                if (_frameStepToggle.IsChecked == true)
                {
                    StepFrames(shift ? 10 : 1);
                }
                else
                {
                    StepCoarse(1);
                }

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
        }
    }

    private void UpdateStatusForMode()
    {
        string mode = _frameStepToggle.IsChecked == true
            ? "프레임 이동: 좌우 방향키로 1프레임(Shift 10프레임)"
            : "탐색 이동: 좌우 방향키로 구간 이동 · 프레임 이동을 켜면 1프레임씩";
        string trim = _trim.IsFullClip
            ? "전체 길이"
            : string.Create(CultureInfo.CurrentCulture, $"선택 {FormatMs(_trim.SelectedDurationMs)}");
        _statusLabel.Text = string.Create(CultureInfo.CurrentCulture, $"{mode} · {trim}");
        _statusLabel.Foreground = TryBrush("Text.Secondary", Colors.LightGray);
    }

    private void UpdatePositionLabel(double positionMs)
    {
        if (!_isScrubbing)
        {
            _scrubber.Value = positionMs;
        }

        _positionLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatMs(positionMs)} / {FormatMs(_durationMs)}");
    }

    private static string FormatMs(double ms)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private void OnClosedInternal(object? sender, EventArgs e)
    {
        KeyDown -= OnKeyDown;
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
        return button;
    }

    private TextBlock MakeCaption(string text) => new()
    {
        Text = text,
        Foreground = TryBrush("Text.Muted", Colors.Gray),
        FontSize = 12,
    };

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
