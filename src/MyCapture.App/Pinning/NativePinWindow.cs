using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// A native layered HWND, one immutable source, and one display DIB: the presentation of a
/// lightweight image pin (<see cref="NativePinHost"/>) and of the resident probe's measurement pins.
/// It has no WPF window, HwndSource, visual tree, application-created Direct3D picture target or
/// closed-pin history. Windows composition can still retain its own system/GPU backing surfaces.
/// HWND/GDI operations and displayed-pixel writes belong to the creating STA. A single
/// cancellable worker fills only its own fresh section before the STA adopts that section.
/// The DIB and the HWND hold only the part of the surface that the screens around it can show
/// (<see cref="NativePinRaster.View.Held"/>): while the size is changing (<see cref="LiveViewportEnabled"/>)
/// and at rest (<see cref="ScreenBoundedRest"/>), so neither a notch nor a settled picture costs what
/// the zoom makes of the picture. For a pin on one screen and not larger than it that part is the whole
/// surface. <see cref="Bounds"/>, <see cref="PixelWidth"/> and <see cref="PixelHeight"/> always
/// describe the whole surface.
/// </summary>
internal sealed class NativePinWindow : IDisposable, INativeMotionTarget
{
    // CS_DBLCLKS: a second press inside the double-click time arrives as WM_LBUTTONDBLCLK.
    private const string ClassName = "MyCapture.PinNative.2";
    private const uint DoubleClicks = 0x0008;
    // A reveal or landing asked for and not drawn yet: its clock starts at the first motion tick, so the
    // rest of the opening turn (and whatever the dispatcher runs before that tick) does not eat the motion.
    private const long PendingStart = -1;
    // A reveal presents its first visible step (one 60 Hz frame in) synchronously, in the opening turn.
    // Its clock then starts at the first motion tick as if that step was shown a frame earlier, so the
    // fade continues one frame on from the picture already on the screen: never back, never a stall.
    internal const double RevealFirstFrameMs = 1000d / 60;
    // The longest the first frame waits for MotionReady (the landing ornament's first frame) before the
    // reveal and landing start anyway.
    internal const double MotionHoldLimitMs = 250;
    private static readonly object ClassGate = new();
    private static readonly NativePinWindowInterop.WindowProcedure Procedure = DispatchMessage;
    private static bool _classRegistered;
    private static int _liveWindows;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _settleTimer, _settlePollTimer;
    private GCHandle _self;
    private NativePinWindowDib? _dib;
    // What _dib and the HWND hold of the surface; the whole of it except in a live frame.
    private NativePinRaster.View _view;
    private NativePinEdgeCache? _edges;
    private NativePinSettleWork? _settleWork;
    private Task _drainTask = Task.CompletedTask;
    private TaskCompletionSource _settled = CompletedSignal();
    private bool _settleRequested;
    private NativePinMotionClock? _motionClock;
    private Task _motionDrain = Task.CompletedTask;
    private long _lastZoomStarted;
    private int _peakWorkerBufferCount;
    private readonly Dictionary<DispatcherOperation, long> _settleOperations = new();
    private bool _observingSettle;
    private bool _countedWindow, _ready, _closing, _hover, _active, _dragging, _failureReported;
    private bool _revealing, _colorAnimating, _opacityAnimating;
    private int _left, _top, _wheelRemainder;
    private long _landingStarted, _revealStarted, _exitStarted, _colorStarted, _hideStarted, _findStarted, _opacityStarted, _holdStarted;
    private double _motionOpacity = 1, _exitStartOpacity = 1, _hideStartOpacity = 1, _opacityFrom = 1,
        _contentScale = 1, _gestureDpi, _gestureZoom;
    private Point _gesturePointer, _lastGesturePointer, _gestureAnchorDip;
    private RectD _gestureBounds;
    private bool _gestureMoved;
    // A wheel or key zoom moves the zoom shown on the screen to the state's zoom (PinZoomMotion); the point
    // under the pointer stays where it is, so the surface's top-left follows from where the motion started.
    private PinZoomMotion? _zoomMotion;
    private double _shownZoom = 1, _motionZoom = 1;
    // The motion's base and the last step's place before rounding to pixels.
    private double _motionLeft, _motionTop, _stepLeft, _stepTop;
    private Point _motionAnchor;
    // Snapping while a pin moves (#259): read once when the move starts, as the WPF drag does.
    private IReadOnlyList<PinSnap.Target> _snapTargets = [];
    private IReadOnlyList<PinDisplayArea> _snapDisplays = [];
    private bool _snapping;
    // The screens' physical rectangles that bound what the pin holds; read again on WM_DISPLAYCHANGE.
    private RectD[]? _screens;
    // The edge-flicker guard (WM_DPICHANGED): the scale the last change left, when it happened, whether
    // the pin has moved since, and whether a change is being applied right now.
    private double _dpiLeftScale;
    private long _dpiChangedAt;
    private bool _movedSinceDpiChange = true, _dpiChanging;
    // A scale Windows gave that the guard held back; a move that ends applies it.
    private double? _heldDpi;
    private PinResizeEdge _resizeEdge;
    private Color _baseColor, _colorFrom, _colorTo, _outlineColor;
    private Brush? _accent = new SolidColorBrush(PinFrameColor.FallbackAccent), _neutral = new SolidColorBrush(PinFrameColor.FallbackNeutral);
    // A pin's presentation: the pin's own state and zoom step, a fading opacity, and a position kept grabbable.
    private readonly bool _product;

    internal NativePinWindow(BitmapSource source, double leftPhysical, double topPhysical,
        double zoom = 1, double dpiScale = 1, AppTheme theme = AppTheme.Glass,
        double? imageWidthDip = null, double? imageHeightDip = null)
        : this(NativePinPixelSource.FromBitmap(source), leftPhysical, topPhysical, zoom, dpiScale,
            theme, imageWidthDip, imageHeightDip) { }

    /// <summary>Takes ownership of the immutable source; closing releases that ownership.</summary>
    internal NativePinWindow(NativePinPixelSource source, double leftPhysical, double topPhysical,
        double zoom = 1, double dpiScale = 1, AppTheme theme = AppTheme.Glass,
        double? imageWidthDip = null, double? imageHeightDip = null)
        : this(source, CreateOwnedState(source, leftPhysical, topPhysical, zoom, dpiScale, imageWidthDip, imageHeightDip),
            leftPhysical, topPhysical, dpiScale, theme, null, null, string.Empty, product: false) { }

    /// <summary>
    /// A pin's presentation. It shares <paramref name="state"/> with the pin (zoom, zoom step,
    /// opacity, click-through, hidden) and takes ownership of the immutable source.
    /// </summary>
    internal NativePinWindow(NativePinPixelSource source, PinViewState state, double leftPhysical, double topPhysical,
        double dpiScale, AppTheme theme, Brush? accent, Brush? neutral, string title)
        : this(source, ValidatePinState(source, state, leftPhysical, topPhysical, dpiScale),
            leftPhysical, topPhysical, dpiScale, theme, accent, neutral, title, product: true) { }

