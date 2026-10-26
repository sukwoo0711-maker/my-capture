using System.Text.RegularExpressions;

namespace MyCapture.App.Pinning;

// Text tests shared by the prose, JSON, code and table planners.
internal static partial class PinTranslationPlanner
{
    /// <summary>
    /// Replaces URLs, e-mails, paths, inline code, placeholders and identifiers with spaces of the same
    /// length, so offsets stay valid while script statistics and word runs ignore them.
    /// </summary>
    internal static string MaskProtected(string text)
    {
        if (text.Length == 0) return text;
        char[]? chars = null;
        try
        {
            foreach (Match match in ProtectedSpan().Matches(text))
            {
                chars ??= text.ToCharArray();
                chars.AsSpan(match.Index, match.Length).Fill(' ');
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological input: nothing is masked rather than failing the language decision.
        }
        return chars is null ? text : new string(chars);
    }

    /// <summary>True when the text, ignoring protected spans, has real words in the direction's source language.</summary>
    internal static bool IsTranslatableText(string text, PinTranslationDirection direction) =>
        PinScript.IsTranslatable(MaskProtected(text), direction);

    /// <summary>
    /// A value or cell that is data rather than prose: number, date, time, currency, percent, identifier-like
    /// single token, code statement. URLs, e-mails, paths and placeholders are removed by <see cref="MaskProtected"/>.
    /// </summary>
    internal static bool LooksLikeData(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        if (DataValue().IsMatch(trimmed)) return true;
        if (LooksLikeCodeLine(trimmed)) return true;
        if (!trimmed.Any(char.IsWhiteSpace) && IdentifierToken().IsMatch(trimmed)) return true;
        return false;
    }

    /// <summary>A line of statement syntax (declaration, call, assignment, braces) that must not be translated.</summary>
    internal static bool LooksLikeCodeLine(string text) => CodeLine().IsMatch(text);

    /// <summary>Shares of Hangul among all letters of the text, after masking protected spans.</summary>
    private static double HangulShare(string masked)
    {
        ScriptCounts counts = PinScript.Count(masked);
        return counts.Letters == 0 ? 0 : (double)counts.Hangul / counts.Letters;
    }

    [GeneratedRegex(
        @"(?:\b(?:https?|ftp|file)://|\bwww\.|\bmailto:)[^\s<>""]*[^\s<>"".,;:!?)\]]"
        + @"|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}"
        + @"|(?<![\w/.:\\])(?:[A-Za-z]:[\\/]|\\\\|~/|\.{1,2}/|/)[^\s<>""|?*]+"
        + @"|`[^`\r\n]+`"
        + @"|\{\{[^{}\r\n]*\}\}|\$\{[^}\r\n]*\}|\{[A-Za-z0-9_.:,\- ]{0,40}\}|%(?:\d+\$)?[sdifcuxXo@]|%\([A-Za-z_]\w*\)[sdif]"
        + @"|\b[A-Za-z0-9]+(?:_[A-Za-z0-9]+)+\b"
        + @"|\b[a-z]+(?:[A-Z][a-z0-9]*)+\b|\b(?:[A-Z][a-z0-9]+){2,}\b"
        + @"|\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+(?:\(\))?|\b\w+\(\)"
        + @"|\bv?\d+(?:\.\d+){1,}(?:[-+][\w.]+)?\b|\b0x[0-9a-fA-F]+\b"
        + @"|\b[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\b"
        + @"|\S{100,}",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProtectedSpan();

    [GeneratedRegex(
        @"^(?:[-+]?\d[\d.,\s]*"
        + @"|[$€£¥₩]\s*\d[\d.,]*[kKmMbB]?|\d[\d.,]*\s*(?:[$€£¥₩%]|USD|EUR|KRW|GBP|JPY|CNY)"
        + @"|\d{4}[-/.]\d{1,2}[-/.]\d{1,2}(?:[T ]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?)?"
        + @"|\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4}"
        + @"|\d{1,2}:\d{2}(?::\d{2})?\s*(?:[AaPp]\.?[Mm]\.?)?"
        + @"|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s*\d{4})?"
        + @"|\d{1,2}(?:st|nd|rd|th)?\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?(?:,?\s*\d{4})?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DataValue();

    // One token that is an identifier, id, hash, constant or kebab-case name rather than a word.
    [GeneratedRegex(
        @"^(?:[A-Z0-9]{2,}(?:[-_][A-Z0-9]+)*|[A-Za-z]+\d[A-Za-z0-9]*|\d+[A-Za-z][A-Za-z0-9]*|[a-z0-9]+(?:-[a-z0-9]+)+|[0-9a-fA-F]{8,})$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex IdentifierToken();

    [GeneratedRegex(
        @"^\s*(?:(?:using|import|from|namespace|class|struct|interface|public|private|protected|internal|const|let|var|def|function|return|package|fn|func)\b[^\r\n]*[;{}=]"
        + @"|(?:def|class)\s+\w+[^\r\n]*:"
        + @"|[\w.$]+\([^\r\n]*\)\s*;?"
        + @"|[\w.$\[\]]+\s*(?:=>|:=|[-+*/%&|^]?=(?!=))[^\r\n]*"
        + @"|[\w<>\[\].?*&:,$]+(?:\s+[\w<>\[\].?*&:$]+)?\s*[-+*/%&|^]?=[^\r\n]*;"
        + @"|(?:if|for|while|foreach|switch|catch)\s*\([^\r\n]*[;{}]"
        + @"|[{}\[\],;]+)\s*$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CodeLine();
}
