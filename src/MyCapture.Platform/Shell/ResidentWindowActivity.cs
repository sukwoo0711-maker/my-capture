using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Collections.Concurrent;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

/// <summary>Probe-only observation of native visibility and the current Windows session's input age.</summary>
public sealed class ResidentWindowActivity : IDisposable
{
    private static readonly ConcurrentDictionary<IntPtr, ResidentWindowActivity> ActiveHooks = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _procedure;
    private readonly Action _beforeShow;
    private IntPtr _hook;
    private bool _insideCallback;
    private bool _disabled;

    public ResidentWindowActivity(Action beforeShow)
    {
        _beforeShow = beforeShow;
        _procedure = ObserveMessage;
        // Thread-local WH_CALLWNDPROC, not a global input hook. It sees new windows, hidden-window
        // re-shows, and native popup windows before their WndProc applies visibility.
        _hook = NativeMethods.SetWindowsHookEx(4, Marshal.GetFunctionPointerForDelegate(_procedure),
            IntPtr.Zero, NativeMethods.GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe resident window presentation.");
        ActiveHooks[_hook] = this;
    }

    public Exception? CallbackFailure { get; private set; }

    public static TimeSpan InputIdleTime()
    {
        var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        // Failure is conservatively active. Subtraction intentionally handles the 32-bit tick wrap.
        return NativeMethods.GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Tick)) : TimeSpan.Zero;
    }

    public static bool HasVisibleProcessWindow()
    {
        bool visible = false;
        uint currentProcess = (uint)Environment.ProcessId;
        bool completed = NativeMethods.EnumWindows((hwnd, unused) =>
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == currentProcess && NativeMethods.IsWindowVisible(hwnd)) visible = true;
            return true;
        }, IntPtr.Zero);
        return !completed || visible;
    }

    private IntPtr ObserveMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disabled)
        {
            bool entered = false;
            try
            {
                var message = Marshal.PtrToStructure<NativeMethods.CallWindowProcedureMessage>(lParam);
                bool showing = message.Message == 0x0018 && message.WParam != IntPtr.Zero; // WM_SHOWWINDOW
                if (message.Message == 0x0046 && message.LParam != IntPtr.Zero) // WM_WINDOWPOSCHANGING
                    showing |= (Marshal.PtrToStructure<NativeMethods.WindowPosition>(message.LParam).Flags & 0x0040) != 0;
                if (showing)
                {
                    if (_insideCallback)
                        throw new InvalidOperationException("A nested window was shown during resident render restoration.");
                    _insideCallback = true;
                    entered = true;
                    _beforeShow();
                }
            }
            catch (Exception error)
            {
                // No managed exception may escape a reverse P/Invoke. Fail the probe explicitly.
                CallbackFailure ??= error;
            }
            finally { if (entered) _insideCallback = false; }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_hook == IntPtr.Zero) return;
        _disabled = true;
        if (!NativeMethods.UnhookWindowsHookEx(_hook))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop resident window observation.");
        ActiveHooks.TryRemove(_hook, out _);
        _hook = IntPtr.Zero;
        GC.KeepAlive(_procedure);
    }
}
