using MyCapture.App;
using MyCapture.Core.Settings;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// The tray menu exactly as the running app builds it on every open (App wires every command,
/// including Pin clipboard and Hide/show all pins), with a new installation's shortcuts. Scenes
/// pass only the commands they act on; the clipboard is never read, so Pin clipboard shows as
/// enabled, which is the menu's default state.
/// </summary>
internal static class TrayMenu
{
    /// <summary>The shortcuts a new installation starts with (optional pin and library keys unassigned).</summary>
    internal static HotkeySettings Keys => NewInstallDefaults.Create().Hotkeys;

    internal static IReadOnlyList<ThemedShellPresenter.MenuEntry> Entries(TrayMenuState? state = null, Action? library = null,
        Action? restoreClosedPin = null)
    {
        static void None() { }
        var commands = new TrayMenuCommands(None, None, None, None, None, None, None, None, restoreClosedPin ?? None, library ?? None,
            None, None, None, PinClipboard: None, HideOrShowAllPins: None);
        return TrayMenuLayout.Build(commands, Keys, state ?? new TrayMenuState());
    }
}
