using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// A borderless, always-on-top frame that outlines the region being recorded and hosts
/// the record / stop / delay controls. The frame is draggable so the user can nudge the
/// captured area after selecting it, exactly as the brief asks.
/// </summary>
/// <remarks>
/// <para>
/// The window covers the region plus a control strip below it. The interior of the
/// region is transparent and, while recording, click-through, so the app being recorded
/// stays usable. A thin accent border makes the captured bounds unmistakable without
/// obscuring pixels — consistent with the capture overlay's "content-first" rule.
/// </para>
/// <para>
/// Start delay reuses the capture countdown convention: the control strip counts down
/// and the first frame is only grabbed after it reaches zero, so the countdown digits
/// are never inside the recorded region anyway (they live in the strip, outside it).
/// </para>
/// </remarks>
internal sealed class RecordingControlWindow : Window
{
    // Six DIPs stay visible on dense/high-DPI displays and provide a forgiving visual target
    // while the transparent interior remains draggable before recording starts.
    private const double BorderThicknessPx = 6.0;
    private const double StripHeight = 56.0;

    // The palette has a compact, predictable width independent from the recorded image.
    // On an unusually narrow work area the Viewbox fits its controls instead of clipping them.
    // The extra width over the pre-pause 460 DIPs keeps the status text readable once the
    // pause button appears during recording.
    private const double MinStripWidth = 560.0;

    // Wait for the in-flight frame to finish before marking the in-region clock as paused.
    private static readonly TimeSpan PauseParkTimeout = TimeSpan.FromSeconds(2);

    // The restored clock must reach the screen before frames flow again. One dispatcher pass at
    // Loaded priority runs after layout/render; this covers the following DWM presentation.
    private static readonly TimeSpan ResumePresentationDelay = TimeSpan.FromMilliseconds(50);

    // Diagonal stripe period (DIPs) of the paused outline, a non-colour cue that remains
    // distinguishable in the high-contrast theme where accent and warning share one colour.
    private const double PausedStripePeriod = 12.0;

    private RectD _screenRegion;
    private readonly RecordingSettings _settings;
    private readonly Func<RegionRecorder> _recorderFactory;
    private readonly Func<string> _outputPathFactory;
    private readonly ILogger _log;

    // Filled glyphs on the 20x20 symbol grid, drawn in the button's own text colour.
    // Frozen: the bar is built on whichever UI thread opens it, and these shapes are shared.
    private static readonly Geometry RecordGlyph = Frozen(new EllipseGeometry(new Point(10, 10), 5.5, 5.5));
    private static readonly Geometry StopGlyph = Frozen(new RectangleGeometry(new Rect(4.5, 4.5, 11, 11), 1.5, 1.5));
    private static readonly Geometry PauseGlyph = Geometry.Parse("M5,4 L8.5,4 L8.5,16 L5,16 Z M11.5,4 L15,4 L15,16 L11.5,16 Z");
    private static readonly Geometry ResumeGlyph = Geometry.Parse("M6,3.75 L16,10 L6,16.25 Z");
    private static readonly Geometry MoreGlyph = Geometry.Parse("M5,9 A1,1 0 1 0 4.99,9 M10,9 A1,1 0 1 0 9.99,9 M15,9 A1,1 0 1 0 14.99,9");
    private static readonly Geometry CheckGlyph = Geometry.Parse("M3.75,10.25 L8.15,14.65 L16.25,5.35");

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private readonly Button _primaryButton;
    private readonly Button _pauseButton;
    private readonly Button _moreButton;
    private readonly Button _cancelButton;
    private readonly TextBlock _statusText;
    private readonly TextBlock _timerText;
    private bool _showTime;
    private bool _showTimeAvailable = true;
    private ContextMenu? _moreMenu;
    private readonly Border _regionFrame;
    private readonly Border _controlStrip;
    private readonly Canvas _root;

    private RecordingWallClockWindow? _wallClock;
    private double _wallClockFontSize = RecordingWallClockWindow.DefaultFontSize;
    private double? _wallClockRelativeX;
    private double? _wallClockRelativeY;

    private RegionRecorder? _recorder;
    private DispatcherTimer? _elapsedTimer;
    private bool _readyAnnounced;
    private DispatcherTimer? _countdownTimer;
    private int _countdownRemaining;
    private bool _stopping;
    private bool _finished;
    private bool _applyingPhysicalLayout;
    private bool _captureExclusionApplied;
    private bool _paletteOverlapsRegion;
    private bool _paused;
    private bool _pauseTransition;