    private NativePinWindow(NativePinPixelSource source, PinViewState state, double leftPhysical, double topPhysical,
        double dpiScale, AppTheme theme, Brush? accent, Brush? neutral, string title, bool product)
    {
        State = state;
        _product = product;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Source = source;
        DpiScale = dpiScale;
        _left = Coordinate(leftPhysical); _top = Coordinate(topPhysical);
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
        _settleTimer.Tick += OnSettleTick;
        _settlePollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(4) };
        _settlePollTimer.Tick += OnSettlePollTick;
        Theme = theme;
        if (product) { _accent = accent; _neutral = neutral; }
        else ResolveTheme();
        _baseColor = _outlineColor = ResolveBaseColor();
        try
        {
            EnsureClass();
            _self = GCHandle.Alloc(this);
            Handle = NativePinWindowInterop.CreateWindowExW(
                NativePinWindowInterop.Layered | NativePinWindowInterop.ToolWindow | NativePinWindowInterop.Topmost,
                ClassName, title, NativePinWindowInterop.Popup, _left, _top,
                DesiredWidth, DesiredHeight, IntPtr.Zero, IntPtr.Zero,
                NativePinWindowInterop.GetModuleHandleW(null), GCHandle.ToIntPtr(_self));
            if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _countedWindow = true;
            Interlocked.Increment(ref _liveWindows);
            // Pins stay in screen captures, as WPF pins do; only a leaving pin is excluded (CloseWithExit).
            // The monitor the window landed on decides its scale, as it decides a WPF window's.
            if (product && (ReadWindowScaleForTest is { } read ? read(Handle) : WindowScale(Handle)) is { } actual
                && Math.Abs(actual - DpiScale) > 1e-9)
                DpiScale = actual;
            WindowDpiScale = DpiScale;
            _screens = ReadScreens();
            _ready = true;
            bool exactSize = DesiredWidth == source.Width && DesiredHeight == source.Height;
            Render(highQuality: exactSize);
            if (!exactSize) { InvalidateSettle(); ForceSettle(); }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Replaces the window's own monitor scale reading; returning null keeps the scale passed in.</summary>
    internal static Func<IntPtr, double?>? ReadWindowScaleForTest { get; set; }

    private static double? WindowScale(IntPtr handle)
    {
        uint dpi = NativePinWindowInterop.GetDpiForWindow(handle);
        return dpi == 0 ? null : dpi / 96d;
    }

    // ----- Callbacks to the pin's host; none is set on a measurement pin. -----

    /// <summary>A key went down; returning true consumes it. Without a handler, + and - zoom.</summary>
    internal Func<int, bool>? KeyHandler { get; set; }
    /// <summary>A left press (clicks 1 or 2); returning true consumes it, so no move or resize starts.</summary>
    internal Func<NativePinPress, bool>? PressHandler { get; set; }
    /// <summary>WM_CONTEXTMENU: the screen point, or null from the keyboard.</summary>
    internal Action<Point?>? ContextMenuRequested { get; set; }
    /// <summary>WM_CLOSE (Alt+F4). Without a handler the window closes itself.</summary>
    internal Action? CloseRequested { get; set; }
    internal Action<bool>? ActiveChanged { get; set; }
    /// <summary>A press ended: whether a gesture held the pointer, and whether a resize changed the zoom.</summary>
    internal Action<bool, bool>? GestureEnded { get; set; }
    /// <summary>The user changed the zoom (wheel, keys, edge resize).</summary>
    internal Action? UserZoomChanged { get; set; }
    /// <summary>A live zoom has settled into its full-quality picture.</summary>
    internal Action? Settled { get; set; }
    internal Action<double>? UserOpacityChanged { get; set; }
    /// <summary>The window's rectangle or scale changed (move, zoom, resize, monitor scale).</summary>
    internal Action? BoundsChanged { get; set; }
    /// <summary>The window was shown (true) or finished hiding (false).</summary>
    internal Action<bool>? ShownChanged { get; set; }
    /// <summary>The pointer entered (true) or left (false) the window.</summary>
    internal Action<bool>? HoverChanged { get; set; }
    /// <summary>A user zoom asks for its readout with the zoom it shows; a step at a limit still asks.</summary>
    internal Action<double>? ZoomShown { get; set; }
    /// <summary>A Ctrl+wheel opacity change asks for its readout with the opacity it moves to.</summary>
    internal Action<double>? OpacityShown { get; set; }
    /// <summary>
    /// Asked at each motion tick while a reveal or landing waits to start; false holds the first frame
    /// (nothing is presented) until it returns true or <see cref="MotionHoldLimitMs"/> has passed.
    /// </summary>
    internal Func<bool>? MotionReady { get; set; }
    /// <summary>The reveal and landing clocks have just started (the tick that ends the hold).</summary>
    internal Action? MotionStarted { get; set; }
    /// <summary>The window is gone (an exit ended, or it was closed).</summary>
    internal Action? Closed { get; set; }
    /// <summary>The first failure after creation (present, settle, motion clock or a callback).</summary>
    internal Action<Exception>? Failed { get; set; }

    internal IntPtr Handle { get; private set; }
    internal NativePinPixelSource? Source { get; private set; }
    internal PinViewState State { get; }
    internal double Zoom => State.Zoom;
    internal double Opacity => State.Opacity;
    internal double EffectiveOpacity => DisplayedOpacity * _motionOpacity;
    /// <summary>The pin opacity on the screen now: the state's, or the value of a fade towards it.</summary>
    internal double DisplayedOpacity => _opacityAnimating
        ? _opacityFrom + ((State.Opacity - _opacityFrom) * FluidMotion.SoftLandingEasing.Ease(
            Math.Clamp(Elapsed(_opacityStarted) / FluidMotion.NormalDuration.TimeSpan.TotalMilliseconds, 0, 1)))
        : State.Opacity;
    internal bool IsHiding => _hideStarted != 0;
    internal bool IsRevealing => _revealing;
    internal bool IsFindCueRunning => _findStarted != 0;
    internal bool IsOpacityFading => _opacityAnimating;
    internal bool IsResizing => _dragging && _resizeEdge != PinResizeEdge.None;
    internal bool IsActive => _active;
    internal double DpiScale { get; private set; }
    internal AppTheme Theme { get; private set; }
    internal Color OutlineColor => _outlineColor;
    internal bool IsVisible { get; private set; }
    internal bool IsClosed { get; private set; }
    internal bool IsLanding => _landingStarted != 0;
    internal bool IsPointerOver => _hover;
    internal bool IsLiveZooming { get; private set; }
    internal bool IsExiting => _exitStarted != 0;
    internal bool IsCaptureExcluded { get; private set; }
    internal bool IsDragging => _dragging;
    internal int PixelWidth => _dib is null ? 0 : _view.SurfaceWidth;
    internal int PixelHeight => _dib is null ? 0 : _view.SurfaceHeight;
    internal int Stride => _dib?.Stride ?? 0;
    /// <summary>The bytes of the display DIB as it is now: the part of the surface the pin holds.</summary>
    internal long DisplayBytes => _dib?.ByteLength ?? 0;
    internal RectD Bounds => new(_left, _top, PixelWidth, PixelHeight);
    /// <summary>The HWND's rectangle: the part of <see cref="Bounds"/> the pin holds (all of it on one screen).</summary>
    internal RectD DisplayedBounds => new(_left + _view.Left, _top + _view.Top, _dib?.Width ?? 0, _dib?.Height ?? 0);
    internal bool IsDisplayClipped => _dib is not null && !_view.IsWhole;
    // Bare UpdateLayeredWindow plus DwmFlush on the measuring PC (3440x1440, 120 Hz), size changing
    // on every call: whole surface 25 ms at 4288x2412, 51 ms at 6248x3515, 84 ms at 10024x5639;
    // the part on the monitor 8 ms at every one of those sizes, 17 ms while that part still grows.
    internal static bool LiveViewportEnabled { get; set; } = true;
    /// <summary>
    /// The settled picture holds only what the screens around the pin can show (design section 7). Off,
    /// it is the whole zoomed surface, as before; the resident probe measures both in one build.
    /// </summary>
    internal static bool ScreenBoundedRest { get; set; } = true;
    /// <summary>
    /// The most newly visible pixels a move of a settled, clipped pin draws at full quality on this thread
    /// (measured about 95 ns a pixel with the outline, so about 9 ms; the present of a screen-wide picture adds
    /// about 5 ms). More than that, and the move shows a live frame and settles after it ends.
    /// </summary>
    internal static long ReholdPixelLimit { get; set; } = 96 * 1024;
    /// <summary>
    /// Stands in for the screens: the clip of live frames and of the settled picture. Tests keep their
    /// windows off every screen.
    /// </summary>
    internal RectD? LiveClipForTest { get; set; }
    /// <summary>The scale Windows last gave this window (WM_DPICHANGED); it can differ from <see cref="DpiScale"/>.</summary>
    internal double WindowDpiScale { get; private set; }
    internal int DpiChangeCount { get; private set; }
    /// <summary>Scale changes not applied: a change back that the pin's own new size caused.</summary>
    internal int IgnoredDpiChangeCount { get; private set; }
    /// <summary>The scale an ignored change asked for, applied when the current move ends.</summary>
    internal double? HeldDpiScale => _heldDpi;
    /// <summary>How long a change back to the scale just left counts as the edge flicker, if the pin has not moved.</summary>
    internal const double DpiFlipWindowMilliseconds = 500;
    internal double LastMoveMilliseconds { get; private set; }
    /// <summary>The last move of a settled, clipped pin drew a live frame (and settles after the move) rather than strips.</summary>
    internal bool LastMoveDrewLive { get; private set; }
    /// <summary>The pixels the last move of a settled pin newly needed (drawn as strips, or shown by a live frame).</summary>
    internal long LastMoveFreshPixels { get; private set; }
    internal int ReholdCount { get; private set; }
    internal long LastReholdPixels { get; private set; }
    internal double LastReholdMilliseconds { get; private set; }
    /// <summary>The part of <see cref="LastReholdMilliseconds"/> spent on the outline of the new part.</summary>
    internal double LastReholdOutlineMilliseconds { get; private set; }
    internal int RasterCount { get; private set; }
    internal int PresentCount { get; private set; }
    internal int MotionTickCount { get; private set; }
    internal int? LastMotionClockHResult { get; private set; }
    internal static int LiveMotionClockCount => NativePinMotionClock.LiveCount;
    internal int RequestedGeneration { get; private set; }
    internal int SettledGeneration { get; private set; }
    internal int WorkerBufferCount => _settleWork?.BufferCount ?? 0;
    internal int PeakWorkerBufferCount => Math.Max(_peakWorkerBufferCount, _settleWork?.PeakBufferCount ?? 0);
    internal static int LiveWorkerBufferCount => NativePinSettleBuffer.LiveCount;
    internal static long LiveWorkerBufferBytes => NativePinSettleBuffer.LiveBytes;
    internal static int LiveDisplaySectionCount => NativePinWindowDib.LiveSectionCount;
    internal static long LiveDisplaySectionBytes => NativePinWindowDib.LiveSectionBytes;
    internal long DisplaySectionBytes => _dib?.SectionBytes ?? 0;
    /// <summary>Display pixels in memory Windows allocated for the DIB: private commit, unlike an adopted section.</summary>
    internal long DisplayPrivateBytes => _dib is null ? 0 : _dib.ByteLength - _dib.SectionBytes;
    internal static long LiveDisplayPrivateBytes => NativePinWindowDib.LivePrivateBytes;
    internal long WorkerSectionBytes => _settleWork?.BufferBytes ?? 0;
    internal long EdgeCacheBytes => _edges?.ByteLength ?? 0;
    internal double LastLiveUiMilliseconds { get; private set; }
    internal double LastLiveSubmitMilliseconds { get; private set; }
    // The parts of the last live frame's raster, so a measurement can say which one follows the pixel count.
    internal double LastLiveAllocateMilliseconds { get; private set; }
    internal double LastLiveStretchMilliseconds { get; private set; }
    internal double LastLiveEdgeMilliseconds { get; private set; }
    internal double LastSettleUiMilliseconds { get; private set; }
    /// <summary>Maximum acceptance/adoption callback cost, not the whole settle interval.</summary>
    internal double LongestSettleUiMilliseconds { get; private set; }
    internal double LongestSettleAdoptionMilliseconds => LongestSettleUiMilliseconds;
    internal double LongestSettleDispatcherOperationMilliseconds { get; private set; }
    internal int SettleDispatcherOperationCount { get; private set; }
    internal double LastSettleMilliseconds { get; private set; }
    internal Action<CancellationToken>? BeforeSettleRasterForTest { get; set; }
    /// <summary>The worker has a finished picture that this thread has not adopted or discarded yet.</summary>
    internal bool IsSettleResultWaitingForTest => _settleWork is { IsReadyToComplete: true, BufferCount: 1 };
    // R4 alternating candidate benchmark (including GdiFlush), 4K at 150%:
    // GDI nearest 9.811 ms median / 9.979 ms max; integer bilinear 68.907 / 71.829.
    // The live frame favors response time; the worker replaces it with Lanczos at rest.
    internal static NativePinLiveMode LiveMode { get; set; } = NativePinLiveMode.GdiNearest;
    internal byte LastPresentedAlpha { get; private set; }
    internal double LastRasterMilliseconds { get; private set; }
    internal double LastPresentMilliseconds { get; private set; }
    internal string? LastPresentError { get; private set; }
    internal int? LastFlushHResult { get; private set; }
    internal static int LiveWindowCount => Volatile.Read(ref _liveWindows);
    internal static int LiveDibCount => NativePinWindowDib.LiveBitmapCount;
    internal static int LiveDcCount => NativePinWindowDib.LiveDcCount;
    internal event Action? Presented;
    private int DesiredWidth => Math.Max(1, checked((int)Math.Round(ShownWidthDip * DpiScale)));
    private int DesiredHeight => Math.Max(1, checked((int)Math.Round(ShownHeightDip * DpiScale)));
    private double ShownWidthDip => IsZoomMoving ? Math.Max(1.0, State.ImageWidthDip * _shownZoom) : State.WidthDip;
    private double ShownHeightDip => IsZoomMoving ? Math.Max(1.0, State.ImageHeightDip * _shownZoom) : State.HeightDip;

    /// <summary>A wheel or key zoom is still moving the pin on the screen towards <see cref="Zoom"/>.</summary>
    internal bool IsZoomMoving => _zoomMotion is { IsActive: true };

    /// <summary>The zoom the screen shows now: <see cref="Zoom"/>, or a step of the motion towards it.</summary>
    internal double ShownZoom => IsZoomMoving ? _shownZoom : Zoom;

    /// <summary>Replaces the zoom motion's clock (milliseconds); tests step it by hand.</summary>
    internal Func<double>? ZoomMotionClockForTest { get; set; }

    private double MotionNow => ZoomMotionClockForTest?.Invoke() ?? PinZoomMotionTiming.Now();

    internal void Show()
    {
        VerifyOpen();
        if (IsVisible) return;
        _revealing = FluidMotion.AnimationsEnabled;
        _revealStarted = PendingStart;
        _holdStarted = 0;
        _motionOpacity = _revealing ? RevealOpacity(RevealFirstFrameMs) : 1;
        IsVisible = true;
        try
        {
            Present();
            _ = NativePinWindowInterop.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE.
            if (_revealing) StartMotion();
        }
        catch { IsVisible = false; throw; }
        ShownChanged?.Invoke(true);
    }

    internal void StartLanding()
    {
        VerifyOpen();
        if (!IsVisible || IsHiding || IsExiting) return;
        _findStarted = 0;
        if (!(_revealing && _revealStarted == PendingStart)) _holdStarted = 0;
        _landingStarted = PendingStart;
        StartMotion();
    }

    /// <summary>Hides the window after the fast fade, keeping its place, zoom and pictures.</summary>
    internal void Hide()
    {
        VerifyOpen();
        if (!IsVisible || IsHiding || IsExiting) return;
        EndGesture();
        FinishZoomMotion();
        _landingStarted = _findStarted = 0;
        _revealing = false;
        if (!FluidMotion.AnimationsEnabled) { FinishHide(); return; }
        _hideStartOpacity = _motionOpacity;
        _hideStarted = Stopwatch.GetTimestamp();
        StartMotion();
    }

    /// <summary>Shows a hidden (or hiding) window with the reveal fade, as a WPF pin's ShowPin does.</summary>
    internal void Reveal()
    {
        VerifyOpen();
        if (IsExiting) return;
        if (!IsVisible) { Show(); return; }
        _hideStarted = 0;
        _revealing = FluidMotion.AnimationsEnabled;
        _revealStarted = PendingStart;
        _holdStarted = 0;
        _motionOpacity = _revealing ? RevealOpacity(RevealFirstFrameMs) : 1;
        Present();
        if (_revealing) StartMotion();
    }

    private void FinishHide()
    {
        _hideStarted = 0;
        _motionOpacity = 1;
        IsVisible = false;
        _ = NativePinWindowInterop.ShowWindow(Handle, 0); // SW_HIDE.
        ShownChanged?.Invoke(false);
    }

    /// <summary>Two pulses of the landing outline (one second of accent under reduced motion), on a quiet baseline.</summary>
    internal void PlayFindCue()
    {
        VerifyOpen();
        if (!IsVisible || IsHiding || IsExiting) return;
        _landingStarted = 0;
        _findStarted = Stopwatch.GetTimestamp();
        _colorAnimating = false;
        _baseColor = ResolveBaseColor();
        StartMotion();
        UpdateMotion();
    }

    internal void SetZoom(double zoom, Point? anchorPhysical = null, bool live = false)
    {
        VerifyOpen(); ValidateScale(zoom);
        FinishZoomMotion();
        double old = Zoom;
        State.SetZoom(zoom);
        ApplyZoomChange(old, anchorPhysical, live, user: false);
    }

    /// <summary>A user zoom by the pin's zoom step (keys, menu), anchored at <paramref name="anchorPhysical"/>.</summary>
    internal void StepZoom(int notches, Point? anchorPhysical = null)
    {
        VerifyOpen();
        if (State.IsClickThrough || IsExiting || notches == 0) return;
        double old = Zoom;
        State.ApplyZoomStep(notches);
        ApplyZoomChange(old, anchorPhysical ?? Center, live: true, user: true);
        ZoomShown?.Invoke(Zoom);
    }

    /// <summary>A user reset to 100% around the pin's centre.</summary>
    internal void ResetZoom()
    {
        VerifyOpen();
        if (State.IsClickThrough || IsExiting) return;
        double old = Zoom;
        State.ResetZoom();
        ApplyZoomChange(old, Center, live: true, user: true);
        ZoomShown?.Invoke(Zoom);
    }

    private Point Center => new(_left + (PixelWidth / 2d), _top + (PixelHeight / 2d));

    private void ApplyZoomChange(double old, Point? anchorPhysical, bool live, bool user)
    {
        // The state's step may land where it started (a limit); a WPF pin then only shows the readout.
        if (user && Math.Abs(Zoom - old) < 1e-9) return;
        long started = Stopwatch.GetTimestamp();
        if (user) _movedSinceDpiChange = true;
        InvalidateSettle();
        Point anchor = anchorPhysical ?? new Point(_left, _top);
        if (user && live && _product && IsVisible && FluidMotion.AnimationsEnabled)
        {
            StartZoomMotion(IsZoomMoving ? _shownZoom : old, anchor);
            LastLiveSubmitMilliseconds = LastLiveUiMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            BoundsChanged?.Invoke();
            UserZoomChanged?.Invoke();
            return;
        }
        FinishZoomMotion(present: false);
        (double left, double top) = PinGeometry.AnchorTopLeftForZoom(_left, _top, old, Zoom, anchor.X, anchor.Y);
        if (user && _product) (left, top) = KeepGrabbable(left, top, DesiredWidth, DesiredHeight);
        _left = Coordinate(left); _top = Coordinate(top);
        Render(highQuality: false);
        if (IsVisible) Present();
        LastLiveSubmitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (live) BeginLiveZoom(); else ForceSettle();
        LastLiveUiMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        BoundsChanged?.Invoke();
        if (user) UserZoomChanged?.Invoke();
    }

    // The motion starts (or turns) from the zoom shown now, around the anchor, and draws its first step now.
    private void StartZoomMotion(double shown, Point anchor)
    {
        _zoomMotion ??= PinZoomMotionTiming.Create();
        // A notch during the motion starts from the step's exact place, not its rounded pixels, so quick
        // notches do not walk the anchor by a rounding each.
        (_motionLeft, _motionTop) = IsZoomMoving ? (_stepLeft, _stepTop) : (_left, _top);
        _motionZoom = shown; _motionAnchor = anchor;
        _shownZoom = _zoomMotion.Retarget(shown, Zoom, MotionNow);
        ShowZoomStep();
        if (IsZoomMoving) StartMotion();
        else BeginLiveZoom();
    }

    // The surface at the shown zoom with the anchor where it was when the motion started, drawn as a live frame.
    private void ShowZoomStep()
    {
        PlaceZoomStep();
        Render(highQuality: false);
        if (IsVisible) Present();
    }

    private void PlaceZoomStep()
    {
        (double left, double top) = PinGeometry.AnchorTopLeftForZoom(_motionLeft, _motionTop, _motionZoom,
            ShownZoom, _motionAnchor.X, _motionAnchor.Y);
        if (_product) (left, top) = KeepGrabbable(left, top, DesiredWidth, DesiredHeight);
        (_stepLeft, _stepTop) = (left, top);
        _left = Coordinate(left); _top = Coordinate(top);
    }

    /// <summary>Ends a running zoom motion at the state's zoom, drawn now; the full-quality picture follows as after a notch.</summary>
    internal void FinishZoomMotion() => FinishZoomMotion(present: true);

    private void FinishZoomMotion(bool present)
    {
        if (_zoomMotion is not { IsActive: true } motion) return;
        _shownZoom = motion.Finish();
        if (!present || IsClosed) return;
        ShowZoomStep();
        BeginLiveZoom();
        BoundsChanged?.Invoke();
    }

    /// <summary>Test hook: one motion tick, as the DWM clock gives it.</summary>
    internal void TickMotionForTest() => UpdateMotion();

    // As PinWindow.ApplyPositionKeepingGrabbable: some of the pin stays on the desktop to grab.
    private (double Left, double Top) KeepGrabbable(double left, double top, double width, double height)
    {
        RectD desktop = LiveClipForTest ?? VirtualScreen();
        return PinGeometry.KeepGrabbable(left, top, width, height,
            desktop.Left, desktop.Top, desktop.Width, desktop.Height, PinWindow.GrabMarginDip * DpiScale);
    }

    private static RectD VirtualScreen() => new(NativePinWindowInterop.GetSystemMetrics(76), NativePinWindowInterop.GetSystemMetrics(77),
        NativePinWindowInterop.GetSystemMetrics(78), NativePinWindowInterop.GetSystemMetrics(79));

    internal void Wheel(int delta, Point anchorPhysical, bool control = false)
    {
        VerifyOpen();
        if (_product && (State.IsClickThrough || IsExiting)) return;
        _wheelRemainder += delta;
        int notches = _wheelRemainder / 120;
        _wheelRemainder %= 120;
        if (notches == 0) return;
        if (!_product)
        {
            if (control) AdjustOpacity(notches * .1);
            else SetZoom(PinGeometry.StepZoom(Zoom, notches, .1), anchorPhysical, live: true);
            return;
        }
        if (control) OpacityShown?.Invoke(FadeOpacityBy(notches * PinWindow.OpacityStep));
        else
        {
            double old = Zoom;
            State.ApplyZoomStep(notches);
            ApplyZoomChange(old, anchorPhysical, live: true, user: true);
            ZoomShown?.Invoke(Zoom);
        }
    }

    /// <summary>A user opacity change: the state moves now, the window fades to it over the normal duration.</summary>
    internal double FadeOpacityBy(double delta)
    {
        VerifyOpen();
        double from = DisplayedOpacity;
        double target = State.AdjustOpacity(delta);
        _opacityAnimating = FluidMotion.AnimationsEnabled && Math.Abs(target - from) > 1e-9;
        if (_opacityAnimating)
        {
            _opacityFrom = from;
            _opacityStarted = Stopwatch.GetTimestamp();
            StartMotion();
        }
        if (IsVisible) Present();
        UserOpacityChanged?.Invoke(target);
        return target;
    }

    internal void ForceSettle()
    {
        VerifyOpen();
        _settleTimer.Stop();
        if (!IsLiveZooming || IsExiting) return;
        _settleRequested = true;
        StartSettleIfReady();
    }

    internal Task WaitForSettledAsync() { _dispatcher.VerifyAccess(); return _settled.Task; }
    internal Task DrainAsync()
    {
        _dispatcher.VerifyAccess();
        return Task.WhenAll(_settleWork?.Drained ?? _drainTask, _motionDrain,
            _motionClock?.Completion ?? Task.CompletedTask);
    }

    internal void AdjustOpacity(double delta) => SetOpacity(State.Opacity + delta);

    internal void SetOpacity(double opacity)
    {
        VerifyOpen();
        if (!double.IsFinite(opacity)) throw new ArgumentOutOfRangeException(nameof(opacity));
        State.SetOpacity(opacity);
        _opacityAnimating = false;
        // Reuse the very same DIB. This changes SourceConstantAlpha in one ULW call and
        // never reruns the picture rasterizer; the OS may still copy the supplied surface.
        if (IsVisible) Present();
    }

    internal void MovePhysical(double x, double y)
    {
        VerifyOpen();
        FinishZoomMotion();
        int left = Coordinate(x), top = Coordinate(y);
        long started = Stopwatch.GetTimestamp();
        LastMoveDrewLive = false;
        // A live frame holds only what was visible where it was drawn; a settled picture holds what the
        // screens around it could show. Either way, what it holds may not cover the part visible at the
        // new place, and that part is drawn before the window moves there.
        NativePinRaster.View held = default;
        bool draw = IsLiveZooming ? IsDisplayClipped
            : !_view.Contains(held = RestView(left, top, PixelWidth, PixelHeight));
        LastMoveFreshPixels = draw && !IsLiveZooming ? held.Pixels - (_view.Intersect(held)?.Pixels ?? 0) : 0;
        if (draw)
        {
            _left = left; _top = top;
            if (IsLiveZooming) Render(highQuality: false);
            else if (!TryRehold(held))
            {
                // A jump rather than a drag step: a live frame now, the full-quality picture once the moves stop.
                Render(highQuality: false);
                InvalidateSettle();
                BeginLiveZoom();
                LastMoveDrewLive = true;
            }
            if (IsVisible) Present();
            else PlaceHidden("SetWindowPos(move)");
        }
        else if (!NativePinWindowInterop.SetWindowPos(Handle, IntPtr.Zero, left + _view.Left, top + _view.Top, 0, 0,
            NativePinWindowInterop.NoSize | NativePinWindowInterop.NoActivate | NativePinWindowInterop.NoZOrder))
            ThrowNative("SetWindowPos(move)");
        // SetWindowPos may synchronously dispatch WM_DPICHANGED, whose suggested
        // position is applied by SetDpiScale. Read the final HWND position instead
        // of overwriting that reentrant adjustment with the original request.
        if (!NativePinWindowInterop.GetWindowRect(Handle, out var actual)) ThrowNative("GetWindowRect(move)");
        _left = actual.Left - _view.Left; _top = actual.Top - _view.Top;
        LastMoveMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        BoundsChanged?.Invoke();
    }

    /// <summary>
    /// Brings the part a settled picture holds to <paramref name="next"/>: the pixels both hold stay (moved
    /// inside the DIB, or copied into one of the new size) and only the newly visible part is drawn, at full
    /// quality, so the result is the settled picture of <paramref name="next"/> byte for byte. Returns false,
    /// changing nothing, when more is new than <see cref="ReholdPixelLimit"/> allows on this thread.
    /// </summary>
    private unsafe bool TryRehold(NativePinRaster.View next)
    {
        if (_dib is not { } current || _edges is null || Source is not { } source) return false;
        NativePinRaster.View old = _view;
        NativePinRaster.View? shared = old.Intersect(next);
        long fresh = next.Pixels - (shared?.Pixels ?? 0);
        if (fresh > ReholdPixelLimit) return false;
        long started = Stopwatch.GetTimestamp();
        // The kept pixels are taken without the outline; it is composited again for the new part below.
        _edges.Restore(current.Bits);
        bool inPlace = next.Width == old.Width && next.Height == old.Height;
        NativePinWindowDib target = inPlace ? current : new NativePinWindowDib(next.Width, next.Height);
        try
        {
            if (shared is { } both)
            {
                int rows = both.Height;
                long bytes = both.Width * 4L;
                int fromY = both.Top - old.Top, toY = both.Top - next.Top;
                byte* from = (byte*)current.Bits + ((both.Left - old.Left) * 4L);
                byte* to = (byte*)target.Bits + ((both.Left - next.Left) * 4L);
                // Inside one buffer, rows that move down are copied from the last one up.
                bool lastFirst = inPlace && toY > fromY;
                for (int i = 0; i < rows; i++)
                {
                    int row = lastFirst ? rows - 1 - i : i;
                    Buffer.MemoryCopy(from + ((long)(fromY + row) * current.Stride),
                        to + ((long)(toY + row) * target.Stride), bytes, bytes);
                }
            }
            using var lease = source.AcquireLease();
            foreach (NativePinRaster.View part in Uncovered(next, shared))
            {
                var at = (IntPtr)((byte*)target.Bits + ((long)(part.Top - next.Top) * target.Stride) + ((part.Left - next.Left) * 4L));
                NativePinResampler.Write(lease, at, part, target.Stride, highQuality: true, _contentScale);
            }
        }
        catch
        {
            if (!inPlace) target.Dispose();
            throw;
        }
        if (!inPlace)
        {
            current.Dispose();
            _dib = target;
        }
        _view = next;
        long outline = Stopwatch.GetTimestamp();
        _edges = NativePinEdgeCache.CaptureAndApply(target.Bits, next, target.Stride, DpiScale, _outlineColor,
            WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build), _contentScale);
        LastReholdOutlineMilliseconds = Stopwatch.GetElapsedTime(outline).TotalMilliseconds;
        RasterCount++;
        ReholdCount++;
        LastReholdPixels = fresh;
        LastReholdMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return true;
    }

