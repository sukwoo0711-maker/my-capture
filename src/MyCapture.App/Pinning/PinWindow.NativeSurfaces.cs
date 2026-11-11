using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

// The pin anchor (IPinAnchor) and what a native-hosted pin draws around itself with WPF: the hover bar
// (one per thread, shared by the native pins), the bottom message (PinFeedbackSurface), the tooltip and
// the context menu. Each looks and acts as on a WPF pin; the windows that anchor to the native HWND move
// to the WPF window when the pin switches.
internal sealed partial class PinWindow
{
    [ThreadStatic] private static PinHoverBar? t_nativeBar;
    [ThreadStatic] private static PinWindow? t_nativeBarPin;
    [ThreadStatic] private static DispatcherTimer? t_nativeBarLeave;

    private readonly List<Window> _nativeOwned = [];
    private PinFeedbackSurface? _feedbackSurface;
    private ToolTip? _nativeToolTip;
    private DispatcherTimer? _nativeToolTipTimer;
    private Action? _refreshContextMenu;
    private bool _nativeOrnamentPending;
    private Stopwatch? _nativeOrnamentClock;

    /// <summary>Replaces opening the native pin's context menu, so tests never put a popup on the screen.</summary>
    internal static Action<ContextMenu>? PresentNativeContextMenuForTest { get; set; }

    /// <summary>Replaces opening the native pin's tooltip, so tests never put a popup on the screen.</summary>
    internal static Action<ToolTip>? PresentNativeToolTipForTest { get; set; }

    internal PinFeedbackSurface? FeedbackWindowForTest => _feedbackSurface;
    internal ToolTip? NativeToolTipForTest => _nativeToolTip;
    internal string FeedbackTextForTest => KoreanWordWrap.Strip(_feedbackText.Text);
    internal static PinHoverBar? SharedNativeHoverBarForTest => t_nativeBar;

    // ----- IPinAnchor -----

    public event Action? AnchorBoundsChanged;
    public event Action<bool>? AnchorShownChanged;
    public event Action? AnchorClosed;

    internal IntPtr AnchorHandle => _native is { Window: { IsClosed: false } window } ? window.Handle : _handle;

    internal RectD AnchorBounds
    {
        get
        {
            (int left, int top, int right, int bottom) = PhysicalBounds;
            return new RectD(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }
    }

    internal double AnchorScale => _native is { } host ? host.Window.DpiScale : VisualTreeHelper.GetDpi(this).DpiScaleX;

    IntPtr IPinAnchor.AnchorHandle => AnchorHandle;
    RectD IPinAnchor.AnchorBounds => AnchorBounds;
    double IPinAnchor.AnchorScale => AnchorScale;
    bool IPinAnchor.IsAnchorShown => IsShown;
    bool IPinAnchor.IsClosed => _isClosed;
    RectD IPinAnchor.ReadAnchorWorkArea() => ReadWorkArea(AnchorHandle);

    /// <summary>Owns <paramref name="window"/> from the presenting window; a native pin hands it on at its switch.</summary>
    public void AttachOwned(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_native is { } host)
        {
            new WindowInteropHelper(window).Owner = host.Window.Handle;
            if (_nativeOwned.Contains(window)) return;
            _nativeOwned.Add(window);
            window.Closed += (_, _) => _nativeOwned.Remove(window);
            return;
        }
        window.Owner = this;
    }

    // Before the native window is destroyed (it would take its owned windows with it), the WPF handle owns them.
    private void HandOverOwnedWindows()
    {
        if (_nativeOwned.Count == 0) return;
        Window[] owned = [.. _nativeOwned];
        _nativeOwned.Clear();
        IntPtr wpf = new WindowInteropHelper(this).EnsureHandle();
        foreach (Window window in owned) new WindowInteropHelper(window).Owner = wpf;
    }

    /// <summary>The owner for a dialog the pin opens: the WPF window, or none for a native pin (see <see cref="DialogOwnerHandle"/>).</summary>
    internal Window? DialogOwner => _native is null ? this : null;

    /// <summary>The native HWND that owns the pin's dialogs while it presents the pin; zero otherwise.</summary>
    internal IntPtr DialogOwnerHandle => _native is { Window: { IsClosed: false } window } ? window.Handle : IntPtr.Zero;

    private bool IsPointerOverPin => _native is { } host
        ? host.Window.IsPointerOver || _feedbackSurface?.IsMouseOver == true
        : IsMouseOver;

