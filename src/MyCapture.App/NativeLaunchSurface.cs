using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MyCapture.Platform.Display;

namespace MyCapture.App;

/// <summary>A system-drawn notice with no theme, settings, WPF resources or application services.</summary>
internal sealed class NativeLaunchSurface(bool secondary) : ILaunchSurface
{
    private IntPtr _window;
    private IntPtr _font;
    internal IntPtr Handle => _window;

    public void Show()
    {
        var monitor = MonitorEnumerator.GetFromCursor();
        double scale = Math.Max(1, monitor.ScaleFactor);
        int width = Math.Min((int)(380 * scale), (int)monitor.WorkArea.Width);
        int height = Math.Min((int)(140 * scale), (int)monitor.WorkArea.Height);
        int left = (int)(monitor.WorkArea.Left + (monitor.WorkArea.Width - width) / 2);
        int top = (int)(monitor.WorkArea.Top + (monitor.WorkArea.Height - height) / 2);
        // The system dialog class and STATIC text use system colours, including high contrast.
        // A tool window does not introduce a transient taskbar button or become WPF's MainWindow.
        _window = CreateWindowEx(0x00000080, "#32770", "MyCapture", 0x80000000 | 0x00C00000,
            left, top, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _ = GetClientRect(_window, out Rect client);
        int margin = (int)(20 * scale);
        string message = secondary
            ? "MyCapture가 시작하는 중입니다.\r\n잠시 뒤 라이브러리가 열립니다."
            : "MyCapture 시작 중";
        IntPtr label = CreateWindowEx(0, "STATIC", message, 0x40000000 | 0x10000000 | 0x00000001,
            margin, margin, Math.Max(1, client.Right - margin * 2), Math.Max(1, client.Bottom - margin * 2),
            _window, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (label == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        _font = CreateFont(-(int)Math.Round(14 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (_font != IntPtr.Zero) _ = SendMessage(label, 0x0030, _font, new IntPtr(1)); // WM_SETFONT
        _ = ShowWindow(_window, 5);
        _ = UpdateWindow(_window);
        if (!IsWindowVisible(_window)) throw new InvalidOperationException("Launch notice is not visible.");
    }

    public void Run(WaitHandle finished, TimeSpan? duration)
    {
        var elapsed = Stopwatch.StartNew();
        IntPtr[] handles = [finished.SafeWaitHandle.DangerousGetHandle()];
        while (IsWindow(_window))
        {
            double remaining = duration is { } limit ? (limit - elapsed.Elapsed).TotalMilliseconds : uint.MaxValue;
            if (remaining <= 0) return;
            uint result = MsgWaitForMultipleObjects(1, handles, false,
                (uint)Math.Min(uint.MaxValue, Math.Ceiling(remaining)), 0x04FF); // QS_ALLINPUT
            if (result == 0 || result == 0x00000102) return; // completed or duration elapsed
            if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            while (PeekMessage(out Message message, IntPtr.Zero, 0, 0, 1))
            {
                if (message.Id == 0x0012) return; // WM_QUIT
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero) { _ = DestroyWindow(_window); _window = IntPtr.Zero; }
        if (_font != IntPtr.Zero) { _ = DeleteObject(_font); _font = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint timeout, uint wakeMask);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}
