using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App;

/// <summary>
/// Streams only the selected feature's local tutorial video, with static instructions for every
/// workflow. The video's first frame, shipped as a still beside it, shows at once while the video
/// opens. A missing file or a media failure leaves the steps as a still fallback.
/// </summary>
internal class FeatureTutorialWindow : Window
{
    /// <summary>The 960×540 videos map 1:1 to this preview at 150% scaling.</summary>
    internal const double PreviewMaxWidth = 640, PreviewMaxHeight = 360;
    private const double PreferredWidth = 720, PreferredHeight = 780, MinimumWidth = 520, MinimumHeight = 480;

    private static FeatureTutorialWindow? _open;
    private readonly ITutorialMedia _media;
    private readonly Button _play = new() { MinWidth = 100 };
    private readonly Button _replay = new() { MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
    private readonly ComboBox _features = new() { MinHeight = 36, Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _instructions = new();
    private readonly StackPanel _steps = new();
    // A Border with an image brush, not an Image: the still duplicates the video, so it stays out
    // of the UI Automation tree.
    private readonly Border _poster = new();
    private string? _posterPath;
    private DispatcherOperation? _pendingOpen;
    private bool _mediaOpen;
    private bool _hasFirstFrame;
    private bool _playing;
    private bool _fallback;
    private bool _closed;
    private bool _watchingMotion;
    private Window? _lifetimeOwner;
    private Func<HotkeySettings> _readHotkeys;
    private FeatureTutorial _feature;

    /// <summary>Replaces the real media of windows created without a factory (ShowTutorial) in tests.</summary>
    internal static Func<ITutorialMedia>? MediaFactoryOverrideForTest { get; set; }

    /// <summary>
    /// Opens Settings → Shortcuts on the row that edits the given <see cref="SettingsDraft"/>
    /// hotkey property. The shell sets it; without it no "Set shortcut" button is offered.
    /// </summary>
    internal static Action<string>? OpenShortcutSetting { get; set; }

    /// <summary>"Set shortcut", offered only while the selected feature's global shortcut is unassigned.</summary>
    internal Button SetShortcutButton { get; } = new() { HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 0, 0, 8) };

    internal FeatureTutorialWindow(HotkeySettings hotkeys, string featureId = "text-copy", Func<ITutorialMedia>? mediaFactory = null)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        _readHotkeys = () => hotkeys;
        _feature = TutorialCatalog.Get(featureId);
        _media = (mediaFactory ?? MediaFactoryOverrideForTest ?? (() => new MediaElementTutorialMedia()))();
        _media.Opened += OnMediaOpened;
        _media.Failed += OnMediaFailed;
        _media.Ended += OnMediaEnded;
        Title = TutorialTitle(_feature);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FitToMonitor(MonitorEnumerator.GetFromCursor());
        StandardWindowTheme.Apply(this);
        SourceInitialized += (_, _) => CaptureWindowExclusion.DisableTransitions(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        var root = new DockPanel { Margin = new Thickness(24, 16, 24, 16) };
        var close = new Button { Content = UiText.Get("Dialog.Close"), MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);

        AutomationProperties.SetName(_features, UiText.Get("Tutorial.Select"));
        foreach (FeatureTutorial feature in TutorialCatalog.All)
            _features.Items.Add(new ComboBoxItem { Content = feature.DisplayName, Tag = feature.Id });
        _features.SelectedItem = _features.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, featureId));
        _features.SelectionChanged += (_, _) =>
        {
            if (_features.SelectedItem is ComboBoxItem { Tag: string id } && id != _feature.Id) ApplyFeature(id);
        };

        var preview = new Grid();
        _status.Text = UiText.Get("TextCapture_TutorialLoading");
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        RenderOptions.SetBitmapScalingMode(_poster, BitmapScalingMode.HighQuality);
        preview.Children.Add(_poster); preview.Children.Add(_media.View); preview.Children.Add(_status);
        // The video fills the frame, so clip it to the frame's inner rounded corners.
        preview.SizeChanged += (_, e) => preview.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = preview,
            Margin = new Thickness(0, 8, 0, 8) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        frame.SetResourceReference(Border.BackgroundProperty, "Surface.Raised");

