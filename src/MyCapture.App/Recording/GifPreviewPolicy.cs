using System.IO;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>The source interval rendered as the GIF preview sample.</summary>
internal readonly record struct GifSampleWindow(double StartMs, double EndMs, bool CoversWholeRange)
{
    internal double? KeptDurationMs { get; init; }

    internal double DurationMs => KeptDurationMs ?? EndMs - StartMs;
}

/// <summary>
/// Chooses the short source intervals that the export dialog renders with the real GIF
/// exporter before the user commits to a full calculation.
/// </summary>
internal static class GifPreviewSampling
{
    /// <summary>
    /// On-screen loop length the sample aims for at the selected speed: long enough to judge
    /// motion and speed, short enough to render in about two seconds for a 1080p30 source.
    /// </summary>
    internal const double TargetPlaybackMs = 3_000;

    /// <summary>
    /// Shortest source span of the visual sample, so slow playback speeds still measure
    /// three seconds of content instead of a statistically weaker sliver.
    /// </summary>
    internal const double MinimumSourceMs = 3_000;

    /// <summary>
    /// Longest source span. The exporter decodes every source frame in the span (about 63
    /// frames/s for 1080p30 on the reference PC), so this caps the sample at roughly three
    /// seconds of work and the decoded preview at 60 frames for the 10 fps presets. Clips up
    /// to this length are rendered whole, which makes the sample the exact result.
    /// </summary>
    internal const double MaximumSourceMs = 6_000;

    /// <summary>
    /// Length of the extra estimate-only renders at the start and end of a longer range. Like
    /// Kap PR #1284 (start, middle and end samples), they catch content that changes later in
    /// a recording; a leading 3 s sample alone misjudged the mixed evidence clip by 19%.
    /// </summary>
    internal const double RefinementSegmentMs = 1_000;

    /// <summary>Default visual sample position: the middle of the free range.</summary>
    internal const double CentrePosition = 0.5;

    /// <summary>
    /// A text or image layer cut by the sample edge must keep at least this much source time;
    /// GIF timing is quantized to 10 ms, and a shorter clipped stub would be rejected by the
    /// exporter even though the full export renders the layer normally.
    /// </summary>
    internal const double MinimumClippedLayerMs = 20;

