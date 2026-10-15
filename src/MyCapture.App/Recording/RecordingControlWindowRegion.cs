using System.ComponentModel;
using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Recording;

/// <summary>The opaque control HWND consists only of its frame ring and palette, in physical pixels.</summary>
internal static class RecordingControlWindowRegion
{
    internal static void Apply(IntPtr hwnd, RectD window, RectD frame, RectD palette, double borderPixels)
    {
        IntPtr region = IntPtr.Zero;
        IntPtr interior = IntPtr.Zero;
        IntPtr strip = IntPtr.Zero;
        try
        {
            region = Rectangle(frame, window);
            // A very small capture still has a solid frame, never an inverted inner rectangle.
            double insetX = Math.Min(borderPixels, frame.Width / 2);
            double insetY = Math.Min(borderPixels, frame.Height / 2);
            interior = Rectangle(new RectD(frame.Left + insetX, frame.Top + insetY,
                frame.Width - (2 * insetX), frame.Height - (2 * insetY)), window);
            strip = Rectangle(palette, window);
            if (CombineRgn(region, region, interior, 4 /* RGN_DIFF */) == 0
                || CombineRgn(region, region, strip, 2 /* RGN_OR */) == 0
                || SetWindowRgn(hwnd, region, true) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not shape the recording controls.");
            // On success Windows owns this HRGN and frees it when replaced or the HWND closes.
            region = IntPtr.Zero;
        }
        finally
        {
            if (region != IntPtr.Zero) _ = DeleteObject(region);
            if (interior != IntPtr.Zero) _ = DeleteObject(interior);
            if (strip != IntPtr.Zero) _ = DeleteObject(strip);
        }
    }

    private static IntPtr Rectangle(RectD rectangle, RectD window)
    {
        IntPtr region = CreateRectRgn(
            checked((int)Math.Round(rectangle.Left - window.Left)),
            checked((int)Math.Round(rectangle.Top - window.Top)),
            checked((int)Math.Round(rectangle.Right - window.Left)),
            checked((int)Math.Round(rectangle.Bottom - window.Top)));
        return region != IntPtr.Zero ? region
            : throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate a recording control region.");
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr region);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
