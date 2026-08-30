using System.Windows.Interop;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

/// <summary>
/// A message-only Win32 window that gives background services a stable HWND without
/// ever putting a window in Alt+Tab or on the taskbar.
/// </summary>
/// <remarks>
/// Construct and dispose this type on the WPF dispatcher thread. Explorer restart
/// notifications, global hotkeys, and notification-area callbacks all arrive through
/// this one message pump, which prevents competing hidden windows from drifting into
/// subtly different lifecycle rules.
/// </remarks>
public sealed class NativeMessageWindow : IDisposable
{
    private readonly HwndSource _source;
    private bool _disposed;

    public NativeMessageWindow()
    {
        var parameters = new HwndSourceParameters("MyCapture.NativeMessageWindow")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WindowProc);

        TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        if (TaskbarCreatedMessage == 0)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "Explorer restart notification could not be registered.");
        }
    }

    /// <summary>The native handle used by RegisterHotKey and Shell_NotifyIcon.</summary>
    public IntPtr Handle => _source.Handle;

    /// <summary>The process-wide registered message Explorer broadcasts after restart.</summary>
    public uint TaskbarCreatedMessage { get; }

    public event EventHandler<NativeWindowMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Posts a message through the real Windows queue. Primarily useful for startup
    /// diagnostics and cross-thread activation signals.
    /// </summary>
    public bool Post(uint message, IntPtr wParam, IntPtr lParam)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.PostMessage(Handle, message, wParam, lParam);
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        var args = new NativeWindowMessageEventArgs(unchecked((uint)message), wParam, lParam);
        MessageReceived?.Invoke(this, args);
        handled = args.Handled;
        return args.Result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }
}

/// <summary>Mutable event data for a native window message.</summary>
public sealed class NativeWindowMessageEventArgs : EventArgs
{
    internal NativeWindowMessageEventArgs(uint message, IntPtr wParam, IntPtr lParam)
    {
        Message = message;
        WParam = wParam;
        LParam = lParam;
    }

    public uint Message { get; }

    public IntPtr WParam { get; }

    public IntPtr LParam { get; }

    public bool Handled { get; set; }

    public IntPtr Result { get; set; }
}
