using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Gallery;

/// <summary>
/// A resizable always-on-top video player window. Replaces the in-sidebar inline player so
/// playback no longer duplicates the card's play affordance or overlaps the details panel.
/// </summary>
internal sealed partial class GalleryVideoPlayerWindow : Window, IGalleryVideoPlayer
{
    private readonly ILogger? _log;
    private readonly MediaElement _video = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        Stretch = System.Windows.Media.Stretch.Uniform,
        ScrubbingEnabled = true,
        IsMuted = true,
        Volume = 0,
    };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Value = 0, IsMoveToPointEnabled = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _playButton = new() { MinWidth = 76, Style = (Style)Application.Current.Resources["Button.Primary"] };
    private readonly ToggleButton _fullScreenButton = new() { Style = (Style)Application.Current.Resources["ToggleButton.Tool"] };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["Font.Mono"], VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new()
    {
        Style = (Style)Application.Current.Resources["Text.MutedBlock"],
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 3, 0, 0),
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _seekUpdating;
    private bool _playing;
    private bool _mediaReady;
    private bool _autoPlayPending;
    private Guid _videoId;
    private Task<VideoSourceInfo>? _sourceProbe;
    private bool _fullScreen;
    private Rect _windowedBounds;
    private WindowState _windowedState;
    private WindowStyle _windowedStyle;
    private ResizeMode _windowedResizeMode;

    internal event EventHandler? PlayerClosed;

    internal GalleryVideoPlayerWindow(ILogger? log = null) : this(log, null) { }

    internal GalleryVideoPlayerWindow(ILogger? log, GalleryPlayerDiagnosticOptions? diagnosticOptions)
    {
        _log = log;
        DiagnosticOptions = diagnosticOptions;
        Title = "MyCapture";
        Width = 720;
        Height = 520;
        SizeToContent = SizeToContent.Manual;
        ConstrainToWorkArea(SystemParameters.WorkArea);
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (diagnosticOptions?.ProductWindowStyle == false)
        {
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
        }

        var close = new Button { Content = UiText.Get("Text_1E8C10206F5B"), Style = (Style)Application.Current.Resources["Button.Ghost"] };
        close.Click += (_, _) => Close();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerStack = new StackPanel();
        headerStack.Children.Add(new TextBlock { Text = UiText.Get("Text_7CB3F50C1B9F"), Style = (Style)Application.Current.Resources["Text.Section"] });
        headerStack.Children.Add(_title);
        Grid.SetColumn(headerStack, 0);
        header.Children.Add(headerStack);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        string fullScreenLabel = UiText.Get("CaptureGuide2_FullScreen");
        _fullScreenButton.Content = fullScreenLabel;
        _fullScreenButton.ToolTip = $"{fullScreenLabel} (F11)";
        AutomationProperties.SetName(_fullScreenButton, fullScreenLabel);
        AutomationProperties.SetAcceleratorKey(_fullScreenButton, "F11");
        _fullScreenButton.Click += (_, _) => SetFullScreen(_fullScreenButton.IsChecked == true);

        // Wrapping the transport keeps every action and the complete timecode reachable
        // on narrow windows and at large text sizes.
        var controls = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        _playButton.Margin = new Thickness(0, 0, 8, 0);
        _fullScreenButton.Margin = new Thickness(0, 0, 8, 0);
        _status.Margin = new Thickness(0, 0, 8, 0);
        controls.Children.Add(_playButton);
        controls.Children.Add(_fullScreenButton);
        InitializeFrameCapture(controls);
        _status.Text = UiText.Get("Text_6E86D1A85620");
        controls.Children.Add(_status);
        controls.Children.Add(_time);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var frame = new Border { Background = System.Windows.Media.Brushes.Black, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _video };
        if (diagnosticOptions?.FrameHeightDip is { } height)
        {
            // Explicit diagnostic sizes remain fixed so their colour-range comparisons
            // can still isolate physical presentation scale.
            frame.Height = height;
            Width = height * 16 / 9 + 44; // Padding and non-client width; diagnostic clips are 16:9.
            SizeToContent = SizeToContent.Height;
        }
        if (diagnosticOptions?.RoundedClip == false)
        {
            frame.CornerRadius = new CornerRadius(0);
            frame.ClipToBounds = false;
        }
        var transport = new StackPanel();
        transport.Children.Add(_seek);
        transport.Children.Add(controls);
        Grid.SetRow(frame, 1);
        Grid.SetRow(transport, 2);
        root.Children.Add(header);
        root.Children.Add(frame);
        root.Children.Add(transport);
        Content = new Border { Padding = new Thickness(14), Child = root };

        StandardWindowTheme.Apply(this);
        SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        SetResourceReference(Window.ForegroundProperty, "Text.Primary");

        _video.MediaOpened += OnMediaOpened;
        _video.MediaEnded += OnMediaEnded;
        _video.MediaFailed += OnMediaFailed;
        _playButton.Click += (_, _) => { if (_mediaReady) SetPlayback(_frameSeekPending ? !_resumeAfterFrameSeek : !_playing); };
        _seek.ValueChanged += OnSeekChanged;
        _timer.Tick += (_, _) => { UpdatePosition(); UpdateFrameCaptureState(); };
        PreviewKeyDown += OnPreviewKeyDown;
        SetStatusPlaying(false);
        UpdateTime();
        if (diagnosticOptions?.CaptureExclusion != false)
            SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(this);
        // Clearing the probe also tells a MediaOpened still awaiting it that the window is gone.
        Closed += (_, _) => { _frameClosed = true; _frameCaptureCts?.Cancel(); CloseFrameSeeking(); _sourceProbe = null; _timer.Stop(); StudioRangeVideoEffect.Clear(_video); _video.Close(); _video.Source = null; PlayerClosed?.Invoke(this, EventArgs.Empty); };
    }

    internal Guid VideoId => _videoId;
    Guid IGalleryVideoPlayer.VideoId => VideoId;

    bool IGalleryVideoPlayer.Open(Guid id, string title, string path, bool autoPlay) => Open(id, title, path, autoPlay);
    void IGalleryVideoPlayer.RequestPlay() => RequestPlay();
    void IGalleryVideoPlayer.ShowPlayer(bool activate)
    {
        if (!IsLoaded)
        {
            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - Width - 24);
            Top = Math.Max(work.Top, work.Top + 90);
        }
        Show();
        if (activate) Activate();
    }

    /// <summary>Starts now when ready, or remembers a request made while the media is opening.</summary>
    internal void RequestPlay()
    {
        _autoPlayPending = true;
        if (_mediaReady) SetPlayback(true);
    }

    internal Action<bool>? PlaybackForTest { get; set; }
    internal Func<string, Task<VideoSourceInfo>>? SourceProbeForTest { get; set; }

    internal MediaElement MediaForDiagnostic => _video;
    internal bool IsMediaReadyForDiagnostic => _mediaReady;
    internal bool IsPlayingForDiagnostic => _playing;
    internal GalleryPlayerDiagnosticOptions? DiagnosticOptions { get; }

    /// <summary>Loads a video; returns false when the file is missing or cannot be opened.</summary>
    internal bool Open(Guid id, string title, string path, bool autoPlay)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (_videoId == id && ReferenceEquals(_video.Source?.ToString(), new Uri(path).ToString()))
        {
            if (autoPlay) RequestPlay();
            return true;
        }

        _videoId = id;
        _frameCaptureCts?.Cancel();
        _frameOpenVersion++;
        ResetFrameSeeking();
        _lastFrameId = null;
        _showFrameButton.Visibility = Visibility.Collapsed;
        _title.Text = title;
        _autoPlayPending = DiagnosticOptions?.OpenMode switch
        {
            GalleryPlayerOpenMode.Playing => true,
            GalleryPlayerOpenMode.PauseImmediately => false,
            _ => autoPlay,
        };
        _mediaReady = false;
        UpdateFrameCaptureState();
        _playing = false;
        _video.Stop();
        // Tear the previous media down completely: switching one player between clips with a
        // different range in place has failed with MILAVERR_UNEXPECTEDWMPFAILURE.
        _video.Close();
        _video.Source = null;
        StudioRangeVideoEffect.Clear(_video);
        _sourceProbe = SourceProbeForTest?.Invoke(path) ?? VideoSourceProbe.ReadAsync(path);
        _video.Source = new Uri(path);
        _video.Play();
        // Mirror the editor's open in the same dispatcher turn. Never seek before MediaOpened.
        if (DiagnosticOptions?.OpenMode == GalleryPlayerOpenMode.PauseImmediately) _video.Pause();
        SetStatusPlaying(false);
        _seek.Value = 0;
        UpdateTime();
        return true;
    }

    internal new void Show()
    {
        if (!IsLoaded && DiagnosticOptions is null && double.IsFinite(Left) && double.IsFinite(Top))
        {
            Rect work = SystemParameters.WorkArea;
            // Off-screen diagnostic hosts deliberately keep their placement. Product
            // callers anchor the player inside this work area before showing it.
            if (work.IntersectsWith(new Rect(Left, Top, Width, Height))) ConstrainToWorkArea(work);
        }
        base.Show();
        _timer.Start();
    }

    internal void ConstrainToWorkArea(Rect work)
    {
        double width = Math.Max(1, work.Width);
        double height = Math.Max(1, work.Height);
        MinWidth = Math.Min(360, width);
        MinHeight = Math.Min(300, height);
        Width = Math.Clamp(Width, MinWidth, width);
        Height = Math.Clamp(Height, MinHeight, height);
        if (double.IsFinite(Left)) Left = Math.Clamp(Left, work.Left, work.Right - Width);
        if (double.IsFinite(Top)) Top = Math.Clamp(Top, work.Top, work.Bottom - Height);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        UpdateFrameCaptureState();
        if (HandleFrameShortcut(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement, e.IsRepeat))
        {
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.F11)
        {
            if (!e.IsRepeat) SetFullScreen(!_fullScreen);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullScreen)
        {
            SetFullScreen(false);
            e.Handled = true;
        }
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        if (fullScreen)
        {
            _windowedState = WindowState;
            _windowedBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            _windowedStyle = WindowStyle;
            _windowedResizeMode = ResizeMode;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _windowedStyle;
            ResizeMode = _windowedResizeMode;
            Left = _windowedBounds.Left;
            Top = _windowedBounds.Top;
            Width = _windowedBounds.Width;
            Height = _windowedBounds.Height;
            WindowState = _windowedState;
        }
        _fullScreen = fullScreen;
        _fullScreenButton.IsChecked = fullScreen;
    }

    private async void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        // NaturalDuration is Windows Media Player's whole-second value (a sub-second clip has
        // none), so the seek range comes from the container duration read in Open.
        double? playerMs = _video.NaturalDuration.HasTimeSpan ? _video.NaturalDuration.TimeSpan.TotalMilliseconds : null;
        Task<VideoSourceInfo>? probe = _sourceProbe;
        if (probe is null)
        {
            return; // The window closed; Open always starts a probe before setting the source.
        }

        VideoSourceInfo source = await probe;
        if (!ReferenceEquals(probe, _sourceProbe))
        {
            return; // Another video was opened, or the window closed, while the probe ran.
        }

        StudioRangeVideoEffect.Apply(_video, source.StudioRange, VideoPlayerKind.Gallery, _log);
        double containerMs = source.DurationMs;

        _mediaReady = true;
        UpdateFrameCaptureState();
        _seek.Maximum = Math.Max(1, VideoDurationResolver.Resolve(containerMs, playerMs, 0));
        if (_frameSeekEngine is MediaElementPreviewEngine native) native.DurationMs = _seek.Maximum;
        UpdateTime();
        // A seek adapter can reopen after EOF; that MediaOpened must not start playback
        // or position the element while the adapter is completing the pending request.
        if (!_frameSeekPending) ApplyPlaybackAfterOpening();
    }

    private void ApplyPlaybackAfterOpening()
    {
        if (_autoPlayPending)
        {
            SetPlayback(true);
        }
        else
        {
            // Open plays to make MediaOpened arrive; a selected (not played) video waits on its
            // first frame instead of running unseen to its end and showing nothing.
            StudioRangeVideoEffect.PositionRequested(_video);
            _video.Position = TimeSpan.Zero;
            SetPlayback(false);
        }
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_frameSeekEngine is MediaElementPreviewEngine native) native.StreamReachedEnd();
        if (_frameSeekPending) return;
        _playing = false;
        _timer.Stop();
        StudioRangeVideoEffect.PositionRequested(_video);
        _video.Position = TimeSpan.Zero;
        SetStatusPlaying(false);
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        UpdateFrameCaptureState();
        _playing = false;
        _timer.Stop();
        SetStatusPlaying(false);
        _status.Text = UiText.Get("Text_875917AFD6EF");
    }

    private void SetPlayback(bool playing, bool seekTransition = false)
    {
        if (!_mediaReady) return;
        if (_frameSeekPending && !seekTransition)
        {
            _resumeAfterFrameSeek = playing;
            SetStatusPlaying(playing);
            return;
        }
        if (playing)
        {
            _framePresentedPositionMs = null;
            _frameSeekFailed = false;
            UpdateFrameCaptureState();
        }
        if (PlaybackForTest is { } playback)
        {
            playback(playing);
            _playing = playing;
            SetStatusPlaying(playing);
            return;
        }
        if (playing)
        {
            if (_video.Position.TotalMilliseconds >= _seek.Maximum - 1)
            {
                StudioRangeVideoEffect.PositionRequested(_video);
                _video.Position = TimeSpan.Zero;
            }
            _video.Play();
            _playing = true;
            _timer.Start();
        }
        else
        {
            _video.Pause();
            _playing = false;
            _timer.Stop();
            UpdatePosition();
        }
        SetStatusPlaying(playing);
    }

    private void SetStatusPlaying(bool playing) =>
        _playButton.Content = playing ? UiText.Get("Text_4F51C0C8ADA8") : UiText.Get("Text_D43A776C5E28");

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seekUpdating || !_mediaReady) return;
        RequestFrameSeek(Math.Clamp(e.NewValue, 0, _seek.Maximum));
        UpdateTime();
    }

    private void UpdatePosition()
    {
        if (!_mediaReady || _frameSeekPending) return;
        _seekUpdating = true;
        try { _seek.Value = Math.Clamp(_video.Position.TotalMilliseconds, 0, _seek.Maximum); }
        finally { _seekUpdating = false; }
        UpdateTime();
    }

    private void UpdateTime() =>
        _time.Text = $"{Format(_video.Position.TotalMilliseconds)} / {Format(_seek.Maximum)}";

    private static string Format(double ms) => VideoTimecode.Format(ms);
}
