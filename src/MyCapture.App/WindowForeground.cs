using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MyCapture.App;

/// <summary>Brings a window, and the dialog it is waiting on, to the front.</summary>
/// <remarks>
/// Windows lets the app take the foreground right after the user's own key or click (a global
/// shortcut, a tray command). A dialog shown without being activated opens behind whatever app
/// is in front, so the commands that answer the user bring their window forward explicitly.
/// </remarks>
internal static class WindowForeground
{
    /// <summary>
    /// Restores <paramref name="window"/> when it is minimized, then activates the dialog it is
    /// waiting on (one of its own dialogs, a native file picker over it, or the app's modal dialog
    /// that keeps it disabled), or the window itself.
    /// </summary>
    internal static void BringForward(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        if (IsEnabled(window))
        {
            _ = window.Activate();
            return;
        }

        if ((WaitingDialog(window) ?? ModalDialog(Application.Current?.Windows.OfType<Window>() ?? [])) is { } dialog)
        {
            if (dialog.WindowState == WindowState.Minimized) dialog.WindowState = WindowState.Normal;
            _ = dialog.Activate();
            return;
        }

        // A native dialog, such as a file picker, is not a WPF window; Windows tracks it as the popup.
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr popup = GetLastActivePopup(handle);
        if (popup != IntPtr.Zero && popup != handle && IsWindowVisible(popup) && IsWindowEnabled(popup))
            _ = SetForegroundWindow(popup);
        else
            _ = window.Activate();
    }

    /// <summary>
    /// The modal dialog this window is waiting on: followed down its owned windows to the one that
    /// still takes input. A window it merely owns, such as a result panel, is not waited on.
    /// </summary>
    internal static Window? WaitingDialog(Window window)
    {
        for (Window current = window; !IsEnabled(current);)
        {
            Window[] shown = current.OwnedWindows.OfType<Window>().Where(owned => owned.IsVisible).ToArray();
            if (shown.LastOrDefault(IsEnabled) is { } dialog) return dialog;
            if (shown.LastOrDefault() is not { } disabled) return null;
            current = disabled;
        }

        return null;
    }

    /// <summary>The newest visible dialog that keeps its owner disabled, the way a modal dialog does.</summary>
    internal static Window? ModalDialog(IEnumerable<Window> windows) =>
        windows.LastOrDefault(window => window.IsVisible && window.Owner is { } owner && IsEnabled(window) && !IsEnabled(owner));

    /// <summary>Whether the window takes input; a modal dialog disables the other windows of its thread.</summary>
    internal static bool IsEnabled(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero ? window.IsEnabled : IsWindowEnabled(handle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
