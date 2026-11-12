namespace MyCapture.Core.Pin;

/// <summary>One frame of a sweep of light across a pin: its left edge and its width as shares of the pin's width.</summary>
public readonly record struct TranslationSweepFrame(double Left, double Width, double Opacity);

/// <summary>
/// One frame of a block turning into its translation. Left of <see cref="Boundary"/> − <see cref="Band"/> the block shows the
/// translation, right of <see cref="Boundary"/> the source, with a soft edge between (shares of the block's width).
/// </summary>
public readonly record struct TranslationBlockFrame(double Progress, double Boundary, double Band, double Rise, bool Done)
{
    /// <summary>The share of the block's width where the translated side starts fading into the source side.</summary>
    public double SolidEnd => Boundary - Band;
}

/// <summary>
/// The time plan of a pin's live translation ("E"): a sweep of light when the command is pressed, the untranslated text
/// dimming once its places are known, a soft left-to-right edge per block as blocks are ready in reading order, and the
/// outline brightening once when everything is in. Every value is a pure function of times the caller passes, so tests
/// step a clock instead of waiting. The durations follow the motion tokens (Fast 83, Normal 167, Deliberate 250 ms).
/// </summary>
public static class TranslationRevealMotion
{
    /// <summary>The sweep crosses the pin once in this time.</summary>
    public const double SweepMs = 500;

    /// <summary>The sweep's width as a share of the pin's width.</summary>
    public const double SweepWidth = 0.28;

    /// <summary>The sweep's strongest white over a light picture.</summary>
    public const double SweepLightOpacity = 0.44;

    /// <summary>The sweep's strongest white over a dark picture.</summary>
    public const double SweepDarkOpacity = 0.11;

    /// <summary>Mean luminance at and below which a picture counts as dark.</summary>
    public const double DarkLuminance = 0.15;

    /// <summary>Mean luminance at and above which a picture counts as light.</summary>
    public const double LightLuminance = 0.85;

    /// <summary>Untranslated text dims over this time once its places are known.</summary>
    public const double PendingFadeMs = 120;

    /// <summary>How strongly a block's own background covers its untranslated text: the text stays at about 0.7.</summary>
    public const double PendingCover = 0.3;

    /// <summary>One block's change.</summary>
    public const double BlockMs = 200;

    /// <summary>The soft edge of an ordinary block, as a share of its width.</summary>
    public const double Band = 0.18;

    /// <summary>The soft edge of a block of several lines or one wider than <see cref="WideShare"/> of the picture.</summary>
    public const double NarrowBand = 0.07;

    /// <summary>A block wider than this share of the picture is wide.</summary>
    public const double WideShare = 0.6;

    /// <summary>Translated text rises this many DIPs into place.</summary>
    public const double RiseDip = 2;

    /// <summary>The usual start-to-start gap between blocks that are ready together.</summary>
    public const double StaggerMs = 45;

    /// <summary>The gap never goes below this.</summary>
    public const double StaggerMinimumMs = 12;

    /// <summary>A long backlog shares this time between its blocks.</summary>
    public const double StaggerBudgetMs = 500;

    /// <summary>The outline brightens and returns over this time (Deliberate).</summary>
    public const double GlowMs = 250;

    /// <summary>Back to the source: one fade (Fast).</summary>
    public const double RevertMs = 83;

    /// <summary>A translation shown again, or a document pin's whole change (Normal).</summary>
    public const double ReshowMs = 167;

    /// <summary>Ease-out cubic over 0..1, clamped.</summary>
    public static double EaseOutCubic(double x)
    {
        double c = Clamp01(x);
        double inverse = 1 - c;
        return 1 - (inverse * inverse * inverse);
    }

    /// <summary>Ease-in-out (half a cosine) over 0..1, clamped.</summary>
    public static double EaseInOut(double x) => 0.5 - (0.5 * Math.Cos(Math.PI * Clamp01(x)));

    /// <summary>The sweep's strongest opacity for a picture of this mean luminance (0 black, 1 white).</summary>
    public static double SweepOpacity(double meanLuminance)
    {
        if (!double.IsFinite(meanLuminance)) return SweepDarkOpacity;
        double share = Clamp01((meanLuminance - DarkLuminance) / (LightLuminance - DarkLuminance));
        return SweepDarkOpacity + ((SweepLightOpacity - SweepDarkOpacity) * share);
    }

