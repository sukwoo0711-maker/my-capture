using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

/// <summary>
/// A hidden top-level Win32 window that gives background services a stable HWND without
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
    private const string WindowName = "MyCapture.NativeMessageWindow";
    // Native HWNDs do not hold managed references to their WNDPROC. Retain each owner until
    // destruction succeeds, including a caller that abandons it or a failed Dispose attempt.
    private static readonly ConcurrentDictionary<IntPtr, NativeMessageWindow> NativeOwners = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly HwndSource? _source;
    private HwndSource? _renderKeepAlive;
    private readonly NativeMethods.WindowProcedure? _nativeProcedure;
    private readonly string? _nativeClass;
    private readonly IntPtr _instance;
    private IntPtr _nativeHandle;
    private bool _nativeClassRegistered;
    private bool _disposed;

    public NativeMessageWindow() : this(separateRenderKeepAlive: false) { }

    /// <summary>Diagnostic opt-in: a pure Win32 shell HWND and an independently releasable WPF HWND.</summary>
    public NativeMessageWindow(bool separateRenderKeepAlive)
    {
        TaskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        if (TaskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Explorer restart notification could not be registered.");
        }

        if (!separateRenderKeepAlive)
        {
            _source = new HwndSource(Parameters(WindowName));
            _source.AddHook(WindowProc);
            return;
        }

        _instance = NativeMethods.GetModuleHandle(null);
        _nativeClass = "MyCapture.ResidentMessage." + Guid.NewGuid().ToString("N");
        // Keep this delegate rooted until DestroyWindow returns (including its final messages).
        _nativeProcedure = NativeWindowProc;
        var windowClass = new NativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_nativeProcedure),
            Instance = _instance,
            ClassName = _nativeClass,
        };
        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the resident message window.");
        _nativeClassRegistered = true;
        try
        {
            // A hidden TOP-LEVEL window: HWND_MESSAGE would lose TaskbarCreated broadcasts and FindWindow.
            _nativeHandle = NativeMethods.CreateWindowEx(0, _nativeClass, WindowName, 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (_nativeHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the resident message window.");
            NativeOwners[_nativeHandle] = this;
            RestoreRenderKeepAlive();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static HwndSourceParameters Parameters(string name) => new(name)
    {
        ParentWindow = IntPtr.Zero, WindowStyle = 0, ExtendedWindowStyle = 0, Width = 0, Height = 0,
    };

    /// <summary>The native handle used by RegisterHotKey and Shell_NotifyIcon.</summary>
    public IntPtr Handle => _source?.Handle ?? _nativeHandle;

    public bool HasSeparateRenderKeepAlive => _nativeClass is not null;

    public bool IsRenderKeepAlivePresent => _renderKeepAlive is not null;

    /// <summary>The first subscriber failure, captured at the reverse P/Invoke boundary.</summary>
    public Exception? CallbackFailure { get; private set; }

    public void ReleaseRenderKeepAlive()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasSeparateRenderKeepAlive) throw new InvalidOperationException("The shell HWND still owns the render keepalive.");
        _renderKeepAlive?.Dispose();
        _renderKeepAlive = null;
    }

    public void RestoreRenderKeepAlive()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasSeparateRenderKeepAlive) throw new InvalidOperationException("No separate render keepalive was configured.");
        _renderKeepAlive ??= new HwndSource(Parameters("MyCapture.ResidentRenderKeepAlive"));
    }

    private IntPtr NativeWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            bool handled = false;
            IntPtr result = WindowProc(hwnd, unchecked((int)message), wParam, lParam, ref handled);
            if (handled) return result;
        }
        catch (Exception error)
        {
            // Exceptions must never unwind through user32. The resident probe checks this latch.
            CallbackFailure ??= error;
        }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Transfers the launching process's foreground permission to the resident only.</summary>
    public static void AllowResidentForegroundActivation()
    {
        IntPtr window = NativeMethods.FindWindow(null, WindowName);
        if (window != IntPtr.Zero)
        {
            _ = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId != 0) _ = NativeMethods.AllowSetForegroundWindow(processId);
        }
    }

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
        _dispatcher.VerifyAccess();
        if (_disposed && _nativeHandle == IntPtr.Zero && !_nativeClassRegistered)
        {
            return;
        }

        _disposed = true;
        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source.Dispose();
        }
        _renderKeepAlive?.Dispose();
        _renderKeepAlive = null;
        if (_nativeHandle != IntPtr.Zero)
        {
            if (!NativeMethods.DestroyWindow(_nativeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not destroy the resident message window.");
            NativeOwners.TryRemove(_nativeHandle, out _);
            _nativeHandle = IntPtr.Zero;
        }
        if (_nativeClassRegistered)
        {
            if (!NativeMethods.UnregisterClass(_nativeClass!, _instance))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not unregister the resident message window.");
            _nativeClassRegistered = false;
        }
        GC.KeepAlive(_nativeProcedure);
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
