using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Themes;

/// <summary>
/// Opens a normal application window inside one monitor's work area, sized at that monitor's DPI.
/// </summary>
/// <remarks>
/// WPF's own sizing reads the primary work area (<see cref="SystemParameters.WorkArea"/>) while
/// CenterScreen centres on the pointer's monitor, so beside an ultrawide primary a window opened on
/// a narrow or portrait secondary monitor came out wider than that monitor, half off-screen and
/// spilling onto the next one. Here the size and the position both come from the target monitor.
/// </remarks>
internal static class MonitorWindowPlacement
{
    /// <summary>A window size and minimum in DIPs at the monitor's scale, and its centred physical bounds.</summary>
    internal readonly record struct WindowFit(Size Size, Size Minimum, RectD Bounds);

    /// <summary>
    /// Fits <paramref name="desired"/> into <paramref name="workArea"/> (physical pixels) at
    /// <paramref name="scale"/>, keeping <paramref name="marginDip"/> free across each dimension when the
    /// monitor allows it, and centres it. A minimum larger than the work area shrinks to the work area,
    /// so the window can always be shown whole.
    /// </summary>
    internal static WindowFit Fit(RectD workArea, double scale, Size desired, Size minimum, double marginDip)
    {
        double factor = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        RectD work = workArea.Normalized();
        double availableWidth = Math.Max(1, Math.Floor(work.Width) / factor);
        double availableHeight = Math.Max(1, Math.Floor(work.Height) / factor);
        double minWidth = Math.Clamp(Finite(minimum.Width), 0, availableWidth);
        double minHeight = Math.Clamp(Finite(minimum.Height), 0, availableHeight);
        double margin = Math.Max(0, Finite(marginDip));
        double width = Math.Clamp(Finite(desired.Width, availableWidth), minWidth, Math.Max(minWidth, availableWidth - margin));
        double height = Math.Clamp(Finite(desired.Height, availableHeight), minHeight, Math.Max(minHeight, availableHeight - margin));
        // Whole physical pixels that never exceed the work area, centred in it.
        double pixelWidth = Math.Min(Math.Floor(work.Width), Math.Round(width * factor));
        double pixelHeight = Math.Min(Math.Floor(work.Height), Math.Round(height * factor));
        double left = Math.Floor(work.Left + (work.Width - pixelWidth) / 2);
        double top = Math.Floor(work.Top + (work.Height - pixelHeight) / 2);
        return new WindowFit(new Size(pixelWidth / factor, pixelHeight / factor), new Size(minWidth, minHeight),
            new RectD(left, top, pixelWidth, pixelHeight));
    }

    /// <summary>
    /// The monitor showing most of <paramref name="screenRegion"/> (physical pixels); for a region on
    /// no monitor, the one nearest its centre. Null only for an empty monitor list.
    /// </summary>
    internal static MonitorInfo? MonitorFor(IReadOnlyList<MonitorInfo> monitors, RectD screenRegion)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        RectD region = screenRegion.Normalized();
        MonitorInfo? best = null;
        double bestArea = 0;
        foreach (MonitorInfo monitor in monitors)
        {
            RectD bounds = monitor.Bounds.Normalized();
            double width = Math.Min(bounds.Right, region.Right) - Math.Max(bounds.Left, region.Left);
            double height = Math.Min(bounds.Bottom, region.Bottom) - Math.Max(bounds.Top, region.Top);
            double area = width > 0 && height > 0 ? width * height : 0;
            if (area > bestArea)
            {
                bestArea = area;
                best = monitor;
            }
        }

        if (best is not null) return best;
        PointD centre = region.Center;
        return monitors.OrderBy(monitor => DistanceSquared(monitor.Bounds.Normalized(), centre)).FirstOrDefault();
    }

    /// <summary>
    /// Opens <paramref name="window"/> centred in <paramref name="monitor"/>'s work area at that
    /// monitor's DPI, from <paramref name="desired"/> and <paramref name="minimum"/> (DIPs). Call before
    /// the window is shown; a later call replaces an earlier one.
    /// </summary>
    internal static void CenterOn(Window window, MonitorInfo monitor, Size desired, Size minimum, double marginDip)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitor);
        WindowFit fit = Fit(monitor.WorkArea, monitor.ScaleFactor, desired, minimum, marginDip);
        double scale = monitor.ScaleFactor > 0 ? monitor.ScaleFactor : 1.0;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.MinWidth = fit.Minimum.Width;
        window.MinHeight = fit.Minimum.Height;
        window.Width = fit.Size.Width;
        window.Height = fit.Size.Height;
        // WPF creates the HWND from these DIP values; the exact physical rectangle follows once it exists.
        window.Left = fit.Bounds.Left / scale;
        window.Top = fit.Bounds.Top / scale;
        Placement.For(window).Target = fit.Bounds;
    }

    /// <summary>
    /// Keeps a window that opens centred on the pointer's monitor (CenterScreen without an owner)
    /// inside that monitor: its current size and minimum are fitted to the monitor's work area when the
    /// window is created. Call once, from the window's constructor.
    /// </summary>
    internal static void KeepOnPointerMonitor(Window window, double marginDip)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnSourceInitialized;
            if (window.WindowStartupLocation != WindowStartupLocation.CenterScreen || window.Owner is not null) return;
            MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
            WindowFit fit = Fit(monitor.WorkArea, monitor.ScaleFactor, new Size(window.Width, window.Height),
                new Size(window.MinWidth, window.MinHeight), marginDip);
            window.MinWidth = fit.Minimum.Width;
            window.MinHeight = fit.Minimum.Height;
            Placement.For(window).Target = fit.Bounds;
        }
    }

    private static double Finite(double value, double fallback = 0) =>
        double.IsFinite(value) ? value : fallback;

    private static double DistanceSquared(RectD bounds, PointD point)
    {
        double dx = point.X < bounds.Left ? bounds.Left - point.X : point.X > bounds.Right ? point.X - bounds.Right : 0;
        double dy = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y > bounds.Bottom ? point.Y - bounds.Bottom : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Applies the target rectangle in physical pixels once the HWND exists, and again after the first
    /// DPI change while the window opens: creating it on one monitor and moving it to another of a
    /// different scale makes WPF rescale it from the old size.
    /// </summary>
    private sealed class Placement
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Placement> Table = new();
        private readonly Window _window;
        private RectD? _target;
        private bool _rendered;

        private Placement(Window window)
        {
            _window = window;
            window.SourceInitialized += (_, _) => Apply();
            window.DpiChanged += (_, _) =>
            {
                if (!_rendered) _ = window.Dispatcher.BeginInvoke(new Action(Apply));
            };
            window.ContentRendered += (_, _) => _rendered = true;
        }

        internal static Placement For(Window window) => Table.GetValue(window, static item => new Placement(item));

        internal RectD? Target
        {
            get => _target;
            set
            {
                _target = value;
                if (new WindowInteropHelper(_window).Handle != IntPtr.Zero) Apply();
            }
        }

        private void Apply()
        {
            IntPtr handle = new WindowInteropHelper(_window).Handle;
            if (_target is not RectD target || handle == IntPtr.Zero || _window.WindowState != WindowState.Normal) return;
            // SWP_NOZORDER | SWP_NOACTIVATE: only the rectangle changes.
            _ = SetWindowPos(handle, IntPtr.Zero, (int)target.Left, (int)target.Top, (int)target.Width, (int)target.Height,
                0x0004 | 0x0010);
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height,
            uint flags);
    }
}
