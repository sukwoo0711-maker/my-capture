using System.Text;
using System.Text.RegularExpressions;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Tests;

internal static class TranslationReferenceProtectedTokens
{
    private const string L = "(?<![A-Za-z0-9_])";
    private const string R = "(?![A-Za-z0-9_])";

    private static readonly Regex CodePatternRegex = new(@"`[^`]+`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex CodePattern() => CodePatternRegex;

    private static readonly Regex UrlPatternRegex = new(@"https?://[!-~-[\s'"")<>]]*[!-~-[\s'"")<>.,;:]]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex UrlPattern() => UrlPatternRegex;

    private static readonly Regex PathPatternRegex = new(@"(?<![A-Za-z])[A-Za-z]:\\[!-~-[\s'""<>]]*[!-~-[\s'""<>.,;:]]|%[A-Za-z_]+%(?:\\[!-~-[\s'""<>]]*[!-~-[\s'""<>.,;:]])?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex PathPattern() => PathPatternRegex;

    private static readonly Regex QuotedPatternRegex = new(@"'[A-Za-z_][A-Za-z0-9_.\-]*'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex QuotedPattern() => QuotedPatternRegex;

    private static readonly Regex CallPatternRegex = new(L + @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\(\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex CallPattern() => CallPatternRegex;

    private static readonly Regex FieldPatternRegex = new(L + @"[A-Za-z][A-Za-z0-9_]*\[\d+(?::\d+)?\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex FieldPattern() => FieldPatternRegex;

    private static readonly Regex HexPatternRegex = new(L + @"0[xX][0-9A-Fa-f]+(?:[_ ][0-9A-Fa-f]{4})*" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex HexPattern() => HexPatternRegex;

    private static readonly Regex BinaryPatternRegex = new(L + @"0[bB][01]+(?:_[01]+)*" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex BinaryPattern() => BinaryPatternRegex;

    private static readonly Regex FlagPatternRegex = new(@"(?<![A-Za-z0-9_-])--?[a-z][a-z0-9]*(?:-[a-z0-9]+)+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex FlagPattern() => FlagPatternRegex;

    private static readonly Regex IdPatternRegex = new(L + @"CVE-\d{4}-\d{4,}" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex IdPattern() => IdPatternRegex;

    private static readonly Regex KeyPatternRegex = new(L + @"(?:Ctrl|Shift|Alt|Win)(?:\+(?:Ctrl|Shift|Alt|Win|F\d{1,2}|[A-Z0-9]))+" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex KeyPattern() => KeyPatternRegex;

    private static readonly Regex DottedPatternRegex = new(L + @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+" + R + @"(?!\()", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex DottedPattern() => DottedPatternRegex;

    private static readonly Regex SnakePatternRegex = new(L + @"[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex SnakePattern() => SnakePatternRegex;

    private static readonly Regex CamelPatternRegex = new(L + @"(?=[A-Za-z0-9]*[a-z])(?:[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+|[a-z]+[A-Z][A-Za-z0-9]*|[A-Z][a-z0-9]*[A-Z]{2,}[A-Za-z0-9]*)" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex CamelPattern() => CamelPatternRegex;

    private static readonly Regex AcronymPatternRegex = new(L + @"[A-Z][A-Z0-9]+" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex AcronymPattern() => AcronymPatternRegex;

    private static readonly Regex AlphanumericPatternRegex = new(@"(?<![A-Za-z0-9_.])\d+[A-Za-z][A-Za-z0-9]*" + R, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex AlphanumericPattern() => AlphanumericPatternRegex;

    private static readonly Regex VersionPatternRegex = new(@"(?<![A-Za-z0-9_.])(?:v\d+(?:\.\d+)+|\d+\.\d+\.\d+(?:\.\d+)?)(?![A-Za-z0-9_]|\.\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex VersionPattern() => VersionPatternRegex;

    private static readonly Regex RangePatternRegex = new(@"(?<![A-Za-z0-9_:\[])\d+:\d+(?![A-Za-z0-9_\]:])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex RangePattern() => RangePatternRegex;

    private static readonly Regex NumberPatternRegex = new(@"(?<![A-Za-z0-9_.])[-+±]?\d+(?:[.,]\d+)?(?:\s?(?:%|°C|°F|[kMG]?Hz|MSPS|ksps|Mbit/s|Mbps|kbps|[kKMGT]B|dBm|ppm|[mµunp]?[AVFW]|[mµun]?s|[kM]?Ω)(?![A-Za-z0-9_]))?(?![A-Za-z0-9_]|[.,]\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex NumberPattern() => NumberPatternRegex;

    private static readonly Regex DigitsRegex = new(@"[-+±]?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex Digits() => DigitsRegex;

    private static readonly Regex BacktickedRegex = new(@"`([^`\s][^`]*)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex Backticked() => BacktickedRegex;

    private static readonly Regex NumberValueRegex = new(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex NumberValue() => NumberValueRegex;

    private static readonly Regex PartlyTranslatedRegex = new(@"[^\s\x00-\x7F]+(?<tail>[A-Za-z0-9_]{4,})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex PartlyTranslated() => PartlyTranslatedRegex;

    private static readonly Regex OutputTokenRegex = new(@"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9_]{2,}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex OutputToken() => OutputTokenRegex;

    private static readonly Regex StrayNumberRegex = new(@"(?<![0-9.,A-Za-z_])\d+(?:[.,]\d+)?(?![0-9])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex StrayNumber() => StrayNumberRegex;

    // Earlier entries win when two candidates start at the same place with the same length.
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("code", CodePattern()),
        ("url", UrlPattern()),
        ("path", PathPattern()),
        ("quoted", QuotedPattern()),
        ("call", CallPattern()),
        ("field", FieldPattern()),
        ("hex", HexPattern()),
        ("bin", BinaryPattern()),
        ("flag", FlagPattern()),
        ("id", IdPattern()),
        ("key", KeyPattern()),
        ("dotted", DottedPattern()),
        ("ident", SnakePattern()),
        ("ident", CamelPattern()),
        ("acro", AcronymPattern()),
        ("alnum", AlphanumericPattern()),
        ("version", VersionPattern()),
        ("range", RangePattern()),
        ("num", NumberPattern()),
    ];

    internal static IReadOnlyList<ProtectedSpan> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var candidates = new List<(ProtectedSpan Span, int Priority)>();
        for (int priority = 0; priority < Patterns.Length; priority++)
        {
            foreach (Match match in Patterns[priority].Pattern.Matches(text))
                candidates.Add((new ProtectedSpan(match.Index, match.Length, match.Value, Patterns[priority].Kind), priority));
        }

        var chosen = new List<ProtectedSpan>();
        int end = 0;
        foreach ((ProtectedSpan span, _) in candidates
            .OrderBy(candidate => candidate.Span.Start)
            .ThenByDescending(candidate => candidate.Span.Length)
            .ThenBy(candidate => candidate.Priority))
        {
            if (span.Start < end) continue;
            chosen.Add(span);
            end = span.Start + span.Length;
        }
        return chosen;
    }

    internal static bool HasTranslatableWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rest = new StringBuilder(text);
        foreach (ProtectedSpan span in Find(text).OrderByDescending(span => span.Start))
            rest.Remove(span.Start, span.Length);
        return EnglishWord().IsMatch(rest.ToString());
    }

    private static readonly Regex EnglishWordRegex = new(@"[A-Za-z]{3,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Regex EnglishWord() => EnglishWordRegex;

}
