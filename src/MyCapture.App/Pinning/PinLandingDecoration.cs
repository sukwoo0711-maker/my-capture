using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>A short, passive, capture-excluded landing ornament above a newly opened pin.</summary>
internal sealed class PinLandingDecoration : Window
{
    internal const double LandingMs = 150;

    // A game-style hit stop: the contact frame holds this long, then the ring, shadow,
    // glow and outline resume where they stopped. The content never moves.
    internal const double HitStopMs = 40;

    // The authored motion ends, before the hit stop is added.
    private const double MotionEndMs = 560;
    private const double OutlineMotionEndMs = 570;

    /// <summary>Elapsed time at which the ornament closes.</summary>
    internal const double DurationMs = MotionEndMs + HitStopMs;

    /// <summary>Elapsed time at which the pin outline is back to its resting colour.</summary>
    internal const double OutlineEndMs = OutlineMotionEndMs + HitStopMs;

    internal const double MarginDip = 64;
    internal const double CornerSizeDip = 120;

    // Largest measured full surface: an 800x500 DIP pin plus 64-DIP margins at 100%.
    // Keep the same physical-pixel budget on higher-DPI displays.
    internal const double MaxAmbientPixelArea = 582_784;

    internal static Func<bool> ReadRemoteSession { get; set; } = () => SystemParameters.IsRemoteSession;
    internal static Func<bool> ReadLayeredSupport { get; set; } = () => CaptureWindowExclusion.SupportsLayeredWindows;
    internal static Func<double> ReadOpeningTime { get; set; } = () => Environment.TickCount64;
    internal static Func<Window, bool> ApplyCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;
    internal static PinLandingThrottle Throttle { get; set; } = new();

    private readonly PinWindow _pin;
    private readonly Stopwatch _clock;
    private readonly LandingSurface _surface;
    private readonly RectD _originalPinBounds;
    private HwndSource? _source;
    private bool _rendering;

    internal PinLandingDecoration(PinWindow pin, PinLandingLayout layout, Stopwatch? clock = null)
    {
        _pin = pin;
        Layout = layout;
        _clock = clock ?? Stopwatch.StartNew();
        _originalPinBounds = ReadPhysicalBounds(pin);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Left = layout.PhysicalBounds.Left / layout.Scale;
        Top = layout.PhysicalBounds.Top / layout.Scale;
        Width = layout.PhysicalBounds.Width / layout.Scale;
        Height = layout.PhysicalBounds.Height / layout.Scale;
        FluidMotion.SetWindowEntrance(this, false);
        _surface = new LandingSurface(layout, pin.LandingAccentColor);
        Content = _surface;
        State = Render(0);
        _surface.Frame = State;
        // Finish fallible artwork/layout setup before the owner can retain this window.
        Owner = pin;
        SourceInitialized += OnSourceInitialized;
        _pin.LocationChanged += OnPinBoundsChanged;
        _pin.SizeChanged += OnPinSizeChanged;
        _pin.IsVisibleChanged += OnPinVisibilityChanged;
        _pin.Closed += OnPinClosed;
    }

    internal PinLandingLayout Layout { get; }
    internal PinLandingFrame State { get; private set; }
    internal bool IsCaptureExcluded { get; private set; }
    internal bool IsClosed { get; private set; }

    internal static bool IsEligible(bool animations, bool remote, bool layered) => animations && !remote && layered;

    internal static PinLandingDecoration? TryShow(PinWindow pin, Stopwatch clock)
    {
        PinLandingDecoration? decoration = null;
        bool shown = false;
        try
        {
            RectD bounds = ReadPhysicalBounds(pin);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            IntPtr handle = new WindowInteropHelper(pin).Handle;
            double scale = MonitorEnumerator.GetFromWindow(handle).ScaleFactor;
            decoration = new PinLandingDecoration(pin, LayoutFor(bounds, scale), clock);
            shown = decoration.ShowDecoration();
            if (shown) return decoration;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Decoration failure must not discard a pin that has already opened successfully.
        }
        finally
        {
            if (!shown && decoration is { IsClosed: false }) decoration.Close();
        }
        return null;
    }

