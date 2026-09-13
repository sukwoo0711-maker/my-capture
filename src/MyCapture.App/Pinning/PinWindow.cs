using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
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
/// Local layout and zoom use WPF DIP; dragging uses one physical virtual-desktop plane
/// so a different monitor DPI cannot shorten the available movement range. The image is drawn with
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
    private readonly PinContentKind _contentKind;
    private readonly string? _originalText;
    private readonly PinViewState _state;
    private readonly Func<PinSettings> _settings;
    private readonly IPinDragInput _dragInput;
    private readonly Image _imageElement;
    private readonly Border _chrome;
    private readonly SolidColorBrush _chromeBrush;
    private readonly ScaleTransform _pinScale;
    private readonly TextBlock _feedback;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly DispatcherTimer _ctrlClickTimer;
    private readonly DispatcherTimer _zoomSettleTimer;

    private bool _liveZooming;
    private bool _dragging;
    private Point _dragAnchor;
    private RectD? _dragDesktop;
    private double _dragGrabMarginPx;
    private int _lastDragCursorX = int.MinValue;
    private int _lastDragCursorY = int.MinValue;
    private IntPtr _handle;
    private bool _isClosed;
    private bool _saveInProgress;

    internal PinWindow(
        PinContent content,
        PinViewState state,
        double initialLeft,
        double initialTop,
        Func<PinSettings>? settings = null,
        IPinDragInput? dragInput = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _image = content.Image;
        _contentKind = content.Kind;
        _originalText = content.OriginalText;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? (static () => new PinSettings());
        _dragInput = dragInput ?? NativePinDragInput.Instance;

        Title = UiText.Get("Text_03151E566E1D");
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
        FluidMotion.SetWindowEntrance(this, false);

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
                ?? new SolidColorBrush(Color.FromArgb(0xE8, 0x15, 0x1E, 0x2B)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        // Image-first chrome: a neutral one-pixel boundary rests quietly and warms to a
        // restrained warm yellow on hover, while rounded clipping keeps scaled pins consistent
        // with the rest of the desktop surfaces.
        _chromeBrush = new SolidColorBrush(ResolveChromeColor("Border.Subtle", hovering: false));
        _pinScale = new ScaleTransform(1, 1);
        _chrome = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = _chromeBrush,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = new Grid { Children = { _imageElement, _feedback } },
            RenderTransform = _pinScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        Content = _chrome;

        ToolTip = _originalText is null
            ? UiText.Get("Text_73D6AF7EE15B")
            : UiText.Get("Text_338C1B26A06A");
        AutomationProperties.SetName(this, UiText.Get("Text_85E926C7F694"));
        AutomationProperties.SetHelpText(
            this,
            _originalText is null
                ? UiText.Get("Text_E22E2C953A8B")
                : UiText.Get("Text_55694B0252DB"));

        BuildContextMenu();

        // Auto-hides the bottom feedback overlay so it never lingers on screen. The tick
        // handler runs on the dispatcher thread that owns this window, so touching WPF
        // elements from it is thread-safe.
        _feedbackTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = FeedbackDuration,
        };
        _feedbackTimer.Tick += OnFeedbackTimerTick;

        // Tracks the double-click interval without modifying the clipboard on a single click.
        // Ctrl+C copies the rendered image; Ctrl+double-click copies text.
        _ctrlClickTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _ctrlClickTimer.Tick += OnCtrlClickTimerTick;
        _zoomSettleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        _zoomSettleTimer.Tick += OnZoomSettleTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnPinLoaded;
        MouseEnter += (_, _) => SetChromeHover(true);
        MouseLeave += (_, _) => SetChromeHover(false);
    }

    /// <summary>Raised when the user asks (via this pin) to close every pin.</summary>
    internal event EventHandler? CloseAllRequested;

    /// <summary>Raised when the user copies this pin's image.</summary>
    internal event EventHandler<BitmapSource>? CopyRequested;

    /// <summary>Raised when a rendered text/table pin requests its retained source text.</summary>
    internal event EventHandler<string>? OriginalTextCopyRequested;

    /// <summary>Raised when the user asks to run OCR on this pin's image.</summary>
    internal event EventHandler<BitmapSource>? OcrRequested;

    /// <summary>Raised when the user asks to save the frozen source image.</summary>
    internal event EventHandler<PinSaveRequestedEventArgs>? SaveRequested;

    /// <summary>The pin's live presentation state.</summary>
    internal PinViewState State => _state;

    /// <summary>Whether this pin represents an image, plain text, or a table.</summary>
    internal PinContentKind ContentKind => _contentKind;

    /// <summary>The exact retained source string, exposed internally for verification.</summary>
    internal string? OriginalText => _originalText;

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
    internal void ForceFeedbackTimeoutForTest()
    {
        _feedbackTimer.Stop();
        HideFeedback(immediate: true);
    }

    /// <summary>Test hook: requests either quick save or Save As without keyboard input.</summary>
    internal void SimulateSaveRequestForTest(PinSaveMode mode) => RequestSave(mode);

    /// <summary>Completes the pin-local save progress state after the manager finishes I/O.</summary>
    internal void ReportSaveResult(PinSaveResult result)
    {
        _saveInProgress = false;
        switch (result.Status)
        {
            case PinSaveStatus.Saved:
                string fileName = string.IsNullOrWhiteSpace(result.Path)
                    ? "PNG"
                    : System.IO.Path.GetFileName(result.Path);
                ShowFeedback(UiText.Format("Text_E3F51EDC0F16", fileName));
                break;
            case PinSaveStatus.Cancelled:
                ShowFeedback(UiText.Get("Text_F122632462DF"));
                break;
            default:
                ShowFeedback(UiText.Get("Text_AE94EC51B2C2"));
                break;
        }
    }

    /// <summary>Completes pin-local clipboard feedback after the asynchronous copy.</summary>
    internal void ReportCopyResult(bool copied) =>
        ShowFeedback(copied ? UiText.Get("Text_9693E1EB3EDB") : UiText.Get("Text_0642E2D15469"));

    /// <summary>Completes feedback for copying a rendered pin's retained source text.</summary>
    internal void ReportOriginalTextCopyResult(bool copied) =>
        ShowFeedback(copied ? UiText.Get("Text_5C9B3C62EC41") : UiText.Get("Text_BCC2F939B733"));

    /// <summary>Test hook: whether the Ctrl+click copy debounce timer is currently armed.</summary>
    internal bool IsCtrlClickTimerRunning => _ctrlClickTimer.IsEnabled;

    /// <summary>
    /// Test hook: simulates a Ctrl+single-click, arming the copy debounce exactly as the mouse
    /// handler does, so the single-vs-double-click race is testable without a live pointer.
    /// </summary>
    internal void SimulateCtrlSingleClickForTest() => StartCtrlClickCopyDebounce();

    /// <summary>
    /// Test hook: simulates a Ctrl+double-click. Image pins request OCR; rendered text/table
    /// pins copy their retained source string. Both cancel the pending single-click copy.
    /// </summary>
    internal void SimulateCtrlDoubleClickForTest() => HandleCtrlDoubleClick();

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

        ShowFeedback(enabled ? UiText.Get("Text_41216A109463") : UiText.Get("Text_FA24C3FC395F"));
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
        if (!FluidMotion.AnimationsEnabled || !IsVisible)
        {
            Hide();
            return;
        }

        var fade = new DoubleAnimation(_chrome.Opacity, 0, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            if (_state.IsHidden && !_isClosed)
            {
                Hide();
            }
        };
        _chrome.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Reveals a hidden pin at its retained position and zoom.</summary>
    internal void ShowPin()
    {
        _state.IsHidden = false;
        _chrome.BeginAnimation(UIElement.OpacityProperty, null);
        Show();
        Topmost = true;
        AnimatePinReveal();
    }

    private void OnPinLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        AnimatePinReveal();
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
        // Neutral at rest, restrained warm yellow on hover — a quiet cue that the pin is
        // interactive without adding decorative colour to the frozen image.
        Color target = ResolveChromeColor(
            hovering ? "Accent.Cool" : "Border.Subtle",
            hovering);
        // Read the presentation value before replacing an in-flight animation; otherwise a
        // fast enter/leave reversal visibly jumps back to the previous base colour.
        Color current = _chromeBrush.Color;
        _chromeBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        if (!FluidMotion.AnimationsEnabled)
        {
            _chromeBrush.Color = target;
            return;
        }

        _chromeBrush.Color = target;
        var animation = new ColorAnimation(current, target, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        _chromeBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static Color ResolveChromeColor(string key, bool hovering)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return hovering
            ? Color.FromArgb(0xFF, 0x7D, 0xD7, 0xF8)
            : Color.FromArgb(0xFF, 0x2B, 0x3A, 0x50);
    }

    // ----- Mouse: drag to move, wheel to zoom, Ctrl+wheel opacity -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        bool ctrl = _dragInput.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl)
        {
            // Ctrl gestures never drag. A single click only focuses; double-click copies
            // recognized text for an image or exact source text for a rendered text/table pin.
            if (e.ClickCount >= 2)
            {
                Focus();
                HandleCtrlDoubleClick();
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

        EndDrag();
        try
        {
            // Capture can fail (for example while another native control owns input).
            // Never leave a gesture active without ownership of subsequent input.
            if (_dragInput.TryCapture(this))
            {
                Point anchor = _dragInput.LocalAnchor(e, this);
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                _dragAnchor = new Point(anchor.X * dpi.DpiScaleX, anchor.Y * dpi.DpiScaleY);
                _dragDesktop = MonitorEnumerator.GetVirtualDesktopBounds();
                _dragGrabMarginPx = GrabMarginDip * dpi.DpiScaleX;
                _dragging = true;
                Focus();
            }
        }
        catch
        {
            EndDrag();
            throw;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        if (!_dragInput.HasCapture(this) || !_dragInput.LeftButtonPressed(e))
        {
            EndDrag();
            return;
        }

        (int cursorX, int cursorY) = _dragInput.CursorPosition;
        if (cursorX == _lastDragCursorX && cursorY == _lastDragCursorY)
        {
            return;
        }

        _lastDragCursorX = cursorX;
        _lastDragCursorY = cursorY;
        MovePhysical(cursorX - _dragAnchor.X, cursorY - _dragAnchor.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        EndDrag();
        base.OnLostMouseCapture(e);
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragDesktop = null;
        _lastDragCursorX = int.MinValue;
        _lastDragCursorY = int.MinValue;
        if (_dragInput.HasCapture(this)) _dragInput.Release(this);
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
            double opacity = AdjustOpacity(OpacityStep * notches);
            ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
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

            case Key.S when ctrl && shift:
                RequestSave(PinSaveMode.SaveAs);
                e.Handled = true;
                break;

            case Key.S when ctrl:
                RequestSave(PinSaveMode.QuickSave);
                e.Handled = true;
                break;
        }
    }

    // ----- State mirroring -----

    private void MoveBy(double dx, double dy)
    {
        if (_handle == IntPtr.Zero)
        {
            Left += dx;
            Top += dy;
            return;
        }
        (int left, int top, _, _) = WindowStyleFacade.GetWindowBounds(_handle);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        MovePhysical(left + (dx * dpi.DpiScaleX), top + (dy * dpi.DpiScaleY));
    }

    private void MovePhysical(double left, double top)
    {
        (int currentLeft, int currentTop, int right, int bottom) = WindowStyleFacade.GetWindowBounds(_handle);
        double width = right - currentLeft;
        double height = bottom - currentTop;
        if (width <= 0 || height <= 0) return;
        RectD desktop = _dragDesktop ?? MonitorEnumerator.GetVirtualDesktopBounds();
        double grabMarginPx = _dragging
            ? _dragGrabMarginPx
            : GrabMarginDip * VisualTreeHelper.GetDpi(this).DpiScaleX;
        (left, top) = PinGeometry.KeepGrabbable(left, top, width, height,
            desktop.Left, desktop.Top, desktop.Width, desktop.Height,
            grabMarginPx);
        var target = new RectD(left, top, width, height);
        if (_dragging)
        {
            PhysicalWindowPositioner.Move(_handle, target);
        }
        else
        {
            PhysicalWindowPositioner.PlaceTopmost(_handle, target);
        }
    }

    internal void MovePhysicalForTest(double left, double top) => MovePhysical(left, top);

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
        BeginLiveZoom();
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
        BeginLiveZoom();
        ShowFeedback("100%");
    }

    private void BeginLiveZoom()
    {
        _liveZooming = true;
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.LowQuality);
        _imageElement.CacheMode ??= new BitmapCache { RenderAtScale = 1, EnableClearType = false, SnapsToDevicePixels = true };
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pinScale.ScaleX = 1;
        _pinScale.ScaleY = 1;
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
    }

    private void OnZoomSettleTick(object? sender, EventArgs e)
    {
        _zoomSettleTimer.Stop();
        CommitCrispZoom();
    }

    /// <summary>Test hook: commit the live-zoom cache the way the settle timer would.</summary>
    internal void ForceZoomSettleForTest() => CommitCrispZoom();

    internal bool IsLiveZoomingForTest => _liveZooming;

    private void CommitCrispZoom()
    {
        _liveZooming = false;
        _imageElement.CacheMode = null;
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.HighQuality);
        _imageElement.InvalidateVisual();
        _chrome.InvalidateVisual();
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
        // Project the physical desktop into this window's current local DIP plane.
        // A primary-monitor DIP rectangle clips movement on mixed-DPI desktops.
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        (int physicalLeft, int physicalTop, _, _) = WindowStyleFacade.GetWindowBounds(_handle);
        return (
            Left + ((desktop.Left - physicalLeft) / dpi.DpiScaleX),
            Top + ((desktop.Top - physicalTop) / dpi.DpiScaleY),
            desktop.Width / dpi.DpiScaleX,
            desktop.Height / dpi.DpiScaleY);
    }

    private void CopyImageToClipboard()
    {
        ShowFeedback(UiText.Get("Text_4C160257F02C"));
        CopyRequested?.Invoke(this, _image);
    }

    private void RequestOcr()
    {
        ShowFeedback(UiText.Get("Text_15A871A266BE"));
        OcrRequested?.Invoke(this, _image);
    }

    private void HandleCtrlDoubleClick()
    {
        _ctrlClickTimer.Stop();
        if (_originalText is not null)
        {
            RequestOriginalTextCopy();
            return;
        }

        RequestOcr();
    }

    private void RequestOriginalTextCopy()
    {
        if (_originalText is null)
        {
            return;
        }

        ShowFeedback(UiText.Get("Text_BDB7C68F7452"));
        OriginalTextCopyRequested?.Invoke(this, _originalText);
    }

    private void RequestSave(PinSaveMode mode)
    {
        if (_saveInProgress)
        {
            ShowFeedback(UiText.Get("Text_88DAAEEDFE4C"));
            return;
        }

        EventHandler<PinSaveRequestedEventArgs>? handler = SaveRequested;
        if (handler is null)
        {
            ShowFeedback(UiText.Get("Text_9A07C688760E"));
            return;
        }

        _saveInProgress = true;
        ShowFeedback(mode == PinSaveMode.SaveAs ? UiText.Get("Text_B53B72EF2371") : UiText.Get("Text_88DAAEEDFE4C"));
        handler.Invoke(this, new PinSaveRequestedEventArgs(mode, _image));
    }

    private double AdjustOpacity(double delta)
    {
        double oldOpacity = Opacity;
        double target = _state.AdjustOpacity(delta);
        BeginAnimation(Window.OpacityProperty, null);
        Opacity = target;
        if (FluidMotion.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(oldOpacity, target, FluidMotion.NormalDuration)
            {
                EasingFunction = FluidMotion.SoftLandingEasing,
                FillBehavior = FillBehavior.Stop,
            };
            BeginAnimation(Window.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }

        return target;
    }

    private void AnimatePinReveal()
    {
        if (_isClosed)
        {
            return;
        }

        _chrome.BeginAnimation(UIElement.OpacityProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _chrome.Opacity = 1;
        _pinScale.ScaleX = 1;
        _pinScale.ScaleY = 1;

        if (!FluidMotion.AnimationsEnabled)
        {
            return;
        }

        var fade = new DoubleAnimation(0, 1, FluidMotion.NormalDuration)
        {
            EasingFunction = FluidMotion.SoftLandingEasing,
            FillBehavior = FillBehavior.Stop,
        };
        var scaleX = new DoubleAnimation(0.97, 1, FluidMotion.NormalDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        var scaleY = scaleX.Clone();
        _chrome.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX, HandoffBehavior.SnapshotAndReplace);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Tracks the configured double-click interval. Expiry never changes the clipboard;
    /// the second click invokes text copy and Ctrl+C independently invokes image copy.
    /// </summary>
    private void StartCtrlClickCopyDebounce()
    {
        int debounceMs = Math.Max(0, _settings().CtrlClickDebounceMs);
        _ctrlClickTimer.Stop();

        if (debounceMs == 0)
        {
            // A Ctrl single-click only focuses the pin. Ctrl+C copies its image.
            return;
        }

        _ctrlClickTimer.Interval = TimeSpan.FromMilliseconds(debounceMs);
        _ctrlClickTimer.Start();
    }

    private void OnCtrlClickTimerTick(object? sender, EventArgs e)
    {
        _ctrlClickTimer.Stop();
    }

    // ----- Feedback toast -----

    private void ShowFeedback(string text)
    {
        // OCR/clipboard completions can arrive after Close detached the timer's stop handler.
        // Never restart that timer or animate a window whose lifetime has ended.
        if (_isClosed) return;
        _feedback.Text = text;
        _feedback.Visibility = Visibility.Visible;
        _feedback.BeginAnimation(UIElement.OpacityProperty, null);
        _feedback.Opacity = 1;
        if (FluidMotion.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(0, 1, FluidMotion.FastDuration)
            {
                EasingFunction = FluidMotion.SoftLandingEasing,
                FillBehavior = FillBehavior.Stop,
            };
            _feedback.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }

        // Restart the countdown on every message so rapid feedback keeps the latest text
        // visible for the full duration rather than expiring mid-sequence.
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void OnFeedbackTimerTick(object? sender, EventArgs e)
    {
        _feedbackTimer.Stop();
        HideFeedback(immediate: false);
    }

    private void HideFeedback(bool immediate)
    {
        _feedback.BeginAnimation(UIElement.OpacityProperty, null);
        if (immediate || !FluidMotion.AnimationsEnabled || _isClosed)
        {
            _feedback.Visibility = Visibility.Collapsed;
            _feedback.Opacity = 1;
            return;
        }

        var fade = new DoubleAnimation(_feedback.Opacity, 0, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            _feedback.Visibility = Visibility.Collapsed;
            _feedback.Opacity = 1;
        };
        _feedback.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    // ----- Context menu -----

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_A81F975C401F"),
            (_, _) => RequestSave(PinSaveMode.SaveAs),
            UiText.Get("Text_78C743CC6F59"),
            "Icon.SaveAs",
            "Ctrl+Shift+S"));
        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_DFC084A111D0"),
            (_, _) => RequestSave(PinSaveMode.QuickSave),
            UiText.Get("Text_8300472A5BE3"),
            "Icon.Save",
            "Ctrl+S"));
        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_37B3D3B11B26"),
            (_, _) => CopyImageToClipboard(),
            iconResourceKey: "Icon.Copy",
            inputGestureText: "Ctrl+C"));
        if (_originalText is not null)
        {
            menu.Items.Add(MenuItemFor(
                UiText.Get("Text_976A6C92B870"),
                (_, _) => RequestOriginalTextCopy(),
                UiText.Get("Text_BB7798D87E07"),
                "Icon.Copy",
                UiText.Get("Text_C7011B8D345C")));
        }
        else
        {
            menu.Items.Add(MenuItemFor(UiText.Get("Text_03A1596F1681"), (_, _) => RequestOcr(), iconResourceKey: "Icon.Copy", inputGestureText: UiText.Get("Text_C7011B8D345C")));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("100% (0)", (_, _) => ResetZoomCentered()));
        menu.Items.Add(MenuItemFor(UiText.Get("Text_3063398CDECD"), (_, _) => ZoomCentered(1), iconResourceKey: "Icon.ZoomIn"));
        menu.Items.Add(MenuItemFor(UiText.Get("Text_D65A9E5A4C1A"), (_, _) => ZoomCentered(-1), iconResourceKey: "Icon.ZoomOut"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(UiText.Get("Text_182650FB6543"), (_, _) =>
        {
            double opacity = AdjustOpacity(-OpacityStep);
            ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
        }));
        menu.Items.Add(MenuItemFor(UiText.Get("Text_D0113E3B1184"), (_, _) =>
        {
            double opacity = AdjustOpacity(OpacityStep);
            ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_67FE4AF7D334"),
            (_, _) => ToggleClickThrough(),
            UiText.Get("Text_BE3D621D8A32"),
            "Icon.Select"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(UiText.Get("Text_A925E80E33B3"), (_, _) => Close(), iconResourceKey: "Icon.Close"));
        menu.Items.Add(MenuItemFor(UiText.Get("Text_E657405F6B2D"), (_, _) => CloseAllRequested?.Invoke(this, EventArgs.Empty), iconResourceKey: "Icon.Close"));

        ContextMenu = menu;
    }

    private static MenuItem MenuItemFor(
        string header,
        RoutedEventHandler onClick,
        string? helpText = null,
        string? iconResourceKey = null,
        string? inputGestureText = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = inputGestureText };
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
        EndDrag();
        _feedbackTimer.Stop();
        _feedbackTimer.Tick -= OnFeedbackTimerTick;
        _ctrlClickTimer.Stop();
        _ctrlClickTimer.Tick -= OnCtrlClickTimerTick;
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Tick -= OnZoomSettleTick;
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnPinLoaded;
        base.OnClosed(e);
    }
}
