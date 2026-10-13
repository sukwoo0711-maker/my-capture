using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>
/// The "MyCapture is ready" notification, naming the capture key. It appears after the first-run
/// welcome and when the capture key changes, never at an ordinary sign-in.
/// </summary>
internal sealed class ResidentReadyNotification
{
    private bool _announced;

    /// <summary>After the first-run welcome: one attempt per resident shell; Explorer recovery does not replay it.</summary>
    internal void Notify(bool hotkeysReady, Hotkey capture, Action<string, string> show)
    {
        if (!hotkeysReady || _announced) return;
        _announced = true;
        Show(capture, show);
    }

    /// <summary>After applied settings change the capture key, so the notification names the new one.</summary>
    internal bool NotifyCaptureKeyChanged(Hotkey previous, Hotkey current, Action<string, string> show)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous == current) return false;
        Show(current, show);
        return true;
    }

    private static void Show(Hotkey capture, Action<string, string> show) =>
        show(UiText.Format("ShellRetake_ReadyTitle", AppIdentity.Label),
            capture.IsAssigned ? UiText.Format("ShellRetake_ReadyMessage", capture) :
                UiText.Get("ShellRetake_ReadyWithoutShortcut"));
}
