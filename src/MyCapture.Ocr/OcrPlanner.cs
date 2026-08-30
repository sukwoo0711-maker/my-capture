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
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(supported);

        if (supported.Count == 0)
        {
            return null;
        }

        foreach (string want in preferred)
        {
            if (string.IsNullOrWhiteSpace(want))
            {
                continue;
            }

            string wantTrimmed = want.Trim();

            // Exact (case-insensitive) match wins.
            foreach (string have in supported)
            {
                if (string.Equals(have, wantTrimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return have;
                }
            }

            // Language-only preference matches any regional variant, e.g. "en" -> "en-US".
            string wantPrimary = PrimaryLanguage(wantTrimmed);
            foreach (string have in supported)
            {
                if (string.Equals(PrimaryLanguage(have), wantPrimary, StringComparison.OrdinalIgnoreCase))
                {
                    return have;
                }
            }
        }

        return null;
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
        foreach (string line in lines)
        {
            if (line is null)
            {
                continue;
            }

            string trimmed = line.TrimEnd();
            if (trimmed.Length > 0)
            {
                kept.Add(trimmed);
            }
        }

        return string.Join('\n', kept);
    }

    private static string PrimaryLanguage(string tag)
    {
        int dash = tag.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? tag : tag[..dash];
    }

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