    internal RecordingControlWindow(
        RectD screenRegion,
        RecordingSettings settings,
        Func<RegionRecorder> recorderFactory,
        Func<string> outputPathFactory,
        ILogger log)
    {
        _screenRegion = screenRegion.Normalized().ToPixelBounds();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        _outputPathFactory = outputPathFactory ?? throw new ArgumentNullException(nameof(outputPathFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // The frame is also the exact recording boundary. Never translate or fade it while
        // the user is positioning the region; normal task windows use StandardWindowTheme.
        FluidMotion.SetWindowEntrance(this, false);

        Title = UiText.Get("Text_C750845B7AA3");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _root = new Canvas { Background = Brushes.Transparent };
        _regionFrame = BuildRegionFrame();
        _root.Children.Add(_regionFrame);

        _controlStrip = BuildControlStrip(out _primaryButton, out _pauseButton, out _statusText, out _timerText,
            out _moreButton, out _cancelButton);
        _root.Children.Add(_controlStrip);
        ShowReadyPrimary();
        UpdatePauseButton();

        Content = _root;
        ApplyPhysicalLayout(positionHwnd: false);

        KeyDown += OnKeyDown;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        DpiChanged += OnDpiChanged;
    }

    internal event EventHandler<RecordingResult>? RecordingFinished;

    internal event EventHandler? Cancelled;

    internal event EventHandler<RecordingFailedEventArgs>? Failed;

    /// <summary>Raised the moment a stop begins, before the deferred finalise runs.</summary>
    internal event EventHandler? Stopping;

    internal bool IsRecording => _recorder is { IsRecording: true };
    internal bool CanCaptureStill => _recorder is { IsReady: true } && !_stopping && !_completionPending && !_finished;

    /// <summary>True while the recording is paused (the capture loop has stopped grabbing).</summary>
    internal bool IsPaused => _paused;

    /// <summary>True while a pause or resume is still being applied.</summary>
    internal bool IsPauseTransitionPending => _pauseTransition;

    /// <summary>
    /// The optional global pause chord shown in the pause button's guidance. Reads the live
    /// application settings by default; tests substitute a fixed chord.
    /// </summary>
    internal Func<Hotkey> ReadPauseHotkey { get; set; } = static () => ShortcutHelpWindow.ReadHotkeys().PauseRecording;

    /// <summary>
    /// Set when the window closed (application exit, session end or a forced close) while
    /// recording and the recorder still finalised a playable private MP4 without the normal
    /// completion handshake. The owner keeps that clip for startup recovery.
    /// </summary>
    internal bool FinalizedOnClose { get; private set; }

    /// <summary>External stop request (e.g. pressing Ctrl+Shift+X again).</summary>
    /// <remarks>Works the same while paused: the paused span is simply not part of the clip.</remarks>
    internal void RequestStop() => StopRecording();

    // The coordinator keeps this visible until gallery registration and editor startup finish.
    internal void ShowCompletionStatus(string text)
    {
        Opacity = 1;
        _showTimeAvailable = false;
        _statusText.Text = text;
        SetButtonContent(_primaryButton, glyph: null, UiText.Get("Text_88DAAEEDFE4C"));
        _primaryButton.IsEnabled = false;
        _completionProgress.Visibility = Visibility.Visible;
        UpdateSessionButtons();
        AnnounceStatus();
    }

    /// <summary>
    /// The time stamp drawn inside the recorded area (the ⋯ menu's "show time"). It replaces the
    /// former "clock" check box on the bar.
    /// </summary>
    internal bool ShowTime
    {
        get => _showTime;
        set
        {
            if (_showTime == value || (value && !_showTimeAvailable)) return;
            _showTime = value;
            SetWallClockEnabled(value);
        }
    }

    /// <summary>The ⋯ menu while it is open.</summary>
    internal ContextMenu? MoreMenuForTest => _moreMenu;

    internal Button PrimaryButtonForTest => _primaryButton;

    internal Button PauseButtonForTest => _pauseButton;

    internal Button MoreButtonForTest => _moreButton;

    internal Button CancelButtonForTest => _cancelButton;

    /// <summary>The visible words on a bar button (its glyph is decoration).</summary>
    internal static string ButtonLabel(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => panel.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// The ⋯ menu: "show time" as a checked entry. It opens below the button and, like the bar, is
    /// kept out of the recording.
    /// </summary>
    internal ContextMenu CreateMoreMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _moreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4 };
        AutomationProperties.SetName(menu, UiText.Get("Onboard_RecordMore"));
        var time = new MenuItem
        {
            Header = UiText.Get("Onboard_RecordShowTime"),
            IsCheckable = true,
            IsChecked = _showTime,
            IsEnabled = _showTimeAvailable && !_stopping && !_finished,
            MinHeight = 36,
            ToolTip = UiText.Get("Text_E2395EBF6C65"),
        };
        // The shared menu template draws no check mark; the icon column carries it.
        time.Icon = _showTime ? MenuGlyph(CheckGlyph) : null;
        AutomationProperties.SetName(time, UiText.Get("Onboard_RecordShowTime"));
        AutomationProperties.SetHelpText(time, UiText.Get("Text_E2395EBF6C65"));
        time.Click += (_, _) =>
        {
            menu.IsOpen = false;
            ShowTime = !ShowTime;
        };
        menu.Items.Add(time);
        menu.Opened += (_, _) =>
        {
            // The popup is its own window; exclude it from capture like this bar.
            if (PresentationSource.FromVisual(menu) is HwndSource popup)
                _ = SetWindowDisplayAffinity(popup.Handle, WdaExcludeFromCapture);
        };
        return menu;
    }

    private void OpenMoreMenu()
    {
        if (_moreMenu is { IsOpen: true }) return;
        ContextMenu menu = CreateMoreMenu();
        menu.Closed += (_, _) => { if (ReferenceEquals(_moreMenu, menu)) _moreMenu = null; };
        _moreMenu = menu;
        menu.IsOpen = true;
    }

    private static FrameworkElement MenuGlyph(Geometry geometry)
    {
        var glyph = new SymbolIcon
        {
            Size = 16,
            Data = geometry,
        };
        glyph.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Primary");
        return glyph;
    }

    /// <summary>An icon and a short name on one bar button; the glyph takes the button's text colour.</summary>
    private static void SetButtonContent(Button button, Geometry? glyph, string label)
    {
        if (glyph is null)
        {
            button.Content = label;
            return;
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var path = new SymbolIcon
        {
            Size = 20,
            StrokeThickness = 0,
            Data = glyph,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetBinding(SymbolIcon.FillProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        panel.Children.Add(path);
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        button.Content = panel;
    }

    /// <summary>Before recording: ● and "start recording", the area's size and frame rate in the tooltip.</summary>
    private void ShowReadyPrimary()
    {
        string label = UiText.Get(_settings.UseStartDelay ? "Text_2AA27E2CEB7C" : "Text_255C28FD0AC2");
        SetButtonContent(_primaryButton, RecordGlyph, label);
        AutomationProperties.SetName(_primaryButton, label);
        _primaryButton.ToolTip = UiText.Format("Onboard_RecordAreaTip", FormatReadyStatus());
        AutomationProperties.SetHelpText(_primaryButton, (string)_primaryButton.ToolTip);
    }

    /// <summary>Cancel exists only before recording: once recording runs, stop is the way out.</summary>
    /// <summary>While recording: ■ Stop in the danger style and ❚❚ Pause; cancel leaves the bar.</summary>
    private void ShowRecordingControls()
    {
        SetButtonContent(_primaryButton, StopGlyph, UiText.Get("Onboard_RecordStop"));
        AutomationProperties.SetName(_primaryButton, UiText.Get("Text_74C6AEBB2B6D"));
        _primaryButton.Style = TryStyle("Button.Danger");
        _pauseButton.Visibility = Visibility.Visible;
        _pauseButton.IsEnabled = true;
        UpdatePauseButton();
        UpdateSessionButtons();
    }

    private bool _previewRecording;

    /// <summary>
    /// Evidence hook: the bar and region outline exactly as recording shows them (paused or not),
    /// without starting a recorder or showing the window.
    /// </summary>
    internal void PreviewRecordingForTest(bool paused, TimeSpan elapsed)
    {
        _previewRecording = true;
        _paused = paused;
        _readyAnnounced = true;
        ShowRecordingControls();
        _statusText.Text = UiText.Get(paused ? "RecordingPause_Status" : "Text_38B12BA58C48");
        _statusText.Foreground = paused ? TryBrush("Text.Primary", Colors.White) : TryBrush("State.Danger", Colors.OrangeRed);
        _timerText.Text = elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        UpdateRegionFrameBrush(_regionFrame);
    }

    private void UpdateSessionButtons()
    {
        bool beforeRecording = !IsRecording && !_previewRecording && !_stopping && !_completionPending && !_finished;
        _cancelButton.Visibility = beforeRecording ? Visibility.Visible : Visibility.Collapsed;
        _moreButton.IsEnabled = !_stopping && !_completionPending && !_finished;
    }

    private bool _completionPending;
    private readonly ProgressBar _completionProgress = new()
    {
        Height = 3,
        IsIndeterminate = true,
        VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 0, 0, -6),
    };

    internal void CompleteAndClose()
    {
        CloseWallClock();
        _completionPending = false;
        Close();
    }

    private Border BuildRegionFrame()
    {
        var frame = new AccessibleRegionFrame
        {
            BorderBrush = TryBrush("Border.Accent", Color.FromRgb(0x7C, 0x6C, 0xFF)),
            BorderThickness = new Thickness(BorderThicknessPx),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(2),
            Cursor = Cursors.SizeAll,
            Focusable = true,
        };

        // Drag the frame (and thus the recording region) by pressing inside it while not
        // recording. Once recording starts the interior becomes click-through so the
        // target app is usable. Keyboard users can focus the frame and nudge it with the
        // arrow keys; Shift increases the step to ten DIPs.
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsRecording && e.ButtonState == MouseButtonState.Pressed)
            {
                _ = frame.Focus();
                try
                {
                    DragMove();
                    UpdateRegionFromVisibleFrame();
                }
                catch (InvalidOperationException)
                {
                    // DragMove throws if the button was already released; ignore.
                }
            }
        };
        frame.GotKeyboardFocus += (_, _) => UpdateRegionFrameBrush(frame);
        frame.LostKeyboardFocus += (_, _) => UpdateRegionFrameBrush(frame);

        AutomationProperties.SetName(frame, UiText.Get("Text_E7A732EAE7D5"));
        AutomationProperties.SetHelpText(
            frame,
            UiText.Get("Text_66069CEAD9AE"));
        return frame;
    }

    private Border BuildControlStrip(
        out Button primary,
        out Button pause,
        out TextBlock status,
        out TextBlock timer,
        out Button more,
        out Button cancel)
    {
        var panel = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ● Start recording, then ■ Stop: an icon and a short name, the size and rate in the tooltip.
        primary = new Button
        {
            MinWidth = 108,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Primary"),
        };
        primary.Click += (_, _) => OnPrimaryClicked();
        Grid.SetColumn(primary, 0);
        panel.Children.Add(primary);

        // Shown only while recording. A regular themed button keeps the theme's focus visual,
        // high-contrast colours and Space/Enter activation after Tab.
        pause = new Button
        {
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Secondary"),
            Visibility = Visibility.Collapsed,
        };
        pause.Click += (_, _) => _ = TogglePause();
        Grid.SetColumn(pause, 1);
        panel.Children.Add(pause);

        // Results only: empty before recording, then the recording state.
        status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(status, 2);
        panel.Children.Add(status);

        timer = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = TryBrush("Text.Primary", Colors.White),
            FontFamily = TryFont("Font.Mono"),
            FontSize = 15,
        };
        AutomationProperties.SetName(timer, UiText.Get("Text_F11BA21FB824"));
        Grid.SetColumn(timer, 3);
        panel.Children.Add(timer);

        // The time stamp option lives in the ⋯ menu instead of a check box on the bar.
        more = new Button
        {
            MinWidth = 40,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Ghost"),
            ToolTip = UiText.Get("Onboard_RecordMoreHelp"),
        };
        var moreGlyph = new SymbolIcon
        {
            Size = 20,
            Data = MoreGlyph,
        };
        moreGlyph.SetBinding(SymbolIcon.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = more });
        more.Content = moreGlyph;
        more.Click += (_, _) => OpenMoreMenu();
        AutomationProperties.SetName(more, UiText.Get("Onboard_RecordMore"));
        AutomationProperties.SetHelpText(more, UiText.Get("Onboard_RecordMoreHelp"));
        Grid.SetColumn(more, 4);
        panel.Children.Add(more);