    /// <summary>
    /// Plans the visual sample. <paramref name="position"/> is 0 for the start of the kept range,
    /// 1 for its end. Screen recordings usually open and close on a still frame while the pointer
    /// travels to the start/stop control, so the default is the middle, which is also the most
    /// representative single place for the size estimate; the exporter seeks to the keyframe
    /// before the sample, so a later position costs no more than the start.
    /// </summary>
    internal static GifSampleWindow Plan(double trimInMs, double trimOutMs, double playbackSpeed, double position = CentrePosition)
    {
        if (!double.IsFinite(trimInMs) || !double.IsFinite(trimOutMs) || trimOutMs <= trimInMs)
        {
            throw new ArgumentOutOfRangeException(nameof(trimOutMs));
        }

        if (!double.IsFinite(playbackSpeed) || playbackSpeed <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playbackSpeed));
        }

        double span = trimOutMs - trimInMs;
        if (span <= MaximumSourceMs)
        {
            return new GifSampleWindow(trimInMs, trimOutMs, true);
        }

        double sample = Math.Clamp(TargetPlaybackMs * playbackSpeed, MinimumSourceMs, MaximumSourceMs);
        double fraction = double.IsFinite(position) ? Math.Clamp(position, 0, 1) : CentrePosition;
        double start = trimInMs + (Math.Round((span - sample) * fraction / 10, MidpointRounding.AwayFromZero) * 10);
        return new GifSampleWindow(start, Math.Min(trimOutMs, start + sample), false);
    }

    internal static GifSampleWindow Plan(VideoEditDocument document, double playbackSpeed, double position = CentrePosition)
    {
        ArgumentNullException.ThrowIfNull(document);
        VideoCutTimeline timeline = document.CreateTimeline();
        if (timeline.RemovedRanges.Count == 0)
        {
            return Plan(timeline.StartMs, timeline.EndMs, playbackSpeed, position);
        }

        return MapWindow(timeline, Plan(0, timeline.OutputDurationMs, playbackSpeed, position));
    }

    private static GifSampleWindow MapWindow(VideoCutTimeline timeline, GifSampleWindow output)
    {
        double sourceStart = timeline.ToSourceTime(output.StartMs);
        double sourceEnd = timeline.ToSourceTime(output.EndMs);
        // Sampling must not turn a valid kept segment into a sub-minimum edge fragment:
        // normalizing that sample would otherwise remove the fragment and skew its measurement.
        foreach (VideoTimeRange range in timeline.KeptRanges)
        {
            if (sourceStart >= range.StartMs && sourceStart < range.EndMs
                && range.EndMs - sourceStart < TrimSelection.MinimumSpanMs)
            {
                sourceStart = Math.Max(range.StartMs, range.EndMs - TrimSelection.MinimumSpanMs);
            }

            if (sourceEnd > range.StartMs && sourceEnd <= range.EndMs
                && sourceEnd - range.StartMs < TrimSelection.MinimumSpanMs)
            {
                sourceEnd = Math.Min(range.EndMs, range.StartMs + TrimSelection.MinimumSpanMs);
            }
        }

        return new GifSampleWindow(sourceStart, sourceEnd, output.CoversWholeRange)
        {
            KeptDurationMs = timeline.ToOutputTime(sourceEnd) - timeline.ToOutputTime(sourceStart),
        };
    }

    /// <summary>
    /// Start and end segments rendered only for the size estimate, skipping any part already
    /// covered by the visual sample.
    /// </summary>
    internal static IReadOnlyList<GifSampleWindow> PlanRefinement(double trimInMs, double trimOutMs, GifSampleWindow visual)
    {
        if (visual.CoversWholeRange)
        {
            return [];
        }

        var segments = new List<GifSampleWindow>(2);
        if (visual.StartMs - trimInMs >= RefinementSegmentMs)
        {
            segments.Add(new GifSampleWindow(trimInMs, trimInMs + RefinementSegmentMs, false));
        }

        if (trimOutMs - visual.EndMs >= RefinementSegmentMs)
        {
            segments.Add(new GifSampleWindow(trimOutMs - RefinementSegmentMs, trimOutMs, false));
        }

        return segments;
    }

    internal static IReadOnlyList<GifSampleWindow> PlanRefinement(VideoEditDocument document, GifSampleWindow visual)
    {
        ArgumentNullException.ThrowIfNull(document);
        VideoCutTimeline timeline = document.CreateTimeline();
        if (timeline.RemovedRanges.Count == 0)
        {
            return PlanRefinement(timeline.StartMs, timeline.EndMs, visual);
        }

        var output = new GifSampleWindow(
            timeline.ToOutputTime(visual.StartMs),
            timeline.ToOutputTime(visual.EndMs),
            visual.CoversWholeRange);
        return PlanRefinement(0, timeline.OutputDurationMs, output)
            .Select(window => MapWindow(timeline, window))
            .ToArray();
    }

    /// <summary>
    /// Returns a detached copy of <paramref name="document"/> trimmed to the sample. Layers wholly
    /// inside the sample are kept unchanged, so a layer the full export would reject still fails
    /// here; layers cut by the sample edge are kept only when their visible part survives GIF
    /// timing quantization.
    /// </summary>
    internal static VideoEditDocument CreateSampleDocument(VideoEditDocument document, GifSampleWindow window)
    {
        ArgumentNullException.ThrowIfNull(document);
        VideoEditDocument sample = document.Clone();
        sample.TrimInMs = window.StartMs;
        sample.TrimOutMs = window.EndMs;
        VideoCutTimeline timeline = sample.CreateTimeline();
        sample.TextOverlays = sample.TextOverlays.Where(overlay => Keep(overlay.StartMs, overlay.EndMs, window, timeline)).ToList();
        sample.FrameEditLayers = sample.FrameEditLayers.Where(layer => Keep(layer.StartMs, layer.EndMs, window, timeline)).ToList();
        return sample;
    }

    /// <summary>
    /// GIF frames over the range as the exporter schedules them: the fixed cadence on the
    /// centisecond grid plus every text or image layer start and end that falls between cadence
    /// boundaries.
    /// </summary>
    /// <param name="layers">Layer intervals relative to the start of the range.</param>
    internal static int EstimateFrameCount(double spanMs, int framesPerSecond, IReadOnlyList<(double StartMs, double EndMs)>? layers = null)
    {
        if (!double.IsFinite(spanMs) || spanMs <= 0 || framesPerSecond <= 0)
        {
            return 0;
        }

        int total = Math.Max(1, (int)Math.Round(spanMs / 10, MidpointRounding.AwayFromZero));
        int cadence = Math.Max(1, 100 / framesPerSecond);
        int frames = (total + cadence - 1) / cadence;
        var extra = new HashSet<int>();
        foreach ((double startMs, double endMs) in layers ?? [])
        {
            foreach (double edge in new[] { startMs, endMs })
            {
                if (!double.IsFinite(edge)) continue;
                int boundary = Math.Clamp((int)Math.Round(edge / 10, MidpointRounding.AwayFromZero), 0, total);
                if (boundary > 0 && boundary < total && boundary % cadence != 0) extra.Add(boundary);
            }
        }

        return frames + extra.Count;
    }

    private static bool Keep(double startMs, double endMs, GifSampleWindow window, VideoCutTimeline timeline)
    {
        double visibleStart = Math.Max(startMs, window.StartMs);
        double visibleEnd = Math.Min(endMs, window.EndMs);
        double keptDuration = timeline.ToOutputTime(visibleEnd) - timeline.ToOutputTime(visibleStart);
        if (!(visibleEnd > visibleStart) || keptDuration <= 0)
        {
            return false;
        }

        bool cut = startMs < window.StartMs || endMs > window.EndMs;
        return !cut || keptDuration >= MinimumClippedLayerMs;
    }
}

