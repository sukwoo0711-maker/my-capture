using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;

namespace MyCapture.App;

/// <summary>A local tutorial and its still, with a static reduced-motion path.</summary>
internal sealed class WelcomeTutorialPreview : Grid, IDisposable
{
    private readonly ITutorialMedia _media;
    private readonly Border _poster = new();
    private readonly bool? _reducedMotion;
    private string? _feature;
    private bool _open, _disposed, _watching;

    internal WelcomeTutorialPreview(WelcomeOptions options)
    {
        _reducedMotion = options.ReducedMotion;
        _media = (options.MediaFactory ?? (() => new MediaElementTutorialMedia()))();
        Children.Add(_poster);
        Children.Add(_media.View);
        IsHitTestVisible = false;
        _media.Opened += OnOpened;
        _media.Failed += OnFailed;
        _media.Ended += OnEnded;
        Loaded += (_, _) =>
        {
            if (!_watching)
            {
                SystemParameters.StaticPropertyChanged += OnMotionChanged;
                _watching = true;
            }
            OpenVideo();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) OpenVideo(); else CloseVideo(); };
    }

    internal string AssetDirectory { get; init; } = Path.Combine(
        Path.GetDirectoryName(typeof(WelcomeWindow).Assembly.Location) ?? AppContext.BaseDirectory, "Assets", "Tutorials");

    internal string? AssetPath => _feature is null ? null : Path.Combine(AssetDirectory,
        $"{_feature}.{(UiText.Culture.TwoLetterISOLanguageName == "ko" ? "ko" : "en")}.mp4");
    internal bool HasPoster => _poster.Background is not null;
    internal bool HasVideo => _open;
    private bool ReducedMotion => _reducedMotion ?? !FluidMotion.AnimationsEnabled;

    internal void SelectFeature(string? feature)
    {
        CloseVideo();
        _feature = feature;
        _poster.Background = null;
        if (feature is null) return;
        string path = Path.ChangeExtension(AssetPath!, ".jpg");
        if (File.Exists(path))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                _poster.Background = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
            }
            catch (Exception error) { Trace.TraceWarning("Welcome tutorial still: {0}", error); }
        }
        AutomationProperties.SetName(this, UiText.Format("Tutorial.VideoName", TutorialCatalog.Get(feature).DisplayName));
        OpenVideo();
    }

    private void OpenVideo()
    {
        if (_disposed || _open || !IsLoaded || !IsVisible || ReducedMotion || AssetPath is not { } path || !File.Exists(path)) return;
        try
        {
            _open = true;
            _media.View.Visibility = Visibility.Visible;
            _media.Open(path);
        }
        catch (Exception error) { OnFailed(error); }
    }

    private void OnOpened()
    {
        if (!_open || _disposed) return;
        if (ReducedMotion) { CloseVideo(); return; }
        try { _media.Play(); }
        catch (Exception error) { OnFailed(error); }
    }

    private void OnFailed(Exception? error)
    {
        Trace.TraceWarning("Welcome tutorial video: {0}", error);
        CloseVideo();
    }

    // Play once: the still remains useful without an endlessly moving distraction.
    private void OnEnded()
    {
        if (!_open) return;
        try { _media.Pause(); }
        catch (Exception error) { OnFailed(error); }
    }

    private void CloseVideo()
    {
        bool wasOpen = _open;
        _open = false;
        _media.View.Visibility = Visibility.Hidden;
        if (!wasOpen) return;
        try { _media.Close(); }
        catch (Exception error) { Trace.TraceWarning("Welcome tutorial close: {0}", error); }
    }

    internal void Pause()
    {
        if (!_open) return;
        try { _media.Pause(); }
        catch (Exception error) { OnFailed(error); }
    }

    private void OnMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || Dispatcher.HasShutdownStarted) return;
        void Apply() { if (ReducedMotion) CloseVideo(); else OpenVideo(); }
        if (Dispatcher.CheckAccess()) Apply(); else _ = Dispatcher.BeginInvoke(new Action(Apply));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watching) SystemParameters.StaticPropertyChanged -= OnMotionChanged;
        _watching = false;
        CloseVideo();
        _media.Opened -= OnOpened;
        _media.Failed -= OnFailed;
        _media.Ended -= OnEnded;
        try { _media.Dispose(); }
        catch (Exception error) { Trace.TraceWarning("Welcome tutorial disposal: {0}", error); }
    }
}
