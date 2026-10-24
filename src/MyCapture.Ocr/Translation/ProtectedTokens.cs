using System.Text;
using System.Text.RegularExpressions;

namespace MyCapture.Ocr.Translation;

/// <summary>A source span the translation must keep unchanged: identifier, number, path, code.</summary>
internal readonly record struct ProtectedSpan(int Start, int Length, string Text, string Kind);

/// <summary>How a protected token appears in a translation that does not contain it verbatim.</summary>
internal enum ProtectedTokenChange
{
    Spacing,
    Unit,
    RangeNotation,
    DottedSplit,
    Case,
    Backticks,
    MissingOrChanged,
    NotInSource,
}

internal sealed record ProtectedTokenIssue(string Kind, string Token, ProtectedTokenChange Change);

/// <summary>
/// Format changes are harmless (spacing, case, unit spelling); altered and invented tokens are
/// values the reader must check against the original.
/// </summary>
internal sealed record ProtectedTokenCheck(
    int Checked,
    int Preserved,
    IReadOnlyList<ProtectedTokenIssue> FormatChanged,
    IReadOnlyList<ProtectedTokenIssue> Altered,
    IReadOnlyList<ProtectedTokenIssue> Invented);

/// <summary>
/// Do-not-translate spans for developer text: identifiers, hex/binary literals, numbers with
/// units, code tokens, paths, flags and versions. Translation runs on the unmasked text and
/// <see cref="Repair"/> puts reformatted or slightly misspelled tokens back afterwards; masking
/// tokens with placeholders such as {0} changed the meaning of OPUS-MT output in the probe.
/// Boundaries are ASCII-only so a Korean particle right next to a token does not hide it.
/// </summary>
internal static partial class ProtectedTokens
{
    private const string L = "(?<![A-Za-z0-9_])";
    private const string R = "(?![A-Za-z0-9_])";

    [GeneratedRegex(@"`[^`]+`", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"https?://[!-~-[\s'"")<>]]*[!-~-[\s'"")<>.,;:]]", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?<![A-Za-z])[A-Za-z]:\\[!-~-[\s'""<>]]*[!-~-[\s'""<>.,;:]]|%[A-Za-z_]+%(?:\\[!-~-[\s'""<>]]*[!-~-[\s'""<>.,;:]])?", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"'[A-Za-z_][A-Za-z0-9_.\-]*'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(L + @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\(\)", RegexOptions.CultureInvariant)]
    private static partial Regex CallPattern();

    [GeneratedRegex(L + @"[A-Za-z][A-Za-z0-9_]*\[\d+(?::\d+)?\]", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(L + @"0[xX][0-9A-Fa-f]+(?:[_ ][0-9A-Fa-f]{4})*" + R, RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    [GeneratedRegex(L + @"0[bB][01]+(?:_[01]+)*" + R, RegexOptions.CultureInvariant)]
    private static partial Regex BinaryPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])--?[a-z][a-z0-9]*(?:-[a-z0-9]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex FlagPattern();

    [GeneratedRegex(L + @"CVE-\d{4}-\d{4,}" + R, RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(L + @"(?:Ctrl|Shift|Alt|Win)(?:\+(?:Ctrl|Shift|Alt|Win|F\d{1,2}|[A-Z0-9]))+" + R, RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(L + @"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+" + R + @"(?!\()", RegexOptions.CultureInvariant)]
    private static partial Regex DottedPattern();

    [GeneratedRegex(L + @"[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+" + R, RegexOptions.CultureInvariant)]
    private static partial Regex SnakePattern();

    [GeneratedRegex(L + @"(?=[A-Za-z0-9]*[a-z])(?:[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+|[a-z]+[A-Z][A-Za-z0-9]*|[A-Z][a-z0-9]*[A-Z]{2,}[A-Za-z0-9]*)" + R, RegexOptions.CultureInvariant)]
    private static partial Regex CamelPattern();

    [GeneratedRegex(L + @"[A-Z][A-Z0-9]+" + R, RegexOptions.CultureInvariant)]
    private static partial Regex AcronymPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])\d+[A-Za-z][A-Za-z0-9]*" + R, RegexOptions.CultureInvariant)]
    private static partial Regex AlphanumericPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(?:v\d+(?:\.\d+)+|\d+\.\d+\.\d+(?:\.\d+)?)(?![A-Za-z0-9_]|\.\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_:\[])\d+:\d+(?![A-Za-z0-9_\]:])", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])[-+±]?\d+(?:[.,]\d+)?(?:\s?(?:%|°C|°F|[kMG]?Hz|MSPS|ksps|Mbit/s|Mbps|kbps|[kKMGT]B|dBm|ppm|[mµunp]?[AVFW]|[mµun]?s|[kM]?Ω)(?![A-Za-z0-9_]))?(?![A-Za-z0-9_]|[.,]\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"[-+±]?\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"`([^`\s][^`]*)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberValue();

    [GeneratedRegex(@"[^\s\x00-\x7F]+(?<tail>[A-Za-z0-9_]{4,})(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex PartlyTranslated();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9_]{2,}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex OutputToken();

    [GeneratedRegex(@"(?<![0-9.,A-Za-z_])\d+(?:[.,]\d+)?(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex StrayNumber();

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

    /// <summary>
    /// Every protected token of the source must appear unchanged in the output. Tokens found
    /// only after removing whitespace or ignoring case are format changes; protected-looking
    /// tokens in the output that the source does not contain are invented.
    /// </summary>
    internal static ProtectedTokenCheck Check(string source, string output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        IReadOnlyList<ProtectedSpan> spans = Find(source);
        var formatChanged = new List<ProtectedTokenIssue>();
        var altered = new List<ProtectedTokenIssue>();
        var invented = new List<ProtectedTokenIssue>();
        int preserved = 0;
        string squashedOutput = Squash(output);
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ProtectedSpan span in spans)
        {
            used.TryGetValue(span.Text, out int already);
            used[span.Text] = already + 1;
            if (CountOccurrences(output, span.Text) > already)
            {
                preserved++;
                continue;
            }

            string squashed = Squash(span.Text);
            if (squashed.Length > 0 && squashedOutput.Contains(squashed, StringComparison.Ordinal))
            {
                formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.Spacing));
                continue;
            }

            if (span.Kind == "num")
            {
                string number = Digits().Match(span.Text).Value;
                if (number.Length > 0 && Regex.IsMatch(output,
                        @"(?<![0-9.])" + Regex.Escape(number.TrimStart('+')) + @"(?![0-9])", RegexOptions.CultureInvariant))
                {
                    formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.Unit));
                    continue;
                }
            }

            if (span.Kind == "range")
            {
                string[] ends = span.Text.Split(':');
                if (Regex.IsMatch(output, @"(?<![0-9])" + ends[0] + @"\s*[~\-–]\s*" + ends[1] + @"(?![0-9])",
                        RegexOptions.CultureInvariant))
                {
                    formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.RangeNotation));
                    continue;
                }
            }

            if (span.Kind == "dotted" && span.Text.Split('.').All(part => CountOccurrences(output, part) > 0))
            {
                formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.DottedSplit));
                continue;
            }

