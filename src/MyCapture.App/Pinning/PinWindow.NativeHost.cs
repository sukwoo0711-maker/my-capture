using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

// The seam between the logical pin and its two hosts. While _native is set the pin is presented by a
// NativePinHost and this WPF window is never shown; every host-dependent member branches here once.
// The pin moves to its WPF window at most once (SwitchToWpf) and never back.
internal sealed partial class PinWindow
{
    private NativePinHost? _native;
    // The native host of a pin that is moving to WPF, kept until the WPF window has drawn under it.
    private NativePinHost? _retiringNative;
    private readonly List<Action> _afterHostSwitch = [];
    private PinSwitchReason _switchReason;
    private bool _switchWasActive;
    private bool _skipRevealOnce;
    private bool _switchPlacementPending;
    private DispatcherTimer? _switchFallbackTimer;

    /// <summary>The pin is presented by its native window (a lightweight picture pin).</summary>
    internal bool IsNativeHosted => _native is not null;

    /// <summary>The pin is between its native window and its WPF window.</summary>
    internal bool IsHostSwitchPending => _retiringNative is not null;

    internal NativePinWindow? NativeWindowForTest => (_native ?? _retiringNative)?.Window;
    internal NativePinHost? NativeHostForTest => _native ?? _retiringNative;

    /// <summary>Raised once when the pin has moved to its WPF window.</summary>
    internal event Action<PinSwitchReason>? HostSwitched;

    /// <summary>Raised when the native window fails after it was created; the pin then moves to WPF.</summary>
    internal event Action<Exception>? NativeHostFailed;

    /// <summary>A native move or resize ended (a WPF pin's mouse-up is seen by the app directly).</summary>
    internal event Action? NativeInteractionEnded;

    /// <summary>How long the switch waits for WPF's first frame before it retires the native window anyway.</summary>
    internal static TimeSpan HostSwitchFrameTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Replaces <see cref="Window.Activate"/> after a switch, so tests never take the foreground.</summary>
    internal Action<PinWindow>? ActivateAfterSwitchForTest { get; set; }

    /// <summary>Called with the native and WPF handles just before the native window closes in a switch.</summary>
    internal Action<IntPtr, IntPtr>? BeforeNativeRetiredForTest { get; set; }

    internal bool CloseOnDoubleClick => _settings().CloseOnDoubleClick;