    // The parts of `next` outside `shared` (which lies inside it): full-width bands above and below, then
    // the columns beside it.
    private static List<NativePinRaster.View> Uncovered(NativePinRaster.View next, NativePinRaster.View? shared)
    {
        if (shared is not { } s) return [next];
        var parts = new List<NativePinRaster.View>(4);
        int w = next.SurfaceWidth, h = next.SurfaceHeight;
        int right = next.Left + next.Width, bottom = next.Top + next.Height;
        int sharedRight = s.Left + s.Width, sharedBottom = s.Top + s.Height;
        if (s.Top > next.Top) parts.Add(new(next.Left, next.Top, next.Width, s.Top - next.Top, w, h));
        if (sharedBottom < bottom) parts.Add(new(next.Left, sharedBottom, next.Width, bottom - sharedBottom, w, h));
        if (s.Left > next.Left) parts.Add(new(next.Left, s.Top, s.Left - next.Left, s.Height, w, h));
        if (sharedRight < right) parts.Add(new(sharedRight, s.Top, right - sharedRight, s.Height, w, h));
        return parts;
    }

    /// <summary>A move a pin makes (drag, probe): part of the pin stays on the desktop to grab.</summary>
    internal void MoveKeepingGrabbable(double x, double y)
    {
        VerifyOpen();
        (double left, double top) = KeepGrabbable(x, y, PixelWidth, PixelHeight);
        MovePhysical(left, top);
    }