        // Before recording only; while recording, stop is the way out.
        cancel = new Button
        {
            Content = UiText.Get("Text_BE876433993A"),
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Ghost"),
        };
        cancel.Click += (_, _) => CancelSession();
        AutomationProperties.SetName(cancel, UiText.Get("Text_E1C96EF1EA0F"));
        Grid.SetColumn(cancel, 5);
        panel.Children.Add(cancel);
        Grid.SetColumnSpan(_completionProgress, 6);
        AutomationProperties.SetName(_completionProgress, UiText.Get("Text_C1767C4F1218"));
        panel.Children.Add(_completionProgress);

        return new Border
        {
            Background = TryBrush("Surface.Floating", Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B)),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x2B, 0x3A, 0x50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Effect = Application.Current?.TryFindResource("Shadow.Floating") as System.Windows.Media.Effects.Effect,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = new Border { Width = MinStripWidth - 2, Height = StripHeight - 2, Child = panel },
            },
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _captureExclusionApplied = hwnd != IntPtr.Zero
            && SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        if (!_captureExclusionApplied)
        {
            _log.LogWarning(
                "Could not exclude recording controls from capture (Win32 {Error})",
                Marshal.GetLastWin32Error());
        }

        ApplyPhysicalLayout(positionHwnd: true);
    }

    private void OnContentRendered(object? sender, EventArgs e) =>
        ApplyPhysicalLayout(positionHwnd: true);

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (!_applyingPhysicalLayout)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => ApplyPhysicalLayout(positionHwnd: true)));
        }
    }

    /// <summary>
    /// Lays the outline and palette out from one physical-pixel source rectangle. The palette may
    /// clamp or flip, but the outline never moves independently from <see cref="_screenRegion"/>.
    /// </summary>
    private void ApplyPhysicalLayout(bool positionHwnd)
    {
        if (_applyingPhysicalLayout)
        {
            return;
        }

        _applyingPhysicalLayout = true;
        try
        {
            double scale = ResolveWindowScale();
            // The desktop bounding rectangle can contain gaps between staggered monitors.
            // Keep every control on one real monitor and outside its taskbar/appbars.
            RectD workArea = MonitorEnumerator.GetFromPoint(_screenRegion.Center).WorkArea;
            RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(
                _screenRegion,
                workArea,
                scale,
                BorderThicknessPx,
                StripHeight,
                MinStripWidth);
            // An excluded frame may sit inside the capture so full-screen edges remain visible.
            // Without exclusion keep every border pixel outside the source rectangle.
            RectD frameBox = IsRecording && _captureExclusionApplied ? _screenRegion : layout.FrameBounds;
            RectD stripBox = layout.PaletteBounds;
            RectD windowPixels = layout.WindowBounds;
            _paletteOverlapsRegion = layout.PaletteOverlapsRegion;

            Width = Math.Max(1, windowPixels.Width / scale);
            Height = Math.Max(1, windowPixels.Height / scale);
            _root.Width = Width;
            _root.Height = Height;

            _regionFrame.Width = frameBox.Width / scale;
            _regionFrame.Height = frameBox.Height / scale;
            Canvas.SetLeft(_regionFrame, (frameBox.Left - windowPixels.Left) / scale);
            Canvas.SetTop(_regionFrame, (frameBox.Top - windowPixels.Top) / scale);

            _controlStrip.Width = stripBox.Width / scale;
            _controlStrip.Height = stripBox.Height / scale;
            Canvas.SetLeft(_controlStrip, (stripBox.Left - windowPixels.Left) / scale);
            Canvas.SetTop(_controlStrip, (stripBox.Top - windowPixels.Top) / scale);

            // Initial values influence which monitor DPI WPF chooses for HWND creation. Once the
            // HWND exists, SetWindowPos below is authoritative and works in physical pixels.
            Left = windowPixels.Left / scale;
            Top = windowPixels.Top / scale;
            if (positionHwnd)
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    PhysicalWindowPositioner.PlaceTopmost(hwnd, windowPixels);
                }
            }

            _wallClock?.UpdateRecordingRegion(_screenRegion, scale);
        }
        finally
        {
            _applyingPhysicalLayout = false;
        }
    }

    private void UpdateRegionFromVisibleFrame()
    {
        try
        {
            Point topLeft = _regionFrame.PointToScreen(new Point(BorderThicknessPx, BorderThicknessPx));
            _screenRegion = new RectD(
                    topLeft.X,
                    topLeft.Y,
                    _screenRegion.Width,
                    _screenRegion.Height)
                .ClampTo(MonitorEnumerator.GetVirtualDesktopBounds())
                .ToPixelBounds();
            ShowReadyPrimary();
            ApplyPhysicalLayout(positionHwnd: true);
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "Could not resolve the moved recording frame in physical pixels");
            ApplyPhysicalLayout(positionHwnd: true);
        }
    }

    private double ResolveWindowScale()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            double scale = target.TransformToDevice.M11;
            if (double.IsFinite(scale) && scale > 0)
            {
                return scale;
            }
        }

        return ResolveScale();
    }

    private void OnPrimaryClicked()
    {
        if (_stopping || _completionPending)
        {
            return;
        }

        if (IsRecording)
        {
            StopRecording();
            return;
        }

        if (_countdownTimer is not null)
        {
            return; // Already counting down.
        }

        if (_settings.UseStartDelay && _settings.StartDelaySeconds > 0)
        {
            BeginCountdown(_settings.StartDelaySeconds);
        }
        else
        {
            BeginRecording();
        }
    }

    private void BeginCountdown(int seconds)
    {
        _countdownRemaining = seconds;
        _primaryButton.IsEnabled = false;
        _statusText.Text = UiText.Format("Text_35114A85CCCE", _countdownRemaining);
        AnnounceStatus();

        _countdownTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _countdownTimer.Tick += (_, _) =>
        {
            _countdownRemaining--;
            if (_countdownRemaining <= 0)
            {
                StopCountdown();
                BeginRecording();
                return;
            }

            _statusText.Text = UiText.Format("Text_35114A85CCCE", _countdownRemaining);
            AnnounceStatus();
        };
        _countdownTimer.Start();
    }

    private void StopCountdown()
    {
        if (_countdownTimer is not null)
        {
            _countdownTimer.Stop();
            _countdownTimer = null;
        }

        _primaryButton.IsEnabled = true;
    }

    private void BeginRecording()
    {
        try
        {
            ApplyPhysicalLayout(positionHwnd: true);
            if (!_captureExclusionApplied && _paletteOverlapsRegion)
                throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
            _recorder = _recorderFactory();
            string output = _outputPathFactory();
            _recorder.Start(_screenRegion, output, _settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start recording");
            CloseWallClock();
            _showTime = false;
            _statusText.Text = UiText.Get("Text_48C3B6EBA637");
            _statusText.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            AnnounceStatus();
            _recorder?.Dispose();
            _recorder = null;
            _regionFrame.Visibility = Visibility.Visible;
            Opacity = 1;
            return;
        }

        _readyAnnounced = false;
        _paused = false;
        ShowRecordingControls();
        _statusText.Text = UiText.Get("Text_72A13C33A79E");
        _statusText.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        AnnounceStatus();

        // Make the region interior click-through so the recorded app stays usable.
        SetRegionClickThrough(true);
        _regionFrame.Visibility = Visibility.Visible;
        _regionFrame.IsHitTestVisible = false;
        _regionFrame.Background = null;
        ApplyPhysicalLayout(positionHwnd: true);

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _elapsedTimer.Tick += (_, _) => UpdateTimer();
        _elapsedTimer.Start();
    }

    private void UpdateTimer()
    {
        if (_recorder is { IsRecording: false } && !_stopping && !_finished)
        {
            StopRecording();
            return;
        }
        if (!_readyAnnounced && _recorder?.IsReady == true)
        {
            _readyAnnounced = true;
            if (!_paused)
            {
                _statusText.Text = UiText.Get("Text_38B12BA58C48");
                AnnounceStatus();
            }
        }
        // Recorded time only: the timer holds still while paused, matching the clip length.
        TimeSpan elapsed = _recorder?.RecordedElapsed ?? TimeSpan.Zero;
        _timerText.Text = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Pauses or resumes the running recording. The pause button and the optional pause
    /// hotkey both enter here, so they share the badge-safe ordering, status text and
    /// accessibility state.
    /// </summary>
    /// <returns>
    /// False when nothing changed: no recording is running, it is stopping, or the previous
    /// pause or resume is still being applied.
    /// </returns>
    internal bool TogglePause()
    {
        if (_pauseTransition || _stopping || _completionPending || _finished
            || _recorder is not { IsRecording: true } recorder)
        {
            return false;
        }

        _pauseTransition = true;
        _pauseButton.IsEnabled = false;
        ApplyPauseToggle(recorder, pause: !_paused);
        return true;
    }

    private async void ApplyPauseToggle(RegionRecorder recorder, bool pause)
    {
        try
        {
            if (pause)
            {
                await PauseAsync(recorder);
            }
            else
            {
                await ResumeAsync(recorder);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not change the recording pause state");
            await Dispatcher.InvokeAsync(EndPauseTransition);
        }
    }

    private async Task PauseAsync(RegionRecorder recorder)
    {
        if (!recorder.Pause())
        {
            EndPauseTransition();
            return;
        }

        _paused = true;
        ApplyPausePresentation();

        // The wall clock is the only control drawn inside the recorded region. Mark it paused
        // only after the capture loop has finished its in-flight frame and parked, so the
        // paused badge itself can never be encoded.
        bool parked = await Task.Run(() => recorder.WaitUntilCaptureParked(PauseParkTimeout)).ConfigureAwait(false);
        await Dispatcher.InvokeAsync(() =>
        {
            if (parked && _paused && !_stopping && !_finished && ReferenceEquals(_recorder, recorder))
            {
                _wallClock?.SetPaused(true);
            }

            EndPauseTransition();
        });
    }

    private async Task ResumeAsync(RegionRecorder recorder)
    {
        if (_wallClock is not null)
        {
            // Restore the in-region clock first and let it reach the screen before frames flow
            // again, so the first frame after the resume never shows the paused badge.
            _wallClock.SetPaused(false);
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            await Task.Delay(ResumePresentationDelay).ConfigureAwait(false);
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (!_stopping && !_finished && ReferenceEquals(_recorder, recorder) && recorder.Resume())
            {
                _paused = false;
                ApplyPausePresentation();
            }
            else if (_paused && !_stopping && !_finished)
            {
                // Resume was refused; keep the clock consistent with the still-paused capture.
                _wallClock?.SetPaused(true);
            }

            EndPauseTransition();
        });
    }

    private void EndPauseTransition()
    {
        _pauseTransition = false;
        _pauseButton.IsEnabled = IsRecording && !_stopping && !_completionPending && !_finished;
    }

    /// <summary>Updates every pause cue outside the recorded pixels (palette, status, outline).</summary>
    private void ApplyPausePresentation()
    {
        UpdatePauseButton();
        if (_paused)
        {
            // The words carry the state; primary text keeps AA contrast in the light themes,
            // where the warning colour is too light for 13 px text on the white palette.
            _statusText.Text = UiText.Get("RecordingPause_Status");
            _statusText.Foreground = TryBrush("Text.Primary", Colors.White);
        }
        else
        {
            _statusText.Text = UiText.Get(_readyAnnounced ? "Text_38B12BA58C48" : "Text_72A13C33A79E");
            _statusText.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        }

        AnnounceStatus();
        AutomationProperties.SetItemStatus(
            _regionFrame,
            _paused ? UiText.Get("RecordingPause_State") : string.Empty);
        UpdateRegionFrameBrush(_regionFrame);
    }

    private void UpdatePauseButton()
    {
        // ❚❚ Pause or ▶ Resume: the glyph shows what pressing it does.
        SetButtonContent(_pauseButton, _paused ? ResumeGlyph : PauseGlyph,
            UiText.Get(_paused ? "RecordingPause_Resume" : "RecordingPause_Pause"));
        string help = UiText.Get(_paused ? "RecordingPause_ResumeHelp" : "RecordingPause_PauseHelp");
        // Read on every refresh so a chord changed in settings shows on the next state change;
        // an unassigned chord adds nothing.
        Hotkey shortcut = ReadPauseHotkey();
        string shortcutText = shortcut.IsAssigned ? shortcut.ToString() : string.Empty;
        if (shortcutText.Length > 0)
        {
            help = UiText.Format("RecordingPauseHotkey_HelpWithShortcut", help, shortcutText);
        }

        _pauseButton.ToolTip = help;
        AutomationProperties.SetName(
            _pauseButton,
            UiText.Get(_paused ? "RecordingPause_ResumeName" : "RecordingPause_PauseName"));
        AutomationProperties.SetHelpText(_pauseButton, help);
        AutomationProperties.SetAcceleratorKey(_pauseButton, shortcutText);
    }

    private void UpdateRegionFrameBrush(Border frame)
    {
        frame.BorderBrush = _paused
            ? CreatePausedFrameBrush()
            : frame.IsKeyboardFocusWithin
                ? TryBrush("Border.Focus", Color.FromRgb(0x97, 0x89, 0xFF))
                : TryBrush("Border.Accent", Color.FromRgb(0x7C, 0x6C, 0xFF));
    }

    /// <summary>
    /// Diagonal warning/surface stripes for the paused outline. The pattern, not only the
    /// colour, tells paused from recording, including in the high-contrast theme.
    /// </summary>
    internal static Brush CreatePausedFrameBrush()
    {
        Color stripe = TryBrush("State.Warning", Color.FromRgb(0xF5, 0xB9, 0x42)) is SolidColorBrush warning
            ? warning.Color
            : Color.FromRgb(0xF5, 0xB9, 0x42);
        Color gap = TryBrush("Surface.Floating", Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B)) is SolidColorBrush surface
            ? surface.Color
            : Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B);
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(PausedStripePeriod, PausedStripePeriod),
        };
        brush.GradientStops.Add(new GradientStop(stripe, 0));
        brush.GradientStops.Add(new GradientStop(stripe, 0.5));
        brush.GradientStops.Add(new GradientStop(gap, 0.5));
        brush.GradientStops.Add(new GradientStop(gap, 1));
        brush.Freeze();
        return brush;
    }

    private async void StopRecording()
    {
        if (_stopping || _recorder is null)
        {
            return;
        }

        _stopping = true;
        Opacity = 1;
        _controlStrip.Visibility = Visibility.Visible;
        _showTimeAvailable = false;
        _pauseButton.IsEnabled = false;
        Stopping?.Invoke(this, EventArgs.Empty);
        _elapsedTimer?.Stop();
        _primaryButton.IsEnabled = false;
        SetButtonContent(_primaryButton, glyph: null, UiText.Get("Text_4E7752D53B90"));
        UpdateSessionButtons();
        _completionProgress.Visibility = Visibility.Visible;
        _statusText.Text = UiText.Get("Text_542965B456C7");
        AnnounceStatus();

        RegionRecorder recorder = _recorder;
        RecordingResult? result = null;
        Exception? stopFailure = null;
        try
        {
            // Encoder finalisation can take seconds for a long/high-resolution clip. Run the
            // blocking join and MP4 finalise away from the dispatcher so the timer/status,
            // accessibility live region and window chrome remain responsive throughout.
            result = await Task.Run(recorder.Stop);
        }
        catch (Exception ex)
        {
            stopFailure = ex;
            _log.LogError(ex, "Stopping the recording failed");
        }
        finally
        {
            recorder.Dispose();
        }

        // Native hotkey callbacks can enter on the owning STA without installing a WPF
        // SynchronizationContext. Marshal explicitly after the encoder join in every case.
        await Dispatcher.InvokeAsync(() =>
        {
            // The recorder has stopped capturing; close wall clock so final frames retain the clock.
            CloseWallClock();

            if (ReferenceEquals(_recorder, recorder))
            {
                _recorder = null;
            }

            _finished = true;
            if (result is not null)
            {
                _completionPending = true;
                ShowCompletionStatus(UiText.Get("Text_528544494302"));
                RecordingFinished?.Invoke(this, result);
            }
            else
            {
                Failed?.Invoke(
                    this,
                    new RecordingFailedEventArgs(
                        stopFailure ?? new InvalidOperationException("The recorder returned no completed clip.")));
            }

            if (result is null)
            {
                Close();
            }
        });
    }

    private void CancelSession()
    {
        if (_stopping)
        {
            return;
        }
        StopCountdown();

        if (IsRecording)
        {
            // Treat an explicit cancel during recording as a stop that still yields the clip;
            // discarding a recording the user made is worse than keeping it.
            StopRecording();
            return;
        }

        CloseWallClock();
        Cancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_stopping || _completionPending)
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (IsRecording)
                {
                    StopRecording();
                }
                else
                {
                    CancelSession();
                }

                break;
            case Key.Enter or Key.Space when !IsRecording && _countdownTimer is null:
                if (_moreButton.IsKeyboardFocusWithin || _cancelButton.IsKeyboardFocusWithin)
                {
                    break;
                }

                e.Handled = true;
                OnPrimaryClicked();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down
                when !IsRecording && _countdownTimer is null && _regionFrame.IsKeyboardFocusWithin:
                double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10.0 : 1.0;
                double deltaX = e.Key switch
                {
                    Key.Left => -step,
                    Key.Right => step,
                    _ => 0,
                };
                double deltaY = e.Key switch
                {
                    Key.Up => -step,
                    Key.Down => step,
                    _ => 0,
                };
                NudgeRegion(deltaX, deltaY);
                e.Handled = true;
                break;
        }
    }

    private void NudgeRegion(double deltaX, double deltaY)
    {
        RectD virtualPixels = MonitorEnumerator.GetVirtualDesktopBounds();
        _screenRegion = new RectD(
                _screenRegion.Left + deltaX,
                _screenRegion.Top + deltaY,
                _screenRegion.Width,
                _screenRegion.Height)
            .ClampTo(virtualPixels)
            .ToPixelBounds();
        ShowReadyPrimary();
        ApplyPhysicalLayout(positionHwnd: true);
    }

    private void SetRegionClickThrough(bool enabled)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Only the transparent region interior should pass clicks through; the control
        // strip must stay interactive. WPF cannot make part of one HWND click-through, so
        // we keep the strip usable by leaving the whole window hit-testable and instead
        // rely on the transparent interior: transparent pixels in an AllowsTransparency
        // window already pass mouse events to the window beneath. No extended style change
        // is needed, so this is a no-op kept for clarity and future tuning.
        _ = enabled;
    }

    private string FormatReadyStatus()
    {
        int w = (int)Math.Round(_screenRegion.Width);
        int h = (int)Math.Round(_screenRegion.Height);
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{w}×{h} · {_settings.TargetFps}fps");
    }

    private void AnnounceStatus()
    {
        AutomationProperties.SetName(_statusText, _statusText.Text);
    }

    private double ResolveScale()
    {
        MonitorInfo monitor = MonitorEnumerator.GetFromPoint(_screenRegion.Center);
        return monitor.ScaleFactor <= 0 ? 1.0 : monitor.ScaleFactor;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing while Media Foundation is writing its MP4 trailer can strand an otherwise
        // valid recording. Keep this short critical section visible and non-dismissible; the
        // window closes itself as soon as Stop() has returned and the private capture file is
        // complete. Application shutdown still reaches OnClosed after that boundary.
        if ((_stopping && !_finished) || _completionPending)
        {
            e.Cancel = true;
            _statusText.Text = UiText.Get("Text_3024E9CAD0E5");
            AnnounceStatus();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseWallClock();
        KeyDown -= OnKeyDown;
        SourceInitialized -= OnSourceInitialized;
        ContentRendered -= OnContentRendered;
        DpiChanged -= OnDpiChanged;
        _countdownTimer?.Stop();
        _elapsedTimer?.Stop();

        if (_recorder is not null)
        {
            try
            {
                if (_recorder.IsRecording && !_finished)
                {
                    // Application exit or a forced close while recording or paused. Stop still
                    // finalises the private MP4 (a paused span is simply not part of it); the
                    // owner keeps that file for startup recovery instead of discarding it.
                    _ = _recorder.Stop();
                    FinalizedOnClose = true;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Recorder cleanup on window close failed");
            }
            finally
            {
                _recorder.Dispose();
                _recorder = null;
            }
        }

        base.OnClosed(e);
    }

    private void SetWallClockEnabled(bool enabled)
    {
        if (enabled)
        {
            if (_wallClock is null && !_stopping && !_finished)
            {
                double scale = ResolveWindowScale();
                _wallClock = new RecordingWallClockWindow(
                    _screenRegion,
                    scale,
                    _settings.TargetFps,
                    _wallClockFontSize,
                    _wallClockRelativeX,
                    _wallClockRelativeY);
                _wallClock.Closed += OnWallClockClosed;
                // Capture is parked while paused, so the badge can be drawn immediately. During a
                // pause/resume transition the transition itself settles the clock state.
                if (_paused && !_pauseTransition)
                {
                    _wallClock.SetPaused(true);
                }

                _wallClock.Show();
            }
        }
        else
        {
            CloseWallClock();
        }
    }

    private void OnWallClockClosed(object? sender, EventArgs e)
    {
        if (sender is RecordingWallClockWindow clock)
        {
            clock.Closed -= OnWallClockClosed;
            _wallClockFontSize = clock.CurrentFontSize;
            _wallClockRelativeX = clock.RelativeX;
            _wallClockRelativeY = clock.RelativeY;
            if (ReferenceEquals(_wallClock, clock))
            {
                _wallClock = null;
            }

            if (_showTime && !_stopping && !_finished)
            {
                _showTime = false;
            }
        }
    }

    private void CloseWallClock()
    {
        if (_wallClock is not null)
        {
            RecordingWallClockWindow clock = _wallClock;
            _wallClock = null;
            clock.Closed -= OnWallClockClosed;
            _wallClockFontSize = clock.CurrentFontSize;
            _wallClockRelativeX = clock.RelativeX;
            _wallClockRelativeY = clock.RelativeY;
            clock.CloseClock();
        }
    }

    private sealed class AccessibleRegionFrame : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() =>
            new RegionFrameAutomationPeer(this);
    }

    private sealed class RegionFrameAutomationPeer(AccessibleRegionFrame owner)
        : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "RecordingRegionFrame";

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Pane;

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }

    private static Brush TryBrush(string key, Color fallback) => ThemeBrushes.Follow(key, fallback);

    private static FontFamily TryFont(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Segoe UI");

    private static Style? TryStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}

