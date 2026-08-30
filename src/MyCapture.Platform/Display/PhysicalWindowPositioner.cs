using System.ComponentModel;
using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>Places a WPF HWND on exact virtual-desktop physical pixel bounds.</summary>
public static class PhysicalWindowPositioner
{
    public static void PlaceTopmost(IntPtr hwnd, RectD screenBounds)
    {
        if (hwnd == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
        }

        RectD bounds = screenBounds.ToPixelBounds();
        if (!NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                (int)bounds.Left,
                (int)bounds.Top,
                Math.Max(1, (int)bounds.Width),
                Math.Max(1, (int)bounds.Height),
                NativeMethods.SWP_SHOWWINDOW))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                "Could not position the capture overlay on its monitor.");
        }
    }
}
