using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// A small always-on-top video player window. Replaces the in-sidebar inline player so
/// playback no longer duplicates the card's play affordance or overlaps the details panel.
/// </summary>
internal sealed class GalleryVideoPlayerWindow : Window
{
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
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _time = new() { FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["Font.Mono"] };
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

    internal event EventHandler? PlayerClosed;

    internal GalleryVideoPlayerWindow()
    {
        Title = "MyCapture";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

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

        var controls = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_playButton, 0);
        controls.Children.Add(_playButton);
        _status.Text = UiText.Get("Text_6E86D1A85620");
        Grid.SetColumn(_status, 1);
        controls.Children.Add(_status);
        Grid.SetColumn(_time, 2);
        controls.Children.Add(_time);

        var root = new StackPanel();
        var frame = new Border { Height = 220, Background = System.Windows.Media.Brushes.Black, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _video };
        root.Children.Add(header);
        root.Children.Add(frame);
        root.Children.Add(_seek);
        root.Children.Add(controls);
        Content = new Border { Padding = new Thickness(14), Child = root };

        StandardWindowTheme.Apply(this);
        SetResourceReference(Window.BackgroundProperty, "Surface.Base");
        SetResourceReference(Window.ForegroundProperty, "Text.Primary");

        _video.MediaOpened += OnMediaOpened;
        _video.MediaEnded += OnMediaEnded;
        _video.MediaFailed += OnMediaFailed;
        _playButton.Click += (_, _) => { if (_mediaReady) SetPlayback(!_playing); };
        _seek.ValueChanged += OnSeekChanged;
        _timer.Tick += (_, _) => UpdatePosition();
        SourceInitialized += (_, _) => CaptureWindowExclusion.TryApply(this);
        Closed += (_, _) => { _timer.Stop(); _video.Close(); PlayerClosed?.Invoke(this, EventArgs.Empty); };
    }

    internal Guid VideoId => _videoId;

    /// <summary>Loads a video; returns false when the file is missing or cannot be opened.</summary>
    internal bool Open(Guid id, string title, string path, bool autoPlay)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (_videoId == id && ReferenceEquals(_video.Source?.ToString(), new Uri(path).ToString()))
        {
            _autoPlayPending |= autoPlay;
            if (autoPlay && _mediaReady) SetPlayback(true);
            return true;
        }

        _videoId = id;
        _title.Text = title;
        _autoPlayPending = autoPlay;
        _mediaReady = false;
        _playing = false;
        _video.Stop();
        _video.Source = new Uri(path);
        _video.Play();
        SetStatusPlaying(false);
        _seek.Value = 0;
        UpdateTime();
        return true;
    }

    internal new void Show()
    {
        base.Show();
        _timer.Start();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        _seek.Maximum = Math.Max(1, _video.NaturalDuration.HasTimeSpan ? _video.NaturalDuration.TimeSpan.TotalMilliseconds : 1);
        if (_autoPlayPending) SetPlayback(true);
        else SetStatusPlaying(false);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false;
        _timer.Stop();
        _video.Position = TimeSpan.Zero;
        SetStatusPlaying(false);
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        _playing = false;
        _timer.Stop();
        SetStatusPlaying(false);
        _status.Text = UiText.Get("Text_875917AFD6EF");
    }

    private void SetPlayback(bool playing)
    {
        if (!_mediaReady) return;
        if (playing)
        {
            if (_video.Position.TotalMilliseconds >= _seek.Maximum - 1) _video.Position = TimeSpan.Zero;
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
        _video.Position = TimeSpan.FromMilliseconds(Math.Clamp(e.NewValue, 0, _seek.Maximum));
        UpdateTime();
    }

    private void UpdatePosition()
    {
        if (!_mediaReady) return;
        _seekUpdating = true;
        try { _seek.Value = Math.Clamp(_video.Position.TotalMilliseconds, 0, _seek.Maximum); }
        finally { _seekUpdating = false; }
        UpdateTime();
    }

    private void UpdateTime() =>
        _time.Text = $"{Format(_video.Position.TotalMilliseconds)} / {Format(_seek.Maximum)}";

    private static string Format(double ms)
    {
        TimeSpan v = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return v.TotalHours >= 1
            ? v.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}
