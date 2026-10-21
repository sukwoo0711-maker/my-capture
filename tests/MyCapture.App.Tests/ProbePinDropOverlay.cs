using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfPath = System.Windows.Shapes.Path;

namespace MyCapture.App.Tests;

/// <summary>A decoration-only native-window variant of the opt-in pin-open probe.</summary>
internal sealed class ProbePinDropOverlay : Window, IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _pin;
    private readonly ProbePinDropVisual _visual;
    private bool _closed;

    internal ProbePinDropOverlay(Window pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        _pin = pin;
        Width = ProbePinDropVisual.SizeDip;
        Height = ProbePinDropVisual.SizeDip;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        _visual = new ProbePinDropVisual(this);
        Content = _visual.Element;
        _visual.FirstFrame += OnVisualFirstFrame;
        _visual.Completed += OnVisualCompleted;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += OnActivated;
        _pin.Closed += OnPinClosed;
    }

    internal int ActivationCount { get; private set; }

    /// <summary>Stopwatch ticks at the first WPF Rendering callback after Loaded; not a DWM present.</summary>
    internal long? FirstFrameTimestamp => _visual.FirstFrameTimestamp;

    internal event EventHandler? FirstFrame;

    /// <summary>
    /// Adds variant D through public WPF APIs. A Canvas has no desired size from its children,
    /// so the decoration does not change the pin's sizing. It inherits the chrome's existing
    /// opacity, reveal scale and clipping; that is a structural difference from the HWND variant.
    /// </summary>
    internal static ProbePinDropVisual? AttachToPin(Window pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Content is not Grid chrome)
        {
            return null;
        }

        var host = new Canvas
        {
            IsHitTestVisible = false,
            Focusable = false,
        };
        Panel.SetZIndex(host, int.MaxValue);
        var visual = new ProbePinDropVisual();
        visual.AttachTo(pin, chrome, host);
        return visual;
    }

    public void Dispose()
    {
        if (!_closed)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnLoaded;
        Activated -= OnActivated;
        _pin.Closed -= OnPinClosed;
        _visual.FirstFrame -= OnVisualFirstFrame;
        _visual.Completed -= OnVisualCompleted;
        _visual.Dispose();
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        Marshal.SetLastPInvokeError(0);
        nint current = GetWindowLongPtr(handle, GwlExStyle);
        ThrowOnZeroWithError(current);
        nint requested = new(current.ToInt64() | WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow);
        Marshal.SetLastPInvokeError(0);
        nint previous = SetWindowLongPtr(handle, GwlExStyle, requested);
        ThrowOnZeroWithError(previous);
        PositionOverPinCorner();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WPF can process a DPI change between SourceInitialized and Loaded. Reapply the
        // physical placement after that transition, as the pin's own restore path does.
        PositionOverPinCorner();
    }

    private void PositionOverPinCorner()
    {
        nint pinHandle = new WindowInteropHelper(_pin).Handle;
        if (pinHandle == 0)
        {
            throw new InvalidOperationException("Show the pin before showing its probe overlay.");
        }

        if (!GetWindowRect(pinHandle, out NativeRect pinBounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        uint dpi = GetDpiForWindow(pinHandle);
        if (dpi == 0)
        {
            throw new InvalidOperationException("GetDpiForWindow returned zero for the probe pin.");
        }

        // Both variants place the same 72-DIP canvas at the pin's top-left. Use native
        // coordinates, including negative desktop coordinates, rather than scaling Left/Top.
        int sizePixels = checked((int)Math.Round(ProbePinDropVisual.SizeDip * dpi / 96.0));
        if (!SetWindowPos(
                new WindowInteropHelper(this).Handle,
                HwndTopmost,
                pinBounds.Left,
                pinBounds.Top,
                sizePixels,
                sizePixels,
                SwpNoActivate | SwpNoOwnerZOrder | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void ThrowOnZeroWithError(nint result)
    {
        int error = Marshal.GetLastPInvokeError();
        if (result == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    private void OnActivated(object? sender, EventArgs e) => ActivationCount++;

    private void OnVisualFirstFrame(object? sender, EventArgs e) => FirstFrame?.Invoke(this, EventArgs.Empty);

    private void OnVisualCompleted(object? sender, EventArgs e) => Dispose();

    private void OnPinClosed(object? sender, EventArgs e) => Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}

/// <summary>The shared glyph, animation and lifetime for separate-window and in-chrome variants.</summary>
internal sealed class ProbePinDropVisual : IDisposable
{
    internal const double SizeDip = 72;
    private const double TipX = 14;
    private const double TipY = 60;
    private const double SourceTip = 125.41;
    private const double GlyphScale = 0.34;

    // Exact signal and highlight paths from Assets/mycapture-mark.svg. The fixed transform
    // rotates the original SE-facing head -90 degrees to NE, preserving the glyph's shape.
    private const string SignalPath = "M125.41 125.41 L161.25 154.74 L175.38 140.62 C178.27 137.73 181.17 137.73 184.06 140.62 C185.5 142.06 185.87 145.33 185.15 150.38 L182.98 163.42 L196.02 176.46 L203.62 168.85 C207.95 164.51 212.3 164.51 216.64 168.85 L223.17 175.38 C227.5 179.71 227.5 184.06 223.17 188.4 L188.4 223.17 C184.06 227.5 179.71 227.5 175.38 223.17 L168.85 216.64 C164.51 212.3 164.51 207.95 168.85 203.62 L176.46 196.02 L163.42 182.98 L150.38 185.15 C145.33 185.87 142.06 185.5 140.62 184.06 C137.73 181.17 137.73 178.27 140.62 175.38 L154.74 161.25 L125.41 125.41 Z";
    private const string HighlightPath = "M208.22 170.75 C209.86 170.58 211.58 171.02 213.39 172.11 L177.55 207.95 C174.66 210.85 173.92 213.74 175.38 216.64 L172.11 213.39 C169.22 210.5 168.85 207.95 171.02 205.78 L203.62 173.2 C205.06 171.74 206.59 170.94 208.22 170.75 Z";

    private readonly ScaleTransform _entryScale = new(1.3, 1.3, TipX, TipY);
    private readonly ScaleTransform _squashScale = new(1, 1, TipX, TipY);
    private readonly TranslateTransform _drop = new(0, -14);
    private readonly ScaleTransform _ringScale = new(0.6, 0.6, 6, 6);
    private readonly Ellipse _ring;
    private readonly UIElement _fadeTarget;
    private readonly DispatcherTimer _lifetime;
    private Window? _pin;
    private Grid? _chrome;
    private Canvas? _host;
    private bool _started;
    private bool _disposed;

    internal ProbePinDropVisual(UIElement? fadeTarget = null)
    {
        Element = new Canvas
        {
            Width = SizeDip,
            Height = SizeDip,
            IsHitTestVisible = false,
            Focusable = false,
        };
        _fadeTarget = fadeTarget ?? Element;

        _ring = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = new SolidColorBrush(Color.FromRgb(0xF3, 0x5B, 0x4D)),
            StrokeThickness = 1,
            Opacity = 0,
            RenderTransform = _ringScale,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(_ring, TipX - 6);
        Canvas.SetTop(_ring, TipY - 6);
        Element.Children.Add(_ring);

        var signal = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(170, 161),
            EndPoint = new Point(223, 226),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xF3, 0x5B, 0x4D), 0),
                new GradientStop(Color.FromRgb(0xBB, 0x25, 0x2D), 1),
            },
        };
        signal.Freeze();
        var glyph = new Canvas
        {
            Width = SizeDip,
            Height = SizeDip,
            RenderTransform = new TransformGroup { Children = { _entryScale, _squashScale, _drop } },
        };
        var source = new Canvas
        {
            RenderTransform = new MatrixTransform(
                0, -GlyphScale, GlyphScale, 0,
                TipX - (SourceTip * GlyphScale), TipY + (SourceTip * GlyphScale)),
            Children =
            {
                new WpfPath { Data = ParseFrozen(SignalPath), Fill = signal },
                new WpfPath { Data = ParseFrozen(HighlightPath), Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x76, 0x67)) },
            },
        };
        glyph.Children.Add(source);
        Element.Children.Add(glyph);
        Element.Loaded += OnLoaded;
        Element.Unloaded += OnUnloaded;
        _lifetime = new DispatcherTimer(DispatcherPriority.Normal, Element.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(560),
        };
        _lifetime.Tick += OnLifetimeElapsed;
    }

    internal Canvas Element { get; }

    internal long? FirstFrameTimestamp { get; private set; }

    internal event EventHandler? FirstFrame;

    internal event EventHandler? Completed;

    internal void AttachTo(Window pin, Grid chrome, Canvas host)
    {
        _pin = pin;
        _chrome = chrome;
        _host = host;
        pin.Closed += OnPinClosed;
        host.Children.Add(Element);
        chrome.Children.Add(host);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Stop();
        _lifetime.Tick -= OnLifetimeElapsed;
        Element.Loaded -= OnLoaded;
        Element.Unloaded -= OnUnloaded;
        CompositionTarget.Rendering -= OnRendering;
        if (_pin is not null)
        {
            _pin.Closed -= OnPinClosed;
        }

        if (_host is not null && _chrome is not null)
        {
            _chrome.Children.Remove(_host);
            _host.Children.Remove(Element);
        }

        _pin = null;
        _chrome = null;
        _host = null;
        _entryScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _entryScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _squashScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _drop.BeginAnimation(TranslateTransform.YProperty, null);
        _ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _ring.BeginAnimation(UIElement.OpacityProperty, null);
        _fadeTarget.SetCurrentValue(UIElement.OpacityProperty, 0.0);
        _fadeTarget.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static Geometry ParseFrozen(string path)
    {
        Geometry geometry = Geometry.Parse("F1 " + path);
        geometry.Freeze();
        return geometry;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Element.Loaded -= OnLoaded;
        CompositionTarget.Rendering += OnRendering;
        _drop.BeginAnimation(TranslateTransform.YProperty, Entrance(-14, 0));
        _entryScale.BeginAnimation(ScaleTransform.ScaleXProperty, Entrance(1.3, 1));
        _entryScale.BeginAnimation(ScaleTransform.ScaleYProperty, Entrance(1.3, 1));

        var squash = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
            Duration = TimeSpan.FromMilliseconds(100),
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50))),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))),
            },
        };
        _squashScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        var ringScale = new DoubleAnimation(0.6, 1.6, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
        };
        _ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, ringScale);
        _ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, ringScale.Clone());
        _ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.5, 0, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
        });
        _fadeTarget.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
        {
            BeginTime = TimeSpan.FromMilliseconds(440),
        });
        _lifetime.Start();
    }

    private static DoubleAnimationUsingKeyFrames Entrance(double from, double to) => new()
    {
        Duration = TimeSpan.FromMilliseconds(180),
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new SplineDoubleKeyFrame(
                to,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
                new KeySpline(0, 0, 0, 1)),
        },
    };

    private void OnRendering(object? sender, EventArgs e)
    {
        FirstFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering -= OnRendering;
        FirstFrame?.Invoke(this, EventArgs.Empty);
    }

    private void OnLifetimeElapsed(object? sender, EventArgs e)
    {
        Dispose();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void OnPinClosed(object? sender, EventArgs e) => Dispose();
}
