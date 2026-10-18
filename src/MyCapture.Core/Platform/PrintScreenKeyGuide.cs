using System.Reflection;
using System.Security;
using Microsoft.Win32;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Platform;

/// <summary>
/// Read-only view of the Windows accessibility setting that makes the Print Screen key open
/// the Windows screen capture tool.
/// </summary>
public interface IWindowsPrintScreenSetting
{
    /// <summary>The stored DWORD, or <see langword="null"/> when the user never changed the setting.</summary>
    int? ReadValue();
}

/// <summary>
/// Reads <c>HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled</c>. Never writes:
/// changing how Windows handles a key is the user's decision, made in Windows Settings.
/// </summary>
public sealed class RegistryPrintScreenSetting : IWindowsPrintScreenSetting
{
    public const string KeyPath = @"Control Panel\Keyboard";
    public const string ValueName = "PrintScreenKeyForSnippingEnabled";

    public int? ReadValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is int value ? value : null;
    }
}

/// <summary>
/// Decides when a MyCapture shortcut that uses the Print Screen key competes with Windows,
/// and words the explanation shown in Settings and in shortcut failure notices.
/// </summary>
public static class PrintScreenKeyGuide
{
    /// <summary>Win32 <c>VK_SNAPSHOT</c>.</summary>
    public const uint VkSnapshot = 0x2C;

    /// <summary>Windows keyboard accessibility settings on both Windows 10 and Windows 11.</summary>
    public const string WindowsSettingsUri = "ms-settings:easeofaccess-keyboard";

    /// <summary>
    /// Windows 11 22H2 updates turned the option on for users who had never chosen, so a
    /// missing value means "on" from this build and "off" before it.
    /// </summary>
    public const int DefaultOnBuild = 22621;

    private static readonly PropertyInfo[] HotkeyProperties = typeof(HotkeySettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.PropertyType == typeof(Hotkey) && property.CanRead)
        .ToArray();

    /// <summary>Localized Settings breadcrumb for the specified Windows build.</summary>
    public static string SettingsPath(int osBuild) =>
        UiText.Get(WindowsFeatureSupport.HasAccessibilitySettingsName(osBuild) ? "PrtScGuide_SettingsPath" : "PrtScGuide_SettingsPath_Windows10");

    public static string Message(int? osBuild = null) => FormatGuide("PrtScGuide_Message", osBuild);

    public static string OpenSettingsFailed(int? osBuild = null) => FormatGuide("PrtScGuide_OpenSettingsFailed", osBuild);

    private static string FormatGuide(string key, int? osBuild) =>
        UiText.Format(key, SettingsPath(osBuild ?? Environment.OSVersion.Version.Build));

    /// <summary>PrintScreen alone or with Shift: the chords Windows claims for its capture tool.</summary>
    public static bool UsesPrintScreen(Hotkey? hotkey) =>
        hotkey is { VirtualKey: VkSnapshot } && hotkey.Modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift;

    public static bool IsWindowsCaptureKeyEnabled(int? storedValue, int osBuild) =>
        storedValue is { } value ? value != 0 : osBuild >= DefaultOnBuild;

    /// <summary>An unreadable setting is reported as off, so no guidance is shown on a guess.</summary>
    public static bool IsWindowsCaptureKeyEnabled(IWindowsPrintScreenSetting setting, int osBuild)
    {
        ArgumentNullException.ThrowIfNull(setting);
        try
        {
            return IsWindowsCaptureKeyEnabled(setting.ReadValue(), osBuild);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>The current user's setting on the running Windows build.</summary>
    public static bool IsWindowsCaptureKeyEnabled() =>
        IsWindowsCaptureKeyEnabled(new RegistryPrintScreenSetting(), Environment.OSVersion.Version.Build);

    /// <summary>
    /// Every shortcut in <paramref name="hotkeys"/>, found by type so a command added later is
    /// covered without listing it here.
    /// </summary>
    public static IEnumerable<Hotkey> AllHotkeys(HotkeySettings hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        return HotkeyProperties
            .Select(property => property.GetValue(hotkeys) as Hotkey)
            .Where(hotkey => hotkey is not null)
            .Select(hotkey => hotkey!);
    }

    /// <summary>True when an assigned shortcut uses Print Screen while Windows also claims the key.</summary>
    public static bool ShouldWarn(HotkeySettings hotkeys, bool windowsCaptureKeyEnabled) =>
        windowsCaptureKeyEnabled && AllHotkeys(hotkeys).Any(UsesPrintScreen);

    /// <summary>
    /// The reason and fix to add to a registration failure notice, or an empty string when no
    /// failed shortcut uses Print Screen or Windows does not claim the key.
    /// </summary>
    public static string FailureNotice(IEnumerable<Hotkey> failedHotkeys, bool windowsCaptureKeyEnabled, int? osBuild = null)
    {
        ArgumentNullException.ThrowIfNull(failedHotkeys);
        return windowsCaptureKeyEnabled && failedHotkeys.Any(UsesPrintScreen)
            ? FormatGuide("PrtScGuide_FailureNotice", osBuild)
            : string.Empty;
    }
}
