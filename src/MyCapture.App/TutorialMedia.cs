using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MyCapture.App;

/// <summary>
/// The feature tutorial's video, behind a seam so window behaviour can be tested without real
/// media. Every member is called on the window's dispatcher and may throw; the window catches.
/// </summary>
internal interface ITutorialMedia : IDisposable
{
    /// <summary>The element that shows the video. It stays in the window for the media's lifetime.</summary>
    UIElement View { get; }

    /// <summary>Starts opening a local file and holds its first frame. Opened or Failed follows.</summary>
    void Open(string path);

    void Play();

    void Pause();

    /// <summary>Moves to the first frame without changing whether the video plays.</summary>
    void Rewind();

    /// <summary>Releases the open file. No event of the released file is raised afterwards.</summary>
    void Close();

    event Action? Opened;

    event Action<Exception?>? Failed;

    event Action? Ended;
}

/// <summary>
/// Streams one local H.264 file through a WPF <see cref="MediaElement"/> in manual mode, the same
/// way the video editor and the library player do. Each file gets its own element, so an event
/// still queued for a released file can never reach the window.
/// </summary>
internal sealed class MediaElementTutorialMedia : ITutorialMedia
{
    private readonly Grid _host = new();
    private MediaElement? _element;
    private Uri? _pendingSource;
    private bool _disposed;

    public UIElement View => _host;

    public event Action? Opened;

    public event Action<Exception?>? Failed;

    public event Action? Ended;

    public void Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Close();
        var element = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            IsMuted = true,
            Volume = 0,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(element, BitmapScalingMode.HighQuality);
        // The window names the host; the element is what UI Automation exposes.
        element.SetBinding(AutomationProperties.NameProperty, new Binding
        {
            Source = _host,
            Path = new PropertyPath(AutomationProperties.NameProperty),
            Mode = BindingMode.OneWay,
        });
        element.MediaOpened += OnMediaOpened;
        element.MediaFailed += OnMediaFailed;
        element.MediaEnded += OnMediaEnded;
        _element = element;
        _pendingSource = new Uri(path, UriKind.Absolute);
        _host.Children.Add(element);
        // As in the video editor, the source is set only once the element is in a loaded tree:
        // asked earlier, a MediaElement can silently never raise MediaOpened for a short clip.
        if (element.IsLoaded) Start(element);
        else element.Loaded += OnElementLoaded;
    }

    public void Play() => _element?.Play();

    public void Pause() => _element?.Pause();

    public void Rewind()
    {
        if (_element is not null) _element.Position = TimeSpan.Zero;
    }

    public void Close()
    {
        MediaElement? element = _element;
        if (element is null) return;
        _element = null;
        _pendingSource = null;
        element.Loaded -= OnElementLoaded;
        element.MediaOpened -= OnMediaOpened;
        element.MediaFailed -= OnMediaFailed;
        element.MediaEnded -= OnMediaEnded;
        _host.Children.Remove(element);
        BindingOperations.ClearBinding(element, AutomationProperties.NameProperty);
        element.Close();
        element.Source = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        var element = (MediaElement)sender;
        element.Loaded -= OnElementLoaded;
        if (!ReferenceEquals(element, _element)) return;
        // Raised from layout, outside the window's own guarded calls: report instead of throwing.
        try { Start(element); }
        catch (Exception error) { Failed?.Invoke(error); }
    }

    private void Start(MediaElement element)
    {
        Uri? source = _pendingSource;
        _pendingSource = null;
        if (source is null) return;
        element.Source = source;
        element.Play();   // Manual mode: Play starts decoding so MediaOpened fires reliably,
        element.Pause();  // and Pause holds the first frame (ScrubbingEnabled draws it).
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _element)) Opened?.Invoke();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _element)) Failed?.Invoke(e.ErrorException);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _element)) Ended?.Invoke();
    }
}