    // ----- The shared hover bar -----

    private PinHoverBar AdoptNativeHoverBar()
    {
        if (t_nativeBarLeave is { } leave) { leave.Stop(); t_nativeBarLeave = null; ResetLeftBar(t_nativeBar); }
        PinHoverBar? bar = t_nativeBar;
        if (bar is null)
        {
            bar = new PinHoverBar(Provenance);
            PinHoverBar created = bar;
            WireHoverBar(bar, static () => t_nativeBarPin);
            bar.Closed += (_, _) =>
            {
                if (!ReferenceEquals(t_nativeBar, created)) return;
                if (t_nativeBarPin is { } holder) holder._hoverBar = null;
                t_nativeBar = null;
                t_nativeBarPin = null;
            };
            t_nativeBar = bar;
        }
        if (!ReferenceEquals(t_nativeBarPin, this))
        {
            if (t_nativeBarPin is { } previous)
            {
                previous._hoverHideTimer.Stop();
                previous._hoverBar = null;
            }
            if (bar.IsVisible) bar.Hide();
            t_nativeBarPin = this;
            _hoverBar = bar;
            bar.SetProvenance(Provenance);
            RefreshSourceMenu(bar);
            RefreshTranslationButton(bar);
            _labeledHoverBarWidth = bar.MeasureLabeledWidth();
        }
        if (_native is { } host) new WindowInteropHelper(bar).Owner = host.Window.Handle;
        return bar;
    }

