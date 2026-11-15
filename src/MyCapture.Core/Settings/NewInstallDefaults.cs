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
/// shows the pins, Ctrl+Shift+F3 reopens a closed pin, and the upload shortcut stays unassigned.
/// Shortcut keys stay visible in every button tooltip, a double-click closes a pin,
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
        settings.Hotkeys.RestoreClosedPin = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkF3);
        settings.Hotkeys.UploadGitHubImage = Hotkey.None;
        settings.Pin.CloseOnDoubleClick = true;
        settings.Pin.RestoreOpenPinsOnLaunch = true;
        settings.Pin.RestoreOpenPinsRevision = 1;
        settings.General.AfterCapture = AfterCaptureAction.QuickAccess;
        settings.General.Theme = MyCapture.Core.Themes.AppThemeNames.ToSetting(MyCapture.Core.Themes.AppTheme.System);
        settings.General.ShowShortcutHints = false;
        settings.General.AlwaysShowShortcutHints = false;
        settings.General.ShortcutHintsRevision = 1;
        ApplyLibraryRetention(settings.Queue);
        CoreShortcuts.Accept(settings.Hotkeys);
    }

    /// <summary>
    /// The library limits of a new installation: images never expire, 1,000 items, 5 GiB. A settings file that
    /// exists but cannot be read also takes these, so a damaged file never turns on the earlier seven-day expiry
    /// and sends captures to the Recycle Bin.
    /// </summary>
    public static void ApplyLibraryRetention(QueueSettings queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        queue.ImageRetentionHours = CaptureRetention.NeverHours;
        queue.MaxBytes = 5L * 1024 * 1024 * 1024;
        queue.MaxItems = 1000;
    }
}