/// <summary>A GIF file-size range; <see cref="IsExact"/> means the bytes were measured.</summary>
internal readonly record struct GifSizeEstimate(long LowBytes, long ExpectedBytes, long HighBytes, bool IsExact)
{
    internal static GifSizeEstimate Exact(long bytes) => new(bytes, bytes, bytes, true);
}

/// <summary>Measured renders of a preset over part or all of the kept range.</summary>
/// <param name="Bytes">GIF bytes of every measured segment together.</param>
/// <param name="SourceMs">Source time those segments cover.</param>
/// <param name="CalculationMsPerSourceMs">Render time per source millisecond of the longest segment.</param>
internal sealed record GifPresetMeasurement(
    GifExportQuality Quality,
    long Bytes,
    double SourceMs,
    double CalculationMsPerSourceMs)
{
    /// <summary>The segments with their block sizes, when they were read; see <see cref="GifSizeEstimator"/>.</summary>
    internal IReadOnlyList<GifMeasuredSegment> Segments { get; init; } = [];

    /// <summary>A measurement of one rendered segment.</summary>
    /// <param name="startsRange">Whether the segment begins exactly at the start of the kept range.</param>
    internal static GifPresetMeasurement Of(GifExportQuality quality, GifSegmentSize size, double sourceMs, double calculationMsPerSourceMs, bool startsRange) =>
        new(quality, size.Bytes, sourceMs, calculationMsPerSourceMs) { Segments = [new GifMeasuredSegment(size, sourceMs, startsRange)] };

    internal GifPresetMeasurement Including(long bytes, double sourceMs) =>
        this with { Bytes = Bytes + bytes, SourceMs = SourceMs + sourceMs, Segments = [] };

    /// <inheritdoc cref="Of"/>
    internal GifPresetMeasurement Including(GifSegmentSize segment, double sourceMs, bool startsRange) =>
        this with
        {
            Bytes = Bytes + segment.Bytes,
            SourceMs = SourceMs + sourceMs,
            Segments = Segments.Count == 0 && Bytes > 0 ? [] : [.. Segments, new GifMeasuredSegment(segment, sourceMs, startsRange)],
        };
}

/// <summary>One rendered segment of a measurement.</summary>
/// <param name="Size">Its block sizes.</param>
/// <param name="SourceMs">Source time it covers.</param>
/// <param name="StartsRange">
/// True when it begins at the start of the kept range: its bytes are then exactly the first
/// bytes of the whole export, because the writer's output depends only on earlier frames.
/// </param>
internal sealed record GifMeasuredSegment(GifSegmentSize Size, double SourceMs, bool StartsRange)
{
    internal double HeadMs => Size.HeadShare * SourceMs;

    internal long TailBytes => Math.Max(0, Size.Bytes - Size.HeadBytes);

    internal double TailMs => SourceMs - HeadMs;
}

