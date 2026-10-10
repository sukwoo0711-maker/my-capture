using System.Globalization;

namespace MyCapture.App.Recording;

/// <summary>
/// Short, glanceable trim wording shared by the trim timeline, its bar and the status line.
/// </summary>
/// <remarks>
/// The handle chips and the kept length are read at a glance, so they round to a tenth of
/// a second; the precise start/end inputs keep millisecond timecode.
/// </remarks>
internal static class TrimPresentation
{
    /// <summary>Formats a position or length as <c>mm:ss.f</c> (or <c>h:mm:ss.f</c> past an hour).</summary>
    internal static string Clock(double milliseconds)
    {
        double tenths = Math.Round(Math.Max(0, double.IsFinite(milliseconds) ? milliseconds : 0) / 100.0, MidpointRounding.AwayFromZero);
        TimeSpan value = TimeSpan.FromMilliseconds(tenths * 100.0);
        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value:mm\\:ss\\.f}")
            : value.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
    }

    /// <summary>Length that remains after the trim.</summary>
    internal static double KeptMs(double inMs, double outMs) => Math.Max(0, outMs - inMs);

    /// <summary>Length that the trim cuts away from the clip.</summary>
    internal static double RemovedMs(double durationMs, double inMs, double outMs) =>
        Math.Max(0, durationMs - KeptMs(inMs, outMs));

    internal static string StartChip(double inMs) => UiText.Format("VideoEditorUx_TrimStartChip", Clock(inMs));

    internal static string EndChip(double outMs) => UiText.Format("VideoEditorUx_TrimEndChip", Clock(outMs));

    internal static string KeptLength(double inMs, double outMs) =>
        UiText.Format("VideoEditorUx_KeptLength", Clock(KeptMs(inMs, outMs)));

    /// <summary>Secondary line under the kept length: how much is cut, or that nothing is.</summary>
    internal static string RemovedLength(double durationMs, double inMs, double outMs)
    {
        double removed = RemovedMs(durationMs, inMs, outMs);
        return removed < 0.5
            ? UiText.Get("VideoEditorUx_NotTrimmed")
            : UiText.Format("VideoEditorUx_RemovedLength", Clock(removed));
    }

    /// <summary>Screen-reader summary of the trim timeline.</summary>
    internal static string TimelineStatus(double inMs, double outMs) =>
        UiText.Format("VideoEditorUx_TrimTimelineStatus", Clock(inMs), Clock(outMs), Clock(KeptMs(inMs, outMs)));
}
