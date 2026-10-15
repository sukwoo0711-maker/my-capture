using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Short, glanceable trim wording shared by the trim timeline, its bar and the status line.
/// </summary>
/// <remarks>
/// Every time uses the editor's one format (<see cref="VideoTimecode"/>), so a chip, the kept
/// length and the start/end inputs show the same value the same way.
/// </remarks>
internal static class TrimPresentation
{
    /// <summary>Formats a position or length as <c>m:ss.fff</c> (or <c>h:mm:ss.fff</c> from an hour).</summary>
    internal static string Clock(double milliseconds) => VideoTimecode.Format(milliseconds);

    /// <summary>Length that remains after the trim.</summary>
    internal static double KeptMs(double inMs, double outMs) => Math.Max(0, outMs - inMs);

    /// <summary>Length that the trim cuts away from the clip.</summary>
    internal static double RemovedMs(double durationMs, double inMs, double outMs) =>
        Math.Max(0, durationMs - KeptMs(inMs, outMs));

    internal static string StartChip(double inMs) => UiText.Format("VideoEditorUx_TrimStartChip", Clock(inMs));

    internal static string EndChip(double outMs) => UiText.Format("VideoEditorUx_TrimEndChip", Clock(outMs));

    internal static string KeptLength(double inMs, double outMs) =>
        UiText.Format("VideoEditorUx_KeptLength", Clock(KeptMs(inMs, outMs)));

    internal static string KeptLength(VideoCutTimeline timeline) =>
        UiText.Format("VideoEditorUx_KeptLength", Clock(timeline.OutputDurationMs));

    internal static string RemovedLength(double durationMs, VideoCutTimeline timeline) =>
        timeline.RemovedRanges.Count == 0
            ? RemovedLength(durationMs, timeline.StartMs, timeline.EndMs)
            : UiText.Format("VideoMulticut_RemovedCount", timeline.RemovedRanges.Count,
                Clock(Math.Max(0, durationMs - timeline.OutputDurationMs)));

    internal static string SegmentLabel(VideoTimeRange range) =>
        UiText.Format("VideoMulticut_SegmentRange", Clock(range.StartMs), Clock(range.EndMs));

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