/// <summary>
/// Sizes of one rendered GIF, read from its blocks. Its first image always covers the canvas,
/// and the next few repaint more than later ones while the kept canvas settles (see
/// <see cref="GifBoundedCanvas"/>); a whole export pays that start only once. The head is the
/// container plus every image that starts in the first half of the playback, and the bytes
/// after it show the settled rate.
/// </summary>
/// <param name="Bytes">Whole file.</param>
/// <param name="FirstImageBytes">Header, loop extension, first image and trailer; zero when unknown.</param>
/// <param name="HeadBytes">Header, loop extension, images starting in the first half, and trailer; zero when unknown.</param>
/// <param name="HeadShare">Share of the playback the head images cover, from 0 to 1.</param>
internal readonly record struct GifSegmentSize(long Bytes, long FirstImageBytes, long HeadBytes, double HeadShare)
{
    /// <summary>Reads the sizes of a written GIF; zero head and first image when it is malformed.</summary>
    internal static GifSegmentSize Measure(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Read(bytes);
    }

    internal static GifSegmentSize Read(ReadOnlySpan<byte> gif)
    {
        var images = new List<(long End, int Delay)>();
        try
        {
            if (gif.Length < 13 || !gif[..3].SequenceEqual("GIF"u8))
            {
                return new GifSegmentSize(gif.Length, 0, 0, 0);
            }

            int index = 13 + ((gif[10] & 0x80) != 0 ? 3 * (1 << ((gif[10] & 0x07) + 1)) : 0);
            int delay = 0;
            while (gif[index] != 0x3B)
            {
                if (gif[index] == 0x21)
                {
                    if (gif[index + 1] == 0xF9)
                    {
                        delay = gif[index + 4] | (gif[index + 5] << 8);
                    }

                    index = SkipSubBlocks(gif, index + 2);
                }
                else if (gif[index] == 0x2C)
                {
                    byte packed = gif[index + 9];
                    index += 10 + ((packed & 0x80) != 0 ? 3 * (1 << ((packed & 0x07) + 1)) : 0);
                    index = SkipSubBlocks(gif, index + 1);
                    images.Add((index, delay));
                    delay = 0;
                }
                else
                {
                    return new GifSegmentSize(gif.Length, 0, 0, 0);
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            return new GifSegmentSize(gif.Length, 0, 0, 0);
        }

        if (images.Count == 0)
        {
            return new GifSegmentSize(gif.Length, 0, 0, 0);
        }

        long total = images.Sum(image => (long)image.Delay);
        long start = 0;
        int head = 0;
        while (head < images.Count && (total == 0 ? head == 0 : start * 2 < total))
        {
            start += images[head].Delay;
            head++;
        }

        double share = total == 0 || head == images.Count ? 1 : (double)start / total;
        return new GifSegmentSize(gif.Length, images[0].End + 1, images[head - 1].End + 1, share);

        static int SkipSubBlocks(ReadOnlySpan<byte> data, int index)
        {
            while (data[index] != 0)
            {
                index += data[index] + 1;
            }

            return index + 1;
        }
    }
}

/// <summary>
/// Coefficients of the size estimate, kept together so they can be re-fitted from the
/// MYCAPTURE_GIF_ESTIMATE_EVIDENCE report whenever the GIF engine changes how frames are
/// encoded.
/// </summary>
/// <param name="LowMargin">
/// Relative margin below the estimate when none of the range is sampled; it shrinks linearly
/// with the sampled fraction and vanishes when the samples cover the whole range.
/// </param>
/// <param name="HighMargin">Same, above the estimate.</param>
/// <param name="ScaledPresetMargin">Extra margin for a preset estimated from another preset.</param>
/// <param name="PixelScalingExponent">Bytes per image follow the pixel count to this power.</param>
/// <param name="RepaintMargin">
/// Further margin on both sides, in full-canvas first images per unsampled range: large
/// repaints (an encoder keyframe that shifts a still page, a page change) cost up to about one
/// full image each, and a few seconds of samples cannot tell how many the rest holds.
/// </param>
/// <param name="CadenceScalingExponent">
/// Bytes after the first image follow the frame-rate ratio to this power: at a lower rate each
/// image repaints what several frames changed.
/// </param>
internal sealed record GifEstimateCalibration(
    double LowMargin,
    double HighMargin,
    double ScaledPresetMargin,
    double PixelScalingExponent,
    double RepaintMargin = 0,
    double CadenceScalingExponent = 1)
{
    /// <summary>
    /// Fitted to 1080p30 synthetic recordings of 10 to 20 s (GifPreviewEstimateEvidenceTests:
    /// scrolling text, still page, still/scroll/code-page mix, panning photo) with changed-area
    /// frames (<see cref="GifFrameDifferencing.ErrorBounded"/>). The centred 3 s sample plus the
    /// 1 s start and end segments put the full size at 0.61 to 1.43 of the estimate: the mixed
    /// clip scrolls only around the centre the sample sees (0.61 to 0.63), and two encoder
    /// keyframe repaints of the still page fall outside the samples (1.27 and 1.43, about 80 kB
    /// at 960 px). A low margin of 0.6 and one first image of repaint margin keep all 16
    /// estimates inside their ranges; the high margin is unchanged. Bytes after the first image
    /// followed the frame rate to the power 0.22 to 0.71 (0.5 used) and the pixel count around
    /// 0.87; with a 25% margin all 48 cross-preset estimates held. The playback speed did not
    /// change the size at all.
    /// </summary>
    internal static GifEstimateCalibration Default { get; } = new(0.6, 0.3, 0.25, 0.87, 1.0, 0.5);
}

/// <summary>
/// Scales measured samples to the whole kept range. A GIF starts with a full-canvas image and
/// a few larger repaints while the kept canvas settles (the head); after that the writer stores
/// only what changed, so the remaining bytes grow with the source span at the rate the
/// segments' later halves measured. A segment that starts the range is the whole export's own
/// beginning, byte for byte, so it is counted as is. The playback speed only changes frame
/// delays.
/// </summary>
internal static class GifSizeEstimator
{
    /// <summary>Estimate from bytes alone, without block sizes: plain proportional scaling.</summary>
    internal static GifSizeEstimate FromSample(long sampleBytes, double sampleSourceMs, double fullSourceMs, GifEstimateCalibration? calibration = null) =>
        FromMeasurement(new GifPresetMeasurement(GifExportQuality.Standard, sampleBytes, sampleSourceMs, 0), fullSourceMs, calibration);

    /// <summary>Estimate of the whole range from a measurement; see the class remarks.</summary>
    internal static GifSizeEstimate FromMeasurement(GifPresetMeasurement measurement, double fullSourceMs, GifEstimateCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(measurement.Bytes);
        if (!double.IsFinite(measurement.SourceMs) || measurement.SourceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(measurement));
        }

        if (!double.IsFinite(fullSourceMs) || fullSourceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fullSourceMs));
        }

        if (measurement.SourceMs >= fullSourceMs - 0.5)
        {
            return GifSizeEstimate.Exact(measurement.Bytes);
        }

        calibration ??= GifEstimateCalibration.Default;
        double expected = ExpectedBytes(measurement, fullSourceMs);
        double uncovered = 1 - (measurement.SourceMs / fullSourceMs);
        double repaints = calibration.RepaintMargin * uncovered * FirstImageOf(measurement);
        return new GifSizeEstimate(
            ToBytes((expected * (1 - (calibration.LowMargin * uncovered))) - repaints),
            ToBytes(expected),
            ToBytes((expected * (1 + (calibration.HighMargin * uncovered))) + repaints),
            false);
    }

    /// <summary>Average full-canvas first image of the measured segments, or zero when unknown.</summary>
    private static double FirstImageOf(GifPresetMeasurement measurement) =>
        measurement.Segments.Count == 0 ? 0 : measurement.Segments.Average(segment => (double)segment.Size.FirstImageBytes);

    private static double ExpectedBytes(GifPresetMeasurement measurement, double fullSourceMs)
    {
        IReadOnlyList<GifMeasuredSegment> segments = measurement.Segments;
        if (segments.Count == 0 || segments.Any(segment => segment.Size.HeadBytes <= 0))
        {
            return measurement.Bytes * (fullSourceMs / measurement.SourceMs);
        }

        GifMeasuredSegment? lead = segments.FirstOrDefault(segment => segment.StartsRange);
        IReadOnlyList<GifMeasuredSegment> rated = lead is not null && segments.Count > 1
            ? segments.Where(segment => !ReferenceEquals(segment, lead)).ToList()
            : segments;
        double restPerMs = rated.Sum(segment => (double)segment.TailBytes) / Math.Max(1, rated.Sum(segment => segment.TailMs));
        if (lead is not null)
        {
            return lead.Size.Bytes + (restPerMs * Math.Max(0, fullSourceMs - lead.SourceMs));
        }

        double head = segments.Average(segment => (double)segment.Size.HeadBytes);
        double headMs = segments.Average(segment => segment.HeadMs);
        return head + (restPerMs * Math.Max(0, fullSourceMs - headMs));
    }

    /// <summary>Wall-clock time of a full calculation, scaled from a sample render.</summary>
    internal static double EstimateCalculationMs(GifPresetMeasurement measurement, double fullSourceMs) =>
        double.IsFinite(measurement.CalculationMsPerSourceMs) && fullSourceMs > 0
            ? Math.Max(0, measurement.CalculationMsPerSourceMs) * fullSourceMs
            : 0;

    /// <summary>
    /// Estimates <paramref name="target"/> from a measurement of another preset over the same
    /// content. The first image follows the pixel count; the rest follows the pixel count and
    /// the frame rate.
    /// </summary>
    internal static GifSizeEstimate FromOtherPreset(
        GifPresetMeasurement measured,
        GifExportQuality target,
        int sourceWidth,
        int sourceHeight,
        double fullSourceMs,
        GifEstimateCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(target);
        calibration ??= GifEstimateCalibration.Default;
        GifSizeEstimate basis = FromMeasurement(measured, fullSourceMs, calibration);
        if (measured.Quality == target)
        {
            return basis;
        }

        double firstImage = Math.Min(FirstImageOf(measured), basis.ExpectedBytes);
        double pixels = PixelFactor(measured.Quality, target, sourceWidth, sourceHeight, calibration);
        double rest = ScaleFactor(measured.Quality, target, sourceWidth, sourceHeight, calibration);
        double factor = ((firstImage * pixels) + ((basis.ExpectedBytes - firstImage) * rest)) / Math.Max(1, basis.ExpectedBytes);
        return new GifSizeEstimate(
            ToBytes(basis.LowBytes * factor * (1 - calibration.ScaledPresetMargin)),
            ToBytes(basis.ExpectedBytes * factor),
            ToBytes(basis.HighBytes * factor * (1 + calibration.ScaledPresetMargin)),
            false);
    }

    /// <summary>Factor for the bytes after the first image: frame rate and pixels, each to its fitted power.</summary>
    internal static double ScaleFactor(GifExportQuality from, GifExportQuality to, int sourceWidth, int sourceHeight, GifEstimateCalibration? calibration = null)
    {
        calibration ??= GifEstimateCalibration.Default;
        double cadence = (double)to.FramesPerSecond / from.FramesPerSecond;
        return Math.Pow(cadence, calibration.CadenceScalingExponent) * PixelFactor(from, to, sourceWidth, sourceHeight, calibration);
    }

    /// <summary>Factor for one image: the pixel-count ratio to the fitted power.</summary>
    internal static double PixelFactor(GifExportQuality from, GifExportQuality to, int sourceWidth, int sourceHeight, GifEstimateCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        calibration ??= GifEstimateCalibration.Default;
        (int fromWidth, int fromHeight) = AnimatedGifExporter.FitWithin(sourceWidth, sourceHeight, from.LongEdge);
        (int toWidth, int toHeight) = AnimatedGifExporter.FitWithin(sourceWidth, sourceHeight, to.LongEdge);
        double pixels = (double)toWidth * toHeight / Math.Max(1, (double)fromWidth * fromHeight);
        return Math.Pow(pixels, calibration.PixelScalingExponent);
    }

    private static long ToBytes(double value) => (long)Math.Max(1, Math.Round(value));
}

