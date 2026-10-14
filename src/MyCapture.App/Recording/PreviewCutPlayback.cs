using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal enum PreviewCutAction { Present, Seek, Wait, Stop, TimedOut }

internal readonly record struct PreviewCutDecision(PreviewCutAction Action, double SourceTimeMs);

/// <summary>
/// A forward-only gate for MediaElement's best-effort cut seeks. A stale decoder position
/// cannot issue the same seek repeatedly; an unobserved landing pauses after one second.
/// </summary>
internal sealed class PreviewCutPlayback
{
    internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(1);
    private double? _targetMs;
    private TimeSpan _requestedAt;

    internal void Reset() => _targetMs = null;

    internal static double LastKeptFrame(VideoCutTimeline timeline, int fps)
    {
        VideoTimeRange last = timeline.KeptRanges[^1];
        // The range end is exclusive. At a source-frame boundary use the frame before it;
        // at a fractional end use the frame that still intersects the kept range.
        double frameMs = FrameStepCalculator.FrameDurationMs(Math.Max(1, fps));
        double target = Math.Max(0, Math.Ceiling(last.EndMs / frameMs - 1e-9) - 1) * frameMs;
        return Math.Max(last.StartMs, target);
    }

    internal PreviewCutDecision Observe(VideoCutTimeline timeline, double positionMs, TimeSpan now)
    {
        if (_targetMs is { } target)
        {
            if (positionMs < target - MediaElementPreviewEngine.PresentationToleranceMs)
            {
                return new(now - _requestedAt >= SettleTimeout ? PreviewCutAction.TimedOut : PreviewCutAction.Wait, target);
            }
            // Keep the last landing as a floor until playback is explicitly reset. A late
            // stale report after an apparently completed seek must not reopen the same cut.
            positionMs = Math.Max(target, positionMs);
        }

        double end = timeline.ToSourceTime(timeline.OutputDurationMs);
        if (positionMs >= end - 0.5) return new(PreviewCutAction.Stop, end);
        if (positionMs < timeline.StartMs || timeline.IsRemoved(positionMs))
        {
            double next = timeline.ToSourceTime(timeline.ToOutputTime(positionMs));
            _targetMs = next;
            _requestedAt = now;
            return new(PreviewCutAction.Seek, next);
        }
        return new(PreviewCutAction.Present, positionMs);
    }
}