    /// <summary>Hands the pin to <paramref name="host"/> before it is first shown.</summary>
    internal void UseNativeHost(NativePinHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_native is not null || _handle != IntPtr.Zero || _isClosed)
            throw new InvalidOperationException("Only a pin that has not been shown can take a native host.");
        _native = host;
    }

    /// <summary>Shows the pin for the first time: the WPF window, or the native one.</summary>
    internal void Present()
    {
        if (_native is { } host) host.Window.Show();
        else Show();
    }

    /// <summary>Activates the pin's window: WPF <see cref="Window.Activate"/>, or the native HWND.</summary>
    internal void ActivatePin()
    {
        if (_native is { } host)
        {
            if (_isClosed || !host.Window.IsVisible) return;
            IntPtr handle = host.Window.Handle;
            if (!NativePinWindowInterop.SetForegroundWindow(handle)) _ = NativePinWindowInterop.SetActiveWindow(handle);
            return;
        }
        Activate();
    }

    /// <summary>Whether the pin is on the screen (fading counts), whichever window presents it.</summary>
    internal bool IsShown => _native is { } host ? host.Window.IsVisible : IsVisible;

    private static (int Left, int Top, int Right, int Bottom) NativeBounds(NativePinHost host)
    {
        if (host.Window.IsClosed) return (0, 0, 0, 0);
        RectD bounds = host.Window.Bounds;
        return ((int)bounds.Left, (int)bounds.Top, (int)(bounds.Left + bounds.Width), (int)(bounds.Top + bounds.Height));
    }

    // The whole surface, never the visible part a live frame holds, so a restore gets the pin's real place.
    private static PinClosedPlacement? NativePlacement(NativePinHost host)
    {
        if (host.Window.IsClosed || host.Window.Handle == IntPtr.Zero) return null;
        RectD bounds = host.Window.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        MonitorInfo monitor = MonitorEnumerator.GetFromWindow(host.Window.Handle);
        return new PinClosedPlacement(bounds, monitor.DeviceName, monitor.Bounds);
    }

    /// <summary>
    /// Moves a native pin to its WPF window (design section 4): same object, bounds and z-order slot;
    /// <paramref name="then"/> runs on the WPF window once the native window has gone. A closing pin
    /// never switches. Returns whether the trigger was taken over (switched, or queued behind a switch).
    /// </summary>
    internal bool SwitchToWpf(PinSwitchReason reason, Action? then = null)
    {
        if (_isClosed) return _native is not null;
        if (_native is not { } host)
        {
            if (_retiringNative is null) return false;
            if (then is not null) _afterHostSwitch.Add(then);
            return true;
        }
        NativePinWindow native = host.Window;
        host.Detach();
        // The shared hover bar and the tooltip belong to native pins; the WPF window has its own.
        ReleaseNativeHoverBar(leaving: false);
        HideNativeToolTip();
        if (native.IsClosed)
        {
            // Nothing to hand over; the pin closes with its window (OnNativeClosed).
            return true;
        }
        _switchWasActive = native.IsActive || NativePinWindowInterop.GetActiveWindow() == native.Handle;
        // 1. Freeze: no gesture, no settle; every transition ends at its final value.
        native.FreezeInput();
        native.CompleteTransitions();
        bool hidden = _state.IsHidden || !native.IsVisible;
        // 2. The whole surface and its scale give the WPF geometry.
        RectD bounds = native.Bounds;
        double scale = native.DpiScale;
        _native = null;
        _retiringNative = host;
        _switchReason = reason;
        if (then is not null) _afterHostSwitch.Add(then);
        Rect dip = SwitchGeometry(bounds, scale);
        Left = dip.Left;
        Top = dip.Top;
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        BeginAnimation(OpacityProperty, null);
        Opacity = _state.Opacity;
        PlaceAtPhysicalOnShow(bounds);
        _switchPlacementPending = true;
        try
        {
            IntPtr wpf = new WindowInteropHelper(this).EnsureHandle();
            if (hidden)
            {
                // A hidden pin keeps WPF's hidden state; revealing it later shows the WPF window.
                RetireNative();
                return true;
            }
            // 3. Directly below the native window in the topmost band, then shown without a reveal.
            PlaceBelow(wpf, native.Handle);
            MoveFeedbackToChromeForSwitch();
            // The first WPF frame shows the outline the native window shows (active or under the pointer).
            _chromeActive = _switchWasActive;
            _chromeHover = native.IsPointerOver;
            UpdateChromeColor(animate: false);
            _skipRevealOnce = true;
            ShowActivated = false;
            ContentRendered += OnSwitchContentRendered;
            _switchFallbackTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = HostSwitchFrameTimeout };
            _switchFallbackTimer.Tick += OnSwitchFallback;
            _switchFallbackTimer.Start();
            Show();
            // Applying Topmost on show may raise the window; its slot is the one under the native window.
            PlaceBelow(wpf, native.Handle);
        }
        catch
        {
            DropRetiringNative();
            throw;
        }
        return true;
    }

    /// <summary>The WPF window's DIP rectangle for a native surface (physical pixels at <paramref name="scale"/>).</summary>
    internal static Rect SwitchGeometry(RectD physical, double scale)
    {
        double s = scale > 0 && double.IsFinite(scale) ? scale : 1;
        return new Rect(physical.Left / s, physical.Top / s, physical.Width / s, physical.Height / s);
    }

    private static void PlaceBelow(IntPtr window, IntPtr above)
    {
        if (window == IntPtr.Zero || above == IntPtr.Zero) return;
        _ = NativePinWindowInterop.SetWindowPos(window, above, 0, 0, 0, 0,
            NativePinWindowInterop.NoMove | NativePinWindowInterop.NoSize | NativePinWindowInterop.NoActivate);
    }

    // 4. WPF has drawn its first frame; once the compositor has it, the native window can go.
    private void OnSwitchContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnSwitchContentRendered;
        if (_retiringNative is null) return;
        _ = NativePinWindowInterop.DwmFlush();
        RetireNative();
    }

    private void OnSwitchFallback(object? sender, EventArgs e) => RetireNative();

    /// <summary>Completes a switch in progress now (another command arrived before WPF's first frame).</summary>
    private void FinishHostSwitch()
    {
        if (_retiringNative is not null) RetireNative();
    }

    // 5-7: close the native window, activate the WPF window if the native one was active, run the trigger.
    private void RetireNative()
    {
        if (_retiringNative is not { } host) return;
        StopSwitchWaits();
        _retiringNative = null;
        BeforeNativeRetiredForTest?.Invoke(host.Window.IsClosed ? IntPtr.Zero : host.Window.Handle, _handle);
        HandOverNativeSurfaces();
        bool activate = _switchWasActive && !_isClosed && IsVisible;
        if (activate)
        {
            if (ActivateAfterSwitchForTest is { } activateForTest) activateForTest(this);
            else Activate();
        }
        // An activation Windows refused leaves the outline of an inactive pin.
        if (_chromeActive && !IsActive) SetChromeActive(false);
        host.Dispose();
        _switchWasActive = false;
        if (_isClosed) { _afterHostSwitch.Clear(); return; }
        HostSwitched?.Invoke(_switchReason);
        ViewStateChanged?.Invoke();
        Action[] queued = [.. _afterHostSwitch];
        _afterHostSwitch.Clear();
        foreach (Action action in queued)
        {
            if (_isClosed) break;
            action();
        }
    }

    private void StopSwitchWaits()
    {
        ContentRendered -= OnSwitchContentRendered;
        if (_switchFallbackTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnSwitchFallback;
            _switchFallbackTimer = null;
        }
    }

    // A pin that closes during its switch: the native window goes at once, the queued triggers are dropped.
    private void DropRetiringNative()
    {
        if (_retiringNative is not { } host) return;
        StopSwitchWaits();
        _retiringNative = null;
        _afterHostSwitch.Clear();
        _switchWasActive = false;
        CloseFeedbackSurface();
        HandOverOwnedWindows();
        host.Dispose();
    }

    private void DisposeNativeHost()
    {
        DropRetiringNative();
        if (_native is { } host)
        {
            _native = null;
            host.Dispose();
        }
    }

    // ----- Triggers that run on the WPF window after a switch -----

    internal void OpenContextMenuAfterSwitch(Point? screenPoint)
    {
        if (_isClosed || ContextMenu is not { } menu) return;
        menu.PlacementTarget = this;
        menu.Placement = screenPoint is null ? PlacementMode.Center : PlacementMode.MousePoint;
        menu.HorizontalOffset = menu.VerticalOffset = 0;
        menu.IsOpen = true;
    }

    internal void ReplayKeyAfterSwitch(Key key, ModifierKeys modifiers)
    {
        if (_isClosed || PresentationSource.FromVisual(this) is not { } source) return;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
        HandleKeyDown(args, modifiers);
    }

    internal void RecognizeAfterSwitch()
    {
        if (_isClosed) return;
        Focus();
        HandleCtrlDoubleClick();
    }

    // ----- Native window callbacks -----

    internal void OnNativeActiveChanged(bool active)
    {
        // As the WPF Activated handler: the stacking order may have changed.
        if (active && !_isClosed) ViewStateChanged?.Invoke();
    }

    internal void OnNativeGestureEnded(bool moved, bool resized)
    {
        _ = moved;
        if (_isClosed) return;
        // As EndDrag: the session records the place, the practice records a resize.
        ViewStateChanged?.Invoke();
        if (resized) NotifyPractice(PracticeEventKind.PinResized);
        // As EndDrag: the bar comes back while the pointer is still over the pin.
        if (_native is { Window.IsPointerOver: true }) ShowHoverBar();
        NativeInteractionEnded?.Invoke();
    }

    internal void OnNativeUserZoomChanged()
    {
        if (!_isClosed) UserZoomChanged?.Invoke(PracticeId);
    }

    internal void OnNativeSettled()
    {
        // As CommitCrispZoom: a settled zoom is a saved presentation change.
        if (!_isClosed) ViewStateChanged?.Invoke();
    }

    internal void OnNativeUserOpacityChanged(double target)
    {
        if (_isClosed) return;
        // The message is part of the pin: it takes the pin's opacity.
        if (_feedbackSurface is { } surface) surface.Opacity = target;
        ViewStateChanged?.Invoke();
        NotifyPractice(PracticeEventKind.OpacityChanged, target);
    }

    // The native window is gone. For the presenting host that is the pin's close (its exit ended, or Windows
    // destroyed it); the WPF window closes on the dispatcher, as a WPF exit's deferred close does.
    internal void OnNativeClosed()
    {
        if (_native is not { Window.IsClosed: true } || _windowClosed) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!_windowClosed) Close();
        });
    }

    internal void OnNativeFailed(Exception error)
    {
        NativeHostFailed?.Invoke(error);
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (_native is not { } host) return;
            if (_isClosed)
            {
                // A failed exit cannot finish itself; the pin is already closed, so its window goes now.
                if (!host.Window.IsClosed) host.Window.Dispose();
                if (!_windowClosed) Close();
                return;
            }
            SwitchToWpf(PinSwitchReason.Failure);
        });
    }

    // ----- Resident probe access (diagnostics only; no product path calls these) -----

    internal Image ResidentProbeImage => _imageElement;

    /// <summary>The probe's hover: what the native window does when the pointer enters it.</summary>
    internal void ResidentProbeHover()
    {
        if (_native is not null) OnNativeHoverChanged(true);
        else ShowHoverBar();
    }
    internal bool ResidentProbeLandingActive => _native is { } host ? host.Window.IsLanding : _landingClock is not null;
    internal bool ResidentProbeBusy => _native is { } native
        ? native.Window.IsLanding || native.Window.IsLiveZooming || native.Window.IsDragging
            || native.Window.IsOpacityFading || native.Window.IsRevealing || native.Window.IsHiding
        : _landingClock is not null || _liveZooming || _dragging
            || _resizeEdge != PinResizeEdge.None || IsMouseOver
            || _hoverBar?.IsMouseOver == true || _hoverBar?.IsKeyboardFocusWithin == true
            || _clickThroughHandle?.IsMouseOver == true || ContextMenu?.IsOpen == true
            || Math.Abs(Opacity - _state.Opacity) > 0.0001 || _chrome.Opacity < 0.9999;

    // The normal manager fits a large picture to the work area. These measurements need its
    // actual 100% pixel surface, including when part of a 4K pin lies outside a smaller monitor.
    internal void ResidentProbeInitialSize()
    {
        if (_native is { } host)
        {
            if (Math.Abs(host.Window.Zoom - 1) > 1e-9) host.Window.SetZoom(1);
            return;
        }
        _state.SetZoom(1);
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        UpdateImageRasterLayout();
    }

    internal void ResidentProbeSetZoom(double zoom)
    {
        if (_native is { } host) host.Window.SetZoom(zoom);
        else ApplyZoom(zoom, Left, Top);
    }

    internal double ResidentProbeAdjustOpacity(double delta) =>
        _native is { } host ? host.Window.FadeOpacityBy(delta) : AdjustOpacity(delta);

    internal bool ResidentProbeHasRasterCache => _imageElement.CacheMode is BitmapCache;

    /// <summary>The probe's wheel notch through the host's own wheel path, at the pin's centre.</summary>
    internal void ResidentProbeWheel(int delta)
    {
        if (_native is { } host)
        {
            RectD bounds = host.Window.Bounds;
            host.Window.Wheel(delta, new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2)));
            return;
        }
        HandleMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.MouseWheelEvent },
            ModifierKeys.None);
    }
}