/// <summary>A share destination whose upload limits the recommendation aims under.</summary>
/// <param name="Bytes">Decimal size limit (a site's "10 MB" is taken as 10,000,000 bytes), or null for none.</param>
/// <param name="MaximumFrames">Frame-count limit of the destination, or null for none.</param>
/// <param name="LabelKey">Resource key of the combo-box label.</param>
internal sealed record GifShareTarget(long? Bytes, int? MaximumFrames, string LabelKey);

/// <summary>How much of the picture changes between sample frames.</summary>
internal enum GifMotionClass
{
    /// <summary>Mostly still UI: text legibility matters more than smoothness.</summary>
    Static,

    /// <summary>Some motion, such as a pointer and occasional page changes.</summary>
    Moderate,

    /// <summary>Scrolling, video or animation: smoothness matters more than resolution.</summary>
    High,
}

internal enum GifRecommendationKind
{
    /// <summary>The selected preset is the preferred one expected to fit.</summary>
    CurrentFits,

    /// <summary>Another preset is the preferred one expected to fit.</summary>
    SwitchPreset,

    /// <summary>No preset fits the size or frame limits; trim the range or export MP4.</summary>
    UseMp4,
}

/// <summary>The limit that ruled out the smallest candidate, for <see cref="GifRecommendationKind.UseMp4"/>.</summary>
internal enum GifLimitReason
{
    None,
    Size,
    DestinationFrames,
    SafetyFrames,
}

