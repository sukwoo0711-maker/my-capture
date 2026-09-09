using MyCapture.Core.Localization;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>One attempt per resident shell; Explorer recovery and settings changes do not replay it.</summary>
internal sealed class ResidentReadyNotification
{
    private bool _announced;

    internal void Notify(bool hotkeysReady, Hotkey capture, Action<string, string> show)
    {
        if (!hotkeysReady || _announced) return;
        _announced = true;
        show(UiText.Format("ShellRetake_ReadyTitle", AppIdentity.Label),
            capture.IsAssigned ? UiText.Format("ShellRetake_ReadyMessage", capture) :
                UiText.Get("ShellRetake_ReadyWithoutShortcut"));
    }
}
