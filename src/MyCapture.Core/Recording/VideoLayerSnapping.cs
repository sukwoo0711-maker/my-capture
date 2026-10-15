namespace MyCapture.Core.Recording;

public enum VideoSnapKind { Playhead, KeptStart, KeptEnd, LayerEdge }

public enum VideoLayerDrag { Move, TrimStart, TrimEnd }

public readonly record struct VideoSnapCandidate(double TimeMs, VideoSnapKind Kind);

public readonly record struct VideoLayerSnapResult(double StartMs, double EndMs, VideoSnapCandidate? Snap);

/// <summary>Snaps a constrained layer gesture in source time, using a screen-space tolerance.</summary>
public static class VideoLayerSnapping
{
    public static VideoLayerSnapResult Apply(
        double startMs, double endMs, double deltaMs, double durationMs,
        VideoLayerDrag drag, TimelineViewport viewport, double width,
        IEnumerable<VideoSnapCandidate> candidates, bool disabled = false, double threshold = 8)
    {
        if (!double.IsFinite(startMs) || !double.IsFinite(endMs) || !double.IsFinite(deltaMs)
            || !double.IsFinite(durationMs) || durationMs <= 0 || startMs < 0 || endMs <= startMs || endMs > durationMs)
        {
            throw new ArgumentOutOfRangeException(nameof(startMs));
        }

        double start, end;
        if (drag == VideoLayerDrag.Move)
        {
            double length = endMs - startMs;
            start = Math.Clamp(startMs + deltaMs, 0, durationMs - length);
            end = Math.Min(durationMs, start + length);
        }
        else
        {
            (start, end) = TextLayerTiming.Resize(startMs, endMs, durationMs,
                drag == VideoLayerDrag.TrimStart, (drag == VideoLayerDrag.TrimStart ? startMs : endMs) + deltaMs);
        }

        var result = new VideoLayerSnapResult(start, end, null);
        if (disabled || !double.IsFinite(width) || width <= 0 || !double.IsFinite(threshold) || threshold < 0)
        {
            return result;
        }

        double best = threshold * viewport.VisibleSpanMs / width;
        if (candidates is IReadOnlyList<VideoSnapCandidate> indexed)
        {
            for (int i = 0; i < indexed.Count; i++) TryCandidate(indexed[i]);
        }
        else
        {
            foreach (VideoSnapCandidate candidate in candidates) TryCandidate(candidate);
        }

        return result;

        void TryCandidate(VideoSnapCandidate candidate)
        {
            if (!double.IsFinite(candidate.TimeMs) || candidate.TimeMs < viewport.ViewStartMs || candidate.TimeMs > viewport.ViewEndMs)
            {
                return;
            }

            // Stable ties respect candidate order (playhead, kept boundaries, then other layers).
            TryEdge(start, drag != VideoLayerDrag.TrimEnd);
            TryEdge(end, drag != VideoLayerDrag.TrimStart);

            void TryEdge(double edge, bool enabled)
            {
                double shift = candidate.TimeMs - edge;
                double distance = Math.Abs(shift);
                if (!enabled || distance > best || (result.Snap.HasValue && distance >= best)) return;
                double snappedStart = drag == VideoLayerDrag.TrimEnd ? start : start + shift;
                double snappedEnd = drag == VideoLayerDrag.TrimStart ? end : end + shift;
                if (snappedStart < 0 || snappedEnd > durationMs || snappedEnd - snappedStart < Math.Min(10, durationMs)) return;
                best = distance;
                result = new VideoLayerSnapResult(snappedStart, snappedEnd, candidate);
            }
        }
    }
}