    /// <summary>Shows only after capture exclusion has succeeded, without changing foreground.</summary>
    internal bool ShowDecoration()
    {
        if (IsClosed || !_pin.IsVisible) return false;
        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        if (!IsCaptureExcluded) return false;
        Show();
        // GetWindowRect and the pin monitor's scale are authoritative, not WPF Left/Top
        // (which use different logical coordinate planes on mixed-DPI desktops).
        PhysicalWindowPositioner.PlaceTopmostWithoutActivation(handle, Layout.PhysicalBounds);
        MatchPinDisplayScale(VisualTreeHelper.GetDpi(this));
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
        Advance(_clock.Elapsed.TotalMilliseconds);
        return !IsClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        WindowStyleFacade.SetClickThrough(handle, enabled: true);
        WindowStyleFacade.ExcludeFromAltTab(handle);
        CaptureWindowExclusion.DisableTransitions(this);
        IsCaptureExcluded = ApplyCaptureExclusion(this);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(NoActivateHook);
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0021) return IntPtr.Zero;
        handled = true;
        return new IntPtr(3); // MA_NOACTIVATE
    }

    private void OnRendering(object? sender, EventArgs e) => Advance(_clock.Elapsed.TotalMilliseconds);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        MatchPinDisplayScale(newDpi);
    }

    private void MatchPinDisplayScale(DpiScale dpi)
    {
        // At a display boundary the ornament's centre can be on a different monitor
        // from the pin's centre. Keep the artwork at the pin monitor's physical scale.
        _surface.RenderTransform = new ScaleTransform(Layout.Scale / dpi.DpiScaleX, Layout.Scale / dpi.DpiScaleY);
    }

    /// <summary>Applies the same deterministic frame used by the live clock and closes at its end.</summary>
    internal void Advance(double ms)
    {
        if (IsClosed) return;
        State = Render(ms);
        if (State.IsComplete)
        {
            Close();
            return;
        }
        _surface.Frame = State;
        _surface.InvalidateVisual();
    }

    private void OnPinBoundsChanged(object? sender, EventArgs e)
    {
        // Initial WPF notifications may arrive after Show without an actual physical move.
        if (ReadPhysicalBounds(_pin) != _originalPinBounds) Close();
    }

    private void OnPinSizeChanged(object sender, SizeChangedEventArgs e) => OnPinBoundsChanged(sender, e);
    private void OnPinVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Close();
    }
    private void OnPinClosed(object? sender, EventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        if (_rendering) CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
        _source?.RemoveHook(NoActivateHook);
        _source = null;
        SourceInitialized -= OnSourceInitialized;
        _pin.LocationChanged -= OnPinBoundsChanged;
        _pin.SizeChanged -= OnPinSizeChanged;
        _pin.IsVisibleChanged -= OnPinVisibilityChanged;
        _pin.Closed -= OnPinClosed;
        base.OnClosed(e);
    }

    private static RectD ReadPhysicalBounds(PinWindow pin)
    {
        (int left, int top, int right, int bottom) = pin.PhysicalBounds;
        return new RectD(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    internal static PinLandingLayout LayoutFor(RectD pin, double scale)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        double width = pin.Width / scale;
        double height = pin.Height / scale;
        double ambientWidth = width + (2 * MarginDip);
        double ambientHeight = height + (2 * MarginDip);
        bool ambient = ambientWidth * ambientHeight * scale * scale <= MaxAmbientPixelArea;
        RectD physical = new RectD(
            pin.Left - (MarginDip * scale), pin.Top - (MarginDip * scale),
            (ambient ? ambientWidth : CornerSizeDip) * scale,
            (ambient ? ambientHeight : CornerSizeDip) * scale).ToPixelBounds();
        return new PinLandingLayout(physical,
            new Rect((pin.Left - physical.Left) / scale, (pin.Top - physical.Top) / scale, width, height), ambient, scale);
    }

    /// <summary>All motion values in DIPs, degrees and opacity, driven by one elapsed time.</summary>
    internal static PinLandingFrame Render(double ms)
    {
        double t = MotionTime(double.IsNaN(ms) ? 0 : Math.Max(0, ms));
        bool complete = t >= MotionEndMs;
        double p = Bezier(Segment(t, 0, LandingMs), .5, 0, .9, .5);
        double d = Math.Max(0, t - LandingMs);
        double press = d <= 45 ? OutQuad(d / 45) : 1 - Bezier(Segment(d, 45, 175), .3, 1.6, .6, 1);
        double fade = 1 - Segment(t, 420, MotionEndMs);
        double r = OutCubic(Segment(d, 0, 260));
        double lift = 1 - p;
        return new PinLandingFrame(
            -15 * lift, -17 * lift, -30 * lift,
            t < LandingMs ? 1.35 - (.35 * p) : 1 - (.14 * press),
            complete ? 0 : Math.Min(Segment(t, 0, 60), fade),
            .4 + (2.3 * r),
            !complete && t >= LandingMs && d <= 260 ? .7 * (1 - OutQuad(d / 260)) : 0,
            t < LandingMs ? Lerp(3, 22, lift) : Lerp(3, 6, r),
            t < LandingMs ? Lerp(8, 36, lift) : Lerp(8, 16, r),
            complete ? 0 : (t < LandingMs ? Lerp(.42, .5, lift) * Segment(t, 0, 83) : Lerp(.44, .32, r)) * fade,
            !complete && t >= LandingMs ? .38 * Math.Exp(-d / 140) * fade : 0,
            t >= LandingMs && t < OutlineMotionEndMs ? Math.Exp(-d / 130) : 0,
            complete);
    }

    /// <summary>
    /// The authored motion time for an elapsed time: unchanged until contact, held on the contact
    /// frame for <see cref="HitStopMs"/>, then the same curve <see cref="HitStopMs"/> later.
    /// </summary>
    internal static double MotionTime(double ms) => ms < LandingMs ? ms : Math.Max(LandingMs, ms - HitStopMs);

    private static double Segment(double t, double from, double to) => Math.Clamp((t - from) / (to - from), 0, 1);
    private static double OutQuad(double t) => 1 - ((1 - t) * (1 - t));
    private static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double Lerp(double from, double to, double p) => from + ((to - from) * p);

    // Solve x(u)=progress before evaluating y(u); unlike KeySpline this permits the
    // authored y=1.6 overshoot during the small compression/rebound after landing.
    private static double Bezier(double progress, double x1, double y1, double x2, double y2)
    {
        if (progress <= 0 || progress >= 1) return progress;
        double low = 0;
        double high = 1;
        for (int i = 0; i < 24; i++)
        {
            double mid = (low + high) / 2;
            if (Cubic(mid, x1, x2) < progress) low = mid;
            else high = mid;
        }
        return Cubic((low + high) / 2, y1, y2);
    }

    private static double Cubic(double t, double first, double second) =>
        (3 * (1 - t) * (1 - t) * t * first) + (3 * (1 - t) * t * t * second) + (t * t * t);

    private sealed class LandingSurface : FrameworkElement
    {
        // Rotating the app mark's pushpin 180 degrees around (175,175), then putting its rotated
        // needle at the origin at 30 DIP, is equivalent to this affine map.
        private const double PushpinSize = 30.0 / (227.5 - 125.41);
        private static readonly Matrix PushpinPlacement = new(-PushpinSize, 0, 0, -PushpinSize, 125.41 * PushpinSize, 125.41 * PushpinSize);
        private static readonly Geometry Pushpin = CreatePushpin();
        private readonly PinLandingLayout _layout;
        private readonly Geometry _outside;
        private readonly Color _accent;
        private readonly Drawing? _pushpinArt;

        internal LandingSurface(PinLandingLayout layout, Color accent)
        {
            _layout = layout;
            _accent = accent;
            _pushpinArt = PushpinArt(this);
            IsHitTestVisible = false;
            var pin = new RectangleGeometry(layout.PinBoundsDip, PinWindow.FrameCornerRadiusDip, PinWindow.FrameCornerRadiusDip);
            var window = new RectangleGeometry(new Rect(0, 0, layout.PhysicalBounds.Width / layout.Scale, layout.PhysicalBounds.Height / layout.Scale));
            _outside = new CombinedGeometry(GeometryCombineMode.Exclude, window, pin);
            _outside.Freeze();
        }

        internal PinLandingFrame Frame { get; set; }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (Frame.IsComplete) return;
            Rect pin = _layout.PinBoundsDip;
            if (_layout.HasAmbientEffects)
            {
                // Clip the composited result, not the blurred source. No shadow or glow
                // pixels can cover the screenshot/text inside the pin's rounded outline.
                drawing.PushClip(_outside);
                Rect shadow = pin;
                shadow.Offset(0, Frame.ShadowOffsetY);
                DrawFeather(drawing, shadow, Frame.ShadowBlur, Colors.Black, Frame.ShadowOpacity);
                DrawFeather(drawing, pin, 14, _accent, Frame.GlowOpacity);
                drawing.Pop();
            }

            Point tip = new(pin.Left + 8, pin.Top + 8);
            if (Frame.RingOpacity > 0)
            {
                var ring = new SolidColorBrush(_accent) { Opacity = Frame.RingOpacity };
                drawing.DrawEllipse(null, new Pen(ring, 2), tip, 8 * Frame.RingScale, 8 * Frame.RingScale);
            }
            if (Frame.PinOpacity <= 0) return;
            drawing.PushOpacity(Frame.PinOpacity);
            drawing.PushTransform(new TranslateTransform(tip.X + Frame.PinOffsetX, tip.Y + Frame.PinOffsetY));
            drawing.PushTransform(new RotateTransform(Frame.PinRotation));
            drawing.PushTransform(new ScaleTransform(Frame.PinScale, Frame.PinScale));
            drawing.PushTransform(new TranslateTransform(0, .4));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(75, 0, 0, 0)), null, Pushpin);
            drawing.Pop();
            if (_pushpinArt is { } art) drawing.DrawDrawing(art);
            else drawing.DrawGeometry(new SolidColorBrush(_accent), null, Pushpin);
            drawing.Pop();
            drawing.Pop();
            drawing.Pop();
            drawing.Pop();
        }

        private static void DrawFeather(DrawingContext drawing, Rect bounds, double blur, Color color, double opacity)
        {
            if (opacity <= 0) return;
            // Twelve translucent contours approximate the blur without allocating a
            // full-window WPF effect render target on every frame of a layered HWND.
            const int bands = 12;
            double previous = 0;
            for (int i = bands; i >= 0; i--)
            {
                double distance = blur * i / bands;
                double cumulative = Math.Exp(-4.5 * distance * distance / (blur * blur));
                double layer = (opacity * (cumulative - previous)) / (1 - (opacity * previous));
                previous = cumulative;
                var brush = new SolidColorBrush(color) { Opacity = layer };
                Rect expanded = bounds;
                expanded.Inflate(distance, distance);
                double radius = PinWindow.FrameCornerRadiusDip + distance;
                drawing.DrawRoundedRectangle(brush, null, expanded, radius, radius);
            }
        }

        private static Geometry CreatePushpin()
        {
            Geometry geometry = Geometry.Parse("M125.41 125.41 L161.25 154.74 L175.38 140.62 C178.27 137.73 181.17 137.73 184.06 140.62 C185.5 142.06 185.87 145.33 185.15 150.38 L182.98 163.42 L196.02 176.46 L203.62 168.85 C207.95 164.51 212.3 164.51 216.64 168.85 L223.17 175.38 C227.5 179.71 227.5 184.06 223.17 188.4 L188.4 223.17 C184.06 227.5 179.71 227.5 175.38 223.17 L168.85 216.64 C164.51 212.3 164.51 207.95 168.85 203.62 L176.46 196.02 L163.42 182.98 L150.38 185.15 C145.33 185.87 142.06 185.5 140.62 184.06 C137.73 181.17 137.73 178.27 140.62 175.38 L154.74 161.25 L125.41 125.41 Z");
            // Geometry.Parse can return a frozen geometry; transform our own mutable copy.
            geometry = geometry.Clone();
            geometry.Transform = new MatrixTransform(PushpinPlacement);
            geometry.Freeze();
            return geometry;
        }

        /// <summary>
        /// The pushpin parts of the app mark (Themes/Controls.xaml, Brand.Mark), so the landing pin
        /// and the icon share one artwork and its colours stay in that resource. Null where the
        /// app resources are not loaded; the pin then falls back to the outline accent.
        /// </summary>
        private static Drawing? PushpinArt(FrameworkElement owner)
        {
            if (owner.TryFindResource("Brand.Mark") is not DrawingImage { Drawing: DrawingGroup mark }) return null;
            var art = new DrawingGroup { Transform = new MatrixTransform(PushpinPlacement) };
            foreach (GeometryDrawing part in mark.Children.OfType<GeometryDrawing>())
            {
                // The mark's pushpin and its highlight sit in the lower-right quarter of the 256 grid.
                Rect bounds = part.Geometry.Bounds;
                if (bounds.Left >= 120 && bounds.Top >= 120) art.Children.Add(part.Clone());
            }
            if (art.Children.Count == 0) return null;
            art.Freeze();
            return art;
        }
    }
}

internal readonly record struct PinLandingFrame(
    double PinOffsetX, double PinOffsetY, double PinRotation, double PinScale, double PinOpacity,
    double RingScale, double RingOpacity, double ShadowOffsetY, double ShadowBlur, double ShadowOpacity,
    double GlowOpacity, double BorderAccentAmount, bool IsComplete);

internal readonly record struct PinLandingLayout(RectD PhysicalBounds, Rect PinBoundsDip, bool HasAmbientEffects, double Scale);

/// <summary>Counts every eligible opening in a rolling second, including suppressed openings.</summary>
internal sealed class PinLandingThrottle
{
    private readonly Queue<double> _openings = new();

    internal bool TryAcquire(double ms)
    {
        while (_openings.TryPeek(out double oldest) && ms - oldest >= 1000) _openings.Dequeue();
        _openings.Enqueue(ms);
        return _openings.Count <= 3;
    }
}
