namespace MyCapture.Core.Recording;

public readonly record struct RecordingAudioKeptRange(
    long SourceStart100ns, long SourceEnd100ns, long OutputStartSample, long OutputEndSample)
{
    public long OutputFrameCount => OutputEndSample - OutputStartSample;
}

/// <summary>Maps kept source ranges to a cumulative sample grid, without per-cut rounding drift.</summary>
public static class RecordingAudioCutMap
{
    public static IReadOnlyList<RecordingAudioKeptRange> Create(IEnumerable<VideoTimeRange> keptRanges)
    {
        ArgumentNullException.ThrowIfNull(keptRanges);
        List<RecordingAudioKeptRange> result = [];
        long previousEnd = 0;
        decimal keptMilliseconds = 0;
        foreach (VideoTimeRange range in keptRanges)
        {
            if (!double.IsFinite(range.StartMs) || !double.IsFinite(range.EndMs)
                || range.StartMs < 0 || range.EndMs < range.StartMs)
                throw new ArgumentException("Kept ranges must be finite and nonnegative.", nameof(keptRanges));
            long start = checked((long)Math.Round(range.StartMs * 10_000, MidpointRounding.AwayFromZero));
            long end = checked((long)Math.Round(range.EndMs * 10_000, MidpointRounding.AwayFromZero));
            if (start < previousEnd) throw new ArgumentException("Kept ranges must be ordered and disjoint.", nameof(keptRanges));
            long outputStart = OutputBoundary(keptMilliseconds);
            // Keep sub-100ns fractions until the cumulative boundary is rounded. Rounding
            // every kept duration to either ticks or samples would drift over many cuts.
            keptMilliseconds += (decimal)range.EndMs - (decimal)range.StartMs;
            long outputEnd = OutputBoundary(keptMilliseconds);
            if (range.EndMs > range.StartMs) result.Add(new(start, end, outputStart, outputEnd));
            previousEnd = end;
        }
        return result;
    }

    private static long OutputBoundary(decimal milliseconds) => checked((long)decimal.Round(
        milliseconds * RecordingAudioFormat.SampleRate / 1000, 0, MidpointRounding.AwayFromZero));
}
