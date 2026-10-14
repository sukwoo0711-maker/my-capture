namespace MyCapture.Core.Recording;

/// <summary>
/// Normalized source-time cuts and split points. Output time closes the removed ranges;
/// source-time positions and layer intervals never move.
/// </summary>
public sealed class VideoCutTimeline
{
    private const double MinimumSpanToleranceMs = 0.0000001;

    private VideoCutTimeline(
        double startMs,
        double endMs,
        List<VideoTimeRange> removedRanges,
        List<VideoTimeRange> keptRanges,
        List<double> splitPoints,
        List<VideoTimeRange> segments)
    {
        StartMs = startMs;
        EndMs = endMs;
        RemovedRanges = removedRanges.AsReadOnly();
        KeptRanges = keptRanges.AsReadOnly();
        SplitPoints = splitPoints.AsReadOnly();
        Segments = segments.AsReadOnly();
        OutputDurationMs = keptRanges.Sum(range => range.DurationMs);
    }

    public double StartMs { get; }

    public double EndMs { get; }

    public IReadOnlyList<VideoTimeRange> RemovedRanges { get; }

    public IReadOnlyList<VideoTimeRange> KeptRanges { get; }

    public IReadOnlyList<double> SplitPoints { get; }

    public IReadOnlyList<VideoTimeRange> Segments { get; }

    public double OutputDurationMs { get; }

    public static VideoCutTimeline Create(
        double startMs,
        double endMs,
        IEnumerable<VideoTimeRange>? removedRanges = null,
        IEnumerable<double>? splitPoints = null)
    {
        double start = double.IsFinite(startMs) ? Math.Max(0, startMs) : 0;
        double end = double.IsFinite(endMs) ? Math.Max(start, endMs) : start;
        double minimum = Math.Min(TrimSelection.MinimumSpanMs, end - start);
        List<VideoTimeRange> removed = NormalizeRemovedRanges(start, end, minimum, removedRanges);
        List<VideoTimeRange> kept = GetKeptRanges(start, end, removed);
        List<double> splits = NormalizeSplitPoints(start, end, minimum, kept, removed, splitPoints);
        List<VideoTimeRange> segments = [];
        foreach (VideoTimeRange range in kept)
        {
            double segmentStart = range.StartMs;
            foreach (double split in splits)
            {
                if (split > segmentStart && split < range.EndMs)
                {
                    segments.Add(new(segmentStart, split));
                    segmentStart = split;
                }
            }

            segments.Add(new(segmentStart, range.EndMs));
        }

        return new(start, end, removed, kept, splits, segments);
    }

    /// <summary>
    /// A seam belongs to the following kept range. The output end maps to the last kept end.
    /// </summary>
    public double ToSourceTime(double outputMs)
    {
        if (KeptRanges.Count == 0)
        {
            return StartMs;
        }

        double output = ClampTime(outputMs, 0, OutputDurationMs);
        double offset = 0;
        foreach (VideoTimeRange range in KeptRanges)
        {
            double outputEnd = offset + range.DurationMs;
            if (output < outputEnd)
            {
                return range.StartMs + (output - offset);
            }

            offset = outputEnd;
        }

        return KeptRanges[^1].EndMs;
    }

    /// <summary>Every source position in a removed range maps to that range's output seam.</summary>
    public double ToOutputTime(double sourceMs)
    {
        double source = ClampTime(sourceMs, StartMs, EndMs);
        double output = 0;
        foreach (VideoTimeRange range in KeptRanges)
        {
            if (source <= range.StartMs)
            {
                return output;
            }

            if (source < range.EndMs)
            {
                return output + (source - range.StartMs);
            }

            output += range.DurationMs;
        }

        return output;
    }

    public bool IsRemoved(double sourceMs) =>
        double.IsFinite(sourceMs)
        && RemovedRanges.Any(range => sourceMs >= range.StartMs && sourceMs < range.EndMs);

    /// <summary>Returns a kept segment or a merged removed range, using half-open boundaries.</summary>
    public VideoTimeRange? GetSegmentAt(double sourceMs)
    {
        if (!double.IsFinite(sourceMs) || sourceMs < StartMs || sourceMs >= EndMs)
        {
            return null;
        }

        foreach (VideoTimeRange range in RemovedRanges)
        {
            if (sourceMs >= range.StartMs && sourceMs < range.EndMs)
            {
                return range;
            }
        }

        foreach (VideoTimeRange range in Segments)
        {
            if (sourceMs >= range.StartMs && sourceMs < range.EndMs)
            {
                return range;
            }
        }

        return null;
    }

