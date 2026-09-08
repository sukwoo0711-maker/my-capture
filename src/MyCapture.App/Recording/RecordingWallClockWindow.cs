using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Recording;

/// <summary>
/// A frameless, always-on-top clock window that displays the local date/time with millisecond precision
/// inside the recording region. It is intentionally included in the screen capture (no display affinity
/// exclusion is applied) and clamped strictly to the recording physical bounds.
/// </summary>
internal sealed class RecordingWallClockWindow : Window
{
    internal const double MinFontSize = 10.0;
    internal const double MaxFontSize = 28.0;
    internal const double DefaultFontSize = 13.0;
    private const double FontSizeStep = 1.0;

    internal const int MinFps = 1;
    internal const int MaxFps = 30;
    internal const double MinIntervalMs = 33.0;
    internal const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private RectD _recordingRegion;
    private double _dpiScale;
    private readonly int _targetFps;
    private double _currentFontSize;
    private double _relativeX;
    private double _relativeY;

    private readonly Border _container;
    private readonly Viewbox _viewbox;
    private readonly TextBlock _clockText;
    private DispatcherTimer? _timer;
    private IntPtr _hwnd;
    private bool _dragging;
    private double _dragAnchorX;
    private double _dragAnchorY;
    private int _lastDragCursorX = int.MinValue;
    private int _lastDragCursorY = int.MinValue;
    private bool _isClosing;

