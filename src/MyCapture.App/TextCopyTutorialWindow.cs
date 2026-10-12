using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>A local, on-demand demonstration of the selector's text-copy path.</summary>
internal sealed class TextCopyTutorialWindow : Window
{
    private static TextCopyTutorialWindow? _open;
    private readonly GifSamplePlayer _player;
    private readonly Func<string, CancellationToken, GifSampleFrames> _decode;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Button _play = new() { MinWidth = 100 };
    private readonly Button _replay = new() { MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private GifSampleFrames? _frames;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _closed;
    private bool _watchingMotion;
    private Window? _lifetimeOwner;

    internal TextCopyTutorialWindow(HotkeySettings hotkeys, Func<string, CancellationToken, GifSampleFrames>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        _decode = decode ?? GifSampleDecoder.Decode;
        _player = new GifSamplePlayer(Dispatcher);
        _player.FrameChanged += ShowFrame;
        Title = UiText.Get("TextCapture_TutorialTitle");
        Width = 720; Height = 750;
        MinWidth = 520; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        StandardWindowTheme.Apply(this);
        SourceInitialized += (_, _) => CaptureWindowExclusion.DisableTransitions(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        var root = new DockPanel { Margin = new Thickness(24, 16, 24, 24) };
        var close = new Button { Content = UiText.Get("Dialog.Close"), MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0), IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);

        var content = new StackPanel();
        content.Children.Add(Paragraph(UiText.Get("TextCapture_TutorialIntro")));
        content.Children.Add(Paragraph(CaptureEntryText(hotkeys)));
        if (hotkeys.CaptureText.IsAssigned)
            content.Children.Add(Paragraph(UiText.Format("TextCapture_TutorialDirect", hotkeys.CaptureText)));

        var preview = new Grid { Height = 360, MaxWidth = 640, Margin = new Thickness(0, 8, 0, 8) };
        _status.Text = UiText.Get("TextCapture_TutorialLoading");
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_image, UiText.Get("TextCapture_TutorialAnimation"));
        preview.Children.Add(_image); preview.Children.Add(_status);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = preview };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");
        content.Children.Add(frame);

        _play.SetResourceReference(StyleProperty, "Button.Secondary");
        _play.Click += (_, _) => TogglePlayback();
        _replay.Content = UiText.Get("TextCapture_TutorialReplay");
        AutomationProperties.SetName(_replay, UiText.Get("TextCapture_TutorialReplay"));
        _replay.SetResourceReference(StyleProperty, "Button.Secondary");
        _replay.Click += (_, _) => Replay();
        var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        controls.Children.Add(_play); controls.Children.Add(_replay);
        content.Children.Add(controls);
        foreach (string key in new[] { "TextCapture_TutorialStep1", "TextCapture_TutorialStep2", "TextCapture_TutorialStep3" })
            content.Children.Add(Paragraph(UiText.Get(key)));
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        UpdateControls();

