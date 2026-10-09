using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>The source interval rendered as the GIF preview sample.</summary>
internal readonly record struct GifSampleWindow(double StartMs, double EndMs, bool CoversWholeRange)
{
    internal double DurationMs => EndMs - StartMs;
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
        sample.TextOverlays = sample.TextOverlays.Where(overlay => Keep(overlay.StartMs, overlay.EndMs, window)).ToList();
        sample.FrameEditLayers = sample.FrameEditLayers.Where(layer => Keep(layer.StartMs, layer.EndMs, window)).ToList();
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

    private static bool Keep(double startMs, double endMs, GifSampleWindow window)
    {
        double visibleStart = Math.Max(startMs, window.StartMs);
        double visibleEnd = Math.Min(endMs, window.EndMs);
        if (!(visibleEnd > visibleStart))
        {
            return false;
        }

        bool cut = startMs < window.StartMs || endMs > window.EndMs;
        return !cut || visibleEnd - visibleStart >= MinimumClippedLayerMs;
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
    internal GifPresetMeasurement Including(long bytes, double sourceMs) =>
        this with { Bytes = Bytes + bytes, SourceMs = SourceMs + sourceMs };
}

/// <summary>
/// Coefficients of the size estimate, kept together so they can be re-fitted from the
/// MYCAPTURE_GIF_ESTIMATE_EVIDENCE report whenever the GIF engine changes how frames are
/// encoded (for example changed-rectangle frames, which make bytes depend on motion).
/// </summary>
/// <param name="LowMargin">
/// Relative margin below the estimate when none of the range is sampled; it shrinks linearly
/// with the sampled fraction and vanishes when the samples cover the whole range.
/// </param>
/// <param name="HighMargin">Same, above the estimate.</param>
/// <param name="ScaledPresetMargin">Extra margin for a preset estimated from another preset.</param>
/// <param name="PixelScalingExponent">Bytes per frame follow the pixel count to this power.</param>
internal sealed record GifEstimateCalibration(
    double LowMargin,
    double HighMargin,
    double ScaledPresetMargin,
    double PixelScalingExponent)
{
    /// <summary>
    /// Fitted to 1080p30 synthetic recordings of 10 to 20 s (GifPreviewEstimateEvidenceTests:
    /// scrolling text, still page, still/scroll/code-page mix, panning photo) with the current
    /// full-frame GIF encoding. The centred 3 s sample plus the 1 s start and end segments put
    /// the full size at 0.94 to 1.03 of the estimate at every preset (the centre alone 0.90 to
    /// 1.04; a leading 3 s sample alone was off by 1.19 on the mixed clip). Full exports at
    /// 960, 640 and 480 px implied pixel exponents of 0.71 to 1.05; 0.87 keeps the cross-preset
    /// error within 16%. Bytes did not depend on the playback speed.
    /// </summary>
    internal static GifEstimateCalibration Default { get; } = new(0.2, 0.3, 0.2, 0.87);
}

/// <summary>
/// Scales measured samples to the whole kept range. The GIF exporter samples the source at a
/// fixed cadence and writes every frame, so the frame count and the bytes grow linearly with
/// the source span; the playback speed only changes frame delays.
/// </summary>
internal static class GifSizeEstimator
{
    internal static GifSizeEstimate FromSample(long sampleBytes, double sampleSourceMs, double fullSourceMs, GifEstimateCalibration? calibration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleBytes);
        if (!double.IsFinite(sampleSourceMs) || sampleSourceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleSourceMs));
        }

        if (!double.IsFinite(fullSourceMs) || fullSourceMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fullSourceMs));
        }

        if (sampleSourceMs >= fullSourceMs - 0.5)
        {
            return GifSizeEstimate.Exact(sampleBytes);
        }

        calibration ??= GifEstimateCalibration.Default;
        double uncovered = 1 - (sampleSourceMs / fullSourceMs);
        double expected = sampleBytes * (fullSourceMs / sampleSourceMs);
        return new GifSizeEstimate(
            ToBytes(expected * (1 - (calibration.LowMargin * uncovered))),
            ToBytes(expected),
            ToBytes(expected * (1 + (calibration.HighMargin * uncovered))),
            false);
    }

    /// <summary>Wall-clock time of a full calculation, scaled from a sample render.</summary>
    internal static double EstimateCalculationMs(GifPresetMeasurement measurement, double fullSourceMs) =>
        double.IsFinite(measurement.CalculationMsPerSourceMs) && fullSourceMs > 0
            ? Math.Max(0, measurement.CalculationMsPerSourceMs) * fullSourceMs
            : 0;

    /// <summary>
    /// Estimates <paramref name="target"/> from a measurement of another preset over the same
    /// content. Frame count follows the cadence; bytes per frame follow the pixel count.
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
        GifSizeEstimate basis = FromSample(measured.Bytes, measured.SourceMs, fullSourceMs, calibration);
        if (measured.Quality == target)
        {
            return basis;
        }

        double factor = ScaleFactor(measured.Quality, target, sourceWidth, sourceHeight, calibration);
        return new GifSizeEstimate(
            ToBytes(basis.LowBytes * factor * (1 - calibration.ScaledPresetMargin)),
            ToBytes(basis.ExpectedBytes * factor),
            ToBytes(basis.HighBytes * factor * (1 + calibration.ScaledPresetMargin)),
            false);
    }

    internal static double ScaleFactor(GifExportQuality from, GifExportQuality to, int sourceWidth, int sourceHeight, GifEstimateCalibration? calibration = null)
    {
        calibration ??= GifEstimateCalibration.Default;
        (int fromWidth, int fromHeight) = AnimatedGifExporter.FitWithin(sourceWidth, sourceHeight, from.LongEdge);
        (int toWidth, int toHeight) = AnimatedGifExporter.FitWithin(sourceWidth, sourceHeight, to.LongEdge);
        double pixels = (double)toWidth * toHeight / Math.Max(1, (double)fromWidth * fromHeight);
        double cadence = (double)to.FramesPerSecond / from.FramesPerSecond;
        return cadence * Math.Pow(pixels, calibration.PixelScalingExponent);
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
    /// 960 px at 5 fps: half the bytes of 960/10 with the same text sharpness, for still UI
    /// walkthroughs where 640 px would blur small text (bytes follow the frame count exactly).
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
    /// the evidence clips measured 0.1 to 0.2% for a still page with a moving pointer, 11% for
    /// a slow photo pan and 16 to 18% for scrolling text, which the research counts as high
    /// motion, so the high boundary is 15%. Between moderate and high only the 960/5 fallback
    /// differs, and it is about the size of 640/10, so the boundary rarely changes the advice.
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
            ? GifSizeEstimator.FromSample(reference.Bytes, reference.SourceMs, fullSourceMs, calibration)
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
