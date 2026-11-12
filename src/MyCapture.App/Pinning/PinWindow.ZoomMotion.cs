using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

// A wheel or key zoom of the WPF host: the state, readout, session and practice take the new zoom at once,
// and the window moves to it over PinZoomMotionTiming.DurationMs with the point under the pointer kept still.
// Each step places the window with one SetWindowPos (left, top, width and height together), driven by the
// DWM clock the native pin uses, never from inside CompositionTarget.Rendering. The low-quality cache stays on
// for the whole motion and the crisp picture is drawn once, after it ends (the settle timer starts then).
//
// Placing each step's origin a tick after its size (to wait for the picture of the new size) was measured on
// 2026-11-13 and drew the anchor further off, not nearer: at 1600 x 1000 the reads within 2 px of the anchor
// during the motion fell from 63-90% to 29-55%. Origin and size therefore move together.
internal sealed partial class PinWindow : INativeMotionTarget
{
    private PinZoomMotion? _zoomMotion;
    private double _shownZoom = 1, _motionZoom = 1, _motionLeft, _motionTop, _motionAnchorX, _motionAnchorY;
    private double _stepLeft, _stepTop;
    private (double Left, double Top, double Width, double Height) _motionDesktop;
    private NativePinMotionClock? _zoomClock;

    /// <summary>A wheel or key zoom is still moving the window towards the state's zoom.</summary>
    internal bool IsZoomMoving => _native is { } host ? host.Window.IsZoomMoving : _zoomMotion is { IsActive: true };

    /// <summary>The zoom the screen shows now: the state's, or a step of the motion towards it.</summary>
    internal double ShownZoom => _native is { } host ? host.Window.ShownZoom
        : _zoomMotion is { IsActive: true } ? _shownZoom : _state.Zoom;

    // The size the WPF window shows now (the state's size at rest).
    private double ShownWidthDip => _zoomMotion is { IsActive: true } ? Math.Max(1.0, _state.ImageWidthDip * _shownZoom) : _state.WidthDip;
    private double ShownHeightDip => _zoomMotion is { IsActive: true } ? Math.Max(1.0, _state.ImageHeightDip * _shownZoom) : _state.HeightDip;

    /// <summary>Replaces the zoom motion's clock (milliseconds); with it set, no DWM clock starts and tests step by hand.</summary>
    internal Func<double>? ZoomMotionClockForTest { get; set; }

    /// <summary>A DWM clock is ticking this pin's zoom motion.</summary>
    internal bool IsZoomClockRunningForTest => _zoomClock is not null;

    private double MotionNow => ZoomMotionClockForTest?.Invoke() ?? PinZoomMotionTiming.Now();

    // Text selection draws its own text at the zoom it is given, so its zoom stays immediate (as before).
    private bool CanMoveZoom => FluidMotion.AnimationsEnabled && _handle != IntPtr.Zero && IsVisible && !_isClosed
        && !_state.IsCollapsed && !IsTextSelectionMode && !IsRecognizingSelection;

    /// <summary>
    /// A user zoom to the state's zoom (already set), from <paramref name="oldZoom"/>, keeping the DIP point
    /// (<paramref name="anchorX"/>, <paramref name="anchorY"/>) where it is on the screen.
    /// </summary>
    private void ZoomToState(double oldZoom, double anchorX, double anchorY)
    {
        double shown = _zoomMotion is { IsActive: true } ? _shownZoom : oldZoom;
        if (!CanMoveZoom)
        {
            StopZoomMotion();
            if (!CanPlaceZoomAtOnce)
            {
                (double left, double top) = PinGeometry.AnchorTopLeftForZoom(Left, Top, shown, _state.Zoom, anchorX, anchorY);
                ApplyZoom(_state.Zoom, left, top);
                return;
            }
            // Without motion the zoom is still one placement (left, top, width and height together), not four.
            CaptureZoomBase(shown, anchorX, anchorY);
            ShowZoomStep();
            BeginLiveZoom();
            ShowFeedback($"{_state.Zoom * 100:0}%");
            return;
        }
        CaptureZoomBase(shown, anchorX, anchorY);
        _zoomMotion ??= PinZoomMotionTiming.Create();
        _shownZoom = _zoomMotion.Retarget(shown, _state.Zoom, MotionNow);
        BeginLiveZoom(settle: false);
        ShowZoomStep();
        if (_zoomMotion.IsActive) StartZoomClock();
        else EndZoomMotion();
        ShowFeedback($"{_state.Zoom * 100:0}%");
    }

    private bool CanPlaceZoomAtOnce => _handle != IntPtr.Zero && IsVisible && !_isClosed && !_state.IsCollapsed;