        Loaded += (_, _) =>
        {
            if (!_watchingMotion)
            {
                SystemParameters.StaticPropertyChanged += OnMotionPreferenceChanged;
                _watchingMotion = true;
            }
            LoadAnimation();
        };
        Deactivated += (_, _) => Pause();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Pause(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible && !_closed) ReleaseAnimation(); };
        Closed += (_, _) =>
        {
            _closed = true;
            ReleaseAnimation();
            _player.FrameChanged -= ShowFrame;
            if (_watchingMotion) SystemParameters.StaticPropertyChanged -= OnMotionPreferenceChanged;
            if (_lifetimeOwner is not null) _lifetimeOwner.IsVisibleChanged -= OnOwnerVisibilityChanged;
            if (ReferenceEquals(_open, this)) _open = null;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    internal bool HasFrames => _frames is not null;
    internal bool IsLoading => _loadCancellation is not null;
    internal bool IsPlaying => _player.IsPlaying;
    internal int FrameIndex => _player.Index;
    internal string AssetPath => Path.Combine(Path.GetDirectoryName(typeof(TextCopyTutorialWindow).Assembly.Location) ?? AppContext.BaseDirectory, "Assets", "Tutorials",
        UiText.Culture.TwoLetterISOLanguageName == "ko" ? "text-copy.ko.gif" : "text-copy.en.gif");

    internal static string CaptureEntryText(HotkeySettings hotkeys) => hotkeys.Capture.IsAssigned
        ? UiText.Format("TextCapture_TutorialCaptureKey", hotkeys.Capture)
        : UiText.Get("TextCapture_TutorialCaptureMenu");

    internal static void ShowTutorial(Window owner, HotkeySettings hotkeys)
    {
        if (!ShortcutHelpWindow.CanShowHelp()) return;
        if (_open is { IsVisible: true }) { _open.Activate(); return; }
        _open?.Close();
        var tutorial = new TextCopyTutorialWindow(hotkeys) { Owner = owner };
        tutorial._lifetimeOwner = owner;
        owner.IsVisibleChanged += tutorial.OnOwnerVisibilityChanged;
        _open = tutorial;
        tutorial.Show();
    }

    internal static bool DismissForCapture()
    {
        TextCopyTutorialWindow[] windows = Application.Current?.Windows.OfType<TextCopyTutorialWindow>().ToArray() ?? [];
        foreach (TextCopyTutorialWindow window in windows) window.Close();
        if (_open is not null) { _open.Close(); return true; }
        return windows.Length > 0;
    }

    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_lifetimeOwner?.IsVisible == false && !_closed) Close();
    }

    private async void LoadAnimation()
    {
        if (_closed || _frames is not null || _loadCancellation is not null || !IsVisible) return;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        int generation = ++_loadGeneration;
        string path = AssetPath;
        try
        {
            GifSampleFrames frames = await DecodeOnStaAsync(path, _decode, cancellation.Token);
            if (_closed || !IsVisible || cancellation.IsCancellationRequested || generation != _loadGeneration) return;
            _frames = frames;
            _player.Load(_frames.Timeline);
            _status.Visibility = Visibility.Collapsed;
            if (FluidMotion.AnimationsEnabled && IsActive) _player.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
            or InvalidOperationException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException)
        {
            if (_closed || !IsVisible || cancellation.IsCancellationRequested || generation != _loadGeneration) return;
            ReleaseAnimation();
            _status.Text = UiText.Get("TextCapture_TutorialUnavailable");
            _status.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
        UpdateControls();
    }

    private static Task<GifSampleFrames> DecodeOnStaAsync(string path,
        Func<string, CancellationToken, GifSampleFrames> decode, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<GifSampleFrames>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GifSampleFrames frames = decode(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                completed.TrySetResult(frames);
            }
            catch (OperationCanceledException) { completed.TrySetCanceled(cancellationToken); }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true, Name = "MyCapture text-copy tutorial decoder" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }

    internal void TogglePlayback()
    {
        if (_frames is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
        UpdateControls();
    }

    internal void Replay()
    {
        if (_frames is null) return;
        _player.Seek(0);
        _player.Play();
        UpdateControls();
    }

    internal void Pause()
    {
        _player.Pause();
        UpdateControls();
    }

    private void OnMotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || Dispatcher.HasShutdownStarted) return;
        void Apply() { if (!_closed && !FluidMotion.AnimationsEnabled) Pause(); }
        if (Dispatcher.CheckAccess()) Apply();
        else _ = Dispatcher.BeginInvoke(new Action(Apply));
    }

    private void ReleaseAnimation()
    {
        ++_loadGeneration;
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        _player.Load(null);
        _image.Source = null;
        _frames = null;
        UpdateControls();
    }

    private void ShowFrame(int index)
    {
        if (_frames is not null) _image.Source = _frames.Frames[Math.Clamp(index, 0, _frames.Frames.Count - 1)];
    }

    private void UpdateControls()
    {
        string caption = UiText.Get(_player.IsPlaying ? "GifPreview_Pause" : "GifPreview_Play");
        _play.Content = caption;
        AutomationProperties.SetName(_play, caption);
        _play.IsEnabled = _replay.IsEnabled = (_frames?.Frames.Count ?? 0) > 1;
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8) };
}
