using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyCapture.App.Capture;

internal static class CaptureWindowVisibility
{
    private const int TransitionsForceDisabled = 3;

    internal static async Task HideAndConfirmAsync(Window window)
    {
        window.Dispatcher.VerifyAccess();
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        int disabled = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, TransitionsForceDisabled, ref disabled, sizeof(int)));
        try
        {
            window.Hide();
            // Hide changes the HWND immediately; the compositor can still hold its last pixels.
            // Disable the transition before hiding, then wait for the submitted desktop update.
            // A stalled compositor must not freeze the dispatcher or start a stale capture.
            int result = await Task.Run(DwmFlush).WaitAsync(TimeSpan.FromSeconds(1));
            Marshal.ThrowExceptionForHR(result);
            if (window.IsVisible || IsWindowVisible(handle))
                throw new InvalidOperationException("The capture source window is still visible.");
        }
        finally
        {
            // This attribute is write-only. The library normally uses the system transition
            // policy; return to that policy after this capture-specific hide.
            int enabled = 0;
            _ = DwmSetWindowAttribute(handle, TransitionsForceDisabled, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
