using System.Runtime.InteropServices;
using MyCapture.Core.Settings;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Shell;

/// <summary>
/// Claims consented hotkeys with a low-level hook on a dedicated message-pump thread.
/// </summary>
public sealed class LowLevelKeyboardHotkeyOverride : IHotkeyOverride
{
    private static readonly TimeSpan ThreadTimeout = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly IntPtr _window;
    private LowLevelHotkeyClaim[] _claims = [];
    private HookThread? _worker;
    private bool _disposed;

    /// <summary>Creates an override that posts commands to the existing shell window.</summary>
    public LowLevelKeyboardHotkeyOverride(NativeMessageWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window.Handle;
    }

    /// <inheritdoc />
    public bool TryClaim(int id, Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        if (!hotkey.IsAssigned || !hotkey.IsSafeGlobalShortcut ||
            (hotkey.Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0)
        {
            return false;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LowLevelHotkeyClaim[] previous = Volatile.Read(ref _claims);
            if (previous.Any(claim => claim.Id != id && claim.Hotkey == hotkey))
            {
                return false;
            }

            if (_worker is not null && !_worker.Thread.IsAlive)
            {
                _worker = null;
            }

            if (_worker is not null && _worker.IsStopping)
            {
                return false;
            }

            if (_worker is null)
            {
                _worker = new HookThread(this);
                _worker.Thread.Start();
                if (!_worker.Ready.Task.Wait(ThreadTimeout) || !_worker.Ready.Task.Result)
                {
                    StopWorker();
                    return false;
                }
            }

            LowLevelHotkeyClaim[] next = [.. previous.Where(claim => claim.Id != id), new(id, hotkey)];
            Volatile.Write(ref _claims, next);
            return true;
        }
    }

    /// <inheritdoc />
    public void Release(int id)
    {
        lock (_gate)
        {
            LowLevelHotkeyClaim[] next = [.. Volatile.Read(ref _claims).Where(claim => claim.Id != id)];
            Volatile.Write(ref _claims, next);
            if (next.Length == 0)
            {
                StopWorker();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _claims, []);
            StopWorker();
        }
    }

    private void StopWorker()
    {
        if (_worker is null) return;
        _worker.Stop();
        if (_worker.Thread.Join(ThreadTimeout))
        {
            _worker = null;
        }
    }

    private sealed class HookThread
    {
        private readonly LowLevelKeyboardHotkeyOverride _owner;
        private readonly NativeMethods.LowLevelKeyboardProc _callback;
        private readonly HashSet<uint> _heldKeys = [];
        private readonly HashSet<uint> _swallowedKeys = [];
        private IntPtr _hook;
        private uint _threadId;
        private int _stopRequested;

        internal HookThread(LowLevelKeyboardHotkeyOverride owner)
        {
            _owner = owner;
            _callback = OnKeyboard;
            Thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MyCapture.HotkeyOverride",
            };
        }

        internal Thread Thread { get; }
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsStopping => Volatile.Read(ref _stopRequested) != 0;

        internal void Stop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            Unhook();
            uint threadId = Volatile.Read(ref _threadId);
            if (threadId != 0)
            {
                _ = NativeMethods.PostThreadMessage(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private void Run()
        {
            try
            {
                // Establish the message queue before making its thread ID visible to Stop.
                _ = NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                Volatile.Write(ref _threadId, NativeMethods.GetCurrentThreadId());
                if (IsStopping) return;

                IntPtr hook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WH_KEYBOARD_LL,
                    Marshal.GetFunctionPointerForDelegate(_callback),
                    NativeMethods.GetModuleHandle(null),
                    0);
                Interlocked.Exchange(ref _hook, hook);
                if (hook == IntPtr.Zero || IsStopping) return;

                Ready.TrySetResult(true);
                while (!IsStopping && NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
                {
                    // Low-level hook callbacks are dispatched by GetMessage itself.
                }
            }
            finally
            {
                Unhook();
                Ready.TrySetResult(false);
                GC.KeepAlive(_callback);
            }
        }

        private void Unhook()
        {
            IntPtr hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
            if (hook != IntPtr.Zero)
            {
                _ = NativeMethods.UnhookWindowsHookEx(hook);
            }
        }

        private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                uint message = unchecked((uint)wParam.ToInt64());
                uint virtualKey = unchecked((uint)Marshal.ReadInt32(lParam));
                HotkeyModifiers modifiers = CurrentModifiers();
                LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
                    virtualKey, message, modifiers, Volatile.Read(ref _owner._claims), _heldKeys, _swallowedKeys);
                if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    _heldKeys.Add(virtualKey);
                }
                else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    _heldKeys.Remove(virtualKey);
                }

                if (match.Swallow)
                {
                    if (match.ReleaseKey)
                    {
                        _swallowedKeys.Remove(virtualKey);
                    }
                    else
                    {
                        _swallowedKeys.Add(virtualKey);
                    }

                    if (match.CommandId is int id)
                    {
                        _ = NativeMethods.PostMessage(
                            _owner._window, NativeMethods.WM_HOTKEY, new IntPtr(id),
                            new IntPtr((long)(virtualKey << 16) | (uint)modifiers));
                    }

                    return new IntPtr(1);
                }
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static HotkeyModifiers CurrentModifiers()
        {
            HotkeyModifiers modifiers = HotkeyModifiers.None;
            if (IsDown(NativeMethods.VK_CONTROL)) modifiers |= HotkeyModifiers.Control;
            if (IsDown(NativeMethods.VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
            if (IsDown(NativeMethods.VK_MENU)) modifiers |= HotkeyModifiers.Alt;
            if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN)) modifiers |= HotkeyModifiers.Windows;
            return modifiers;
        }

        private static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
