using MyCapture.Core.Queue;

namespace MyCapture.Core.Settings;

/// <summary>
/// The settings a new installation starts with: only region capture and screen recording own a
/// global shortcut, the small key captions under buttons are visible, and library images have
/// no time limit within a 1,000-item / 5 GiB capacity budget.
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
/// values. Bare F3 is File Explorer's search key and Ctrl+Shift+Z is redo in many editors, so a
/// new user should assign the optional commands deliberately rather than lose those keys
/// elsewhere. Shortcut keys stay visible in every button tooltip.
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
        settings.Hotkeys.OpenLibrary = Hotkey.None;
        settings.Hotkeys.PasteToScreen = Hotkey.None;
        settings.Hotkeys.HideAllPins = Hotkey.None;
        settings.Hotkeys.UploadGitHubImage = Hotkey.None;
        settings.General.ShowShortcutHints = true;
        settings.General.ShortcutHintsRevision = 1;
        settings.Queue.ImageRetentionHours = CaptureRetention.NeverHours;
        settings.Queue.MaxBytes = 5L * 1024 * 1024 * 1024;
        settings.Queue.MaxItems = 1000;
    }
}