            if (span.Kind is "acro" or "ident" or "dotted"
                && output.Contains(span.Text, StringComparison.OrdinalIgnoreCase))
            {
                formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.Case));
                continue;
            }

            altered.Add(new(span.Kind, span.Text, ProtectedTokenChange.MissingOrChanged));
        }

        string squashedSource = Squash(source);
        foreach (ProtectedSpan span in Find(output))
        {
            bool inSource;
            if (span.Kind == "num")
            {
                string number = Digits().Match(span.Text).Value.TrimStart('+');
                inSource = number.Length == 0 || Regex.IsMatch(source,
                    @"(?<![0-9.,])" + Regex.Escape(number) + @"(?![0-9]|[.,][0-9])", RegexOptions.CultureInvariant);
            }
            else
            {
                inSource = CountOccurrences(source, span.Text) > 0
                    || squashedSource.Contains(Squash(span.Text), StringComparison.Ordinal);
            }

            if (inSource) continue;
            if (span.Kind == "code" && CountOccurrences(source, span.Text.Trim('`')) > 0)
            {
                formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.Backticks));
                continue;
            }

            if (source.Contains(span.Text, StringComparison.OrdinalIgnoreCase))
            {
                formatChanged.Add(new(span.Kind, span.Text, ProtectedTokenChange.Case));
                continue;
            }

            invented.Add(new(span.Kind, span.Text, ProtectedTokenChange.NotInSource));
        }

        return new(spans.Count, preserved, formatChanged, altered, invented);
    }

    /// <summary>
    /// Post-edit without masking: puts back protected source tokens the engine reformatted
    /// (spacing, µ/μ, case) or slightly misspelled (one or two edits on an identifier), and
    /// leaves everything else alone. Returns the repaired text and the number of repairs.
    /// </summary>
    internal static (string Text, int Repairs) Repair(string source, string output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        string text = output;
        int repairs = 0;
        if (!source.Contains('\\') && text.Contains("\\_", StringComparison.Ordinal))
        {
            text = text.Replace("\\_", "_", StringComparison.Ordinal);
            repairs++;
        }

        if (!source.Contains('`'))
        {
            string unticked = Backticked().Replace(text, "$1");
            if (!string.Equals(unticked, text, StringComparison.Ordinal))
            {
                text = unticked;
                repairs++;
            }
        }

        var missingNumbers = new List<string>();
        foreach (ProtectedSpan span in Find(source))
        {
            if (CountOccurrences(text, span.Text) > 0) continue;

            // Same characters with different spacing, micro sign or case.
            var pattern = new StringBuilder();
            foreach (char c in span.Text)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (pattern.Length > 0) pattern.Append(@"\s*");
                pattern.Append(c is 'µ' or 'μ' ? "[µμu]" : Regex.Escape(c.ToString()));
            }

            Match loose = Regex.Match(text, @"(?<![A-Za-z0-9_])" + pattern + @"(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (loose.Success)
            {
                text = text.Remove(loose.Index, loose.Length).Insert(loose.Index, span.Text);
                repairs++;
                continue;
            }

            if (span.Kind == "num")
            {
                // Same value and unit with a different sign or spacing, e.g. "~ 1 %" for "±1%".
                Match value = NumberValue().Match(span.Text);
                string unit = span.Text[(value.Index + value.Length)..].Trim();
                string numberPattern = @"(?<![0-9.,])[-+±~≈]?\s*" + Regex.Escape(value.Value)
                    + (unit.Length > 0 ? @"\s*" + Regex.Escape(unit) : "") + @"(?![0-9])";
                Match numberMatch = Regex.Match(text, numberPattern, RegexOptions.CultureInvariant);
                if (numberMatch.Success && !string.Equals(span.Text, value.Value, StringComparison.Ordinal))
                {
                    text = text.Remove(numberMatch.Index, numberMatch.Length).Insert(numberMatch.Index, span.Text);
                    repairs++;
                    continue;
                }

                if (!numberMatch.Success) missingNumbers.Add(span.Text);
                continue;
            }

            // An identifier translated in part, e.g. "시간outError" for "TimeoutError".
            if (span.Kind is "ident" or "acro" or "dotted" && span.Text.Length >= 6)
            {
                Match partial = PartlyTranslated().Match(text);
                if (partial.Success && span.Text.EndsWith(partial.Groups["tail"].Value, StringComparison.Ordinal))
                {
                    text = text.Remove(partial.Index, partial.Length).Insert(partial.Index, span.Text);
                    repairs++;
                    continue;
                }
            }

            // A misspelled identifier: same shape, small edit distance, not present in the source.
            if (span.Kind is "acro" or "ident" or "dotted" or "field" or "call" && span.Text.Length >= 3)
            {
                Match? best = null;
                int bestDistance = int.MaxValue;
                foreach (Match candidate in OutputToken().Matches(text))
                {
                    if (CountOccurrences(source, candidate.Value) > 0) continue;
                    int distance = Levenshtein(candidate.Value, span.Text);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }

                if (best is not null && bestDistance <= Math.Max(1, span.Text.Length / 4))
                {
                    text = text.Remove(best.Index, best.Length).Insert(best.Index, span.Text);
                    repairs++;
                }
            }
        }

        // One number missing and one unknown number of similar shape: treat it as the same value.
        if (missingNumbers.Count == 1)
        {
            var strays = StrayNumber().Matches(text)
                .Where(match => !Regex.IsMatch(source, @"(?<![0-9.,])" + Regex.Escape(match.Value) + @"(?![0-9]|[.,][0-9])",
                    RegexOptions.CultureInvariant))
                .ToList();
            string wanted = NumberValue().Match(missingNumbers[0]).Value;
            if (strays.Count == 1 && Levenshtein(strays[0].Value, wanted) <= 2)
            {
                text = text.Remove(strays[0].Index, strays[0].Length).Insert(strays[0].Index, wanted);
                repairs++;
            }
        }

        return (text, repairs);
    }

    /// <summary>
    /// True when the text has English words to translate once identifiers, numbers and access
    /// codes such as "rw" are set aside; cells made only of those stay as they are.
    /// </summary>
    internal static bool HasTranslatableWords(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rest = new StringBuilder(text);
        foreach (ProtectedSpan span in Find(text).OrderByDescending(span => span.Start))
            rest.Remove(span.Start, span.Length);
        return EnglishWord().IsMatch(rest.ToString());
    }

    [GeneratedRegex(@"[A-Za-z]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishWord();

    private static string Squash(string value) => new([.. value.Where(c => !char.IsWhiteSpace(c))]);

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = index == 0 || !IsAsciiWord(haystack[index - 1]) || !IsAsciiWord(needle[0]);
            int after = index + needle.Length;
            bool rightOk = after >= haystack.Length || !IsAsciiWord(haystack[after]) || !IsAsciiWord(needle[^1]);
            if (leftOk && rightOk) count++;
            index += needle.Length;
        }
        return count;
    }

    private static bool IsAsciiWord(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
}
