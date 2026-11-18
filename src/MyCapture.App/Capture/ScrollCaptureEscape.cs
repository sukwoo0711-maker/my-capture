using MyCapture.Platform.Shell;

namespace MyCapture.App.Capture;

/// <summary>
/// Esc cancels a running scroll capture. The scrolled application keeps the foreground, so only Esc is
/// registered, as a hotkey on the resident message window and only while the loop runs: no hook sees the
/// user's other keys. The first press releases it, and so does the end of the capture.
/// </summary>
internal sealed class ScrollCaptureEscape : IDisposable
{
    internal const int HotkeyId = 0x5A11;
    private const int HotkeyMessage = 0x0312;
    private readonly NativeMessageWindow _window;
    private readonly IntPtr _handle;
    private readonly Action _cancel;
    private readonly Action<IntPtr> _unregister;
    private bool _claimed = true;

    private ScrollCaptureEscape(NativeMessageWindow window, Action cancel, Action<IntPtr> unregister)
    {
        _window = window;
        _handle = window.Handle;
        _cancel = cancel;
        _unregister = unregister;
        window.MessageReceived += OnMessage;
    }

    internal bool IsClaimed => _claimed;

    /// <summary>
    /// Claims Esc for <paramref name="cancel"/>, or returns null when another program owns it (the tray
    /// command still cancels). The seams default to <c>RegisterHotKey</c> with MOD_NOREPEAT, as the
    /// selector's own Esc claim; the test assembly never lets the default claim the user's key.
    /// </summary>
    internal static ScrollCaptureEscape? TryClaim(NativeMessageWindow? window, Action cancel,
        Func<IntPtr, bool>? register = null, Action<IntPtr>? unregister = null)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        if (window is null) return null;
        register ??= static handle => !NativeCaptureOverlay.SuppressSystemEscapeHotkey
            && NativeSelectorInterop.RegisterHotKey(handle, HotkeyId, 0x4000, 0x1B);
        if (!register(window.Handle)) return null;
        return new ScrollCaptureEscape(window, cancel,
            unregister ?? (static handle => _ = NativeSelectorInterop.UnregisterHotKey(handle, HotkeyId)));
    }

    private void OnMessage(object? sender, NativeWindowMessageEventArgs e)
    {
        if (!_claimed || e.Message != HotkeyMessage || e.WParam.ToInt64() != HotkeyId) return;
        e.Handled = true;
        // One press cancels; later presses belong to the application again.
        Dispose();
        _cancel();
    }

    public void Dispose()
    {
        if (!_claimed) return;
        _claimed = false;
        _window.MessageReceived -= OnMessage;
        _unregister(_handle);
    }
}