    internal RecordingWallClockWindow(
        RectD recordingRegion,
        double dpiScale,
        int targetFps,
        double initialFontSize = DefaultFontSize,
        double? initialRelativeX = null,
        double? initialRelativeY = null)
    {
        _recordingRegion = recordingRegion.Normalized().ToPixelBounds();
        _dpiScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        _targetFps = Math.Clamp(targetFps, MinFps, MaxFps);
        _currentFontSize = Math.Clamp(initialFontSize, MinFontSize, MaxFontSize);

        if (initialRelativeX.HasValue && initialRelativeY.HasValue)
        {
            _relativeX = initialRelativeX.Value;
            _relativeY = initialRelativeY.Value;
        }
        else
        {
            _relativeX = 8.0 * _dpiScale;
            _relativeY = 8.0 * _dpiScale;
        }

        Title = "MyCapture — 녹화 시계";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.SizeAll;
        FluidMotion.SetWindowEntrance(this, false);

        _clockText = new TextBlock
        {
            Text = GetCurrentTimeString(),
            FontFamily = TryFont("Font.Mono"),
            FontSize = _currentFontSize,
            FontWeight = FontWeights.Medium,
            Foreground = TryBrush("Text.Badge", Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };

        _viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _clockText,
        };

        _container = new Border
        {
            Background = TryBrush("Surface.Floating", Color.FromArgb(0xF2, 0x15, 0x1E, 0x2B)),
            BorderBrush = TryBrush("Border.Subtle", Color.FromRgb(0x2B, 0x3A, 0x50)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Child = _viewbox,
            ToolTip = "녹화 시계 (드래그로 이동, 마우스 휠로 크기 조절)",
        };

        AutomationProperties.SetName(_container, "녹화 시계");
        AutomationProperties.SetHelpText(
            _container,
            "현재 녹화 시각 표시 (yyyy-MM-dd HH:mm:ss.fff). 마우스 왼쪽 버튼으로 드래그하여 이동하고 마우스 휠로 크기를 조절합니다.");

        Content = _container;

        RepositionAndPlace();

        SourceInitialized += OnSourceInitialized;
        DpiChanged += OnDpiChanged;
    }

    internal double CurrentFontSize => _currentFontSize;
    internal double RelativeX => _relativeX;
    internal double RelativeY => _relativeY;
    internal RectD RecordingRegion => _recordingRegion;
    internal double DpiScale => _dpiScale;
    internal int TargetFps => _targetFps;
    internal bool IsClosing => _isClosing;
    internal DispatcherTimer? Timer => _timer;
    internal bool IsTimerRunning => _timer is not null && _timer.IsEnabled;
    internal TimeSpan? TimerInterval => _timer?.Interval;
    internal string CurrentText => _clockText.Text;
    internal Viewbox Viewbox => _viewbox;
    internal Border Container => _container;
    internal TextBlock TextBlock => _clockText;
    internal RectD PhysicalBounds => new(Left * _dpiScale, Top * _dpiScale, Width * _dpiScale, Height * _dpiScale);
    internal double PhysicalWidth => Width * _dpiScale;
    internal double PhysicalHeight => Height * _dpiScale;
    internal double PhysicalLeft => Left * _dpiScale;
    internal double PhysicalTop => Top * _dpiScale;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != IntPtr.Zero)
        {
            // Capture exclusion is intentionally omitted so the clock is included in the recording.
            RepositionAndPlace();
            StartTimer();
        }
    }

    internal static TimeSpan CalculateTimerInterval(int targetFps)
    {
        int boundedFps = Math.Clamp(targetFps, MinFps, MaxFps);
        double intervalMs = Math.Max(MinIntervalMs, 1000.0 / boundedFps);
        return TimeSpan.FromMilliseconds(intervalMs);
    }

    internal void StartTimer()
    {
        if (_timer is not null || _isClosing)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = CalculateTimerInterval(_targetFps),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    internal void StopTimer()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _clockText.Text = GetCurrentTimeString();
    }

    internal static string FormatTime(DateTime dateTime) =>
        dateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    internal static string GetCurrentTimeString() =>
        FormatTime(DateTime.Now);

    internal void UpdateTime(DateTime dateTime)
    {
        if (_isClosing)
        {
            return;
        }

        _clockText.Text = FormatTime(dateTime);
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        double newScale = e.NewDpi.DpiScaleX > 0 ? e.NewDpi.DpiScaleX : 1.0;
        ApplyDpiScale(newScale);
    }

    internal void ApplyDpiScale(double newDpiScale)
    {
        _dpiScale = double.IsFinite(newDpiScale) && newDpiScale > 0 ? newDpiScale : 1.0;
        if (_hwnd != IntPtr.Zero)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(RepositionAndPlace));
        }
        else
        {
            RepositionAndPlace();
        }
    }

    internal void UpdateRecordingRegion(RectD newRegion, double dpiScale)
    {
        _recordingRegion = newRegion.Normalized().ToPixelBounds();
        _dpiScale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1.0;
        RepositionAndPlace();
    }

    private void UpdateContainerPadding(double widthDip, double heightDip)
    {
        double padX = Math.Clamp((widthDip - 10.0) / 4.0, 0.0, 8.0);
        double padY = Math.Clamp((heightDip - 10.0) / 4.0, 0.0, 4.0);
        _container.Padding = new Thickness(padX, padY, padX, padY);
    }

    private void CalculateLayout(
        out double widthDip,
        out double heightDip,
        out double leftPx,
        out double topPx,
        out double widthPx,
        out double heightPx)
    {
        _container.Padding = new Thickness(8, 4, 8, 4);
        _container.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double desiredWidthDip = Math.Max(100.0, Math.Ceiling(_container.DesiredSize.Width) + 4.0);
        double desiredHeightDip = Math.Max(24.0, Math.Ceiling(_container.DesiredSize.Height));

        double regionWidthPx = Math.Max(1.0, _recordingRegion.Width);
        double regionHeightPx = Math.Max(1.0, _recordingRegion.Height);

        double desiredWidthPx = Math.Max(1.0, Math.Round(desiredWidthDip * _dpiScale));
        double desiredHeightPx = Math.Max(1.0, Math.Round(desiredHeightDip * _dpiScale));

        widthPx = Math.Max(1.0, Math.Min(desiredWidthPx, regionWidthPx));
        heightPx = Math.Max(1.0, Math.Min(desiredHeightPx, regionHeightPx));

        widthDip = widthPx / _dpiScale;
        heightDip = heightPx / _dpiScale;

        UpdateContainerPadding(widthDip, heightDip);

        double targetLeft = _recordingRegion.Left + _relativeX;
        double targetTop = _recordingRegion.Top + _relativeY;

        double maxLeft = Math.Max(_recordingRegion.Left, _recordingRegion.Right - widthPx);
        leftPx = Math.Clamp(targetLeft, _recordingRegion.Left, maxLeft);

        double maxTop = Math.Max(_recordingRegion.Top, _recordingRegion.Bottom - heightPx);
        topPx = Math.Clamp(targetTop, _recordingRegion.Top, maxTop);
    }

    private void RepositionAndPlace()
    {
        CalculateLayout(
            out double widthDip,
            out double heightDip,
            out double leftPx,
            out double topPx,
            out double widthPx,
            out double heightPx);

        _relativeX = leftPx - _recordingRegion.Left;
        _relativeY = topPx - _recordingRegion.Top;

        Width = widthDip;
        Height = heightDip;
        Left = leftPx / _dpiScale;
        Top = topPx / _dpiScale;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var target = new RectD(leftPx, topPx, widthPx, heightPx);
        PhysicalWindowPositioner.PlaceTopmost(_hwnd, target);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            _dragging = true;
            Point anchor = e.GetPosition(this);
            _dragAnchorX = anchor.X * _dpiScale;
            _dragAnchorY = anchor.Y * _dpiScale;
            _lastDragCursorX = int.MinValue;
            _lastDragCursorY = int.MinValue;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _hwnd == IntPtr.Zero)
        {
            return;
        }

        (int cursorX, int cursorY) = WindowStyleFacade.GetCursorPosition();
        if (cursorX == _lastDragCursorX && cursorY == _lastDragCursorY)
        {
            return;
        }

        _lastDragCursorX = cursorX;
        _lastDragCursorY = cursorY;

        double targetLeft = cursorX - _dragAnchorX;
        double targetTop = cursorY - _dragAnchorY;

        (int currentLeft, int currentTop, int right, int bottom) = WindowStyleFacade.GetWindowBounds(_hwnd);
        double width = right - currentLeft;
        double height = bottom - currentTop;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double maxLeft = Math.Max(_recordingRegion.Left, _recordingRegion.Right - width);
        double clampedLeft = Math.Clamp(targetLeft, _recordingRegion.Left, maxLeft);

        double maxTop = Math.Max(_recordingRegion.Top, _recordingRegion.Bottom - height);
        double clampedTop = Math.Clamp(targetTop, _recordingRegion.Top, maxTop);

        _relativeX = clampedLeft - _recordingRegion.Left;
        _relativeY = clampedTop - _recordingRegion.Top;

        var target = new RectD(clampedLeft, clampedTop, width, height);
        PhysicalWindowPositioner.Move(_hwnd, target);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            SyncDipCoordinates();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            _dragging = false;
            SyncDipCoordinates();
        }
    }

    private void SyncDipCoordinates()
    {
        if (_hwnd != IntPtr.Zero)
        {
            (int left, int top, _, _) = WindowStyleFacade.GetWindowBounds(_hwnd);
            Left = left / _dpiScale;
            Top = top / _dpiScale;
            _relativeX = left - _recordingRegion.Left;
            _relativeY = top - _recordingRegion.Top;
        }
    }

    internal void AdjustFontSize(int notches)
    {
        if (notches == 0)
        {
            return;
        }

        double newSize = Math.Clamp(_currentFontSize + (notches * FontSizeStep), MinFontSize, MaxFontSize);
        if (Math.Abs(newSize - _currentFontSize) > 1e-4)
        {
            _currentFontSize = newSize;
            _clockText.FontSize = _currentFontSize;
            RepositionAndPlace();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = e.Delta / 120;
        if (notches != 0)
        {
            AdjustFontSize(notches);
            e.Handled = true;
        }
    }

    internal void CloseClock()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        StopTimer();
        SourceInitialized -= OnSourceInitialized;
        DpiChanged -= OnDpiChanged;
        try
        {
            Close();
        }
        catch
        {
            // Suppress if already closed
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        StopTimer();
        SourceInitialized -= OnSourceInitialized;
        DpiChanged -= OnDpiChanged;
        base.OnClosed(e);
    }

    private static Brush TryBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static FontFamily TryFont(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Cascadia Mono, Consolas, Malgun Gothic, monospace");
}