internal sealed record GifRecommendation(
    GifRecommendationKind Kind,
    GifExportQuality Quality,
    GifSizeEstimate Estimate,
    GifLimitReason Reason,
    double MaximumGifSourceMs,
    double? Speed);

/// <summary>
/// Length policy and recommendation for GIF export. There is no fixed duration warning: the
/// guidance follows the measured size against the chosen share limit, the destination's frame
/// limit, and a frame count beyond which GIF is the wrong format, with sources below.
/// </summary>
internal static class GifSharePolicy
{
    /// <summary>
    /// 960 px at 5 fps: the text sharpness of 960/10 for still UI walkthroughs where 640 px
    /// would blur small text. With changed-area frames it measured 0.6 to 0.9 of the bytes of
    /// 960/10 (scrolling text to a still page), not half: an unchanged frame costs nothing at
    /// either rate.
    /// </summary>
    internal static GifExportQuality SharpLowRate { get; } = new(UiText.Get("GifPreview_PresetSharpLowRate"), 960, 5);

    /// <summary>Offered presets, sharpest and largest first.</summary>
    internal static IReadOnlyList<GifExportQuality> Presets { get; } =
        [GifExportQuality.Standard, SharpLowRate, GifExportQuality.Compact, GifExportQuality.Smallest];