    // The shared bar stays for the next native pin; it only stops following this one.
    private void ReleaseNativeHoverBar(bool leaving)
    {
        _hoverHideTimer.Stop();
        if (!ReferenceEquals(t_nativeBarPin, this) || t_nativeBar is not { } bar)
        {
            if (_native is not null || _retiringNative is not null) _hoverBar = null;
            return;
        }
        t_nativeBarPin = null;
        _hoverBar = null;
        // The bar must not be owned by a window that is about to be destroyed.
        new WindowInteropHelper(bar).Owner = IntPtr.Zero;
        if (!bar.IsVisible) return;
        if (!leaving || !FluidMotion.AnimationsEnabled) { bar.Hide(); return; }
        // As a WPF pin's exit: the bar fades with the pin, then waits hidden for the next native pin.
        bar.Leave();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, bar.Dispatcher) { Interval = FluidMotion.FastDuration.TimeSpan };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(t_nativeBarLeave, timer)) return;
            t_nativeBarLeave = null;
            ResetLeftBar(bar);
        };
        t_nativeBarLeave?.Stop();
        t_nativeBarLeave = timer;
        timer.Start();
    }

    private static void ResetLeftBar(PinHoverBar? bar)
    {
        if (bar is null) return;
        if (t_nativeBarPin is null && bar.IsVisible) bar.Hide();
        bar.BeginAnimation(OpacityProperty, null);
        bar.Opacity = 1;
        bar.IsHitTestVisible = true;
    }

    // ----- The bottom message -----

    private void SyncFeedbackSurface()
    {
        if (_native is not { } host) return;
        bool show = !_isClosed && !_state.IsHidden && !host.Window.IsClosed && host.Window.IsVisible
            && !host.Window.IsHiding && _feedback.Visibility == Visibility.Visible;
        if (!show)
        {
            CloseFeedbackSurface();
            return;
        }
        PinFeedbackSurface surface = _feedbackSurface ??= CreateFeedbackSurface(host);
        surface.Opacity = _state.Opacity;
        // The message wraps inside the pin, as it does inside a WPF pin.
        surface.MaxWidth = Math.Max(1, _state.WidthDip);
        surface.MaxHeight = Math.Max(1, _state.HeightDip);
        if (!surface.IsVisible) surface.Show();
        PlaceFeedbackSurface();
    }

    private PinFeedbackSurface CreateFeedbackSurface(NativePinHost host)
    {
        if (_feedback.Parent is Panel panel) panel.Children.Remove(_feedback);
        var surface = new PinFeedbackSurface(_feedback)
        {
            MoveRequested = point => _native?.Window.BeginMoveAtScreen(point),
            WheelRequested = (delta, point, control) => _native?.Window.Wheel(delta, point, control),
        };
        new WindowInteropHelper(surface).Owner = host.Window.Handle;
        surface.MouseEnter += (_, _) => _hoverHideTimer.Stop();
        surface.MouseLeave += (_, _) => ScheduleHoverBarHide();
        surface.SizeChanged += (_, _) => PlaceFeedbackSurface();
        return surface;
    }

    private void PlaceFeedbackSurface()
    {
        if (_feedbackSurface is not { IsVisible: true } surface || _native is not { } host || host.Window.IsClosed) return;
        IntPtr handle = new WindowInteropHelper(surface).Handle;
        if (handle == IntPtr.Zero) return;
        surface.UpdateLayout();
        double scale = host.Window.DpiScale;
        RectD target = PinFeedbackSurface.Place(host.Window.Bounds, surface.ActualWidth * scale, surface.ActualHeight * scale);
        try { PhysicalWindowPositioner.Move(handle, target); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The message is a convenience; the pin keeps working where Windows leaves it.
        }
    }

    private void CloseFeedbackSurface()
    {
        if (_feedbackSurface is not { } surface) return;
        _feedbackSurface = null;
        surface.Release();
    }

    // A switch: the message moves inside the WPF window before its first frame, and the message window
    // keeps showing it through a live brush until the native window goes, so no frame lacks it.
    private void MoveFeedbackToChromeForSwitch()
    {
        if (_feedbackSurface is not { } surface || !ReferenceEquals(_feedback.Parent, surface)) return;
        double width = _feedback.ActualWidth, height = _feedback.ActualHeight;
        surface.Content = null;
        _chrome.Children.Add(_feedback);
        if (width <= 0 || height <= 0) return;
        surface.Content = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Margin = _feedback.Margin,
            IsHitTestVisible = false,
            Fill = new VisualBrush(_feedback) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
        };
    }

    // The message goes back inside the WPF window that now presents the pin.
    private void ReturnFeedbackToChrome()
    {
        CloseFeedbackSurface();
        if (_feedback.Parent is null) _chrome.Children.Add(_feedback);
    }

    // ----- Tooltip -----

    private void ScheduleNativeToolTip()
    {
        if (_isClosed || _state.IsClickThrough || ToolTip is not string) return;
        _nativeToolTipTimer ??= CreateNativeToolTipTimer();
        _nativeToolTipTimer.Stop();
        _nativeToolTipTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, ToolTipService.GetInitialShowDelay(this)));
        _nativeToolTipTimer.Tag = false;
        _nativeToolTipTimer.Start();
    }

    private DispatcherTimer CreateNativeToolTipTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (timer.Tag is true) { HideNativeToolTip(); return; }
            ShowNativeToolTip();
        };
        return timer;
    }

    private void ShowNativeToolTip()
    {
        if (_native is not { } host || _isClosed || ToolTip is not string text || !host.Window.IsPointerOver
            || host.Window.IsDragging || _state.IsClickThrough || ContextMenu?.IsOpen == true) return;
        ToolTip tip = _nativeToolTip ??= new ToolTip { Placement = PlacementMode.Mouse };
        tip.Content = text;
        if (PresentNativeToolTipForTest is { } present) present(tip);
        else tip.IsOpen = true;
        // As ToolTipService: the tip closes after its show duration.
        if (_nativeToolTipTimer is { } timer)
        {
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, ToolTipService.GetShowDuration(this)));
            timer.Tag = true;
            timer.Start();
        }
    }

    private void HideNativeToolTip()
    {
        _nativeToolTipTimer?.Stop();
        if (_nativeToolTip is { IsOpen: true } tip) tip.IsOpen = false;
    }

    // ----- Context menu -----

    /// <summary>The pin's context menu over the native window: the same items in the same order, at the pointer.</summary>
    internal void OpenNativeContextMenu(Point? screenPoint)
    {
        if (_isClosed || ContextMenu is not { } menu || _native is not { } host) return;
        HideNativeToolTip();
        _refreshContextMenu?.Invoke();
        menu.PlacementTarget = null;
        if (screenPoint is null)
        {
            // From the keyboard: at the pin's centre, as a WPF pin's menu opens.
            RectD bounds = host.Window.Bounds;
            double scale = host.Window.DpiScale;
            menu.Placement = PlacementMode.Absolute;
            menu.HorizontalOffset = (bounds.Left + (bounds.Width / 2)) / scale;
            menu.VerticalOffset = (bounds.Top + (bounds.Height / 2)) / scale;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
        }
        if (PresentNativeContextMenuForTest is { } present) present(menu);
        else menu.IsOpen = true;
    }

    // ----- Native window callbacks for the surfaces -----

    /// <summary>A key or press reached the native window: the tooltip steps aside, as WPF's does.</summary>
    internal void OnNativeInput() => HideNativeToolTip();

    internal void OnNativeCtrlClick() => StartCtrlClickCopyDebounce();

    internal void OnNativeCtrlDoubleClick() => HandleCtrlDoubleClick();

    internal void OnNativeGestureStarting()
    {
        HideNativeToolTip();
        HideHoverBar();
    }

    internal void OnNativeBoundsChanged()
    {
        if (_isClosed) return;
        PlaceHoverBar();
        _clickThroughHandle?.Place();
        if (_feedbackSurface is not null)
        {
            _feedbackSurface.MaxWidth = Math.Max(1, _state.WidthDip);
            _feedbackSurface.MaxHeight = Math.Max(1, _state.HeightDip);
            PlaceFeedbackSurface();
        }
        AnchorBoundsChanged?.Invoke();
    }

    internal void OnNativeShownChanged(bool shown)
    {
        if (_isClosed) return;
        if (!shown) { HideHoverBar(); HideNativeToolTip(); CancelPendingProgress(); }
        UpdateClickThroughHandle();
        SyncFeedbackSurface();
        AnchorShownChanged?.Invoke(shown);
    }

    internal void OnNativeHoverChanged(bool over)
    {
        if (_isClosed) return;
        if (over)
        {
            ShowHoverBar();
            ScheduleNativeToolTip();
        }
        else
        {
            HideNativeToolTip();
            ScheduleHoverBarHide();
        }
    }

    internal void OnNativeZoomShown(double zoom)
    {
        HideNativeToolTip();
        ShowFeedback($"{zoom * 100:0}%");
    }

    internal void OnNativeOpacityShown(double opacity)
    {
        HideNativeToolTip();
        ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
    }

    // ----- Landing start -----

    /// <summary>
    /// The native window's first motion tick after the pin opened: the landing ornament is created here,
    /// with its clock stopped, and the reveal and landing wait until it has drawn its first frame, so its
    /// window's first-use costs come before the motion instead of between two of its frames.
    /// </summary>
    internal bool OnNativeMotionReady()
    {
        if (_nativeOrnamentPending)
        {
            _nativeOrnamentPending = false;
            if (!_isClosed && !_state.IsHidden && _native is { Window: { IsLanding: true, IsVisible: true } })
            {
                _nativeOrnamentClock = new Stopwatch();
                TryShowLandingDecoration(_nativeOrnamentClock);
                if (_landingDecoration is null) _nativeOrnamentClock = null;
            }
        }
        return _nativeOrnamentClock is null || _landingDecoration is not { IsClosed: false } decoration || decoration.HasRendered;
    }

    /// <summary>The reveal and landing clocks start now; the ornament's clock starts with them.</summary>
    internal void OnNativeMotionStarted()
    {
        _nativeOrnamentClock?.Start();
        _nativeOrnamentClock = null;
    }

    private void ApplyNativeClickThrough(bool enabled)
    {
        if (_retiringNative is not null) FinishHostSwitch();
        if (_native is not { } host) return;
        if (!host.Window.IsClosed) host.Window.SetClickThrough(enabled);
        if (enabled) HideNativeToolTip();
    }

    // ----- Lifetime -----

    /// <summary>A native pin closes or leaves: its bar, message and tooltip go with it.</summary>
    private void ReleaseNativeSurfaces(bool leaving)
    {
        ReleaseNativeHoverBar(leaving);
        HideNativeToolTip();
        CloseFeedbackSurface();
        if (ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
    }

    /// <summary>The pin moves to its WPF window: the anchored windows follow it before the native window goes.</summary>
    private void HandOverNativeSurfaces()
    {
        ReleaseNativeHoverBar(leaving: false);
        HideNativeToolTip();
        ReturnFeedbackToChrome();
        HandOverOwnedWindows();
    }

    private void CloseNativeSurfaces()
    {
        if (ReferenceEquals(t_nativeBarPin, this)) ReleaseNativeHoverBar(leaving: false);
        HideNativeToolTip();
        if (_nativeToolTipTimer is { } timer) { timer.Stop(); _nativeToolTipTimer = null; }
        CloseFeedbackSurface();
    }
}
