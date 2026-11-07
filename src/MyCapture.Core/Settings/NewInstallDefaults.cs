using MyCapture.Core.Queue;

namespace MyCapture.Core.Settings;

/// <summary>
/// The settings a new installation starts with. Region capture, screen recording, the library and
/// pin commands own global shortcuts, the theme follows Windows, key captions appear while Alt is held, and
/// library images have no time limit within a 1,000-item / 5 GiB capacity budget.
/// </summary>
/// <remarks>
/// <para>
/// The property initialisers on <see cref="AppSettings"/> keep the values earlier versions
/// shipped with. They are what a settings file written before a property existed reads back, so
/// an existing user keeps every chord (F3, Shift+F3, F9, Ctrl+Shift+Z). The settings store
/// separately migrates the former hidden-caption default.
/// </para>
/// <para>
/// Only a launch with no settings file at all, and the "reset to defaults" command, use these
/// values. The four core shortcuts take priority over other programs, and the first launch
/// explains the assignments; a reset keeps the user's own answer about that priority instead.
/// Ctrl+Shift+Z opens the library, F3 pins content, Shift+F3 hides or
/// shows the pins, and the upload shortcut stays unassigned.
/// Shortcut keys stay visible in every button tooltip, double-click does not close a new pin,
/// and pins left open at exit reopen at the next launch.
/// </para>
/// </remarks>
public static class NewInstallDefaults
{
    /// <summary>Creates the settings a launch without a settings file starts from.</summary>
    public static AppSettings Create()
    {
        var settings = new AppSettings();
        Apply(settings);
        return settings;
    }

    /// <summary>Overwrites the values that differ from the legacy property defaults.</summary>
    public static void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Hotkeys.PasteToScreen = new Hotkey(HotkeyModifiers.None, Hotkey.VkF3);
        settings.Hotkeys.HideAllPins = new Hotkey(HotkeyModifiers.Shift, Hotkey.VkF3);
        settings.Hotkeys.UploadGitHubImage = Hotkey.None;
        settings.Pin.CloseOnDoubleClick = true;
        settings.Pin.RestoreOpenPinsOnLaunch = true;
        settings.General.AfterCapture = AfterCaptureAction.QuickAccess;
        settings.General.Theme = MyCapture.Core.Themes.AppThemeNames.ToSetting(MyCapture.Core.Themes.AppTheme.System);
        settings.General.ShowShortcutHints = false;
        settings.General.AlwaysShowShortcutHints = false;
        settings.General.ShortcutHintsRevision = 1;
        settings.Queue.ImageRetentionHours = CaptureRetention.NeverHours;
        settings.Queue.MaxBytes = 5L * 1024 * 1024 * 1024;
        settings.Queue.MaxItems = 1000;
        CoreShortcuts.Accept(settings.Hotkeys);
    }
}
