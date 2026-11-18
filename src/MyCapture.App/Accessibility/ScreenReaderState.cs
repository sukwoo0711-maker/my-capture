using System.Runtime.InteropServices;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Accessibility;

/// <summary>
/// Whether a screen reader runs, from the system flag that Narrator, NVDA and JAWS set while they run
/// (SPI_GETSCREENREADER). It is a flag assistive programs set voluntarily, not a detection: a reader that
/// does not set it is missed, and a crashed one may leave it set, which keeps lightweight pins on the safe
/// WPF side. UI Automation listeners are deliberately not consulted, because any automation helper is one.
/// The flag is read at start and again whenever the app's message window gets WM_SETTINGCHANGE for it.
/// </summary>
internal sealed class ScreenReaderState : IDisposable
{
    internal const uint GetScreenReader = 0x0046;
    internal const uint SetScreenReader = 0x0047;
    internal const uint SettingChangeMessage = 0x001A;

    private readonly Func<bool> _reader;
    private NativeMessageWindow? _window;

    /// <param name="window">The app's message window; null reads the flag once and never changes.</param>
    /// <param name="reader">Replaces the system call, for tests.</param>
    internal ScreenReaderState(NativeMessageWindow? window = null, Func<bool>? reader = null)
    {
        _reader = reader ?? ReadSystemFlag;
        Active = ReadSafely();
        if (window is null) return;
        _window = window;
        window.MessageReceived += OnMessage;
    }

    /// <summary>The flag as of the last read.</summary>
    internal bool Active { get; private set; }

    /// <summary>Raised on the window's thread after the flag changed, with the new value.</summary>
    internal event Action<bool>? Changed;

    /// <summary>Reads the system flag. A failed call counts as no screen reader.</summary>
    internal static bool ReadSystemFlag() => SystemParametersInfo(GetScreenReader, 0, out int on, 0) && on != 0;

    /// <summary>Whether a window message is the notice that the screen reader flag changed.</summary>
    internal static bool IsScreenReaderNotice(uint message, IntPtr wParam) =>
        message == SettingChangeMessage && unchecked((uint)wParam.ToInt64()) == SetScreenReader;

    /// <summary>Reads the flag again; raises <see cref="Changed"/> when it differs.</summary>
    internal void Refresh()
    {
        bool now = ReadSafely();
        if (now == Active) return;
        Active = now;
        Changed?.Invoke(now);
    }

    private bool ReadSafely()
    {
        try { return _reader(); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    // Never marks the message handled: the other users of the window still see it.
    private void OnMessage(object? sender, NativeWindowMessageEventArgs e)
    {
        if (IsScreenReaderNotice(e.Message, e.WParam)) Refresh();
    }

    public void Dispose()
    {
        if (_window is { } window) window.MessageReceived -= OnMessage;
        _window = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
}