    internal void SetClickThrough(bool enabled)
    {
        VerifyOpen();
        if (enabled) EndGesture();
        State.SetClickThrough(WindowStyleFacade.SetClickThrough(Handle, enabled));
        if (WindowStyleFacade.IsClickThrough(Handle) != enabled)
            throw new InvalidOperationException("The native pin click-through style did not change.");
    }

    internal void SetVisualState(bool hover, bool active)
    {
        VerifyOpen();
        if (_hover == hover && _active == active) return;
        _hover = hover; _active = active;
        _colorFrom = _baseColor;
        _colorTo = ResolveBaseColor();
        _colorStarted = Stopwatch.GetTimestamp();
        _colorAnimating = FluidMotion.AnimationsEnabled;
        if (_colorAnimating) StartMotion();
        else { _baseColor = _colorTo; UpdateOutline(); }
    }

    internal void SetTheme(AppTheme theme)
    {
        VerifyOpen(); Theme = theme; ResolveTheme();
        _colorAnimating = false;
        _baseColor = ResolveBaseColor();
        UpdateOutline();
    }

    internal void SetDpiScale(double scale)
    {
        VerifyOpen(); ValidateScale(scale);
        if (Math.Abs(scale - DpiScale) < 1e-9) return;
        FinishZoomMotion(present: false);
        InvalidateSettle();
        DpiScale = scale;
        // Like PinWindow.OnDpiChanged, keep image DIP dimensions and zoom. Only the
        // physical display DIB, stroke and corner coverage follow the new monitor scale.
        Render(highQuality: false);
        if (IsVisible) Present();
        else PlaceHidden("SetWindowPos(DPI)");
        ForceSettle();
        BoundsChanged?.Invoke();
    }

    /// <summary>
    /// WM_DPICHANGED at <paramref name="suggested"/> (the HWND's suggested top-left). A change back to the
    /// scale the last change left, before the pin has moved and within <see cref="DpiFlipWindowMilliseconds"/>,
    /// is the pin's own new size putting its centre over the other monitor (the edge flicker): it is not
    /// applied, nor is a change that arrives while one is being applied. A move that ends with Windows'
    /// scale still different applies it then (<see cref="EndGesture"/>).
    /// </summary>
    private void OnDpiChanged(double scale, Point suggested)
    {
        if (!double.IsFinite(scale) || scale <= 0) return;
        WindowDpiScale = scale;
        if (Math.Abs(scale - DpiScale) < 1e-9) { _heldDpi = null; return; }
        bool flipBack = Math.Abs(scale - _dpiLeftScale) < 1e-9 && !_movedSinceDpiChange
            && Stopwatch.GetElapsedTime(_dpiChangedAt).TotalMilliseconds < DpiFlipWindowMilliseconds;
        if (_dpiChanging || flipBack) { IgnoredDpiChangeCount++; _heldDpi = scale; return; }
        ApplyDpiChange(scale, suggested);
    }

    private void ApplyDpiChange(double scale, Point suggested)
    {
        (_left, _top) = ScaledTopLeft(scale, suggested);
        _heldDpi = null;
        _dpiLeftScale = DpiScale;
        _dpiChangedAt = Stopwatch.GetTimestamp();
        _movedSinceDpiChange = false;
        DpiChangeCount++;
        _dpiChanging = true;
        try { SetDpiScale(scale); }
        finally { _dpiChanging = false; }
        // A move in progress continues from here: the pointer keeps its place on the picture.
        if (_dragging && _resizeEdge == PinResizeEdge.None)
        {
            _gesturePointer = _lastGesturePointer;
            _gestureBounds = Bounds;
        }
    }

    // The surface's top-left at a new scale. During a move the point under the pointer stays under it (in
    // DIP of the picture), so the pin neither jumps nor drifts as it crosses; otherwise Windows' suggestion
    // places the HWND, which starts inside the surface when the pin holds only part of it.
    private (int Left, int Top) ScaledTopLeft(double scale, Point suggested)
    {
        if (_dragging && _resizeEdge == PinResizeEdge.None)
            return (Coordinate(_lastGesturePointer.X - (_gestureAnchorDip.X * scale)),
                Coordinate(_lastGesturePointer.Y - (_gestureAnchorDip.Y * scale)));
        double ratio = scale / DpiScale;
        return (Coordinate(suggested.X - Math.Round(_view.Left * ratio)), Coordinate(suggested.Y - Math.Round(_view.Top * ratio)));
    }

