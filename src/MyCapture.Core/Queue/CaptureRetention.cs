using MyCapture.Core.Settings;

namespace MyCapture.Core.Queue;

/// <summary>
/// Gallery image time-to-live. The shipped default is seven days (168 hours);
/// videos and pinned/leased records are never expired by this policy.
/// </summary>
public static class CaptureRetention
{
    /// <summary>Matches the historical hard-coded <c>AddDays(-7)</c> cutoff.</summary>
    public const int DefaultImageRetentionHours = 7 * 24;

    public static TimeSpan TimeToLive(QueueSettings? queue)
    {
        int hours = queue?.ImageRetentionHours ?? DefaultImageRetentionHours;
        hours = Math.Clamp(hours, SettingsRanges.ImageRetentionHours.Min, SettingsRanges.ImageRetentionHours.Max);
        return TimeSpan.FromHours(hours);
    }

    public static DateTimeOffset ExpiresAt(DateTimeOffset createdAt, TimeSpan ttl) => createdAt + ttl;

    public static TimeSpan Remaining(DateTimeOffset createdAt, TimeSpan ttl, DateTimeOffset now) =>
        ExpiresAt(createdAt, ttl) - now;

    /// <summary>
    /// User-facing countdown. Whole remaining days use <c>d-N</c>; leftover hours
    /// and minutes are appended so the label matches the real TTL unit.
    /// </summary>
    public static string FormatCountdown(
        DateTimeOffset createdAt,
        TimeSpan ttl,
        DateTimeOffset now,
        bool isImage,
        bool isPinned)
    {
        if (!isImage)
        {
            return UiText.Get("Gallery.RetentionVideo");
        }

        if (isPinned)
        {
            return UiText.Get("Gallery.RetentionKept");
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