    internal static IReadOnlyList<double> Speeds { get; } = [0.5, 1, 1.5, 2, 3, 4];

    /// <summary>
    /// Upload limits of common destinations (research-gif.md, checked 2026-10-10):
    /// Notion Free "all files you upload must be under 5MB"
    /// (notion.com/help/images-files-and-media); GitHub issue/PR attachments "10MB for images
    /// and gifs" (docs.github.com, attaching-files); X animated GIF up to 15 MB and at most 350
    /// frames, 1280x1080 (docs.x.com/x-api/media/quickstart/best-practices; every preset is at
    /// most 960 px, inside 1280x1080); Discord free uploads 20 MB since 2026-08
    /// (support.discord.com, File Attachments FAQ). The 10 MB default also satisfies Jira,
    /// Teams chat and Slack.
    /// </summary>
    internal static IReadOnlyList<GifShareTarget> Targets { get; } =
    [
        new(5_000_000, null, "GifPreview_Target5"),
        new(10_000_000, null, "GifPreview_Target10"),
        new(15_000_000, 350, "GifPreview_Target15"),
        new(20_000_000, null, "GifPreview_Target20"),
        new(null, null, "GifPreview_TargetNone"),
    ];

    /// <summary>GitHub attachments: the share target this app integrates with directly.</summary>
    internal const int DefaultTargetIndex = 1;

    /// <summary>
    /// Above this many frames the dialog recommends MP4 whatever the size limit: about 120 s
    /// at 10 fps, a minute of calculation for a 1080p30 source on the reference PC and roughly
    /// 190 MB at 960 px. It is guidance, not a cap; 결과 계산 stays available and cancellable.
    /// </summary>
    internal const int SafetyFrameLimit = 1_200;

    /// <summary>
    /// Above this playback length the dialog suggests 1.5x or 2x. Windows Snipping Tool and
    /// GIPHY Capture stop GIFs at 30 s, and Snagit recommends segments under 30 s
    /// (techsmith.com Snagit animated GIF tutorial).
    /// </summary>
    internal const double LongPlaybackMs = 30_000;

    /// <summary>Speeds offered by the long-playback suggestion; the speed never changes on its own.</summary>
    internal static IReadOnlyList<double> SuggestedSpeeds { get; } = [1.5, 2];

    /// <summary>
    /// Motion class boundaries for the mean changed-block share of the sample. research-gif.md
    /// proposed still UI under 5% and high motion over 25% of changed pixels; with 4x4 blocks
    /// the evidence clips measured 0.1 to 0.2% for a still page with a moving pointer, 8 to 11%
    /// for a slow photo pan and 16 to 18% for scrolling text, which the research counts as high
    /// motion, so the high boundary is 15%. Between moderate and high only the 960/5 fallback
    /// differs (1.2 to 1.8 times the size of 640/10 with changed-area frames).
    /// </summary>
    internal const double StaticMotionLimit = 0.05;

    /// <inheritdoc cref="StaticMotionLimit"/>
    internal const double HighMotionLimit = 0.15;

    internal static GifMotionClass Classify(double motionFraction) => motionFraction switch
    {
        < StaticMotionLimit => GifMotionClass.Static,
        > HighMotionLimit => GifMotionClass.High,
        _ => GifMotionClass.Moderate,
    };

    /// <summary>
    /// Preference order: still UI keeps 960 px and drops to 5 fps first; moving content keeps
    /// 10 fps and lowers the resolution first, and skips 960/5 entirely.
    /// </summary>
    internal static IReadOnlyList<GifExportQuality> CandidatesFor(GifMotionClass motion) => motion switch
    {
        GifMotionClass.Static => [GifExportQuality.Standard, SharpLowRate, GifExportQuality.Compact, GifExportQuality.Smallest],
        GifMotionClass.Moderate => [GifExportQuality.Standard, GifExportQuality.Compact, SharpLowRate, GifExportQuality.Smallest],
        _ => [GifExportQuality.Standard, GifExportQuality.Compact, GifExportQuality.Smallest],
    };