        _play.SetResourceReference(StyleProperty, "Button.Compact");
        _play.Click += (_, _) => TogglePlayback();
        _replay.Content = UiText.Get("TextCapture_TutorialReplay");
        AutomationProperties.SetName(_replay, UiText.Get("TextCapture_TutorialReplay"));
        _replay.SetResourceReference(StyleProperty, "Button.Compact");
        _replay.Click += (_, _) => Replay();
        var controls = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        controls.Children.Add(_play); controls.Children.Add(_replay);

        SetShortcutButton.Content = UiText.Get("Tutorial.SetShortcut");
        SetShortcutButton.SetResourceReference(StyleProperty, "Button.Compact");
        AutomationProperties.SetName(SetShortcutButton, UiText.Get("Tutorial.SetShortcut"));
        AutomationProperties.SetHelpText(SetShortcutButton, UiText.Get("Tutorial.SetShortcutHelp"));
        SetShortcutButton.ToolTip = UiText.Get("Tutorial.SetShortcutHelp");
        SetShortcutButton.Click += (_, _) => OpenShortcutSettings();

        var steps = new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var content = new TutorialLayoutPanel { Preview = frame, Steps = steps };
        content.Children.Add(_features);
        content.Children.Add(_instructions);
        content.Children.Add(frame);
        content.Children.Add(controls);
        content.Children.Add(steps);
        UpdateInstructions();
        root.Children.Add(content);
        Content = root;
        UpdateControls();

