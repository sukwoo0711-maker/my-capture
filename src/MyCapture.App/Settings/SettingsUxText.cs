using System.Globalization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

/// <summary>One entry of the automatic-deletion list: the stored hour count and its label.</summary>
internal sealed record RetentionChoice(string Hours, string Label);

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
    /// The retention description for the chosen hours: the period that applies, or that images
    /// are not deleted after a set time. An entry the draft would reject shows the accepted range.
    /// </summary>
    internal static string RetentionSummary(string? hoursText)
    {
        if (!TryParseHours(hoursText, out int hours) || !CaptureRetention.IsValidHours(hours))
            return UiText.Get("SettingsUx_RetentionRange");
        return CaptureRetention.IsNever(hours)
            ? UiText.Get("Retention_SummaryNever")
            : UiText.Format("SettingsUx_RetentionSummary", Duration(hours));
    }

    /// <summary>
    /// The automatic-deletion choices: 1, 7, 30 and 90 days, one year and never. An hour count
    /// that is none of them (an earlier version let users type any) is offered first as a custom
    /// choice while it is the current or the saved value, so opening the window never changes it.
    /// </summary>
    internal static IReadOnlyList<RetentionChoice> RetentionChoices(string? current, string? saved = null)
    {
        var choices = new List<RetentionChoice>(8);
        foreach (string? value in new[] { saved, current })
        {
            if (!TryParseHours(value, out int hours) || !SettingsRanges.ImageRetentionHours.Contains(hours)
                || CaptureRetention.PresetHours.Contains(hours)) continue;
            string key = Canonical(hours);
            if (choices.Any(choice => choice.Hours == key)) continue;
            choices.Add(new RetentionChoice(key, UiText.Format("Retention_Custom", hours)));
        }

        foreach (int hours in CaptureRetention.PresetHours)
            choices.Add(new RetentionChoice(Canonical(hours), PresetLabel(hours)));
        return choices;
    }

    /// <summary>The list value matching <paramref name="hoursText"/>, or the text itself when it does not parse.</summary>
    internal static string CanonicalHours(string? hoursText) =>
        TryParseHours(hoursText, out int hours) ? Canonical(hours) : hoursText ?? string.Empty;

    private static string PresetLabel(int hours) => hours switch
    {
        24 => UiText.Get("Retention_Day1"),
        168 => UiText.Get("Retention_Day7"),
        720 => UiText.Get("Retention_Day30"),
        2160 => UiText.Get("Retention_Day90"),
        8760 => UiText.Get("Retention_Year1"),
        CaptureRetention.NeverHours => UiText.Get("Retention_Never"),
        _ => throw new ArgumentOutOfRangeException(nameof(hours)),
    };

    private static string Canonical(int hours) => hours.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseHours(string? text, out int hours) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hours);
}
