using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// A borderless, top-most, independent window that pins one frozen image to the screen,
/// the way Snipaste's pinned screenshots behave.
/// </summary>
/// <remarks>
/// <para>
/// The window is a thin shell over <see cref="PinViewState"/> and <see cref="PinGeometry"/>:
/// pointer and keyboard input are turned into calls on the pure state, and the results
/// (size, position, opacity, click-through) are mirrored onto the live window. All the
/// clampable arithmetic lives in the core layer and is unit-tested there; this class only
/// owns the WPF plumbing that cannot run without a message pump.
/// </para>
/// <para>
/// Coordinates are device-independent (DIP) throughout, which under PerMonitorV2 is exactly
/// what WPF <c>Window.Left/Top/Width/Height</c> expect. The image is drawn with
/// <see cref="BitmapScalingMode.HighQuality"/> so scaled pins stay crisp.
/// </para>
/// </remarks>
internal sealed class PinWindow : Window
{
    private const double GrabMarginDip = 24.0;
    private const double OpacityStep = 0.1;
    private const double ArrowNudgeDip = 1.0;
    private const double ArrowNudgeShiftDip = 10.0;

    /// <summary>How long the bottom feedback overlay stays visible before auto-hiding.</summary>
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromMilliseconds(1000);

    private readonly BitmapSource _image;
    private readonly PinViewState _state;
    private readonly Func<PinSettings> _settings;
    private readonly Image _imageElement;
    private readonly Border _chrome;
    private readonly TextBlock _feedback;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly DispatcherTimer _ctrlClickTimer;

    private bool _dragging;
    private Point _dragAnchor;
    private IntPtr _handle;
    private bool _isClosed;

