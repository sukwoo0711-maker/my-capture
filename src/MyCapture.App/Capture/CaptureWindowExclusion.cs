using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyCapture.App.Capture;

internal static class CaptureWindowExclusion
{
    // EnsureHandle creates an invisible HWND, so failure is handled before any UI pixels appear.
    internal static bool TryApply(Window window)
    {
        window.Dispatcher.VerifyAccess();
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        return handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, 0x11);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
