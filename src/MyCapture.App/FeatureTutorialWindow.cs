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

/// <summary>Loads only the selected local animation, with static instructions for every workflow.</summary>
internal class FeatureTutorialWindow : Window
{
    private static FeatureTutorialWindow? _open;
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
    private readonly ComboBox _features = new() { MinHeight = 36, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _instructions = new();
    private readonly StackPanel _steps = new();
    private Func<HotkeySettings> _readHotkeys;
    private FeatureTutorial _feature;

    internal FeatureTutorialWindow(HotkeySettings hotkeys, string featureId = "text-copy", Func<string, CancellationToken, GifSampleFrames>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        _readHotkeys = () => hotkeys;
        _feature = TutorialCatalog.Get(featureId);
        _decode = decode ?? GifSampleDecoder.Decode;
        _player = new GifSamplePlayer(Dispatcher);
        _player.FrameChanged += ShowFrame;
        Title = TutorialTitle(_feature);
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
        AutomationProperties.SetName(_features, UiText.Get("Tutorial.Select"));
        foreach (FeatureTutorial feature in TutorialCatalog.All)
            _features.Items.Add(new ComboBoxItem { Content = feature.DisplayName, Tag = feature.Id });
        _features.SelectedItem = _features.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, featureId));
        _features.SelectionChanged += (_, _) =>
        {
            if (_features.SelectedItem is ComboBoxItem { Tag: string id } && id != _feature.Id) ApplyFeature(id);
        };
        content.Children.Add(_features);
        content.Children.Add(_instructions);

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
        content.Children.Add(_steps);
        UpdateInstructions();
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
        IsVisibleChanged += (_, _) => { if (_closed) return; if (!IsVisible) ReleaseAnimation(); else LoadAnimation(); };
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
    internal string AssetPath => Path.Combine(Path.GetDirectoryName(typeof(FeatureTutorialWindow).Assembly.Location) ?? AppContext.BaseDirectory, "Assets", "Tutorials",
        $"{_feature.Id}.{(UiText.Culture.TwoLetterISOLanguageName == "ko" ? "ko" : "en")}.gif");

    internal string SelectedFeatureId => _feature.Id;
    internal static string TutorialTitle(FeatureTutorial feature) => feature.Id == "text-copy"
        ? UiText.Get("TextCapture_TutorialTitle") : feature.DisplayName;

    internal static void ShowTutorial(Window owner, HotkeySettings hotkeys, string featureId) =>
        ShowTutorial(owner, () => hotkeys, featureId);

    internal static void ShowTutorial(Window owner, Func<HotkeySettings> readHotkeys, string featureId)
    {
        if (!ShortcutHelpWindow.CanShowHelp()) return;
        if (_open is { IsVisible: true })
        {
            _open._readHotkeys = readHotkeys;
            _open.SelectFeature(featureId);
            _open.UpdateInstructions();
            _open.Activate();
            return;
        }
        _open?.Close();
        FeatureTutorialWindow tutorial = featureId == "text-copy"
            ? new TextCopyTutorialWindow(readHotkeys()) : new FeatureTutorialWindow(readHotkeys(), featureId);
        tutorial._readHotkeys = readHotkeys;
        tutorial.UpdateInstructions();
        tutorial.Owner = owner;
        tutorial._lifetimeOwner = owner;
        owner.IsVisibleChanged += tutorial.OnOwnerVisibilityChanged;
        _open = tutorial;
        tutorial.Show();
    }

    internal static bool DismissForCapture()
    {
        FeatureTutorialWindow[] windows = Application.Current?.Windows.OfType<FeatureTutorialWindow>().ToArray() ?? [];
        bool found = windows.Length > 0 || _open is not null;
        foreach (FeatureTutorialWindow window in windows) window.Close();
        _open?.Close();
        return found;
    }

    internal void SelectFeature(string featureId)
    {
        _ = TutorialCatalog.Get(featureId);
        _features.SelectedItem = _features.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, featureId));
    }

    private void ApplyFeature(string featureId)
    {
        ReleaseAnimation();
        _feature = TutorialCatalog.Get(featureId);
        UpdateInstructions();
        _status.Text = UiText.Get("TextCapture_TutorialLoading");
        _status.Visibility = Visibility.Visible;
        LoadAnimation();
    }

    private void UpdateInstructions()
    {
        Title = TutorialTitle(_feature);
        HotkeySettings keys = _readHotkeys();
        _instructions.Children.Clear();
        if (_feature.Id == "text-copy")
            _instructions.Children.Add(Paragraph(UiText.Get("TextCapture_TutorialIntro")));
        _instructions.Children.Add(Paragraph(_feature.EntryText(keys)));
        if (_feature.Id == "text-copy" && keys.CaptureText.IsAssigned)
            _instructions.Children.Add(Paragraph(UiText.Format("TextCapture_TutorialDirect", keys.CaptureText)));
        _steps.Children.Clear();
        foreach (string step in _feature.Steps) _steps.Children.Add(Paragraph(step));
        AutomationProperties.SetName(_image, _feature.Id == "text-copy"
            ? UiText.Get("TextCapture_TutorialAnimation") : _feature.DisplayName);
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
            _status.Text = UiText.Get(_feature.Id == "text-copy" ? "TextCapture_TutorialUnavailable" : "Tutorial.Unavailable");
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
        }) { IsBackground = true, Name = "MyCapture feature tutorial decoder" };
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

    protected void OnMotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
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
