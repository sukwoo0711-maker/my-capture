using System.Globalization;
using System.Text;

namespace MyCapture.Ocr;

/// <summary>
/// The pure, WinRT-free decisions the OCR service makes: which language to use, how much to
/// scale the image, and how to normalise recognised text.
/// </summary>
/// <remarks>
/// Everything here is deterministic and free of <c>Windows.Media.Ocr</c> and imaging types, so
/// language selection/fallback, dimension capping and text/line normalisation are unit-tested
/// directly without invoking the OS engine. <see cref="WindowsOcrService"/> calls into this and
/// only adds the WinRT glue that cannot run headless.
/// </remarks>
public static class OcrPlanner
{
    private const double SameRowMinimumOverlap = 0.45;
    private const double WordCollisionMinimumOverlap = 0.55;

    /// <summary>The upscale range the engine honours for small UI text.</summary>
    public const double MinUpscale = 1.0;

    public const double MaxUpscale = 4.0;

    /// <summary>
    /// Chooses the first preferred BCP-47 tag the engine supports, matching case-insensitively
    /// and allowing a language-only preference ("en") to match a regional support tag ("en-US").
    /// </summary>
    /// <param name="preferred">Preferred tags in priority order.</param>
    /// <param name="supported">Tags the OS reports it can recognise.</param>
    /// <returns>
    /// The supported tag to use, or <see langword="null"/> when none match — the caller then
    /// falls back to the user-profile default.
    /// </returns>
    public static string? SelectLanguage(
        IReadOnlyList<string> preferred,
        IReadOnlyList<string> supported)
    {
        IReadOnlyList<string> selected = SelectLanguages(preferred, supported, maxLanguages: 1);
        return selected.Count == 0 ? null : selected[0];
    }

    /// <summary>
    /// Chooses a bounded set of preferred, supported OCR languages. Only one regional variant
    /// per language/script family is retained, avoiding redundant passes such as en-US plus
    /// en-GB while keeping materially different engines such as zh-Hans and zh-Hant.
    /// </summary>
    public static IReadOnlyList<string> SelectLanguages(
        IReadOnlyList<string> preferred,
        IReadOnlyList<string> supported,
        int maxLanguages = 2)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(supported);

        if (supported.Count == 0 || maxLanguages <= 0)
        {
            return [];
        }

