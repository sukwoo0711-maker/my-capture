using System.Globalization;

namespace MyCapture.App.Gallery;

/// <summary>
/// Turns a capture's creation date into a stable, sortable group key and a
/// Korean-friendly heading ("오늘", "어제", or a localised date).
/// </summary>
/// <remarks>
/// <para>
/// Split out from the view model so the bucketing rule — today, yesterday, then one
/// group per calendar day — can be unit-tested against a fixed "now" without a clock or
/// a UI thread. The gallery groups newest-first, so a group's <see cref="GalleryDateGroup.SortKey"/>
/// is the day itself and callers sort descending.
/// </para>
/// <para>
/// Grouping is by local calendar day rather than by a rolling 24-hour window: a capture
/// taken at 00:10 belongs to "오늘", and one taken at 23:50 the previous evening belongs
/// to "어제", which is what a user scanning a history expects.
/// </para>
/// </remarks>
public static class GalleryDateGrouping
{
    /// <summary>
    /// Resolves the group for <paramref name="createdAt"/> relative to <paramref name="now"/>.
    /// </summary>
    public static GalleryDateGroup Resolve(DateTimeOffset createdAt, DateTimeOffset now)
    {
        DateOnly day = DateOnly.FromDateTime(createdAt.LocalDateTime);
        DateOnly today = DateOnly.FromDateTime(now.LocalDateTime);
        DateOnly yesterday = today.AddDays(-1);

        string heading;
        if (day == today)
        {
            heading = "오늘";
        }
        else if (day == yesterday)
        {
            heading = "어제";
        }
        else if (day.Year == today.Year)
        {
            // Within the current year the year is noise; a month/day heading reads faster.
            heading = day.ToString("M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR"));
        }
        else
        {
            heading = day.ToString("yyyy년 M월 d일", CultureInfo.GetCultureInfo("ko-KR"));
        }

        return new GalleryDateGroup(day, heading);
    }
}

/// <summary>
/// One day's worth of captures in the gallery, identified by its calendar day.
/// </summary>
/// <param name="SortKey">
/// The calendar day. Groups are shown newest-first, so callers order by this descending.
/// </param>
/// <param name="Heading">The Korean-friendly heading shown above the group.</param>
public readonly record struct GalleryDateGroup(DateOnly SortKey, string Heading);
