using MyCapture.Core.Settings;

namespace MyCapture.Core.Queue;

/// <summary>
/// Gallery image time-to-live. The legacy fallback is seven days (168 hours);
/// new installations use <see cref="NeverHours"/> through <see cref="NewInstallDefaults"/>.
/// Videos and pinned/leased records are never expired by this policy.
/// </summary>
public static class CaptureRetention
{
    /// <summary>Matches the historical hard-coded <c>AddDays(-7)</c> cutoff.</summary>
    public const int DefaultImageRetentionHours = 7 * 24;

    /// <summary>
    /// The stored <see cref="QueueSettings.ImageRetentionHours"/> that turns automatic image
    /// deletion off. A value above the accepted hour range, so a version without this choice
    /// clamps it to the longest period (one year) instead of deleting anything sooner.
    /// </summary>
    public const int NeverHours = int.MaxValue;

    /// <summary>The periods the settings window offers, in hours, ending with "never".</summary>
    public static IReadOnlyList<int> PresetHours { get; } = Array.AsReadOnly([24, 7 * 24, 30 * 24, 90 * 24, 365 * 24, NeverHours]);

    /// <summary>
    /// The time-to-live used while automatic deletion is off: far enough that nothing expires,
    /// near enough that date arithmetic (now − ttl, created + ttl) stays in range.
    /// </summary>
    public static TimeSpan Indefinite { get; } = TimeSpan.FromDays(365 * 1000);

    public static bool IsNever(int hours) => hours == NeverHours;

    public static bool IsIndefinite(TimeSpan ttl) => ttl >= Indefinite;

    /// <summary>A period a settings file may store: an hour count in range, or <see cref="NeverHours"/>.</summary>
    public static bool IsValidHours(int hours) => IsNever(hours) || SettingsRanges.ImageRetentionHours.Contains(hours);

    public static TimeSpan TimeToLive(QueueSettings? queue)
    {
        int hours = queue?.ImageRetentionHours ?? DefaultImageRetentionHours;
        if (IsNever(hours)) return Indefinite;
        hours = Math.Clamp(hours, SettingsRanges.ImageRetentionHours.Min, SettingsRanges.ImageRetentionHours.Max);
        return TimeSpan.FromHours(hours);
    }

    public static DateTimeOffset ExpiresAt(DateTimeOffset createdAt, TimeSpan ttl) => createdAt + ttl;

    public static TimeSpan Remaining(DateTimeOffset createdAt, TimeSpan ttl, DateTimeOffset now) =>
        ExpiresAt(createdAt, ttl) - now;

    /// <summary>
    /// User-facing time until automatic deletion, with explicit units and expiry wording.
    /// </summary>
    public static string FormatCountdown(
        DateTimeOffset createdAt,
        TimeSpan ttl,
        DateTimeOffset now,
        bool isImage,
        bool isPinned,
        bool isInFolder = false)
    {
        if (isInFolder) return UiText.Get("Folder410.RetentionKept");

        if (!isImage)
        {
            return UiText.Get("Gallery.RetentionVideo");
        }

        if (isPinned)
        {
            return UiText.Get("Gallery.RetentionKept");
        }

        if (IsIndefinite(ttl))
        {
            return UiText.Get("Gallery.RetentionNever");
        }

        TimeSpan remaining = Remaining(createdAt, ttl, now);
        if (remaining <= TimeSpan.Zero)
        {
            return UiText.Get("Gallery.RetentionExpired");
        }

        int days = (int)Math.Floor(remaining.TotalDays);
        int hours = remaining.Hours;
        int minutes = remaining.Minutes;

        if (days > 0)
        {
            return hours > 0
                ? UiText.Format("Gallery.RetentionDaysHours", days, hours)
                : UiText.Format("Gallery.RetentionDays", days);
        }

        if (hours > 0)
        {
            return minutes > 0
                ? UiText.Format("Gallery.RetentionHoursMinutes", hours, minutes)
                : UiText.Format("Gallery.RetentionHours", hours);
        }

        return UiText.Format("Gallery.RetentionMinutes", Math.Max(1, minutes));
    }
}
