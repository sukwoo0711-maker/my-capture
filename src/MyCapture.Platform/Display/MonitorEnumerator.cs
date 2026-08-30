using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>
/// One physical display.
/// </summary>
/// <param name="DeviceName">Adapter device name, for example <c>\\.\DISPLAY1</c>.</param>
/// <param name="Bounds">
/// Full monitor rectangle in physical pixels, in virtual-desktop coordinates.
/// </param>
/// <param name="WorkArea">Bounds minus the taskbar and other appbars.</param>
/// <param name="Dpi">Effective DPI. 96 is 100% scaling.</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
public sealed record MonitorInfo(
    string DeviceName,
    RectD Bounds,
    RectD WorkArea,
    uint Dpi,
    bool IsPrimary)
{
    /// <summary>
    /// Scale factor, 1.5 at 150%.
    /// </summary>
    /// <remarks>
    /// Used to size annotation handles and default stroke widths so they look the
    /// same to the user regardless of which monitor a capture came from.
    /// </remarks>
    public double ScaleFactor => Dpi / 96.0;

    public int PixelWidth => (int)Math.Round(Bounds.Width);

    public int PixelHeight => (int)Math.Round(Bounds.Height);
}

/// <summary>
/// Enumerates displays and resolves which display a point belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Because the process is manifested as PerMonitorV2, every coordinate Win32 returns
/// here is already in physical pixels on a single virtual-desktop plane. That is the
/// property the whole capture pipeline relies on: without PerMonitorV2 the OS
/// virtualises coordinates per DPI context and captures come back cropped or blurred
/// on scaled monitors.
/// </para>
/// <para>
/// Not cached. Display topology changes when a laptop is docked, a monitor is
/// unplugged, or scaling is altered, and a stale cache produces a capture overlay
/// that covers the wrong area — a failure the user cannot diagnose. Enumeration costs
/// microseconds, so it is done fresh on every capture.
/// </para>
/// </remarks>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> GetAll()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT clip, IntPtr data)
        {
            MonitorInfo? info = Describe(hMonitor);
            if (info is not null)
            {
                monitors.Add(info);
            }

            return true; // keep enumerating
        }

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // EnumDisplayMonitors can legitimately return nothing on a session with
            // no attached display (locked RDP session, headless host). Returning an
            // empty list would make callers crash on Records[0]; a synthetic primary
            // keeps them on a sane path.
            monitors.Add(new MonitorInfo(
                DeviceName: "\\\\.\\DISPLAY1",
                Bounds: new RectD(0, 0, 1920, 1080),
                WorkArea: new RectD(0, 0, 1920, 1040),
                Dpi: 96,
                IsPrimary: true));
        }

        return monitors;
    }

    /// <summary>
    /// The display containing <paramref name="screenPoint"/>, or the nearest one.
    /// </summary>
    public static MonitorInfo GetFromPoint(PointD screenPoint)
    {
        var pt = new NativeMethods.POINT
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y),
        };

        IntPtr handle = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return Describe(handle) ?? GetPrimary();
    }

    public static MonitorInfo GetFromWindow(IntPtr hwnd)
    {
        IntPtr handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return Describe(handle) ?? GetPrimary();
    }

    public static MonitorInfo GetFromCursor()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            IntPtr handle = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            MonitorInfo? info = Describe(handle);
            if (info is not null)
            {
                return info;
            }
        }

        return GetPrimary();
    }

    public static MonitorInfo GetPrimary()
    {
        IReadOnlyList<MonitorInfo> all = GetAll();
        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

    /// <summary>
    /// Rectangle enclosing every display, in physical pixels.
    /// </summary>
    public static RectD GetVirtualDesktopBounds()
    {
        IReadOnlyList<MonitorInfo> all = GetAll();

        double left = all.Min(m => m.Bounds.Left);
        double top = all.Min(m => m.Bounds.Top);
        double right = all.Max(m => m.Bounds.Right);
        double bottom = all.Max(m => m.Bounds.Bottom);

        return new RectD(left, top, right - left, bottom - top);
    }

    private static MonitorInfo? Describe(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
        };

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref info))
        {
            return null;
        }

        uint dpi = 96;
        if (NativeMethods.GetDpiForMonitor(
                hMonitor, NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            dpi = dpiX;
        }

        return new MonitorInfo(
            DeviceName: info.szDevice ?? string.Empty,
            Bounds: ToRect(info.rcMonitor),
            WorkArea: ToRect(info.rcWork),
            Dpi: dpi,
            IsPrimary: (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0);
    }

    private static RectD ToRect(NativeMethods.RECT r) =>
        new(r.Left, r.Top, r.Width, r.Height);
}