    // The geometry the zoom's steps are measured from: the window now, the zoom it shows, the still point.
    private void CaptureZoomBase(double shown, double anchorX, double anchorY)
    {
        // A notch during the motion starts from the exact origin of the step on the screen, not the window's
        // rounded pixels, so rounding does not add up over quick notches.
        (_motionLeft, _motionTop) = _zoomMotion is { IsActive: true } ? (_stepLeft, _stepTop) : (Left, Top);
        _motionZoom = shown;
        _motionAnchorX = anchorX; _motionAnchorY = anchorY;
        _motionDesktop = VirtualDesktopDip();
    }

    void INativeMotionTarget.OnNativeMotionTick(NativePinMotionClock clock, int result)
    {
        if (!ReferenceEquals(clock, _zoomClock)) return;
        if (_isClosed || result < 0)
        {
            FinishZoomMotion();
            return;
        }
        TickZoomMotion();
    }

    /// <summary>Test hook: one motion tick at the injected clock's time.</summary>
    internal void TickZoomMotionForTest()
    {
        if (_native is { } host) host.Window.TickMotionForTest();
        else TickZoomMotion();
    }

    private void TickZoomMotion()
    {
        if (_zoomMotion is not { IsActive: true } motion)
        {
            StopZoomClock();
            return;
        }
        _shownZoom = motion.Sample(MotionNow);
        ShowZoomStep();
        if (!motion.IsActive) EndZoomMotion();
    }

    // The window at the shown zoom: one SetWindowPos for the place and the size, the anchor kept still.
    private void ShowZoomStep()
    {
        double width = ShownWidthDip, height = ShownHeightDip;
        (double left, double top) = PinGeometry.AnchorTopLeftForZoom(_motionLeft, _motionTop, _motionZoom,
            _zoomMotion is { IsActive: true } ? _shownZoom : _state.Zoom, _motionAnchorX, _motionAnchorY);
        (left, top) = PinGeometry.KeepGrabbable(left, top, width, height,
            _motionDesktop.Left, _motionDesktop.Top, _motionDesktop.Width, _motionDesktop.Height, GrabMarginDip);
        (_stepLeft, _stepTop) = (left, top);
        PlaceDip(left, top, width, height);
        if (_zoomMotion is not { IsActive: true })
        {
            // At rest the window's properties take the exact DIP geometry. PlaceDip used WPF's own conversion of
            // these values, so this sync moves nothing.
            Width = _state.WidthDip;
            Height = _state.HeightDip;
            Left = left;
            Top = top;
        }
    }

    // One SetWindowPos with the physical rectangle WPF itself gives these DIP values as the window's Left, Top,
    // Width and Height (DIP times the window's scale, rounded half away from zero), so the properties set
    // afterwards land on the same pixels and cause no second placement. A placement relative to the window's
    // current pixels rounded a fractional Top (119.79 on 120) differently and moved the window again by a pixel.
    private void PlaceDip(double left, double top, double width, double height)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int x = ToDevice(left * dpi.DpiScaleX);
        int y = ToDevice(top * dpi.DpiScaleY);
        int w = Math.Max(1, ToDevice(width * dpi.DpiScaleX));
        int h = Math.Max(1, ToDevice(height * dpi.DpiScaleY));
        _ = NativePinWindowInterop.SetWindowPos(_handle, IntPtr.Zero, x, y, w, h,
            NativePinWindowInterop.NoZOrder | NativePinWindowInterop.NoActivate);
    }

    // WPF's logical-to-device rounding for a window's position and size.
    private static int ToDevice(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private void StartZoomClock()
    {
        if (_zoomClock is not null || ZoomMotionClockForTest is not null) return;
        _zoomClock = new NativePinMotionClock(Dispatcher, this);
    }

    private void StopZoomClock()
    {
        NativePinMotionClock? clock = _zoomClock;
        _zoomClock = null;
        clock?.Stop();
    }

    // The motion has reached the state's zoom: the clock stops and the crisp picture follows once, as after a notch.
    private void EndZoomMotion()
    {
        StopZoomClock();
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
    }

    /// <summary>Ends a running zoom motion at the state's zoom now (another gesture or change replaces it).</summary>
    internal void FinishZoomMotion()
    {
        if (_native is { } host)
        {
            host.Window.FinishZoomMotion();
            return;
        }
        if (_zoomMotion is not { IsActive: true } motion) return;
        _shownZoom = motion.Finish();
        if (!_isClosed && _handle != IntPtr.Zero) ShowZoomStep();
        EndZoomMotion();
    }

    // Drops a running motion without placing the window: the caller sets the geometry itself.
    private void StopZoomMotion()
    {
        _zoomMotion?.Finish();
        StopZoomClock();
    }
}