    internal PinWindow(
        BitmapSource image,
        PinViewState state,
        double initialLeft,
        double initialTop,
        Func<PinSettings>? settings = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? (static () => new PinSettings());

        Title = "MyCapture 화면 고정";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;

        Left = initialLeft;
        Top = initialTop;
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        Opacity = _state.Opacity;

        _imageElement = new Image
        {
            Source = _image,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.HighQuality);

        _feedback = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 5, 10, 5),
            Foreground = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White,
            Background = Application.Current?.TryFindResource("Surface.Floating") as Brush
                ?? new SolidColorBrush(Color.FromArgb(0xE8, 0x11, 0x1A, 0x2B)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        // Image-first chrome: a neutral one-pixel boundary rests quietly and warms to a
        // restrained system-blue on hover, while rounded clipping keeps scaled pins consistent
        // with the rest of the desktop surfaces.
        _chrome = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Application.Current?.TryFindResource("Border.Subtle") as Brush
                ?? new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0x32, 0x4A)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new Grid { Children = { _imageElement, _feedback } },
        };

        Content = _chrome;

        ToolTip =
            "드래그: 이동 · 휠: 확대/축소 · Ctrl+휠: 투명도 · +/-/0 · 방향키 이동 · Ctrl+C 복사 · Esc/Del 닫기";
        AutomationProperties.SetName(this, "MyCapture 화면 고정 창");
        AutomationProperties.SetHelpText(
            this,
            "고정된 화면 이미지입니다. 드래그로 이동, 마우스 휠로 확대·축소, Ctrl+휠로 투명도 조절, +/- 키로 확대·축소, 0 키로 100%, 방향키로 이동, Ctrl+C로 복사, Esc 또는 Delete로 닫습니다. 클릭 통과를 켜면 이 창이 마우스를 받지 않습니다. 되돌리려면 Shift+F3을 두 번 눌러 모든 고정 창을 숨겼다가 다시 표시하면 클릭 통과가 해제됩니다.");

        BuildContextMenu();

        // Auto-hides the bottom feedback overlay so it never lingers on screen. The tick
        // handler runs on the dispatcher thread that owns this window, so touching WPF
        // elements from it is thread-safe.
        _feedbackTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = FeedbackDuration,
        };
        _feedbackTimer.Tick += OnFeedbackTimerTick;

        // Debounces a plain Ctrl+click copy so a following click can turn it into a
        // Ctrl+double-click OCR request instead. The interval is read from settings when the
        // click arrives; the timer is created once here.
        _ctrlClickTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _ctrlClickTimer.Tick += OnCtrlClickTimerTick;

        SourceInitialized += OnSourceInitialized;
        MouseEnter += (_, _) => SetChromeHover(true);
        MouseLeave += (_, _) => SetChromeHover(false);
    }

    /// <summary>Raised when the user asks (via this pin) to close every pin.</summary>
    internal event EventHandler? CloseAllRequested;

    /// <summary>Raised when the user copies this pin's image.</summary>
    internal event EventHandler<BitmapSource>? CopyRequested;

    /// <summary>Raised when the user asks to run OCR on this pin's image.</summary>
    internal event EventHandler<BitmapSource>? OcrRequested;

    /// <summary>The pin's live presentation state.</summary>
    internal PinViewState State => _state;

    /// <summary>The native handle, valid after the window is shown.</summary>
    internal IntPtr Handle => _handle;

    /// <summary>Whether this pin has been closed and should be pruned by the manager.</summary>
    internal bool IsClosed => _isClosed;

    /// <summary>Test hook: whether the bottom feedback overlay is currently visible.</summary>
    internal bool IsFeedbackVisible => _feedback.Visibility == Visibility.Visible;

    /// <summary>Test hook: whether the auto-hide feedback timer is currently running.</summary>
    internal bool IsFeedbackTimerRunning => _feedbackTimer.IsEnabled;

    /// <summary>
    /// Test hook: fires the feedback auto-hide logic immediately, without waiting for the
    /// real timer interval, so tests can prove the overlay hides without brittle sleeps.
    /// </summary>
    internal void ForceFeedbackTimeoutForTest() => OnFeedbackTimerTick(this, EventArgs.Empty);

    /// <summary>Test hook: whether the Ctrl+click copy debounce timer is currently armed.</summary>
    internal bool IsCtrlClickTimerRunning => _ctrlClickTimer.IsEnabled;

    /// <summary>
    /// Test hook: simulates a Ctrl+single-click, arming the copy debounce exactly as the mouse
    /// handler does, so the copy-vs-OCR race behaviour is testable without a live pointer.
    /// </summary>
    internal void SimulateCtrlSingleClickForTest() => StartCtrlClickCopyDebounce();

    /// <summary>
    /// Test hook: simulates a Ctrl+double-click, which cancels any pending copy and requests
    /// OCR — proving the second click wins the race and the clipboard is never touched.
    /// </summary>
    internal void SimulateCtrlDoubleClickForTest()
    {
        _ctrlClickTimer.Stop();
        RequestOcr();
    }

    /// <summary>Test hook: fires the copy debounce immediately as the real timer tick would.</summary>
    internal void ForceCtrlClickTimeoutForTest() => OnCtrlClickTimerTick(this, EventArgs.Empty);

    /// <summary>Applies click-through by toggling extended window styles via the facade.</summary>
    internal void ApplyClickThrough(bool enabled)
    {
        _state.SetClickThrough(enabled);
        if (_handle != IntPtr.Zero)
        {
            WindowStyleFacade.SetClickThrough(_handle, enabled);
        }

        ShowFeedback(enabled ? "클릭 통과 켜짐 · 해제: Shift+F3 두 번" : "클릭 통과 꺼짐");
    }

    /// <summary>Toggles click-through and returns the new state.</summary>
    internal bool ToggleClickThrough()
    {
        bool next = !_state.IsClickThrough;
        ApplyClickThrough(next);
        return next;
    }

    /// <summary>The pin's window bounds in physical pixels; empty if not yet shown.</summary>
    internal (int Left, int Top, int Right, int Bottom) PhysicalBounds =>
        _handle != IntPtr.Zero ? WindowStyleFacade.GetWindowBounds(_handle) : (0, 0, 0, 0);

    /// <summary>Hides the pin while retaining its position and zoom.</summary>
    internal void HidePin()
    {
        _state.IsHidden = true;
        Hide();
    }

    /// <summary>Reveals a hidden pin at its retained position and zoom.</summary>
    internal void ShowPin()
    {
        _state.IsHidden = false;
        Show();
        Topmost = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        // Re-apply any click-through state that was set before the handle existed.
        if (_state.IsClickThrough)
        {
            WindowStyleFacade.SetClickThrough(_handle, enabled: true);
        }
    }

    private void SetChromeHover(bool hovering)
    {
        // Neutral at rest, restrained system-blue on hover — a quiet cue that the pin is
        // interactive without adding decorative colour to the frozen image.
        string key = hovering ? "Accent.Cool" : "Border.Subtle";
        Brush fallback = hovering
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x63, 0xB3, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0x32, 0x4A));
        _chrome.BorderBrush = Application.Current?.TryFindResource(key) as Brush ?? fallback;
        _chrome.BorderThickness = new Thickness(1);
    }

    // ----- Mouse: drag to move, wheel to zoom, Ctrl+wheel opacity -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl)
        {
            // Ctrl gestures never drag. A double-click runs OCR; a single-click copies, but
            // only after a debounce so a second click can promote it to the OCR gesture and it
            // never races the OCR request onto the clipboard.
            if (e.ClickCount >= 2)
            {
                _ctrlClickTimer.Stop();
                Focus();
                RequestOcr();
            }
            else
            {
                StartCtrlClickCopyDebounce();
                Focus();
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            // Plain double-click closes the pin only when the setting says so, matching Snipaste.
            if (_settings().CloseOnDoubleClick)
            {
                Close();
                e.Handled = true;
                return;
            }
        }

        _dragging = true;
        _dragAnchor = e.GetPosition(this);
        _ = CaptureMouse();
        Focus();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        Point current = e.GetPosition(this);
        double dx = current.X - _dragAnchor.X;
        double dy = current.Y - _dragAnchor.Y;
        MoveBy(dx, dy);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int notches = e.Delta / 120;
        if (notches == 0)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double opacity = _state.AdjustOpacity(OpacityStep * notches);
            Opacity = opacity;
            ShowFeedback($"투명도 {opacity * 100:0}%");
        }
        else
        {
            Point pointer = e.GetPosition(this);
            ZoomAt(notches, pointer.X + Left, pointer.Y + Top);
        }

        e.Handled = true;
    }

    // ----- Keyboard -----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        double nudge = shift ? ArrowNudgeShiftDip : ArrowNudgeDip;

        switch (e.Key)
        {
            case Key.Escape:
            case Key.Delete:
                Close();
                e.Handled = true;
                break;

            case Key.Add:
            case Key.OemPlus:
                ZoomCentered(1);
                e.Handled = true;
                break;

            case Key.Subtract:
            case Key.OemMinus:
                ZoomCentered(-1);
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                ResetZoomCentered();
                e.Handled = true;
                break;

            case Key.Left:
                MoveBy(-nudge, 0);
                e.Handled = true;
                break;
            case Key.Right:
                MoveBy(nudge, 0);
                e.Handled = true;
                break;
            case Key.Up:
                MoveBy(0, -nudge);
                e.Handled = true;
                break;
            case Key.Down:
                MoveBy(0, nudge);
                e.Handled = true;
                break;

            case Key.C when ctrl:
                CopyImageToClipboard();
                e.Handled = true;
                break;
        }
    }

    // ----- State mirroring -----

    private void MoveBy(double dx, double dy)
    {
        double left = Left + dx;
        double top = Top + dy;

        (double dLeft, double dTop, double dWidth, double dHeight) = VirtualDesktopDip();
        (left, top) = PinGeometry.KeepGrabbable(
            left, top, Width, Height, dLeft, dTop, dWidth, dHeight, GrabMarginDip);

        Left = left;
        Top = top;
    }

    private void ZoomAt(int notches, double pointerXDip, double pointerYDip)
    {
        double oldZoom = _state.Zoom;
        double newZoom = _state.ApplyZoomStep(notches);
        if (Math.Abs(newZoom - oldZoom) < 1e-9)
        {
            ShowFeedback($"{newZoom * 100:0}%");
            return;
        }

        (double newLeft, double newTop) = PinGeometry.AnchorTopLeftForZoom(
            Left, Top, oldZoom, newZoom, pointerXDip, pointerYDip);

        Width = _state.WidthDip;
        Height = _state.HeightDip;
        ApplyPositionKeepingGrabbable(newLeft, newTop);
        ShowFeedback($"{newZoom * 100:0}%");
    }

    private void ZoomCentered(int notches) =>
        ZoomAt(notches, Left + (Width / 2.0), Top + (Height / 2.0));

    private void ResetZoomCentered()
    {
        double centerX = Left + (Width / 2.0);
        double centerY = Top + (Height / 2.0);
        double oldZoom = _state.Zoom;
        double newZoom = _state.ResetZoom();

        (double newLeft, double newTop) = PinGeometry.AnchorTopLeftForZoom(
            Left, Top, oldZoom, newZoom, centerX, centerY);

        Width = _state.WidthDip;
        Height = _state.HeightDip;
        ApplyPositionKeepingGrabbable(newLeft, newTop);
        ShowFeedback("100%");
    }

    private void ApplyPositionKeepingGrabbable(double left, double top)
    {
        (double dLeft, double dTop, double dWidth, double dHeight) = VirtualDesktopDip();
        (left, top) = PinGeometry.KeepGrabbable(
            left, top, Width, Height, dLeft, dTop, dWidth, dHeight, GrabMarginDip);
        Left = left;
        Top = top;
    }

    private (double Left, double Top, double Width, double Height) VirtualDesktopDip()
    {
        // SystemParameters.VirtualScreen* are already in DIP on the primary DPI context,
        // which is the coordinate space WPF Window.Left/Top use.
        return (
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private void CopyImageToClipboard()
    {
        CopyRequested?.Invoke(this, _image);
        ShowFeedback("복사됨");
    }

    private void RequestOcr()
    {
        OcrRequested?.Invoke(this, _image);
        ShowFeedback("텍스트 인식 중…");
    }

    /// <summary>
    /// Arms the copy debounce. If no second click arrives within the configured window the
    /// timer fires and the copy happens; a second click (Ctrl+double-click) stops it first and
    /// runs OCR instead, so the two gestures never race the clipboard.
    /// </summary>
    private void StartCtrlClickCopyDebounce()
    {
        int debounceMs = Math.Max(0, _settings().CtrlClickDebounceMs);
        _ctrlClickTimer.Stop();

        if (debounceMs == 0)
        {
            CopyImageToClipboard();
            return;
        }

        _ctrlClickTimer.Interval = TimeSpan.FromMilliseconds(debounceMs);
        _ctrlClickTimer.Start();
    }

    private void OnCtrlClickTimerTick(object? sender, EventArgs e)
    {
        _ctrlClickTimer.Stop();
        CopyImageToClipboard();
    }

    // ----- Feedback toast -----

    private void ShowFeedback(string text)
    {
        _feedback.Text = text;
        _feedback.Visibility = Visibility.Visible;

        // Restart the countdown on every message so rapid feedback keeps the latest text
        // visible for the full duration rather than expiring mid-sequence.
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void OnFeedbackTimerTick(object? sender, EventArgs e)
    {
        _feedbackTimer.Stop();
        _feedback.Visibility = Visibility.Collapsed;
    }

    // ----- Context menu -----

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor("100% (0)", (_, _) => ResetZoomCentered()));
        menu.Items.Add(MenuItemFor("확대 (+)", (_, _) => ZoomCentered(1), iconResourceKey: "Icon.ZoomIn"));
        menu.Items.Add(MenuItemFor("축소 (-)", (_, _) => ZoomCentered(-1), iconResourceKey: "Icon.ZoomOut"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("더 투명하게 (Ctrl+휠)", (_, _) =>
        {
            double opacity = _state.AdjustOpacity(-OpacityStep);
            Opacity = opacity;
            ShowFeedback($"투명도 {opacity * 100:0}%");
        }));
        menu.Items.Add(MenuItemFor("더 불투명하게 (Ctrl+휠)", (_, _) =>
        {
            double opacity = _state.AdjustOpacity(OpacityStep);
            Opacity = opacity;
            ShowFeedback($"투명도 {opacity * 100:0}%");
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("복사 (Ctrl+C)", (_, _) => CopyImageToClipboard(), iconResourceKey: "Icon.Copy"));
        menu.Items.Add(MenuItemFor("텍스트 인식 (OCR)", (_, _) => RequestOcr(), iconResourceKey: "Icon.Ocr"));
        menu.Items.Add(MenuItemFor(
            "클릭 통과 전환",
            (_, _) => ToggleClickThrough(),
            "마우스 입력을 아래 창으로 통과시킵니다. 되돌리려면 Shift+F3을 두 번 누르세요.",
            "Icon.Select"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("닫기 (Esc)", (_, _) => Close(), iconResourceKey: "Icon.Close"));
        menu.Items.Add(MenuItemFor("모두 닫기", (_, _) => CloseAllRequested?.Invoke(this, EventArgs.Empty), iconResourceKey: "Icon.Close"));

        ContextMenu = menu;
    }

    private static MenuItem MenuItemFor(
        string header,
        RoutedEventHandler onClick,
        string? helpText = null,
        string? iconResourceKey = null)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetName(item, header);
        if (helpText is not null)
        {
            AutomationProperties.SetHelpText(item, helpText);
        }

        if (iconResourceKey is not null && Application.Current?.TryFindResource(iconResourceKey) is Geometry geometry)
        {
            var glyph = new Path
            {
                Data = geometry,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
                Stretch = Stretch.None,
            };
            glyph.SetBinding(
                Shape.StrokeProperty,
                new Binding(nameof(Control.Foreground))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1),
                    FallbackValue = Application.Current.TryFindResource("Text.Secondary") as Brush ?? Brushes.LightGray,
                });

            var canvas = new Canvas { Width = 20, Height = 20 };
            canvas.Children.Add(glyph);
            item.Icon = new Viewbox
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                Child = canvas,
            };
        }

        item.Click += onClick;
        return item;
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _feedbackTimer.Stop();
        _feedbackTimer.Tick -= OnFeedbackTimerTick;
        _ctrlClickTimer.Stop();
        _ctrlClickTimer.Tick -= OnCtrlClickTimerTick;
        SourceInitialized -= OnSourceInitialized;
        base.OnClosed(e);
    }
}
