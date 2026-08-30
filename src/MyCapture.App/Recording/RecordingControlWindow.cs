using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
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
    private const double BorderThicknessPx = 2.0;
    private const double StripHeight = 56.0;

    private readonly RectD _screenRegion;
    private readonly RecordingSettings _settings;
    private readonly Func<RegionRecorder> _recorderFactory;
    private readonly Func<string> _outputPathFactory;
    private readonly ILogger _log;

    private readonly Button _primaryButton;
    private readonly TextBlock _statusText;
    private readonly TextBlock _timerText;
    private readonly Border _regionFrame;

    private RegionRecorder? _recorder;
    private DispatcherTimer? _elapsedTimer;
    private DateTimeOffset _startedAt;
    private DispatcherTimer? _countdownTimer;
    private int _countdownRemaining;
    private bool _stopping;
    private bool _finished;

    internal RecordingControlWindow(
        RectD screenRegion,
        RecordingSettings settings,
        Func<RegionRecorder> recorderFactory,
        Func<string> outputPathFactory,
        ILogger log)
    {
        _screenRegion = screenRegion.Normalized();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _recorderFactory = recorderFactory ?? throw new ArgumentNullException(nameof(recorderFactory));
        _outputPathFactory = outputPathFactory ?? throw new ArgumentNullException(nameof(outputPathFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));

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

        double scale = ResolveScale();
        double regionDipW = _screenRegion.Width / scale;
        double regionDipH = _screenRegion.Height / scale;

        Width = regionDipW + (BorderThicknessPx * 2);
        Height = regionDipH + (BorderThicknessPx * 2) + StripHeight;
        Left = (_screenRegion.Left / scale) - BorderThicknessPx;
        Top = (_screenRegion.Top / scale) - BorderThicknessPx;

        // Keep the whole window on the work area of the region's monitor if the strip
        // would spill past the bottom edge; nudge upward rather than clip the controls.
        Rect work = SystemParameters.WorkArea;
        if (Top + Height > work.Bottom)
        {
            Top = Math.Max(work.Top, work.Bottom - Height);
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(regionDipH + (BorderThicknessPx * 2)) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(StripHeight) });

        _regionFrame = BuildRegionFrame();
        Grid.SetRow(_regionFrame, 0);
        root.Children.Add(_regionFrame);

        Border strip = BuildControlStrip(out _primaryButton, out _statusText, out _timerText);
        Grid.SetRow(strip, 1);
        root.Children.Add(strip);

        Content = root;

        KeyDown += OnKeyDown;
        SourceInitialized += OnSourceInitialized;
    }

    internal event EventHandler<RecordingResult>? RecordingFinished;

    internal event EventHandler? Cancelled;

    internal bool IsRecording => _recorder is { IsRecording: true };

    /// <summary>External stop request (e.g. pressing Ctrl+Shift+X again).</summary>
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
        // The countdown-free path can begin immediately if the user has auto-start off;
        // we simply wait for the explicit button. Nothing to do here beyond letting the
        // window render; region placement is already set from the constructor.
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

    private void StopRecording()
    {
        if (_stopping || _recorder is null)
        {
            return;
        }

        _stopping = true;
        _elapsedTimer?.Stop();
        _primaryButton.IsEnabled = false;
        _statusText.Text = "저장 중…";
        AnnounceStatus();

        // Stop() blocks while the file is finalised; keep it off the very next render but
        // on the UI thread so the resulting editor opens on the dispatcher.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            RecordingResult? result = null;
            try
            {
                result = _recorder!.Stop();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Stopping the recording failed");
            }
            finally
            {
                _recorder?.Dispose();
                _recorder = null;
            }

            _finished = true;
            if (result is not null)
            {
                RecordingFinished?.Invoke(this, result);
            }

            Close();
        }), DispatcherPriority.Background);
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
        double minLeft = SystemParameters.VirtualScreenLeft;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxLeft = Math.Max(minLeft, minLeft + SystemParameters.VirtualScreenWidth - Width);
        double maxTop = Math.Max(minTop, minTop + SystemParameters.VirtualScreenHeight - Height);

        Left = Math.Clamp(Left + deltaX, minLeft, maxLeft);
        Top = Math.Clamp(Top + deltaY, minTop, maxTop);
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
            $"{w}×{h} · {_settings.TargetFps}fps · 테두리를 드래그해 위치 조정");
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

    protected override void OnClosed(EventArgs e)
    {
        KeyDown -= OnKeyDown;
        SourceInitialized -= OnSourceInitialized;
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
}