    /// <summary>
    /// Picks the first candidate whose upper size estimate and frame count fit the target, or
    /// MP4 when none does, plus a faster speed when the playback would be long.
    /// </summary>
    /// <param name="measurements">Latest measurement per preset; at least one is required.</param>
    /// <param name="motionFraction">Mean changed-block share of the visual sample.</param>
    /// <param name="layers">Text and image layer intervals relative to the range start.</param>
    internal static GifRecommendation Recommend(
        double fullSourceMs,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyDictionary<GifExportQuality, GifPresetMeasurement> measurements,
        GifExportQuality current,
        double currentSpeed,
        GifShareTarget target,
        double motionFraction,
        IReadOnlyList<(double StartMs, double EndMs)>? layers = null,
        GifEstimateCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(target);
        if (measurements.Count == 0)
        {
            throw new ArgumentException("At least one measurement is required.", nameof(measurements));
        }

        IReadOnlyList<GifExportQuality> candidates = CandidatesFor(Classify(motionFraction));
        GifLimitReason reason = GifLimitReason.None;
        GifSizeEstimate last = default;
        foreach (GifExportQuality preset in candidates)
        {
            GifSizeEstimate estimate = EstimateFor(preset, fullSourceMs, sourceWidth, sourceHeight, measurements, calibration);
            last = estimate;
            int frames = GifPreviewSampling.EstimateFrameCount(fullSourceMs, preset.FramesPerSecond, layers);
            if (frames > SafetyFrameLimit)
            {
                reason = GifLimitReason.SafetyFrames;
                continue;
            }

            if (target.MaximumFrames is int maximumFrames && frames > maximumFrames)
            {
                reason = GifLimitReason.DestinationFrames;
                continue;
            }

            if (target.Bytes is long bytes && estimate.HighBytes > bytes)
            {
                reason = GifLimitReason.Size;
                continue;
            }

            GifRecommendationKind kind = preset == current ? GifRecommendationKind.CurrentFits : GifRecommendationKind.SwitchPreset;
            return new GifRecommendation(kind, preset, estimate, GifLimitReason.None, fullSourceMs, SuggestSpeed(fullSourceMs, currentSpeed));
        }

        GifExportQuality smallest = candidates[^1];
        return new GifRecommendation(GifRecommendationKind.UseMp4, smallest, last, reason,
            MaximumGifSourceMs(smallest, last, fullSourceMs, target, 2 * (layers?.Count ?? 0)), null);
    }

    /// <summary>Uses the preset's own measurement when present, else scales the nearest one.</summary>
    internal static GifSizeEstimate EstimateFor(
        GifExportQuality preset,
        double fullSourceMs,
        int sourceWidth,
        int sourceHeight,
        IReadOnlyDictionary<GifExportQuality, GifPresetMeasurement> measurements,
        GifEstimateCalibration? calibration = null)
    {
        GifPresetMeasurement reference = Reference(preset, measurements);
        return reference.Quality == preset
            ? GifSizeEstimator.FromMeasurement(reference, fullSourceMs, calibration)
            : GifSizeEstimator.FromOtherPreset(reference, preset, sourceWidth, sourceHeight, fullSourceMs, calibration);
    }

    /// <summary>1.5x or 2x when either brings a long playback within <see cref="LongPlaybackMs"/>.</summary>
    internal static double? SuggestSpeed(double fullSourceMs, double currentSpeed)
    {
        if (fullSourceMs / currentSpeed <= LongPlaybackMs)
        {
            return null;
        }

        foreach (double speed in SuggestedSpeeds)
        {
            if (speed > currentSpeed && fullSourceMs / speed <= LongPlaybackMs)
            {
                return speed;
            }
        }

        return null;
    }

    private static GifPresetMeasurement Reference(GifExportQuality preset, IReadOnlyDictionary<GifExportQuality, GifPresetMeasurement> measurements)
    {
        if (measurements.TryGetValue(preset, out GifPresetMeasurement? own))
        {
            return own;
        }

        // The nearest preset in bytes per second scales best; prefer longer measurements.
        double size = Weight(preset);
        return measurements.Values
            .OrderBy(measurement => Math.Abs(Math.Log(Weight(measurement.Quality) / size)))
            .ThenByDescending(measurement => measurement.SourceMs)
            .First();
    }

    private static double Weight(GifExportQuality quality) => (double)quality.LongEdge * quality.LongEdge * quality.FramesPerSecond;

    /// <summary>Longest source range that keeps <paramref name="preset"/> within every limit.</summary>
    private static double MaximumGifSourceMs(GifExportQuality preset, GifSizeEstimate estimate, double fullSourceMs, GifShareTarget target, int layerBoundaries)
    {
        double maximum = (SafetyFrameLimit - layerBoundaries) * 1000.0 / preset.FramesPerSecond;
        if (target.MaximumFrames is int frames)
        {
            maximum = Math.Min(maximum, (frames - layerBoundaries) * 1000.0 / preset.FramesPerSecond);
        }

        if (target.Bytes is long bytes)
        {
            double highPerMs = estimate.HighBytes / Math.Max(1, fullSourceMs);
            maximum = Math.Min(maximum, bytes / Math.Max(1e-9, highPerMs));
        }

        return Math.Max(0, Math.Floor(maximum / 1000) * 1000);
    }
}