    /// <summary>The sweep <paramref name="elapsedMs"/> after the press, or null before it starts and after it leaves.</summary>
    public static TranslationSweepFrame? Sweep(double elapsedMs, double opacity)
    {
        if (!(elapsedMs > 0) || elapsedMs >= SweepMs) return null;
        double eased = EaseInOut(elapsedMs / SweepMs);
        return new(-SweepWidth + ((1 + SweepWidth) * eased), SweepWidth, opacity);
    }

    /// <summary>How strongly an untranslated block is covered <paramref name="sinceKnownMs"/> after its place was known.</summary>
    public static double PendingCoverAt(double sinceKnownMs) =>
        sinceKnownMs < 0 ? 0 : PendingCover * EaseOutCubic(sinceKnownMs / PendingFadeMs);

    /// <summary>The soft edge for a block of <paramref name="lineCount"/> lines and its width against the picture's.</summary>
    public static double BandFor(int lineCount, double blockWidth, double pictureWidth) =>
        lineCount > 1 || (pictureWidth > 0 && blockWidth > pictureWidth * WideShare) ? NarrowBand : Band;

    /// <summary>A block <paramref name="sinceStartMs"/> after its change started.</summary>
    public static TranslationBlockFrame Block(double sinceStartMs, double band)
    {
        if (sinceStartMs >= BlockMs) return new(1, 1 + band, band, 0, true);
        if (sinceStartMs <= 0) return new(0, 0, band, RiseDip, false);
        double progress = sinceStartMs / BlockMs;
        return new(progress, progress * (1 + band), band, RiseDip * (1 - EaseOutCubic(progress)), false);
    }

    /// <summary>The share of accent the outline takes <paramref name="sinceFinishMs"/> after the last block, 0 outside the glow.</summary>
    public static double Glow(double sinceFinishMs) =>
        sinceFinishMs < 0 || sinceFinishMs >= GlowMs ? 0 : Math.Sin(Math.PI * sinceFinishMs / GlowMs);

    /// <summary>A linear fade's progress, 0 at its start and 1 at <paramref name="durationMs"/>.</summary>
    public static double Fade(double sinceStartMs, double durationMs) =>
        durationMs <= 0 ? 1 : Clamp01(sinceStartMs / durationMs);

    /// <summary>The start-to-start gap for <paramref name="backlog"/> blocks waiting together.</summary>
    public static double StaggerGap(int backlog) =>
        backlog <= 0 ? StaggerMs : Math.Max(StaggerMinimumMs, Math.Min(StaggerMs, StaggerBudgetMs / backlog));

    internal static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : double.IsNaN(value) ? 0 : value;
}

/// <summary>
/// When each block starts its change. Blocks are handed over as they are ready; those that have not started yet wait in
/// reading order (their index) and start one gap apart, the gap shrinking with the backlog so it never takes much longer
/// than <see cref="TranslationRevealMotion.StaggerBudgetMs"/> to start them all.
/// </summary>
public sealed class TranslationRevealSchedule
{
    private readonly SortedDictionary<int, double> _starts = new();
    private double _lastStarted = double.NegativeInfinity;

    /// <summary>The planned start of every block handed over so far.</summary>
    public IReadOnlyDictionary<int, double> Starts => _starts;

    /// <summary>The latest end among the planned blocks, or null before any.</summary>
    public double? LastEnd => _starts.Count == 0 ? null : _starts.Values.Max() + TranslationRevealMotion.BlockMs;

    /// <summary>Hands over blocks that are ready at <paramref name="nowMs"/>; a block handed over before keeps its plan once started.</summary>
    public void Ready(IEnumerable<int> blocks, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        foreach (KeyValuePair<int, double> started in _starts)
            if (started.Value <= nowMs && started.Value > _lastStarted) _lastStarted = started.Value;
        var waiting = new SortedSet<int>(_starts.Where(item => item.Value > nowMs).Select(item => item.Key));
        foreach (int block in blocks)
            if (!_starts.ContainsKey(block)) waiting.Add(block);
        if (waiting.Count == 0) return;
        double gap = TranslationRevealMotion.StaggerGap(waiting.Count);
        double previous = _lastStarted;
        foreach (int block in waiting)
        {
            double start = double.IsNegativeInfinity(previous) ? nowMs : Math.Max(nowMs, previous + gap);
            _starts[block] = start;
            previous = start;
        }
    }

    /// <summary>Whether <paramref name="block"/> has a plan.</summary>
    public bool Contains(int block) => _starts.ContainsKey(block);
}
