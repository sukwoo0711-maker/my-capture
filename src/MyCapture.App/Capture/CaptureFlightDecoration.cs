using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>
/// A passive, capture-excluded, click-through layered window that draws one captured image
/// and its shadow in virtual-desktop physical pixels (<see cref="CaptureFlight"/>).
/// </summary>
/// <remarks>
/// It never activates and never takes input, like <see cref="Pinning.PinLandingDecoration"/>.
/// The surface draws in physical pixels whatever DPI Windows gives the window, so an image drawn
/// at its source rectangle covers exactly the pixels it was cut from.
/// </remarks>
internal sealed class CaptureFlightDecoration : Window
{
    internal static Func<Window, bool> ApplyCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;

    private readonly FlightSurface _surface;
    private HwndSource? _source;

    internal CaptureFlightDecoration(BitmapSource image, RectD bounds, double scale)
    {
        Bounds = bounds;
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
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        Left = bounds.Left / scale;
        Top = bounds.Top / scale;
        Width = Math.Max(1, bounds.Width / scale);
        Height = Math.Max(1, bounds.Height / scale);
        FluidMotion.SetWindowEntrance(this, false);
        _surface = new FlightSurface(image, new Point(bounds.Left, bounds.Top));
        Content = _surface;
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>The window's virtual-desktop rectangle in physical pixels.</summary>
    internal RectD Bounds { get; }

    internal bool IsCaptureExcluded { get; private set; }

    internal bool IsClosed { get; private set; }

    /// <summary>What the window draws now, in virtual-desktop physical pixels.</summary>
    internal CaptureFlightFrame Frame => _surface.Frame;

    /// <summary>Shows only after capture exclusion has succeeded, without changing the foreground window.</summary>
    internal bool ShowDecoration()
    {
        if (IsClosed) return false;
        IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
        if (!IsCaptureExcluded) return false;
        Show();
        // GetWindowRect is authoritative, not WPF Left/Top, which use a different logical
        // coordinate plane on mixed-DPI desktops.
        PhysicalWindowPositioner.PlaceTopmostWithoutActivation(handle, Bounds);
        MatchDisplayScale(VisualTreeHelper.GetDpi(this));
        return !IsClosed;
    }

    /// <summary>Puts the window above every other topmost window, such as a card shown after it.</summary>
    internal void Raise()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!IsClosed && handle != IntPtr.Zero) PhysicalWindowPositioner.PlaceTopmostWithoutActivation(handle, Bounds);
    }

    internal void Draw(CaptureFlightFrame frame)
    {
        if (IsClosed) return;
        _surface.Frame = frame;
        _surface.InvalidateVisual();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        WindowStyleFacade.SetClickThrough(handle, enabled: true);
        WindowStyleFacade.ExcludeFromAltTab(handle);
        // A native close fade could outlive the HWND and appear in the next screenshot.
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

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        MatchDisplayScale(newDpi);
    }

    private void MatchDisplayScale(DpiScale dpi)
    {
        _surface.Dpi = dpi;
        _surface.InvalidateVisual();
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        _source?.RemoveHook(NoActivateHook);
        _source = null;
        SourceInitialized -= OnSourceInitialized;
        base.OnClosed(e);
    }

    private sealed class FlightSurface : FrameworkElement
    {
        private readonly BitmapSource _image;
        private readonly Point _origin;

        internal FlightSurface(BitmapSource image, Point origin)
        {
            _image = image;
            _origin = origin;
            IsHitTestVisible = false;
        }

        internal CaptureFlightFrame Frame { get; set; }

        internal DpiScale Dpi { get; set; } = new(1, 1);

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            CaptureFlightFrame frame = Frame;
            if (frame.Opacity <= 0 || frame.Image.IsEmpty || frame.Image.Width <= 0 || frame.Image.Height <= 0) return;
            // Physical virtual-desktop pixels to this window's DIPs: the image drawn at its source
            // rectangle lands on exactly the pixels it was cut from.
            double sx = 1 / Dpi.DpiScaleX, sy = 1 / Dpi.DpiScaleY;
            drawing.PushTransform(new MatrixTransform(sx, 0, 0, sy, -_origin.X * sx, -_origin.Y * sy));
            drawing.PushOpacity(frame.Opacity);
            Rect shadow = frame.Image;
            shadow.Offset(0, frame.ShadowOffset);
            DrawFeather(drawing, shadow, frame.ShadowBlur, frame.ShadowOpacity);
            drawing.DrawImage(_image, frame.Image);
            drawing.Pop();
            drawing.Pop();
        }

        private static void DrawFeather(DrawingContext drawing, Rect bounds, double blur, double opacity)
        {
            if (opacity <= 0 || blur <= 0) return;
            // Twelve translucent contours approximate a blur without allocating a full-window
            // WPF effect render target on every frame of a layered window.
            const int bands = 12;
            double previous = 0;
            for (int i = bands; i >= 0; i--)
            {
                double distance = blur * i / bands;
                double cumulative = Math.Exp(-4.5 * distance * distance / (blur * blur));
                double layer = (opacity * (cumulative - previous)) / (1 - (opacity * previous));
                previous = cumulative;
                var brush = new SolidColorBrush(Colors.Black) { Opacity = layer };
                brush.Freeze();
                Rect expanded = bounds;
                expanded.Inflate(distance, distance);
                drawing.DrawRoundedRectangle(brush, null, expanded, distance, distance);
            }
        }
    }
}

/// <summary>
/// One drawn state of a capture in flight, in virtual-desktop physical pixels: the image
/// rectangle, its shadow (blur, downward offset and opacity) and the whole layer's opacity.
/// </summary>
internal readonly record struct CaptureFlightFrame(
    Rect Image, double ShadowBlur, double ShadowOffset, double ShadowOpacity, double Opacity);