    public bool TrySplit(double sourceMs, out VideoCutTimeline timeline)
    {
        timeline = this;
        if (!double.IsFinite(sourceMs) || IsRemoved(sourceMs)
            || GetSegmentAt(sourceMs) is not VideoTimeRange segment
            || IsShorterThanMinimum(sourceMs - segment.StartMs, TrimSelection.MinimumSpanMs)
            || IsShorterThanMinimum(segment.EndMs - sourceMs, TrimSelection.MinimumSpanMs))
        {
            return false;
        }

        timeline = Create(StartMs, EndMs, RemovedRanges, SplitPoints.Append(sourceMs));
        return true;
    }

    public bool TryRemoveSegment(VideoTimeRange segment, out VideoCutTimeline timeline)
    {
        timeline = this;
        double minimum = Math.Min(TrimSelection.MinimumSpanMs, EndMs - StartMs);
        if (!Segments.Contains(segment) || IsShorterThanMinimum(OutputDurationMs - segment.DurationMs, minimum)
            || Segments.Count <= 1)
        {
            return false;
        }

        timeline = Create(StartMs, EndMs, RemovedRanges.Append(segment), SplitPoints);
        return true;
    }

    public bool TryRestoreSegment(VideoTimeRange segment, out VideoCutTimeline timeline)
    {
        timeline = this;
        if (!RemovedRanges.Contains(segment))
        {
            return false;
        }

        timeline = Create(StartMs, EndMs, RemovedRanges.Where(range => range != segment), SplitPoints);
        return true;
    }

    private static List<VideoTimeRange> NormalizeRemovedRanges(
        double start,
        double end,
        double minimum,
        IEnumerable<VideoTimeRange>? ranges)
    {
        List<VideoTimeRange> removed = [];
        foreach (VideoTimeRange range in (ranges ?? [])
            .Where(range => double.IsFinite(range.StartMs) && double.IsFinite(range.EndMs))
            .Select(range => new VideoTimeRange(Math.Max(start, range.StartMs), Math.Min(end, range.EndMs)))
            .Where(range => range.EndMs > range.StartMs)
            .OrderBy(range => range.StartMs)
            .ThenBy(range => range.EndMs))
        {
            if (removed.Count == 0)
            {
                removed.Add(IsShorterThanMinimum(range.StartMs - start, minimum) ? range with { StartMs = start } : range);
            }
            else if (range.StartMs <= removed[^1].EndMs
                || IsShorterThanMinimum(range.StartMs - removed[^1].EndMs, minimum))
            {
                removed[^1] = removed[^1] with { EndMs = Math.Max(removed[^1].EndMs, range.EndMs) };
            }
            else
            {
                removed.Add(range);
            }
        }

        if (removed.Count > 0 && IsShorterThanMinimum(end - removed[^1].EndMs, minimum))
        {
            removed[^1] = removed[^1] with { EndMs = end };
        }

        // Corrupt drafts must not produce an empty recording. Editing commands reject this
        // state before normalization; recovery instead keeps the complete outer trim.
        if (removed.Count == 1 && removed[0].StartMs == start && removed[0].EndMs == end)
        {
            removed.Clear();
        }

        return removed;
    }

    private static List<VideoTimeRange> GetKeptRanges(
        double start,
        double end,
        List<VideoTimeRange> removed)
    {
        List<VideoTimeRange> kept = [];
        double position = start;
        foreach (VideoTimeRange range in removed)
        {
            if (range.StartMs > position)
            {
                kept.Add(new(position, range.StartMs));
            }

            position = range.EndMs;
        }

        if (position < end)
        {
            kept.Add(new(position, end));
        }

        return kept;
    }

    private static List<double> NormalizeSplitPoints(
        double start,
        double end,
        double minimum,
        List<VideoTimeRange> kept,
        List<VideoTimeRange> removed,
        IEnumerable<double>? splitPoints)
    {
        List<double> splits = [];
        foreach (double split in (splitPoints ?? []).Where(double.IsFinite).Distinct().Order())
        {
            double previous = splits.Count > 0 ? splits[^1] : start;
            if (split <= start || split >= end
                || IsShorterThanMinimum(split - previous, minimum)
                || IsShorterThanMinimum(end - split, minimum))
            {
                continue;
            }

            // Retain markers at and inside cuts so restoring a removed range also restores
            // its edit points. Any resulting kept segment must still meet the minimum span.
            bool valid = kept.Concat(removed).Any(range =>
                split == range.StartMs || split == range.EndMs
                || (!IsShorterThanMinimum(split - range.StartMs, minimum)
                    && !IsShorterThanMinimum(range.EndMs - split, minimum)));
            if (valid)
            {
                splits.Add(split);
            }
        }

        return splits;
    }

    // Frame-grid multiplication can put a 100 ms span just below 100; source coordinates
    // remain exact and only the minimum-length decision tolerates that floating-point noise.
    private static bool IsShorterThanMinimum(double durationMs, double minimumMs) =>
        minimumMs - durationMs > MinimumSpanToleranceMs;

    private static double ClampTime(double timeMs, double start, double end) =>
        double.IsNaN(timeMs) ? start : Math.Clamp(timeMs, start, end);
}
