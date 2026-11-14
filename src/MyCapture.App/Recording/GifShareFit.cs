namespace MyCapture.App.Recording;

/// <summary>The outcome of fitting a GIF export under a share limit.</summary>
/// <param name="Quality">The setting whose encode is the best one found.</param>
/// <param name="Bytes">The size of that encode.</param>
/// <param name="Fits">Whether <paramref name="Bytes"/> is at most the upper limit.</param>
/// <param name="Encodes">Encodes spent, the one already made by the caller included.</param>
internal sealed record GifShareFitResult(GifExportQuality Quality, long Bytes, bool Fits, int Encodes);

/// <summary>
/// Keeps a GIF export strictly under the chosen share limit. The recommendation only estimates;
/// the real size is checked after the encode, and a file over the limit is encoded again at a
/// smaller setting: down the preset ladder to the first that fits, then a bisection of the
/// long edge between that preset and the one above it, so the result lands just under the limit
/// instead of a whole preset below it.
/// </summary>
internal static class GifShareFit
{
    /// <summary>Encodes one export may spend, the first included; bounds the extra time.</summary>
    internal const int MaximumEncodes = 7;

    /// <summary>The bisection stops when the long edge is known to within this many pixels.</summary>
    internal const int LongEdgeStep = 16;

    /// <summary>
    /// The most bytes a result may take for <paramref name="target"/>: below it by 0.01% of the
    /// target or 1,024 bytes, whichever is more, so a site that rejects "10 MB or more" accepts it.
    /// </summary>
    internal static long UpperLimit(long target) => target - Math.Max(target / 10_000, 1_024);

    /// <summary>Safety factor on the first guess from the square law, so it lands under the limit.</summary>
    private const double GuessMargin = 0.98;

    /// <summary>The smallest long edge a preset uses; the frame rate is lowered instead of going below it.</summary>
    internal static int MinimumLongEdge { get; } = GifSharePolicy.Presets.Min(preset => preset.LongEdge);

    /// <summary>Frame rates the presets use, highest first.</summary>
    private static IReadOnlyList<int> PresetRates { get; } =
        GifSharePolicy.Presets.Select(preset => preset.FramesPerSecond).Distinct().OrderByDescending(rate => rate).ToList();

    /// <summary>
    /// Fits an export whose first encode (<paramref name="start"/>, <paramref name="startBytes"/>)
    /// is already made. <paramref name="encode"/> encodes a setting and returns the file's size.
    /// The frame rate is kept and the long edge alone is searched, assuming bytes follow its
    /// square: a first guess from that law, then the guess is corrected by each result until the
    /// largest edge that fits is known to within <see cref="LongEdgeStep"/>. Only when even the
    /// smallest preset edge is over does the frame rate drop to the next preset rate.
    /// </summary>
    internal static GifShareFitResult Fit(GifExportQuality start, long startBytes, long target, Func<GifExportQuality, long> encode)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(encode);
        long limit = UpperLimit(target);
        int encodes = 1;
        if (startBytes <= limit)
        {
            return new GifShareFitResult(start, startBytes, true, encodes);
        }

        GifExportQuality last = start;
        long lastBytes = startBytes;
        int fps = start.FramesPerSecond;
        // Reference over-limit point of the stage: edge and the bytes expected at this rate.
        int overEdge = start.LongEdge;
        double overBytes = startBytes;
        while (encodes < MaximumEncodes)
        {
            int low = 0; // largest edge known to fit; 0 = none yet
            GifExportQuality? best = null;
            long bestBytes = 0;
            int high = overEdge; // smallest edge known to be over
            bool minimumOver = false;
            double refEdge = overEdge;
            double refBytes = overBytes;
            while (encodes < MaximumEncodes)
            {
                int next = SnapDown(refEdge * Math.Sqrt(limit / Math.Max(1d, refBytes)) * (best is null ? GuessMargin : 1.0));
                next = Math.Max(next, MinimumLongEdge);
                int floor = low == 0 ? MinimumLongEdge : low + LongEdgeStep;
                if (next >= high) next = high - LongEdgeStep;
                if (next < floor) next = floor;
                if (next >= high || (low != 0 && high - low <= LongEdgeStep))
                {
                    break;
                }

                var candidate = new GifExportQuality($"{next} px · {fps} fps", next, fps);
                long bytes = encode(candidate);
                encodes++;
                last = candidate;
                lastBytes = bytes;
                refEdge = next;
                refBytes = bytes;
                if (bytes <= limit)
                {
                    low = next;
                    best = candidate;
                    bestBytes = bytes;
                }
                else
                {
                    high = next;
                    if (next <= MinimumLongEdge)
                    {
                        minimumOver = true;
                        break;
                    }
                }
            }

            if (best is not null)
            {
                return new GifShareFitResult(best, bestBytes, true, encodes);
            }

            int lower = PresetRates.FirstOrDefault(rate => rate < fps);
            if (lower == 0 || !minimumOver && encodes >= MaximumEncodes)
            {
                break;
            }

            // Even the smallest edge is over at this rate: drop the rate, assuming bytes follow it.
            overBytes = overBytes * lower / fps;
            fps = lower;
        }

        return new GifShareFitResult(last, lastBytes, false, encodes);
    }

    private static int SnapDown(double edge) => (int)Math.Floor(edge / LongEdgeStep) * LongEdgeStep;
}
