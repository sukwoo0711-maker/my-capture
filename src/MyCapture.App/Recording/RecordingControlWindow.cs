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

    // The control strip must never be narrower than this, or (for a small capture region) the
    // record/stop button, status text and elapsed timer overlap. The window widens to this
    // minimum and the region outline stays centred at its true size.
    private const double MinStripWidth = 460.0;

    private RectD _screenRegion;
    private readonly RecordingSettings _settings;
    private readonly Func<RegionRecorder> _recorderFactory;
    private readonly Func<string> _outputPathFactory;
    private readonly ILogger _log;

    private readonly Button _primaryButton;
    private readonly TextBlock _statusText;
    private readonly TextBlock _timerText;
    private readonly Border _regionFrame;
    private readonly Border _controlStrip;
    private readonly Canvas _root;

    private RegionRecorder? _recorder;
    private DispatcherTimer? _elapsedTimer;
    private DateTimeOffset _startedAt;
    private DispatcherTimer? _countdownTimer;
    private int _countdownRemaining;
    private bool _stopping;
    private bool _finished;
    private bool _applyingPhysicalLayout;
    private bool _captureExclusionApplied;
    private bool _paletteOverlapsRegion;

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

        Title = "MyCapture — 영역 녹화";
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

        _controlStrip = BuildControlStrip(out _primaryButton, out _statusText, out _timerText);
        _controlStrip.MinWidth = MinStripWidth;
        _root.Children.Add(_controlStrip);

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

    /// <summary>External stop request (e.g. pressing Ctrl+X again).</summary>
    internal void RequestStop() => StopRecording();

    private Border BuildRegionFrame()
    {
        var frame = new AccessibleRegionFrame
        {
            BorderBrush = TryBrush("Border.Accent", Color.FromRgb(0xFF, 0xD4, 0x00)),
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
        frame.GotKeyboardFocus += (_, _) =>
            frame.BorderBrush = TryBrush("Border.Focus", Color.FromRgb(0xFF, 0xE1, 0x4D));
        frame.LostKeyboardFocus += (_, _) =>
            frame.BorderBrush = TryBrush("Border.Accent", Color.FromRgb(0xFF, 0xD4, 0x00));

        AutomationProperties.SetName(frame, "녹화 영역 테두리 (드래그로 이동)");
        AutomationProperties.SetHelpText(
            frame,
            "Tab으로 선택한 뒤 방향키로 이동합니다. Shift와 함께 누르면 10픽셀씩 이동합니다.");
        return frame;
    }

    private Border BuildControlStrip(out Button primary, out TextBlock status, out TextBlock timer)
    {
        var panel = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        primary = new Button
        {
            Content = _settings.UseStartDelay ? "지연 후 녹화" : "녹화 시작",
            MinWidth = 108,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Primary"),
        };
        primary.Click += (_, _) => OnPrimaryClicked();
        AutomationProperties.SetName(primary, "녹화 시작 또는 정지");
        Grid.SetColumn(primary, 0);
        panel.Children.Add(primary);

        status = new TextBlock
        {
            Text = FormatReadyStatus(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            Foreground = TryBrush("Text.Secondary", Colors.LightGray),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(status, 1);
        panel.Children.Add(status);

        timer = new TextBlock
        {
            Text = "00:00",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = TryBrush("Text.Primary", Colors.White),
            FontFamily = TryFont("Font.Mono"),
            FontSize = 15,
        };
        AutomationProperties.SetName(timer, "녹화 경과 시간");
        Grid.SetColumn(timer, 2);
        panel.Children.Add(timer);

        var cancel = new Button
        {
            Content = "취소",
            MinWidth = 64,
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryStyle("Button.Ghost"),
        };
        cancel.Click += (_, _) => CancelSession();
        AutomationProperties.SetName(cancel, "녹화 취소");
        Grid.SetColumn(cancel, 3);
        panel.Children.Add(cancel);

        return new Border
        {
            Background = TryBrush("Surface.Floating", Color.FromArgb(0xF2, 0x2A, 0x24, 0x1C)),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x40, 0x38, 0x2C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Effect = Application.Current?.TryFindResource("Shadow.Floating") as System.Windows.Media.Effects.Effect,
            Child = panel,
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
            RectD virtualPixels = MonitorEnumerator.GetVirtualDesktopBounds();
            RecordingControlLayout layout = RecordingControlLayoutPlanner.Plan(
                _screenRegion,
                virtualPixels,
                scale,
                BorderThicknessPx,
                StripHeight,
                MinStripWidth);
            RectD frameBox = layout.FrameBounds;
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
            _controlStrip.Height = StripHeight;
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
            _statusText.Text = FormatReadyStatus();
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
        _statusText.Text = string.Create(CultureInfo.CurrentCulture, $"{_countdownRemaining}초 후 시작");
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

            _statusText.Text = string.Create(CultureInfo.CurrentCulture, $"{_countdownRemaining}초 후 시작");
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
            _recorder = _recorderFactory();
            string output = _outputPathFactory();
            _recorder.Start(_screenRegion, output, _settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start recording");
            _statusText.Text = "녹화를 시작할 수 없습니다";
            _statusText.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
            AnnounceStatus();
            _recorder?.Dispose();
            _recorder = null;
            _regionFrame.Visibility = Visibility.Visible;
            Opacity = 1;
            return;
        }

        _startedAt = DateTimeOffset.Now;
        _primaryButton.Content = "녹화 정지";
        _primaryButton.Style = TryStyle("Button.Danger");
        _statusText.Text = "녹화 중 · Esc 또는 정지로 종료";
        _statusText.Foreground = TryBrush("State.Danger", Colors.OrangeRed);
        AnnounceStatus();

        // Make the region interior click-through so the recorded app stays usable.
        SetRegionClickThrough(true);
        _regionFrame.Visibility = Visibility.Collapsed;
        if (!_captureExclusionApplied && _paletteOverlapsRegion)
        {
            // Supported Windows 11 builds exclude the palette through display affinity. If that
            // OS contract unexpectedly fails and no outside placement exists, hide the palette
            // during capture rather than burn controls into the user's video. Ctrl+X still
            // stops the recording.
            Opacity = 0;
        }

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _elapsedTimer.Tick += (_, _) => UpdateTimer();
        _elapsedTimer.Start();
    }

    private void UpdateTimer()
    {
        TimeSpan elapsed = DateTimeOffset.Now - _startedAt;
        _timerText.Text = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    private async void StopRecording()
    {
        if (_stopping || _recorder is null)
        {
            return;
        }

        _stopping = true;
        Opacity = 1;
        Stopping?.Invoke(this, EventArgs.Empty);
        _elapsedTimer?.Stop();
        _primaryButton.IsEnabled = false;
        _statusText.Text = "파일 확정 중… 창을 닫지 마세요";
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
            if (ReferenceEquals(_recorder, recorder))
            {
                _recorder = null;
            }
        }

        _finished = true;
        if (result is not null)
        {
            RecordingFinished?.Invoke(this, result);
        }
        else
        {
            Failed?.Invoke(
                this,
                new RecordingFailedEventArgs(
                    stopFailure ?? new InvalidOperationException("The recorder returned no completed clip.")));
        }

        Close();
    }

    private void CancelSession()
    {
        StopCountdown();

        if (IsRecording)
        {
            // Treat an explicit cancel during recording as a stop that still yields the clip;
            // discarding a recording the user made is worse than keeping it.
            StopRecording();
            return;
        }

        Cancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
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
        _statusText.Text = FormatReadyStatus();
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
        if (_stopping && !_finished)
        {
            e.Cancel = true;
            _statusText.Text = "파일 확정 중… 완료되면 자동으로 닫힙니다";
            AnnounceStatus();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
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
                    _recorder.Stop();
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

    private static Brush TryBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

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
        RectD virtualDesktop,
        double dpiScale,
        double borderDip,
        double stripHeightDip,
        double minimumStripWidthDip)
    {
        RectD region = screenRegion.Normalized().ToPixelBounds();
        RectD desktop = virtualDesktop.Normalized().ToPixelBounds();
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        double borderPx = Math.Max(0, borderDip) * scale;
        double stripHeightPx = Math.Max(1, stripHeightDip * scale);
        RectD frame = region.Inflate(borderPx);

        double stripWidthPx = Math.Min(
            desktop.Width,
            Math.Max(frame.Width, Math.Max(1, minimumStripWidthDip * scale)));
        double stripLeft = Math.Clamp(
            region.Center.X - (stripWidthPx / 2),
            desktop.Left,
            Math.Max(desktop.Left, desktop.Right - stripWidthPx));

        double below = frame.Bottom;
        double above = frame.Top - stripHeightPx;
        double stripTop = below + stripHeightPx <= desktop.Bottom
            ? below
            : above >= desktop.Top
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
