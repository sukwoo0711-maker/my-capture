using System.Windows.Input;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private bool? _pendingChromeFocus;
    internal const int EscapeHotkeyId = 0x5A10;
    private IntPtr _escapeHotkeyWindow;
    internal Func<IntPtr> ReadForegroundWindow { get; set; } = NativeSelectorInterop.GetForegroundWindow;
    // MOD_NOREPEAT, VK_ESCAPE. Bound to the selector HWND, so WM_HOTKEY arrives at its own window procedure.
    internal Func<IntPtr, bool> RegisterEscapeHotkey { get; set; } =
        hwnd => !SuppressSystemEscapeHotkey && NativeSelectorInterop.RegisterHotKey(hwnd, EscapeHotkeyId, 0x4000, 0x1B);
    /// <summary>Set once by the test assembly: tests on a shared desktop never claim the user's Esc key.</summary>
    internal static bool SuppressSystemEscapeHotkey { get; set; }
    internal Action<IntPtr> UnregisterEscapeHotkey { get; set; } =
        hwnd => _ = NativeSelectorInterop.UnregisterHotKey(hwnd, EscapeHotkeyId);
    internal bool ClaimsEscapeForTest => _escapeHotkeyWindow != IntPtr.Zero;

    /// <summary>
    /// Windows refuses SetForegroundWindow once the user's key-up or pointer input has reached another
    /// process (the recording selector appears 100 ms and a full-desktop grab after its shortcut).
    /// The selector is then on top, yet every key still goes to that application and Esc cannot
    /// cancel it. Until the selector is activated, Esc alone is claimed for this HWND.
    /// </summary>
    private void ClaimEscapeWithoutForeground()
    {
        if (_closed || !IsVisible || Handle == IntPtr.Zero || _escapeHotkeyWindow != IntPtr.Zero) return;
        IntPtr foreground = ReadForegroundWindow();
        if (foreground == Handle || (foreground != IntPtr.Zero && NativeSelectorInterop.GetAncestor(foreground, 3) == Handle)) return;
        if (RegisterEscapeHotkey(Handle)) _escapeHotkeyWindow = Handle;
    }

    private void ReleaseEscapeClaim()
    {
        if (_escapeHotkeyWindow == IntPtr.Zero) return;
        IntPtr hwnd = _escapeHotkeyWindow;
        _escapeHotkeyWindow = IntPtr.Zero;
        UnregisterEscapeHotkey(hwnd);
    }

    private void OnEscapeHotkey()
    {
        ReleaseEscapeClaim();
        // The hotkey is the user's input to this process: activation is allowed again.
        _ = Activate();
        _ = HandleKey(Key.Escape, ModifierKeys.None);
    }

    private void FocusChrome(bool reverse)
    {
        if (_chrome?.FocusFromBackground(reverse) == true) _pendingChromeFocus = null;
        else _pendingChromeFocus = reverse;
    }

    private void ReturnFocusAfterPointer()
    {
        if (_chrome?.KeyboardActivation != true && InputManager.Current.MostRecentInputDevice is MouseDevice) Activate();
    }
}