    // WM_DISPLAYCHANGE: a monitor came, went or changed size, so what the screens can show changed too.
    private void OnDisplayChanged()
    {
        _screens = ReadScreens();
        if (_dib is null || IsClosed || IsExiting) return;
        NativePinRaster.View next = IsLiveZooming ? LiveView(PixelWidth, PixelHeight) : RestView(_left, _top, PixelWidth, PixelHeight);
        if (next == _view) return;
        if (IsLiveZooming) Render(highQuality: false);
        else if (!TryRehold(next))
        {
            InvalidateSettle();
            Render(highQuality: false);
            ForceSettle();
        }
        if (IsVisible) Present();
        else PlaceHidden("SetWindowPos(display)");
        BoundsChanged?.Invoke();
    }

    // A hidden window is not presented; keep its rectangle equal to what its DIB holds.
    private void PlaceHidden(string operation)
    {
        if (!NativePinWindowInterop.SetWindowPos(Handle, IntPtr.Zero, _left + _view.Left, _top + _view.Top,
            _dib!.Width, _dib.Height, NativePinWindowInterop.NoActivate | NativePinWindowInterop.NoZOrder))
            ThrowNative(operation);
    }

    internal byte[] CopyDisplayPixels()
    {
        VerifyOpen();
        return _dib!.CopyPixels();
    }

