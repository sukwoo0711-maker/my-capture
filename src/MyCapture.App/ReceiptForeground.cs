using System.Runtime.InteropServices;

namespace MyCapture.App;

internal static class ReceiptForeground
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    internal static nint Get() => GetForegroundWindow();
    internal static void Restore(nint hwnd) { if (hwnd != 0 && IsWindow(hwnd)) _ = SetForegroundWindow(hwnd); }
}