        var selected = new List<string>(Math.Min(maxLanguages, supported.Count));
        var selectedFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string want in preferred)
        {
            if (string.IsNullOrWhiteSpace(want))
            {
                continue;
            }

            string wanted = want.Trim();
            string wantedPrimary = PrimaryLanguage(wanted);
            string wantedFamily = RecognitionFamily(wanted);
            if (selectedFamilies.Contains(wantedFamily))
            {
                continue;
            }

            string? match = supported.FirstOrDefault(
                have => !string.IsNullOrWhiteSpace(have) &&
                    string.Equals(have, wanted, StringComparison.OrdinalIgnoreCase));

            match ??= supported.FirstOrDefault(
                have => !string.IsNullOrWhiteSpace(have) &&
                    string.Equals(
                        RecognitionFamily(have),
                        wantedFamily,
                        StringComparison.OrdinalIgnoreCase));

            match ??= supported.FirstOrDefault(
                have => !string.IsNullOrWhiteSpace(have) &&
                    string.Equals(PrimaryLanguage(have), wantedPrimary, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                continue;
            }

            selected.Add(match);
            _ = selectedFamilies.Add(RecognitionFamily(match));
            if (selected.Count == maxLanguages)
            {
                break;
            }
        }

        return selected;
    }

    /// <summary>
    /// Clamps and caps the requested upscale so the prepared bitmap never exceeds the engine's
    /// maximum dimension, while preserving aspect ratio.
    /// </summary>
    /// <param name="pixelWidth">Source width in pixels.</param>
    /// <param name="pixelHeight">Source height in pixels.</param>
    /// <param name="requestedFactor">Caller's requested upscale (clamped to 1–4×).</param>
    /// <param name="maxDimension">The engine's <c>MaxImageDimension</c>.</param>
    /// <returns>
    /// The effective scale to apply. May be below 1.0 when the source itself already exceeds the
    /// maximum dimension and must be shrunk to be recognisable at all.
    /// </returns>
    public static double ResolveScale(
        int pixelWidth,
        int pixelHeight,
        double requestedFactor,
        int maxDimension)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || maxDimension <= 0)
        {
            return 1.0;
        }

        double clamped = Math.Clamp(requestedFactor, MinUpscale, MaxUpscale);

        int longEdge = Math.Max(pixelWidth, pixelHeight);

        // The cap that keeps long-edge * scale <= maxDimension.
        double cap = (double)maxDimension / longEdge;

        // Upscale up to the requested factor, but never past the dimension cap. When the source
        // already exceeds the maximum, cap is below 1 and we must downscale to fit.
        double scale = cap < 1.0 ? cap : Math.Min(clamped, cap);

        // Guard against a pathological zero.
        return scale <= 0 ? 1.0 : scale;
    }

    /// <summary>
    /// Raises the requested scale for small crops, where UI glyphs are most likely to fall below
    /// the Windows OCR engine's useful size, while retaining the engine dimension cap.
    /// </summary>
    public static double ResolveAdaptiveScale(
        int pixelWidth,
        int pixelHeight,
        double requestedFactor,
        int maxDimension)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || maxDimension <= 0)
        {
            return 1.0;
        }

        int shortEdge = Math.Min(pixelWidth, pixelHeight);
        int longEdge = Math.Max(pixelWidth, pixelHeight);
        double adaptiveMinimum = longEdge <= 640 || shortEdge <= 180
            ? 4.0
            : longEdge <= 1200 || shortEdge <= 320
                ? 3.0
                : MinUpscale;

        return ResolveScale(
            pixelWidth,
            pixelHeight,
            Math.Max(requestedFactor, adaptiveMinimum),
            maxDimension);
    }

    /// <summary>
    /// Normalises a recognised word's text: trims surrounding whitespace and collapses any
    /// internal runs (the OS occasionally emits stray spacing) to single spaces.
    /// </summary>
    public static string NormalizeWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return CollapseWhitespace(text.Trim());
    }

    /// <summary>
    /// Builds a line's text from its words, single-space joined, as Windows OCR reports words
    /// without their inter-word spacing.
    /// </summary>
    public static string BuildLineText(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var builder = new StringBuilder();
        foreach (string word in words)
        {
            string normalized = NormalizeWord(word);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                _ = builder.Append(' ');
            }

            _ = builder.Append(normalized);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Joins recognised lines into the final block text with a single newline between non-empty
    /// lines, dropping empty lines and trailing whitespace.
    /// </summary>
    public static string BuildBlockText(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var kept = new List<string>();
        string? previous = null;
        foreach (string line in lines)
        {
            if (line is null)
            {
                continue;
            }

            string normalized = NormalizeWord(line);
            if (normalized.Length > 0 &&
                !string.Equals(previous, normalized, StringComparison.Ordinal))
            {
                kept.Add(normalized);
                previous = normalized;
            }
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// Scores useful recognition content without rewarding repeated lines. The score is only
    /// used to compare OCR passes over the same source; it is not exposed as user confidence.
    /// </summary>
    public static int ScoreLines(IEnumerable<OcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        int score = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (OcrLine line in lines)
        {
            string text = NormalizeWord(line.Text);
            if (text.Length == 0 || !seen.Add(text))
            {
                continue;
            }

            int meaningfulCharacters = text.Count(char.IsLetterOrDigit);
            int words = line.Words.Count(word => NormalizeWord(word.Text).Length > 0);
            score += (meaningfulCharacters * 4) + (words * 2) + 1;
        }

        return score;
    }

    /// <summary>
    /// Merges a supplementary OCR pass into a primary pass by line position. Collocated words
    /// are de-duplicated, while non-overlapping Korean/Latin words on the same visual row are
    /// retained in left-to-right order.
    /// </summary>
    public static IReadOnlyList<OcrLine> MergeLines(
        IEnumerable<OcrLine> primary,
        IEnumerable<OcrLine> supplementary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(supplementary);

        var merged = new List<OcrLine>();

        foreach (OcrLine candidate in primary.Concat(supplementary))
        {
            string candidateText = NormalizeWord(candidate.Text);
            if (candidateText.Length == 0)
            {
                continue;
            }

            int exactDuplicate = merged.FindIndex(existing =>
                string.Equals(NormalizeWord(existing.Text), candidateText, StringComparison.Ordinal) &&
                ((!HasArea(existing.Bounds) && !HasArea(candidate.Bounds)) ||
                    BoundsOverlap(existing.Bounds, candidate.Bounds, 0.35)));
            if (exactDuplicate >= 0)
            {
                continue;
            }

            int rowIndex = merged.FindIndex(existing => IsSameVisualRow(existing.Bounds, candidate.Bounds));
            if (rowIndex < 0 || merged[rowIndex].Words.Count == 0 || candidate.Words.Count == 0)
            {
                merged.Add(candidate with { Text = candidateText });
                continue;
            }

            merged[rowIndex] = MergeRow(merged[rowIndex], candidate);
        }

        return merged
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();
    }

    private static string PrimaryLanguage(string tag)
    {
        int dash = tag.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? tag : tag[..dash];
    }

    private static string RecognitionFamily(string tag)
    {
        string[] parts = tag.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 &&
            parts[1].Length == 4 &&
            parts[1].All(char.IsAsciiLetter))
        {
            return string.Concat(parts[0], "-", parts[1]);
        }

        return parts.Length == 0 ? tag : parts[0];
    }

    private static OcrLine MergeRow(OcrLine primary, OcrLine supplementary)
    {
        var words = primary.Words
            .Where(word => NormalizeWord(word.Text).Length > 0)
            .Select(word => word with { Text = NormalizeWord(word.Text) })
            .ToList();

        foreach (OcrWord candidate in supplementary.Words)
        {
            string candidateText = NormalizeWord(candidate.Text);
            if (candidateText.Length == 0)
            {
                continue;
            }

            int collision = words.FindIndex(existing =>
                BoundsOverlap(existing.Bounds, candidate.Bounds, WordCollisionMinimumOverlap));
            if (collision < 0)
            {
                words.Add(candidate with { Text = candidateText });
                continue;
            }

            OcrWord existing = words[collision];
            if (string.Equals(existing.Text, candidateText, StringComparison.Ordinal))
            {
                continue;
            }

            // A longer run of letters/digits is usually the less-truncated interpretation.
            if (WordQuality(candidateText) > WordQuality(existing.Text))
            {
                words[collision] = candidate with { Text = candidateText };
            }
        }

        words = words
            .OrderBy(word => word.Bounds.X)
            .ThenBy(word => word.Bounds.Y)
            .ToList();

        OcrRect bounds = default;
        foreach (OcrWord word in words)
        {
            bounds = OcrRect.Union(bounds, word.Bounds);
        }

        return new OcrLine(BuildLineText(words.Select(word => word.Text)), bounds, words);
    }

    private static bool IsSameVisualRow(OcrRect first, OcrRect second)
    {
        if (!HasArea(first) || !HasArea(second))
        {
            return false;
        }

        double overlap = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y));
        double overlapRatio = overlap / Math.Min(first.Height, second.Height);
        if (overlapRatio < SameRowMinimumOverlap)
        {
            return false;
        }

        double horizontalGap = Math.Max(0, Math.Max(first.X, second.X) - Math.Min(first.Right, second.Right));
        return horizontalGap <= Math.Max(first.Height, second.Height) * 6.0;
    }

    private static bool BoundsOverlap(OcrRect first, OcrRect second, double minimumRatio)
    {
        if (!HasArea(first) || !HasArea(second))
        {
            return false;
        }

        double width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.X, second.X));
        double height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y));
        double intersection = width * height;
        double smallerArea = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return smallerArea > 0 && intersection / smallerArea >= minimumRatio;
    }

    private static bool HasArea(OcrRect rect) => rect.Width > 0 && rect.Height > 0;

    private static int WordQuality(string text) => text.Count(char.IsLetterOrDigit);

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool previousWasSpace = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace)
                {
                    _ = builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                _ = builder.Append(c);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }
}