        Loaded += (_, _) =>
        {
            if (!_watchingMotion)
            {
                SystemParameters.StaticPropertyChanged += OnMotionPreferenceChanged;
                _watchingMotion = true;
            }
            // Loaded runs before the first render, so the window appears with the still in place.
            ShowPoster();
            RequestOpen();
        };
        Deactivated += (_, _) => Pause();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Pause(); };
        IsVisibleChanged += (_, _) => { if (_closed) return; if (!IsVisible) ReleaseMedia(); else RequestOpen(); };
        Closed += (_, _) =>
        {
            _closed = true;
            ReleaseMedia();
            _media.Opened -= OnMediaOpened;
            _media.Failed -= OnMediaFailed;
            _media.Ended -= OnMediaEnded;
            try { _media.Dispose(); }
            catch (Exception error) { Trace.TraceWarning("Feature tutorial media could not be disposed: {0}", error); }
            if (_watchingMotion) SystemParameters.StaticPropertyChanged -= OnMotionPreferenceChanged;
            _watchingMotion = false;
            if (_lifetimeOwner is not null) _lifetimeOwner.IsVisibleChanged -= OnOwnerVisibilityChanged;
            _lifetimeOwner = null;
            if (ReferenceEquals(_open, this)) _open = null;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    /// <summary>An Open was issued for the selected feature and not yet released.</summary>
    internal bool HasMedia => _mediaOpen;
    /// <summary>The current media reported open, so its first frame is on screen.</summary>
    internal bool HasFirstFrame => _hasFirstFrame;
    /// <summary>The selected feature's first-frame still is showing, beneath the video.</summary>
    internal bool HasPoster => _poster.Background is not null;
    internal bool IsOpenPending => _pendingOpen is not null;
    internal bool IsPlaying => _playing;
    internal bool IsShowingFallback => _fallback;
    internal bool IsWatchingMotionPreference => _watchingMotion;
    internal Window? LifetimeOwner => _lifetimeOwner;

    /// <summary>Where the selected feature's videos live; tests point it at fixtures.</summary>
    internal string AssetDirectory { get; init; } = Path.Combine(
        Path.GetDirectoryName(typeof(FeatureTutorialWindow).Assembly.Location) ?? AppContext.BaseDirectory, "Assets", "Tutorials");

    internal string AssetPath => Path.Combine(AssetDirectory,
        $"{_feature.Id}.{(UiText.Culture.TwoLetterISOLanguageName == "ko" ? "ko" : "en")}.mp4");

    /// <summary>The video's first frame as a JPEG beside it.</summary>
    internal string PosterPath => Path.ChangeExtension(AssetPath, ".jpg");

    internal string SelectedFeatureId => _feature.Id;
    /// <summary>
    /// "{feature} tutorial": the window title and the accessible name of every button that opens
    /// it, so a screen reader never hears a bare feature name that sounds like the feature itself.
    /// </summary>
    internal static string TutorialTitle(FeatureTutorial feature) => UiText.Format("Tutorial.AccessibleName", TutorialSubject(feature));

    /// <summary>
    /// The accessible name of a "View tutorial" button: "{feature} 사용법 보기" / "{feature} — View
    /// tutorial". It contains the visible label, so speech input can say what the button shows.
    /// </summary>
    internal static string OpenButtonName(FeatureTutorial feature) => UiText.Format("Tutorial.OpenAccessibleName", TutorialSubject(feature));

    /// <summary>The feature's own title, or its category-qualified name when another tutorial shares that title.</summary>
    internal static string TutorialSubject(FeatureTutorial feature)
    {
        string title = UiText.Get(feature.TitleKey);
        return TutorialCatalog.All.Count(other => UiText.Get(other.TitleKey) == title) > 1 ? feature.DisplayName : title;
    }

    internal static void ShowTutorial(Window owner, HotkeySettings hotkeys, string featureId) =>
        ShowTutorial(owner, () => hotkeys, featureId);

    internal static void ShowTutorial(Window owner, Func<HotkeySettings> readHotkeys, string featureId)
    {
        if (!ShortcutHelpWindow.CanShowHelp()) return;
        if (_open is { IsVisible: true })
        {
            _open.AttachOwner(owner);
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
        tutorial.AttachOwner(owner);
        // Size for the owner's monitor before showing: CenterOwner then keeps the window in its work area.
        IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
        if (ownerHandle != IntPtr.Zero) tutorial.FitToMonitor(MonitorEnumerator.GetFromWindow(ownerHandle));
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

    /// <summary>Fits the window to a monitor's work area, converted to this window's DIPs.</summary>
    internal void FitToMonitor(MonitorInfo monitor)
    {
        double scale = Math.Max(1, monitor.Dpi) / 96.0;
        FitToWorkArea(new Size(monitor.WorkArea.Width / scale, monitor.WorkArea.Height / scale));
    }

    /// <summary>
    /// Keeps the preferred size where it fits and otherwise shrinks to the work area; the layout
    /// then shrinks the 16:9 preview first and scrolls the steps only after that.
    /// </summary>
    internal void FitToWorkArea(Size workArea)
    {
        double width = Math.Max(1, workArea.Width), height = Math.Max(1, workArea.Height);
        MinWidth = Math.Min(MinimumWidth, width);
        MinHeight = Math.Min(MinimumHeight, height);
        Width = Math.Clamp(PreferredWidth, MinWidth, width);
        Height = Math.Clamp(PreferredHeight, MinHeight, height);
    }

    private void ApplyFeature(string featureId)
    {
        ReleaseMedia();
        _feature = TutorialCatalog.Get(featureId);
        UpdateInstructions();
        // The still is one small file, so it follows every selection at once; the video opens
        // only for the feature the selection settles on.
        ShowPoster();
        RequestOpen();
    }

    /// <summary>
    /// Shows the selected feature's first-frame still and hides the loading text over it. Without
    /// the still, the loading text stays until the video opens.
    /// </summary>
    private void ShowPoster()
    {
        string path = PosterPath;
        if (path == _posterPath) return;
        ClearPoster();
        _posterPath = path;
        if (!File.Exists(path))
        {
            Trace.TraceWarning("Feature tutorial {0} has no first-frame still at {1}; the video shows once it opens.", _feature.Id, path);
            return;
        }
        try
        {
            var image = new BitmapImage();
            using (FileStream stream = File.OpenRead(path))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            var brush = new ImageBrush(image) { Stretch = Stretch.Uniform };
            brush.Freeze();
            _poster.Background = brush;
            if (!_fallback) _status.Visibility = Visibility.Collapsed;
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Feature tutorial {0} could not show its first-frame still {1}: {2}", _feature.Id, path, error);
        }
    }

    private void ClearPoster()
    {
        _poster.Background = null;
        _posterPath = null;
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
        // An unassigned shortcut is not a dead end: the button opens the row that assigns it.
        if (OffersSetShortcut(_feature, keys)) _instructions.Children.Add(SetShortcutButton);
        _steps.Children.Clear();
        foreach (string step in _feature.StepsFor(keys)) _steps.Children.Add(Paragraph(step));
        AutomationProperties.SetName(_media.View, _feature.Id == "text-copy"
            ? UiText.Get("TextCapture_TutorialAnimation") : UiText.Format("Tutorial.VideoName", TutorialSubject(_feature)));
    }

    /// <summary>Whether the feature has a global shortcut that is unassigned and the shell can open its row.</summary>
    internal static bool OffersSetShortcut(FeatureTutorial feature, HotkeySettings keys) =>
        OpenShortcutSetting is not null && feature.GlobalKey is { } key && !key(keys).IsAssigned
        && ShortcutSettingTarget.ForFeature(feature.Id) is not null;

    /// <summary>Closes the tutorial so it does not cover the row, then opens Settings → Shortcuts on it.</summary>
    private void OpenShortcutSettings()
    {
        if (ShortcutSettingTarget.ForFeature(_feature.Id) is not { } row || OpenShortcutSetting is not { } open) return;
        Close();
        open(row);
    }

    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_lifetimeOwner?.IsVisible == false && !_closed) Close();
    }

    private void AttachOwner(Window owner)
    {
        if (ReferenceEquals(_lifetimeOwner, owner)) return;
        if (_lifetimeOwner is not null) _lifetimeOwner.IsVisibleChanged -= OnOwnerVisibilityChanged;
        Owner = owner;
        _lifetimeOwner = owner;
        owner.IsVisibleChanged += OnOwnerVisibilityChanged;
    }

    /// <summary>
    /// Opens the selected feature once input settles, so rapidly stepping through the list opens
    /// only the feature it stops on.
    /// </summary>
    private void RequestOpen()
    {
        if (_closed || _pendingOpen is not null || Dispatcher.HasShutdownStarted) return;
        // ContextIdle, after input and rendering: creating a MediaElement holds up the next frame,
        // so waiting lets the selected feature's still reach the screen before the video opens.
        _pendingOpen = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(OpenSelected));
    }

    private void OpenSelected()
    {
        _pendingOpen = null;
        if (_closed || !IsLoaded || !IsVisible || _mediaOpen || _fallback) return;
        string path = AssetPath;
        if (!File.Exists(path))
        {
            ShowFallback(null, $"the video {path} is missing");
            return;
        }
        _mediaOpen = true;
        _hasFirstFrame = false;
        _status.Text = UiText.Get("TextCapture_TutorialLoading");
        ShowPoster();
        _status.Visibility = HasPoster ? Visibility.Collapsed : Visibility.Visible;
        try { _media.Open(path); }
        catch (Exception error)
        {
            ShowFallback(error, $"the video {path} could not be opened");
            return;
        }
        UpdateControls();
    }

    private void OnMediaOpened()
    {
        if (_closed || !_mediaOpen || _fallback) return;
        _hasFirstFrame = true;
        _status.Visibility = Visibility.Collapsed;
        if (FluidMotion.AnimationsEnabled && IsActive) StartPlayback();
        else UpdateControls();
    }

    private void OnMediaFailed(Exception? error)
    {
        if (_closed || !_mediaOpen) return;
        ShowFallback(error, $"playback of {AssetPath} failed");
    }

    private void OnMediaEnded()
    {
        if (_closed || !_mediaOpen || _fallback) return;
        // Loop: back to the first frame, and keep playing when it was playing.
        try
        {
            _media.Rewind();
            if (_playing) _media.Play();
        }
        catch (Exception error) { ShowFallback(error, "the video could not loop"); }
    }

    internal void TogglePlayback()
    {
        if (!CanControl) return;
        if (_playing) Pause();
        else StartPlayback();
    }

    internal void Replay()
    {
        if (!CanControl) return;
        try { _media.Rewind(); }
        catch (Exception error)
        {
            ShowFallback(error, "the video could not rewind");
            return;
        }
        StartPlayback();
    }

    internal void Pause()
    {
        if (_mediaOpen && _playing)
        {
            try { _media.Pause(); }
            catch (Exception error)
            {
                ShowFallback(error, "the video could not pause");
                return;
            }
        }
        _playing = false;
        UpdateControls();
    }

    protected void OnMotionPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation) || Dispatcher.HasShutdownStarted) return;
        void Apply() { if (!_closed && !FluidMotion.AnimationsEnabled) Pause(); }
        if (Dispatcher.CheckAccess()) Apply();
        else _ = Dispatcher.BeginInvoke(new Action(Apply));
    }

    private bool CanControl => !_closed && _mediaOpen && _hasFirstFrame && !_fallback;

    private void StartPlayback()
    {
        if (!CanControl) return;
        try { _media.Play(); }
        catch (Exception error)
        {
            ShowFallback(error, "the video could not play");
            return;
        }
        _playing = true;
        UpdateControls();
    }

    /// <summary>Closes the current video; the selected feature's steps stay.</summary>
    private void ReleaseMedia()
    {
        _pendingOpen?.Abort();
        _pendingOpen = null;
        bool wasOpen = _mediaOpen;
        _mediaOpen = false;
        _hasFirstFrame = false;
        _playing = false;
        _fallback = false;
        if (wasOpen)
        {
            try { _media.Close(); }
            catch (Exception error) { Trace.TraceWarning("Feature tutorial video could not be closed: {0}", error); }
        }
        UpdateControls();
    }

    /// <summary>Shows the steps as a still fallback, as when Windows has no video playback.</summary>
    private void ShowFallback(Exception? error, string reason)
    {
        Trace.TraceWarning("Feature tutorial {0} shows its steps only because {1}: {2}", _feature.Id, reason,
            error?.ToString() ?? "no error details");
        ReleaseMedia();
        _fallback = true;
        // The message replaces the picture; the steps below stay.
        ClearPoster();
        _status.Text = UiText.Get(_feature.Id == "text-copy" ? "TextCapture_TutorialUnavailable" : "Tutorial.Unavailable");
        _status.Visibility = Visibility.Visible;
        UpdateControls();
    }

    private void UpdateControls()
    {
        string caption = UiText.Get(_playing ? "GifPreview_Pause" : "GifPreview_Play");
        _play.Content = caption;
        AutomationProperties.SetName(_play, caption);
        _play.IsEnabled = _replay.IsEnabled = CanControl;
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8) };

    /// <summary>
    /// Stacks the tutorial top to bottom. The 16:9 preview takes the height the other rows leave,
    /// up to its 640×360 maximum; only when it reaches its minimum do the steps start to scroll.
    /// </summary>
    private sealed class TutorialLayoutPanel : Panel
    {
        private const double PreviewMinHeight = 90;
        private double _previewHeight;

        internal required FrameworkElement Preview { get; init; }
        internal required FrameworkElement Steps { get; init; }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? PreviewMaxWidth : availableSize.Width;
            double fixedHeight = 0, desiredWidth = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (ReferenceEquals(child, Preview) || ReferenceEquals(child, Steps)) continue;
                child.Measure(new Size(width, double.PositiveInfinity));
                fixedHeight += child.DesiredSize.Height;
                desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            }
            Steps.Measure(new Size(width, double.PositiveInfinity));
            double stepsWanted = Steps.DesiredSize.Height;
            Thickness margin = Preview.Margin;
            double marginX = margin.Left + margin.Right, marginY = margin.Top + margin.Bottom;
            double largest = Math.Min(PreviewMaxHeight, Math.Max(0, width - marginX) * 9 / 16);
            double room = availableSize.Height - fixedHeight - marginY;
            _previewHeight = double.IsInfinity(room) ? largest
                : Math.Clamp(room - stepsWanted, Math.Min(PreviewMinHeight, largest), largest);
            Preview.Measure(new Size(_previewHeight * 16 / 9 + marginX, _previewHeight + marginY));
            double stepsHeight = double.IsInfinity(room) ? stepsWanted : Math.Clamp(room - _previewHeight, 0, stepsWanted);
            Steps.Measure(new Size(width, stepsHeight));
            desiredWidth = Math.Max(desiredWidth, Math.Max(Preview.DesiredSize.Width, Steps.DesiredSize.Width));
            return new Size(desiredWidth, fixedHeight + _previewHeight + marginY + stepsHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double top = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (ReferenceEquals(child, Preview))
                {
                    Thickness margin = Preview.Margin;
                    double width = _previewHeight * 16 / 9 + margin.Left + margin.Right;
                    double height = _previewHeight + margin.Top + margin.Bottom;
                    child.Arrange(new Rect(Math.Max(0, (finalSize.Width - width) / 2), top, width, height));
                    top += height;
                }
                else if (ReferenceEquals(child, Steps))
                {
                    // Last row: whatever height is left, so a taller window never clips the steps.
                    double height = Math.Max(0, finalSize.Height - top);
                    child.Arrange(new Rect(0, top, finalSize.Width, height));
                    top += height;
                }
                else
                {
                    child.Arrange(new Rect(0, top, finalSize.Width, child.DesiredSize.Height));
                    top += child.DesiredSize.Height;
                }
            }
            return finalSize;
        }
    }
}