    /// <summary>Flushes submitted composition work; this is not a physical scan-out timestamp.</summary>
    internal double FlushPresentation()
    {
        VerifyOpen();
        long started = Stopwatch.GetTimestamp();
        LastFlushHResult = NativePinWindowInterop.DwmFlush();
        if (LastFlushHResult < 0)
        {
            LastPresentError = "DwmFlush failed: 0x" + LastFlushHResult.Value.ToString("X8");
            Marshal.ThrowExceptionForHR(LastFlushHResult.Value);
        }
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// Shrinks and fades the window out, then closes it. <paramref name="excludeFromCapture"/> keeps the
    /// leaving window out of screen captures, as a WPF pin's exit does; where it is refused, or without
    /// animations or visibility, the window closes at once.
    /// </summary>
    internal void CloseWithExit(Func<IntPtr, bool>? excludeFromCapture = null)
    {
        VerifyOpen();
        if (IsExiting) return;
        if (!IsVisible || IsHiding || !FluidMotion.AnimationsEnabled) { Close(); return; }
        if (excludeFromCapture is not null)
        {
            IsCaptureExcluded = excludeFromCapture(Handle);
            if (!IsCaptureExcluded) { Close(); return; }
        }
        EndGesture();
        FinishZoomMotion(present: false);
        CancelSettle();
        _exitStartOpacity = _motionOpacity * DisplayedOpacity / Math.Max(State.Opacity, 1e-9);
        _opacityAnimating = false;
        _landingStarted = _findStarted = 0; _revealing = false; _colorAnimating = false;
        _exitStarted = Stopwatch.GetTimestamp();
        StartMotion();
    }

    internal void Close() => Dispose();

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_closing) return;
        _closing = true;
        try
        {
            StopTimers();
            if (_dragging) EndGesture(notify: false);
            if (Handle != IntPtr.Zero)
            {
                if (!NativePinWindowInterop.DestroyWindow(Handle)) ThrowNative("DestroyWindow");
            }
            else ReleaseOwnedResources();
        }
        finally { _closing = false; }
    }

    private void Render(bool highQuality)
    {
        NativePinPixelSource source = Source ?? throw new ObjectDisposedException(nameof(NativePinWindow));
        int width = DesiredWidth, height = DesiredHeight;
        NativePinRaster.View view = highQuality ? RestView(_left, _top, width, height) : LiveView(width, height);
        long allocating = Stopwatch.GetTimestamp();
        if (_dib is null || _dib.Width != view.Width || _dib.Height != view.Height)
        {
            // The window manager retains the last submitted image while the sole display
            // DIB is replaced. No second persistent DIB is kept for interactive scaling.
            _dib?.Dispose(); _dib = null;
            _dib = new NativePinWindowDib(view.Width, view.Height);
        }
        _view = view;
        using var lease = source.AcquireLease();
        double radius = WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build);
        if (highQuality)
            LastRasterMilliseconds = NativePinRaster.RenderCached(lease, _dib.Bits, view, _dib.Stride,
                DpiScale, _outlineColor, radius, true, out _edges, _contentScale);
        else
        {
            long started = Stopwatch.GetTimestamp();
            NativePinLiveRaster.Write(LiveMode, lease, _dib.Dc, _dib.Bits, view, _dib.Stride, _contentScale);
            long stretched = Stopwatch.GetTimestamp();
            _edges = NativePinEdgeCache.CaptureAndApply(_dib.Bits, view, _dib.Stride,
                DpiScale, _outlineColor, radius, _contentScale);
            long finished = Stopwatch.GetTimestamp();
            LastRasterMilliseconds = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
            LastLiveAllocateMilliseconds = Stopwatch.GetElapsedTime(allocating, started).TotalMilliseconds;
            LastLiveStretchMilliseconds = Stopwatch.GetElapsedTime(started, stretched).TotalMilliseconds;
            LastLiveEdgeMilliseconds = Stopwatch.GetElapsedTime(stretched, finished).TotalMilliseconds;
        }
        RasterCount++;
    }

    // The part of the surface that a live frame draws: what lies on the virtual screen. A pin's
    // surface grows with its zoom (a 4K picture at 259% is 213 MB) while the screen does not.
    private NativePinRaster.View LiveView(int width, int height) => LiveView(_left, _top, width, height);

    private NativePinRaster.View LiveView(int left, int top, int width, int height)
    {
        if (!LiveViewportEnabled) return NativePinRaster.View.Whole(width, height);
        if (ScreenBoundedRest || LiveClipForTest is not null) return ScreenView(left, top, width, height);
        long screenLeft = NativePinWindowInterop.GetSystemMetrics(76), screenTop = NativePinWindowInterop.GetSystemMetrics(77);
        return NativePinRaster.View.Clip(left, top, width, height, screenLeft, screenTop,
            screenLeft + NativePinWindowInterop.GetSystemMetrics(78), screenTop + NativePinWindowInterop.GetSystemMetrics(79));
    }

    /// <summary>The part of the surface a settled picture holds at <paramref name="left"/>, <paramref name="top"/>.</summary>
    private NativePinRaster.View RestView(int left, int top, int width, int height) =>
        ScreenBoundedRest ? ScreenView(left, top, width, height) : NativePinRaster.View.Whole(width, height);

    // What the screens around the surface can show: the bounding box of the monitors it touches (all of them
    // when it touches none), so a pin across two monitors keeps both parts. Tests stand in one rectangle.
    private NativePinRaster.View ScreenView(int left, int top, int width, int height)
    {
        if (LiveClipForTest is { } clip)
            return NativePinRaster.View.Clip(left, top, width, height, (long)Math.Floor(clip.Left),
                (long)Math.Floor(clip.Top), (long)Math.Ceiling(clip.Right), (long)Math.Ceiling(clip.Bottom));
        return NativePinRaster.View.Held(left, top, width, height, _screens ??= ReadScreens());
    }

    private static RectD[] ReadScreens()
    {
        try { return MonitorEnumerator.GetAll().Select(m => m.Bounds).ToArray(); }
        catch (Win32Exception) { return []; }
    }

    private void Present()
    {
        if (Handle == IntPtr.Zero || _dib is null || !IsVisible) return;
        long start = Stopwatch.GetTimestamp();
        var destination = new NativePinWindowInterop.Point(_left + _view.Left, _top + _view.Top);
        var size = new NativePinWindowInterop.Size(_dib.Width, _dib.Height);
        var source = new NativePinWindowInterop.Point(0, 0);
        var blend = new NativePinWindowInterop.Blend { ConstantAlpha = (byte)Math.Round(Math.Clamp(EffectiveOpacity, 0, 1) * 255), AlphaFormat = 1 };
        if (!NativePinWindowInterop.UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _dib.Dc, ref source, 0, ref blend, 2))
            ThrowNative("UpdateLayeredWindow");
        LastPresentMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastPresentedAlpha = blend.ConstantAlpha;
        PresentCount++;
        Presented?.Invoke();
    }

    private void BeginLiveZoom()
    {
        IsLiveZooming = true;
        _settleTimer.Stop(); _settleTimer.Start();
    }

    private void InvalidateSettle()
    {
        RequestedGeneration++;
        _lastZoomStarted = Stopwatch.GetTimestamp();
        _settleRequested = false;
        _settleTimer.Stop();
        _settleWork?.Cancel();
        _settled.TrySetCanceled();
        _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsLiveZooming = true;
        StartSettleObservation();
    }

    private void CancelSettle()
    {
        _settleTimer.Stop(); _settlePollTimer.Stop(); _settleRequested = false;
        _settleWork?.Cancel();
        _settled.TrySetCanceled();
        IsLiveZooming = false;
        StopSettleObservation();
    }

    private void StartSettleObservation()
    {
        if (_observingSettle) return;
        _observingSettle = true;
        _dispatcher.Hooks.OperationStarted += OnSettleOperationStarted;
        _dispatcher.Hooks.OperationCompleted += OnSettleOperationCompleted;
    }

    private void OnSettleOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        // These two hooks run on the owning dispatcher. OperationPosted can run on
        // another thread and is deliberately not used as a duration start point.
        if (IsLiveZooming && _dispatcher.CheckAccess())
            _settleOperations[e.Operation] = Stopwatch.GetTimestamp();
    }

    private void OnSettleOperationCompleted(object? sender, DispatcherHookEventArgs e)
    {
        if (!_dispatcher.CheckAccess()) return;
        if (_settleOperations.Remove(e.Operation, out long started))
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LongestSettleDispatcherOperationMilliseconds = Math.Max(LongestSettleDispatcherOperationMilliseconds, elapsed);
            SettleDispatcherOperationCount++;
        }
        // The operation accepting HQ is included through its real completion,
        // rather than truncating its measurement when IsLiveZooming changes.
        if (!IsLiveZooming && _settleOperations.Count == 0) StopSettleObservation();
    }

    private void StopSettleObservation()
    {
        if (_observingSettle)
        {
            _dispatcher.Hooks.OperationStarted -= OnSettleOperationStarted;
            _dispatcher.Hooks.OperationCompleted -= OnSettleOperationCompleted;
            _observingSettle = false;
        }
        _settleOperations.Clear();
    }

    private void StartSettleIfReady()
    {
        if (!_settleRequested || _settleWork is not null || IsClosed || IsExiting) return;
        _settleRequested = false;
        var lease = Source!.AcquireLease();
        int width = DesiredWidth, height = DesiredHeight, generation = RequestedGeneration;
        // The settled picture holds what the screens around the pin can show (the whole surface when
        // ScreenBoundedRest is off), wherever the pin is when the render starts.
        NativePinRaster.View view = RestView(_left, _top, width, height);
        // Coverage was prepared on this STA for the same live geometry. Passing only
        // its immutable numeric tables avoids creating WPF Dispatchers on pool threads.
        // When the live frame held another part, the settled outline is built here once per settle
        // (about 1 to 3 ms), never per notch.
        var coverage = _edges is { } prepared && prepared.Area == view ? prepared.PreparedCoverage
            : new NativePinRaster.Chrome(view, checked(view.Width * 4), DpiScale,
                WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build), 1);
        Color outline = _outlineColor;
        Action<CancellationToken>? testGate = BeforeSettleRasterForTest;
        var work = new NativePinSettleWork(generation);
        _settleWork = work;
        work.Start(token =>
        {
            using (lease)
            {
                NativePinSettleBuffer? buffer = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    buffer = new NativePinSettleBuffer(view.Width, view.Height);
                    work.BufferAllocated(buffer.ByteLength);
                    testGate?.Invoke(token);
                    token.ThrowIfCancellationRequested();
                    double elapsed = NativePinRaster.RenderPrepared(lease, buffer.Pointer, view, buffer.Stride,
                        outline, true, coverage, out var edges, cancellationToken: token);
                    token.ThrowIfCancellationRequested();
                    return new NativePinSettleFrame(buffer, edges, generation, elapsed, view);
                }
                catch { buffer?.Dispose(); throw; }
            }
        });
        _drainTask = work.Drained;
        // The worker only fills memory and publishes a managed ready flag. Posting
        // to a Dispatcher from a pool thread initializes persistent USER resources
        // on that thread; the owner STA therefore observes completion itself.
        _settlePollTimer.Start();
    }

    private void OnSettlePollTick(object? sender, EventArgs e)
    {
        if (_settleWork is not { } work || IsClosed)
        {
            _settlePollTimer.Stop(); return;
        }
        if (!work.IsReadyToComplete) return;
        _settlePollTimer.Stop();
        try { CompleteSettle(work); }
        catch (Exception error) { RecordCallbackFailure(error); }
    }

    private void CompleteSettle(NativePinSettleWork work)
    {
        _dispatcher.VerifyAccess();
        if (!ReferenceEquals(_settleWork, work)) { work.Cancel(); return; }
        if (!work.IsReadyToComplete) return;
        long started = Stopwatch.GetTimestamp();
        NativePinSettleFrame? frame = work.TakeResult();
        try
        {
            if (work.Error is not null) throw work.Error;
            // The pin may have moved while the worker drew (a drag during the live zoom). A picture that
            // no longer covers the part to hold is completed here when little is missing, else drawn again.
            NativePinRaster.View now = frame is null ? default : RestView(_left, _top, frame.View.SurfaceWidth, frame.View.SurfaceHeight);
            bool covers = frame is null || frame.View.Contains(now);
            if (frame is not null && !covers
                && now.Pixels - (frame.View.Intersect(now)?.Pixels ?? 0) > ReholdPixelLimit
                && !IsClosed && !IsExiting && frame.Generation == RequestedGeneration)
            {
                _settleRequested = true;
            }
            else if (frame is not null && !IsClosed && !IsExiting && frame.Generation == RequestedGeneration)
            {
                _dib?.Dispose(); _dib = null;
                _dib = new NativePinWindowDib(frame.Buffer);
                _view = frame.View;
                _edges = frame.Edges;
                // Theme/hover may have changed while the immutable picture was rendered.
                _edges.Redraw(_dib.Bits, _outlineColor);
                if (!covers) TryRehold(now);
                LastRasterMilliseconds = frame.RasterMilliseconds;
                RasterCount++;
                if (IsVisible) Present();
                else PlaceHidden("SetWindowPos(settle)");
                // ULW can synchronously dispatch DPI changes or invoke a Presented
                // observer that requests another size. Such a request remains live.
                if (frame.Generation == RequestedGeneration)
                {
                    SettledGeneration = frame.Generation;
                    IsLiveZooming = false;
                    if (_settleOperations.Count == 0) StopSettleObservation();
                    LastSettleMilliseconds = Stopwatch.GetElapsedTime(_lastZoomStarted).TotalMilliseconds;
                    _settled.TrySetResult();
                    Settled?.Invoke();
                }
            }
        }
        catch (Exception error) { RecordCallbackFailure(error); _settled.TrySetException(error); }
        finally
        {
            frame?.Dispose(); work.FinishTransfer();
            _peakWorkerBufferCount = Math.Max(_peakWorkerBufferCount, work.PeakBufferCount);
            _drainTask = work.Drained;
            _settleWork = null;
            LastSettleUiMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LongestSettleUiMilliseconds = Math.Max(LongestSettleUiMilliseconds, LastSettleUiMilliseconds);
        }
        StartSettleIfReady();
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(); return completion;
    }

    private void OnSettleTick(object? sender, EventArgs e)
    {
        try { ForceSettle(); }
        catch (Exception error) { RecordCallbackFailure(error); }
    }

    private void StartMotion()
    {
        if (_motionClock is not null) return;
        if (_motionDrain.IsCompletedSuccessfully) _motionDrain = Task.CompletedTask;
        _motionClock = new NativePinMotionClock(_dispatcher, this);
    }

    private void StopMotion()
    {
        NativePinMotionClock? clock = _motionClock;
        if (clock is null) return;
        _motionClock = null;
        clock.Stop();
        _motionDrain = _motionDrain.IsCompletedSuccessfully ? clock.Completion : Task.WhenAll(_motionDrain, clock.Completion);
    }

    void INativeMotionTarget.OnNativeMotionTick(NativePinMotionClock clock, int result) => OnNativeMotionTick(clock, result);

    internal void OnNativeMotionTick(NativePinMotionClock clock, int result)
    {
        _dispatcher.VerifyAccess();
        if (!ReferenceEquals(_motionClock, clock) || IsClosed) return;
        LastMotionClockHResult = result;
        if (result < 0)
        {
            RecordCallbackFailure(new COMException("DwmFlush motion clock failed.", result));
            return;
        }
        MotionTickCount++;
        try { UpdateMotion(); }
        catch (Exception error) { RecordCallbackFailure(error); }
    }

    private void UpdateMotion()
    {
        if (IsClosed) return;
        long now = Stopwatch.GetTimestamp();
        bool revealPending = _revealing && _revealStarted == PendingStart;
        if (revealPending || _landingStarted == PendingStart)
        {
            // The first frame is on the screen; the motion starts once the pin's companions are ready
            // (the landing ornament's first frame), so their one-off costs fall before it, not inside it.
            if (_holdStarted == 0) _holdStarted = now;
            if (!IsHiding && !IsExiting && Elapsed(_holdStarted) < MotionHoldLimitMs && MotionReady?.Invoke() == false) return;
            _holdStarted = 0;
            now = Stopwatch.GetTimestamp();
            if (revealPending) _revealStarted = now - (long)(2 * RevealFirstFrameMs * Stopwatch.Frequency / 1000);
            if (_landingStarted == PendingStart) _landingStarted = now;
            MotionStarted?.Invoke();
            if (IsClosed) return;
        }
        bool raster = false, edge = false, present = IsLanding || IsExiting;
        if (_revealing)
        {
            double elapsed = Elapsed(_revealStarted);
            // Never below the step already shown (a tick inside the first frame's time).
            _motionOpacity = Math.Max(_motionOpacity, RevealOpacity(elapsed)); present = true;
            if (elapsed >= FluidMotion.FastDuration.TimeSpan.TotalMilliseconds) _revealing = false;
        }
        if (_colorAnimating)
        {
            double progress = Math.Clamp(Elapsed(_colorStarted) / FluidMotion.FastDuration.TimeSpan.TotalMilliseconds, 0, 1);
            _baseColor = Blend(_colorFrom, _colorTo, FluidMotion.StandardEasing.Ease(progress));
            if (progress >= 1) _colorAnimating = false;
        }
        if (_opacityAnimating)
        {
            present = true;
            if (Elapsed(_opacityStarted) >= FluidMotion.NormalDuration.TimeSpan.TotalMilliseconds) _opacityAnimating = false;
        }
        double accent = 0;
        if (IsLanding)
        {
            double elapsed = Elapsed(_landingStarted);
            accent = FluidMotion.AnimationsEnabled ? PinLandingDecoration.Render(elapsed).BorderAccentAmount : 1;
            if (elapsed >= PinLandingDecoration.OutlineEndMs) { _landingStarted = 0; accent = 0; }
        }
        if (IsFindCueRunning)
        {
            // As PinWindow.OnLandingRendering: two landing outlines in a row, or a one-second accent.
            double elapsed = Elapsed(_findStarted);
            bool animated = FluidMotion.AnimationsEnabled;
            accent = animated ? PinLandingDecoration.Render(elapsed % PinLandingDecoration.OutlineEndMs).BorderAccentAmount : 1;
            if (elapsed >= (animated ? PinLandingDecoration.OutlineEndMs * 2 : 1000))
            {
                _findStarted = 0; accent = 0;
                _baseColor = ResolveBaseColor();
            }
        }
        Color outline = Blend(_baseColor, ResolveEmphasizedColor(), accent);
        if (outline != _outlineColor) { _outlineColor = outline; edge = true; }
        if (IsHiding)
        {
            double progress = Math.Clamp(Elapsed(_hideStarted) / FluidMotion.FastDuration.TimeSpan.TotalMilliseconds, 0, 1);
            if (progress >= 1) { FinishHide(); StopMotion(); return; }
            _motionOpacity = _hideStartOpacity * (1 - FluidMotion.StandardEasing.Ease(progress));
            present = true;
        }
        bool zoomStep = false;
        if (IsZoomMoving)
        {
            _shownZoom = _zoomMotion!.Sample(MotionNow);
            PlaceZoomStep();
            zoomStep = raster = present = true;
        }
        if (IsExiting)
        {
            double progress = Math.Clamp(Elapsed(_exitStarted) / 120, 0, 1);
            if (progress >= 1) { Dispose(); return; }
            double eased = FluidMotion.StandardEasing.Ease(progress);
            _motionOpacity = _exitStartOpacity * (1 - eased);
            _contentScale = 1 - ((1 - FluidMotion.ShrinkExitScale) * eased);
            raster = present = true;
        }
        if (raster) Render(highQuality: false);
        else if (edge) RedrawEdges();
        if (raster || edge || present) Present();
        if (zoomStep)
        {
            // The full-quality picture is drawn once, after the motion has ended (as after a notch without it).
            if (!IsZoomMoving) BeginLiveZoom();
            BoundsChanged?.Invoke();
        }
        if (!IsLanding && !_revealing && !_colorAnimating && !IsExiting && !IsHiding && !IsFindCueRunning && !_opacityAnimating
            && !IsZoomMoving)
            StopMotion();
    }

    /// <summary>
    /// Ends every transition at its final value: no accent, full reveal, the target opacity,
    /// a finished hide. An exit is not ended here.
    /// </summary>
    internal void CompleteTransitions()
    {
        VerifyOpen();
        if (IsExiting) return;
        FinishZoomMotion();
        _landingStarted = _findStarted = 0;
        _revealing = _colorAnimating = _opacityAnimating = false;
        if (IsHiding) FinishHide();
        else _motionOpacity = 1;
        _baseColor = ResolveBaseColor();
        StopMotion();
        _outlineColor = _baseColor;
        RedrawEdges();
        if (IsVisible) Present();
    }

    /// <summary>Stops the move or resize that holds the pointer and drops a pending full-quality picture.</summary>
    internal void FreezeInput()
    {
        VerifyOpen();
        EndGesture(notify: false);
        FinishZoomMotion();
        CancelSettle();
    }

    private void UpdateOutline()
    {
        _outlineColor = _baseColor;
        RedrawEdges();
        if (IsVisible) Present();
    }

    private void RedrawEdges()
    {
        if (_dib is null || _edges is null) return;
        LastRasterMilliseconds = _edges.Redraw(_dib.Bits, _outlineColor);
    }

    private void ResolveTheme()
    {
        var colors = ThemeCatalog.ColorsFor(Theme, AppAccent.Red);
        var accent = colors[PinFrameColor.AccentKey];
        var neutral = colors[PinFrameColor.NeutralKey];
        _accent = new SolidColorBrush(Color.FromArgb(accent.A, accent.R, accent.G, accent.B));
        _neutral = new SolidColorBrush(Color.FromArgb(neutral.A, neutral.R, neutral.G, neutral.B));
    }
    // A find cue pulses on the quiet baseline, so both pulses show on an active pin (as PinWindow.UpdateChromeColor).
    private Color ResolveBaseColor() => PinFrameColor.Resolve(_accent, _neutral,
        ThemeService.IsSystemHighContrast || Theme == AppTheme.HighContrast, (_hover || _active) && !IsFindCueRunning);

    /// <summary>The pin's frame token brushes changed (theme, accent or contrast); the outline follows at once.</summary>
    internal void SetFrameBrushes(Brush? accent, Brush? neutral, AppTheme theme)
    {
        VerifyOpen();
        _accent = accent; _neutral = neutral; Theme = theme;
        _colorAnimating = false;
        _baseColor = ResolveBaseColor();
        if (IsLanding || IsFindCueRunning) return; // The next motion tick blends the new colours.
        UpdateOutline();
    }
    private Color ResolveEmphasizedColor() => PinFrameColor.Resolve(_accent, _neutral,
        ThemeService.IsSystemHighContrast || Theme == AppTheme.HighContrast, emphasized: true);
    private static Color Blend(Color from, Color to, double amount)
    {
        double p = Math.Clamp(amount, 0, 1);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * p));
        return Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private void BeginGesture(Point pointer, Point local, bool control)
    {
        if (State.IsClickThrough || IsExiting || IsHiding) return;
        EndGesture();
        // The edge is the one the user saw under the pointer: a running zoom motion shows a step short of (or
        // past) the state's size, so it is judged before the press ends the motion. The gesture then starts
        // from the state's geometry, and a resize follows the pointer's travel from the press.
        PinResizeEdge edge = control ? PinResizeEdge.None : EdgeAt(local);
        FinishZoomMotion();
        _gesturePointer = _lastGesturePointer = pointer; _gestureBounds = Bounds; _gestureZoom = Zoom; _gestureDpi = DpiScale;
        _gestureAnchorDip = new Point((pointer.X - _left) / DpiScale, (pointer.Y - _top) / DpiScale);
        _gestureMoved = false;
        _resizeEdge = edge;
        _ = NativePinWindowInterop.SetCapture(Handle);
        _dragging = NativePinWindowInterop.GetCapture() == Handle;
        if (!_dragging) _resizeEdge = PinResizeEdge.None;
        _snapping = _dragging && _resizeEdge == PinResizeEdge.None && SnapEnabled?.Invoke() == true;
        if (_snapping)
        {
            _snapTargets = SnapTargetReader?.Invoke() ?? [];
            _snapDisplays = SnapDisplayReader?.Invoke() ?? [];
        }
    }

    /// <summary>Whether a move snaps to other pins and the work area (the pin setting); null means never.</summary>
    internal Func<bool>? SnapEnabled { get; set; }

    /// <summary>The other pins' whole-surface rectangles, read once when a move starts.</summary>
    internal Func<IReadOnlyList<PinSnap.Target>>? SnapTargetReader { get; set; }

    /// <summary>The displays a move snaps to the work area of, read once when a move starts.</summary>
    internal Func<IReadOnlyList<PinDisplayArea>>? SnapDisplayReader { get; set; }

    /// <summary>Whether Shift is held, which bypasses snapping.</summary>
    internal Func<bool>? SnapBypass { get; set; }

    // The whole surface moves, not the part the screens show: the pin's edges are what meet the others.
    private (double Left, double Top) Snap(double left, double top)
    {
        if (!_snapping) return (left, top);
        RectD snapped = PinSnap.Calculate(new RectD(left, top, PixelWidth, PixelHeight), _snapDisplays, _snapTargets,
            SnapBypass?.Invoke() == true);
        return (snapped.Left, snapped.Top);
    }

    /// <summary>Starts a move from a press on a window over the pin (its readout), at a screen point.</summary>
    internal void BeginMoveAtScreen(Point screen)
    {
        VerifyOpen();
        BeginGesture(screen, default, control: true);
    }

    // The edge under a point in surface pixels, as PinWindow.ResizeEdgeAt (in DIP), on the size the screen shows.
    private PinResizeEdge EdgeAt(Point surface) => State.IsClickThrough || IsExiting ? PinResizeEdge.None
        : PinResize.HitTest(surface.X / DpiScale, surface.Y / DpiScale, ShownWidthDip, ShownHeightDip);

    private void ContinueGesture(Point pointer)
    {
        if (!_dragging) return;
        double dx = pointer.X - _gesturePointer.X, dy = pointer.Y - _gesturePointer.Y;
        if (dx == 0 && dy == 0 && !_gestureMoved) return;
        _gestureMoved = true;
        _lastGesturePointer = pointer;
        _movedSinceDpiChange = true;
        if (_resizeEdge == PinResizeEdge.None)
        {
            double left = _gestureBounds.Left + dx, top = _gestureBounds.Top + dy;
            if (_product) (left, top) = KeepGrabbable(left, top, PixelWidth, PixelHeight);
            (left, top) = Snap(left, top);
            MovePhysical(left, top);
        }
        else
        {
            double old = Zoom;
            InvalidateSettle();
            var placement = PinResize.Calculate(_gestureBounds, _gestureZoom, _resizeEdge, dx, dy,
                DpiScale / _gestureDpi, DpiScale / _gestureDpi,
                State.ImageWidthDip * DpiScale, State.ImageHeightDip * DpiScale, DpiScale, DpiScale);
            State.SetZoom(placement.Zoom);
            _left = Coordinate(placement.Left); _top = Coordinate(placement.Top);
            BeginLiveZoom(); Render(highQuality: false); if (IsVisible) Present();
            BoundsChanged?.Invoke();
            if (Math.Abs(Zoom - old) >= 1e-9) { UserZoomChanged?.Invoke(); ZoomShown?.Invoke(Zoom); }
        }
    }

    private void EndGesture(bool notify = true)
    {
        bool wasDragging = _dragging;
        bool wasResizing = _dragging && _resizeEdge != PinResizeEdge.None;
        bool resized = wasResizing && Math.Abs(Zoom - _gestureZoom) >= 1e-9;
        // A scale Windows gave during the move and the flicker guard held back applies where the move ends.
        if (wasDragging && !wasResizing && _gestureMoved && !IsClosed && !_closing && !IsExiting && _dib is not null
            && _heldDpi is { } held && Math.Abs(held - DpiScale) > 1e-9)
            ApplyDpiChange(held, new Point(_left + _view.Left, _top + _view.Top));
        if (wasDragging && !wasResizing && _gestureMoved) _heldDpi = null;
        _dragging = false; _resizeEdge = PinResizeEdge.None;
        _snapping = false; _snapTargets = []; _snapDisplays = [];
        if (Handle != IntPtr.Zero && NativePinWindowInterop.GetCapture() == Handle) _ = NativePinWindowInterop.ReleaseCapture();
        if (wasResizing && !IsClosed && !_closing) ForceSettle();
        if (notify && wasDragging && !IsClosed && !_closing) GestureEnded?.Invoke(_gestureMoved, resized);
    }

    private IntPtr OnMessage(uint message, IntPtr wparam, IntPtr lparam)
    {
        if (message == 0x0082) // WM_NCDESTROY: callback remains rooted until this call finishes.
        {
            IntPtr destroyed = Handle;
            NativePinWindowInterop.SetWindowLong(Handle, NativePinWindowInterop.UserData, IntPtr.Zero);
            Handle = IntPtr.Zero;
            ReleaseOwnedResources();
            return NativePinWindowInterop.DefWindowProcW(destroyed, message, wparam, lparam);
        }
        if (!_ready) return NativePinWindowInterop.DefWindowProcW(Handle, message, wparam, lparam);
        switch (message)
        {
            case 0x0010: // WM_CLOSE (Alt+F4).
                if (CloseRequested is { } close) close(); else Dispose();
                return IntPtr.Zero;
            case 0x0014: return new IntPtr(1); // No background brush/erase under per-pixel alpha.
            case 0x0006: // WM_ACTIVATE.
            {
                bool active = (wparam.ToInt64() & 0xFFFF) != 0;
                SetVisualState(_hover, active);
                ActiveChanged?.Invoke(active);
                break;
            }
            case 0x0020 when (lparam.ToInt64() & 0xFFFF) == 1: // WM_SETCURSOR over the client area.
                _ = NativePinWindowInterop.SetCursor(NativePinWindowInterop.LoadCursorW(IntPtr.Zero, CursorFor(CursorEdge())));
                return new IntPtr(1);
            case 0x0100: // WM_KEYDOWN, scoped to this HWND; no global hook or keyboard injection.
            {
                int key = unchecked((int)wparam.ToInt64());
                if (KeyHandler is { } handler)
                {
                    if (handler(key)) return IntPtr.Zero;
                    break;
                }
                int steps = key is 0x6B or 0xBB ? 1 : key is 0x6D or 0xBD ? -1 : 0;
                if (steps == 0 || State.IsClickThrough || IsExiting) break;
                SetZoom(PinGeometry.StepZoom(Zoom, steps, .1),
                    new Point(_left + (PixelWidth / 2d), _top + (PixelHeight / 2d)), live: true);
                return IntPtr.Zero;
            }
            case 0x0201: // WM_LBUTTONDOWN.
            case 0x0203: // WM_LBUTTONDBLCLK (CS_DBLCLKS).
            {
                if (IsExiting) return IntPtr.Zero;
                // Client coordinates are the HWND's; the resize hit test needs the surface's.
                Point local = MessagePoint(lparam);
                long keys = wparam.ToInt64();
                bool control = (keys & 0x0008) != 0, shift = (keys & 0x0004) != 0; // MK_CONTROL, MK_SHIFT.
                Point screen = ToScreen(local);
                if (PressHandler is { } press && press(new NativePinPress(message == 0x0203 ? 2 : 1, control, shift, screen)))
                    return IntPtr.Zero;
                BeginGesture(screen, new Point(local.X + _view.Left, local.Y + _view.Top), control);
                return IntPtr.Zero;
            }
            case 0x0202: EndGesture(); return IntPtr.Zero;
            case 0x0215: EndGesture(); return IntPtr.Zero; // WM_CAPTURECHANGED.
            case 0x0200:
            {
                if (!_hover)
                {
                    SetVisualState(true, _active);
                    var tracking = new NativePinWindowInterop.MouseTracking { Size = (uint)Marshal.SizeOf<NativePinWindowInterop.MouseTracking>(), Flags = 2, Window = Handle };
                    _ = NativePinWindowInterop.TrackMouseEvent(ref tracking);
                    HoverChanged?.Invoke(true);
                }
                ContinueGesture(ToScreen(MessagePoint(lparam))); return IntPtr.Zero;
            }
            case 0x02A3: SetVisualState(false, _active); HoverChanged?.Invoke(false); return IntPtr.Zero;
            case 0x020A: // WM_MOUSEWHEEL: the point is on the screen; MK_CONTROL rides in the low word.
                Wheel(unchecked((short)(wparam.ToInt64() >> 16)), MessagePoint(lparam), (wparam.ToInt64() & 0x0008) != 0);
                return IntPtr.Zero;
            case 0x007B when ContextMenuRequested is { } menu: // WM_CONTEXTMENU: -1,-1 from the keyboard.
                menu(lparam.ToInt64() == -1 || unchecked((int)lparam.ToInt64()) == -1 ? null : MessagePoint(lparam));
                return IntPtr.Zero;
            case 0x02E0: // WM_DPICHANGED.
            {
                var suggested = Marshal.PtrToStructure<NativePinWindowInterop.Rect>(lparam);
                OnDpiChanged((wparam.ToInt64() & 0xFFFF) / 96d, new Point(suggested.Left, suggested.Top));
                return IntPtr.Zero;
            }
            case 0x02E4 when lparam != IntPtr.Zero: // WM_GETDPISCALEDSIZE: the size the pin will have, not a linear guess.
            {
                double scale = (wparam.ToInt64() & 0xFFFF) / 96d;
                if (scale <= 0) break;
                (int left, int top) = ScaledTopLeft(scale, new Point(_left + _view.Left, _top + _view.Top));
                int width = Math.Max(1, checked((int)Math.Round(State.WidthDip * scale)));
                int height = Math.Max(1, checked((int)Math.Round(State.HeightDip * scale)));
                NativePinRaster.View held = RestView(left, top, width, height);
                Marshal.WriteInt32(lparam, 0, held.Width);
                Marshal.WriteInt32(lparam, 4, held.Height);
                return new IntPtr(1);
            }
            case 0x007E: // WM_DISPLAYCHANGE: the screens (and so what the pin holds) may have changed.
                OnDisplayChanged();
                break;
        }
        return NativePinWindowInterop.DefWindowProcW(Handle, message, wparam, lparam);
    }

    private static IntPtr DispatchMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam)
    {
        NativePinWindow? owner = null;
        try
        {
            IntPtr address = NativePinWindowInterop.GetWindowLong(hwnd, NativePinWindowInterop.UserData);
            if (message == 0x0081) // WM_NCCREATE.
            {
                address = Marshal.PtrToStructure<NativePinWindowInterop.CreateInfo>(lparam).Parameter;
                _ = NativePinWindowInterop.SetWindowLong(hwnd, NativePinWindowInterop.UserData, address);
            }
            if (address != IntPtr.Zero) owner = GCHandle.FromIntPtr(address).Target as NativePinWindow;
            if (owner is null) return NativePinWindowInterop.DefWindowProcW(hwnd, message, wparam, lparam);
            owner.Handle = hwnd;
            return owner.OnMessage(message, wparam, lparam);
        }
        catch (Exception error)
        {
            // Exceptions cannot cross a reverse P/Invoke. The probe reads this sticky failure;
            // timers stop rather than repeatedly throwing or reporting successful frames.
            owner?.RecordCallbackFailure(error);
            return NativePinWindowInterop.DefWindowProcW(hwnd, message, wparam, lparam);
        }
    }

    private void ReleaseOwnedResources()
    {
        StopTimers();
        IsVisible = false; IsClosed = true; _ready = false;
        CancelSettle();
        _revealing = _colorAnimating = _opacityAnimating = IsLiveZooming = false;
        _landingStarted = _exitStarted = _hideStarted = _findStarted = 0;
        Source?.Dispose(); Source = null; Presented = null; _edges = null;
        BeforeSettleRasterForTest = null;
        Action? closed = Closed;
        KeyHandler = null; PressHandler = null; ContextMenuRequested = null; CloseRequested = null;
        ActiveChanged = null; GestureEnded = null; UserZoomChanged = null; Settled = null;
        UserOpacityChanged = null; Closed = null; Failed = null;
        BoundsChanged = null; ShownChanged = null; HoverChanged = null; ZoomShown = null; OpacityShown = null;
        MotionReady = null; MotionStarted = null;
        try { _dib?.Dispose(); _dib = null; }
        finally
        {
            if (_self.IsAllocated) _self.Free();
            if (_countedWindow) { _countedWindow = false; Interlocked.Decrement(ref _liveWindows); }
            closed?.Invoke();
        }
    }

    private void StopTimers() { StopMotion(); _settleTimer.Stop(); _settlePollTimer.Stop(); }
    private void RecordCallbackFailure(Exception error)
    {
        LastPresentError ??= error.GetType().Name + ": " + error.Message;
        StopTimers(); CancelSettle(); _landingStarted = _findStarted = 0; IsLiveZooming = false;
        if (_failureReported || IsClosed || Failed is not { } failed) return;
        _failureReported = true;
        failed(error);
    }
    private void ThrowNative(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        LastPresentError = operation + " failed: " + error;
        throw new Win32Exception(error, LastPresentError);
    }
    private void VerifyOpen() { _dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(IsClosed, this); }
    private static bool ControlDown => (NativePinWindowInterop.GetKeyState(0x11) & 0x8000) != 0;

    // The resize cursors of PinWindow.ResizeCursor: the edge being dragged, else the edge under the pointer.
    private PinResizeEdge CursorEdge()
    {
        if (_dragging) return _resizeEdge;
        if (ControlDown || !NativePinWindowInterop.GetCursorPos(out var point)
            || !NativePinWindowInterop.ScreenToClient(Handle, ref point)) return PinResizeEdge.None;
        return EdgeAt(new Point(point.X + _view.Left, point.Y + _view.Top));
    }

    internal static IntPtr CursorFor(PinResizeEdge edge) => new(edge switch
    {
        PinResizeEdge.Left or PinResizeEdge.Right => 32644,          // IDC_SIZEWE
        PinResizeEdge.Top or PinResizeEdge.Bottom => 32645,          // IDC_SIZENS
        PinResizeEdge.TopLeft or PinResizeEdge.BottomRight => 32642, // IDC_SIZENWSE
        PinResizeEdge.TopRight or PinResizeEdge.BottomLeft => 32643, // IDC_SIZENESW
        _ => 32512,                                                   // IDC_ARROW
    });
    private static Point MessagePoint(IntPtr packed) => new(unchecked((short)packed.ToInt64()), unchecked((short)(packed.ToInt64() >> 16)));
    private Point ToScreen(Point point)
    {
        var native = new NativePinWindowInterop.Point((int)point.X, (int)point.Y);
        if (!NativePinWindowInterop.ClientToScreen(Handle, ref native)) ThrowNative("ClientToScreen");
        return new Point(native.X, native.Y);
    }
    private static int Coordinate(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        return checked((int)Math.Round(value));
    }
    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
    }
    private static PinViewState CreateOwnedState(NativePinPixelSource source, double left, double top,
        double zoom, double dpi, double? imageWidthDip, double? imageHeightDip)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("The native pin must be created on an STA thread.");
            ValidateScale(dpi); ValidateScale(zoom);
            _ = Coordinate(left); _ = Coordinate(top);
            double width = imageWidthDip ?? source.Width / dpi, height = imageHeightDip ?? source.Height / dpi;
            ValidateScale(width); ValidateScale(height);
            return new PinViewState(width, height, zoom, 1, .1);
        }
        catch { source?.Dispose(); throw; }
    }
    private static PinViewState ValidatePinState(NativePinPixelSource source, PinViewState state,
        double left, double top, double dpi)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(state);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("The native pin must be created on an STA thread.");
            ValidateScale(dpi);
            _ = Coordinate(left); _ = Coordinate(top);
            return state;
        }
        catch { source?.Dispose(); throw; }
    }
    private static double Elapsed(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    /// <summary>The reveal's opacity after <paramref name="ms"/>: linear over the fast duration, as before.</summary>
    internal static double RevealOpacity(double ms) =>
        Math.Clamp(ms / FluidMotion.FastDuration.TimeSpan.TotalMilliseconds, 0, 1);
    private static void EnsureClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return;
            var value = new NativePinWindowInterop.WindowClass
            {
                Size = (uint)Marshal.SizeOf<NativePinWindowInterop.WindowClass>(), Procedure = Procedure,
                Instance = NativePinWindowInterop.GetModuleHandleW(null), ClassName = ClassName,
                Style = DoubleClicks, Cursor = NativePinWindowInterop.LoadCursorW(IntPtr.Zero, CursorFor(PinResizeEdge.None)),
                // No CS_DROPSHADOW, background brush, caption or DWM frame extension.
            };
            if (NativePinWindowInterop.RegisterClassExW(ref value) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _classRegistered = true;
        }
    }
}

/// <summary>A left press on a native pin: one click or the second of a double-click, its modifiers and screen point.</summary>
internal readonly record struct NativePinPress(int Clicks, bool Control, bool Shift, Point Screen);