internal sealed class RecordingFailedEventArgs(Exception exception) : EventArgs
{
    internal Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}

internal readonly record struct RecordingControlLayout(
    RectD WindowBounds,
    RectD FrameBounds,
    RectD PaletteBounds,
    bool PaletteOverlapsRegion);

/// <summary>
/// Pure physical-pixel layout used by the recording controls. Keeping it independent from WPF
/// makes negative origins, mixed-DPI scale factors and monitor-edge flipping deterministic tests.
/// </summary>
internal static class RecordingControlLayoutPlanner
{
    internal static RecordingControlLayout Plan(
        RectD screenRegion,
        RectD paletteWorkArea,
        double dpiScale,
        double borderDip,
        double stripHeightDip,
        double minimumStripWidthDip)
    {
        RectD region = screenRegion.Normalized().ToPixelBounds();
        RectD desktop = paletteWorkArea.Normalized().ToPixelBounds();
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        double borderPx = Math.Max(0, borderDip) * scale;
        double stripHeightPx = Math.Min(desktop.Height, Math.Max(1, stripHeightDip * scale));
        RectD frame = region.Inflate(borderPx);

        double stripWidthPx = Math.Min(
            desktop.Width,
            Math.Max(1, minimumStripWidthDip * scale));
        double stripLeft = Math.Clamp(
            region.Center.X - (stripWidthPx / 2),
            desktop.Left,
            Math.Max(desktop.Left, desktop.Right - stripWidthPx));

        double below = frame.Bottom;
        double above = frame.Top - stripHeightPx;
        double stripTop = below >= desktop.Top && below + stripHeightPx <= desktop.Bottom
            ? below
            : above >= desktop.Top && above + stripHeightPx <= desktop.Bottom
                ? above
                : Math.Clamp(
                    frame.Top,
                    desktop.Top,
                    Math.Max(desktop.Top, desktop.Bottom - stripHeightPx));

        var palette = new RectD(stripLeft, stripTop, stripWidthPx, stripHeightPx);
        RectD window = Union(frame, palette);
        return new RecordingControlLayout(
            window,
            frame,
            palette,
            RectanglesOverlap(palette, region));
    }

    private static RectD Union(RectD left, RectD right) => new(
        Math.Min(left.Left, right.Left),
        Math.Min(left.Top, right.Top),
        Math.Max(left.Right, right.Right) - Math.Min(left.Left, right.Left),
        Math.Max(left.Bottom, right.Bottom) - Math.Min(left.Top, right.Top));

    private static bool RectanglesOverlap(RectD left, RectD right) =>
        left.Left < right.Right
        && left.Right > right.Left
        && left.Top < right.Bottom
        && left.Bottom > right.Top;
}
