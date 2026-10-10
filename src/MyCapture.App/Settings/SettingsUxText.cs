using System.Globalization;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

/// <summary>Settings window text built from the values being edited.</summary>
internal static class SettingsUxText
{
    /// <summary>
    /// A retention period in whole days where it divides evenly, otherwise days and hours:
    /// 168 → "7일", 36 → "1일 12시간", 5 → "5시간".
    /// </summary>
    internal static string Duration(int hours)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hours);
        int days = hours / 24;
        int rest = hours % 24;
        string Days() => UiText.Format(days == 1 ? "SettingsUx_DurationDay" : "SettingsUx_DurationDays", days);
        string Hours() => UiText.Format(rest == 1 ? "SettingsUx_DurationHour" : "SettingsUx_DurationHours", rest);
        if (days == 0) return Hours();
        return rest == 0 ? Days() : UiText.Format("SettingsUx_DurationDaysHours", Days(), Hours());
    }

    /// <summary>
    /// The retention description for the typed hours. An entry the draft would reject shows
    /// the accepted range instead, next to the field's own validation message.
    /// </summary>
    internal static string RetentionSummary(string? hoursText) =>
        int.TryParse(hoursText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
            && SettingsRanges.ImageRetentionHours.Contains(hours)
            ? UiText.Format("SettingsUx_RetentionSummary", Duration(hours))
            : UiText.Get("SettingsUx_RetentionRange");
}
