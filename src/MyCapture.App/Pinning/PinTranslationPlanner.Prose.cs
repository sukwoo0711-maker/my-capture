using System.Text;
using System.Text.RegularExpressions;

namespace MyCapture.App.Pinning;

// Prose: PDF/Edge/Word copies, notes and mixed text.
internal static partial class PinTranslationPlanner
{
    // Hard-wrapped lines must be at least this long (trimmed) to count as a wrapped paragraph; poems and addresses are shorter.
    private const int MinWrappedLineLength = 35;
    private const double WrappedLineRatio = 0.7;

    private static PinTranslationPlan CreateProse(string source, PinTranslationDirection direction)
    {
        var builder = new PlanBuilder(source);
        List<LineSpan> lines = SplitLines(source);
        int index = 0;
        while (index < lines.Count)
        {
            if (IsBlank(source, lines[index])) { index++; continue; }
            int end = index;
            while (end < lines.Count && !IsBlank(source, lines[end])) end++;
            AddParagraphRun(builder, source, lines, index, end, direction);
            index = end;
        }
        return new PinTranslationPlan(PinTextFormat.Prose, builder.Finish());
    }

    /// <summary>One run of consecutive non-blank lines: footer lines split it or are bridged, then each group is joined or translated per line.</summary>
    private static void AddParagraphRun(PlanBuilder builder, string source, List<LineSpan> lines, int from, int to, PinTranslationDirection direction)
    {
        var group = new List<int>();
        for (int index = from; index < to; index++)
        {
            if (!IsFooterLine(source, lines[index]))
            {
                group.Add(index);
                continue;
            }
            if (group.Count == 0) continue;
            int next = index + 1;
            while (next < to && IsFooterLine(source, lines[next])) next++;
            if (next < to && Continues(source, lines[group[^1]], lines[next])) continue;
            AddGroup(builder, source, lines, group, direction);
            group.Clear();
        }
        if (group.Count > 0) AddGroup(builder, source, lines, group, direction);
    }

    /// <summary>The paragraph clearly goes on after a footer when the next text starts with a lowercase letter.</summary>
    private static bool Continues(string source, LineSpan previous, LineSpan next)
    {
        (int Start, int End)? previousText = TrimSpan(source, previous.Start, previous.End);
        (int Start, int End)? nextText = TrimSpan(source, next.Start, next.End);
        return previousText is not null && nextText is not null && char.IsLower(source[nextText.Value.Start]);
    }

    private static void AddGroup(PlanBuilder builder, string source, List<LineSpan> lines, List<int> group, PinTranslationDirection direction)
    {
        if (group.Count >= 2 && TryAddWrapped(builder, source, lines, group, direction)) return;
        int index = 0;
        while (index < group.Count)
        {
            // A list item whose text goes on in the next lines (no marker, lowercase start, no sentence end) is one piece.
            int end = index + 1;
            if (HasListMarker(source, lines[group[index]]))
            {
                while (end < group.Count && ContinuesListItem(source, lines[group[end - 1]], lines[group[end]])) end++;
            }
            if (end - index < 2 || !TryAddListItem(builder, source, lines, group, index, end, direction))
            {
                for (int line = index; line < end; line++) AddLine(builder, source, lines[group[line]], direction);
            }
            index = end;
        }
    }

    private static bool HasListMarker(string source, LineSpan line)
    {
        (int Start, int End)? text = TrimSpan(source, line.Start, line.End);
        return text is not null && ListMarker().IsMatch(source[text.Value.Start..text.Value.End]);
    }

    private static bool ContinuesListItem(string source, LineSpan previous, LineSpan next)
    {
        (int Start, int End)? previousText = TrimSpan(source, previous.Start, previous.End);
        (int Start, int End)? nextText = TrimSpan(source, next.Start, next.End);
        if (previousText is null || nextText is null) return false;
        return char.IsLower(source[nextText.Value.Start])
            && source[previousText.Value.End - 1] is not ('.' or '!' or '?' or ':' or ';' or '。')
            && !ListMarker().IsMatch(source[nextText.Value.Start..nextText.Value.End]);
    }

    private static bool TryAddListItem(PlanBuilder builder, string source, List<LineSpan> lines, List<int> group, int from, int to, PinTranslationDirection direction)
    {
        var texts = new List<(int Start, int End)>(to - from);
        for (int index = from; index < to; index++)
        {
            (int Start, int End)? text = TrimSpan(source, lines[group[index]].Start, lines[group[index]].End);
            if (text is null) return false;
            texts.Add(text.Value);
        }
        Match marker = ListMarker().Match(source[texts[0].Start..texts[0].End]);
        texts[0] = (texts[0].Start + marker.Length, texts[0].End);
        return TryAddJoined(builder, source, texts, direction);
    }

    private static bool TryAddWrapped(PlanBuilder builder, string source, List<LineSpan> lines, List<int> group, PinTranslationDirection direction)
    {
        var texts = new List<(int Start, int End)>(group.Count);
        foreach (int index in group)
        {
            (int Start, int End)? text = TrimSpan(source, lines[index].Start, lines[index].End);
            if (text is null) return false;
            if (ListMarker().IsMatch(source[text.Value.Start..text.Value.End])) return false;
            texts.Add(text.Value);
        }
        return LooksWrapped(source, texts) && TryAddJoined(builder, source, texts, direction);
    }

    /// <summary>Adds one piece for lines that are one sentence run; its Content is the joined text. False when it is not translatable text.</summary>
    private static bool TryAddJoined(PlanBuilder builder, string source, List<(int Start, int End)> texts, PinTranslationDirection direction)
    {
        string joined = JoinWrapped(source, texts);
        string masked = MaskProtected(joined);
        if (LooksLikeCodeLine(joined) || !PinScript.IsTranslatable(masked, direction)) return false;
        if (direction == PinTranslationDirection.EnglishToKorean && HangulShare(masked) >= MixedShare) return false;
        builder.Piece(texts[0].Start, texts[^1].End, PlanKind.Prose, joined);
        return true;
    }

    /// <summary>At least 2/3 of the non-last lines reach 70% of the longest line and most of them do not end a sentence.</summary>
    private static bool LooksWrapped(string source, List<(int Start, int End)> texts)
    {
        int longest = texts.Max(static text => text.End - text.Start);
        if (longest < MinWrappedLineLength) return false;
        int body = texts.Count - 1, full = 0, sentenceEnds = 0, capitals = 0;
        for (int index = 0; index < body; index++)
        {
            (int start, int end) = texts[index];
            if (end - start >= longest * WrappedLineRatio) full++;
            if (source[end - 1] is '.' or '!' or '?' or ':' or ';' or '。' or '？' or '！') sentenceEnds++;
            // A wrapped sentence goes on in lowercase; lines that each start a capitalised item are a list.
            if (char.IsUpper(source[texts[index + 1].Start])) capitals++;
        }
        return full * 3 >= body * 2 && sentenceEnds * 3 <= body && capitals * 3 <= body;
    }

    private static string JoinWrapped(string source, List<(int Start, int End)> texts)
    {
        var joined = new StringBuilder();
        for (int index = 0; index < texts.Count; index++)
        {
            string line = source[texts[index].Start..texts[index].End];
            if (index == 0)
            {
                joined.Append(line);
                continue;
            }
            // A hyphen that only marks a line break is dropped (config-/uration); one before a digit or capital stays.
            bool hyphenated = joined.Length >= 2 && char.IsLetter(joined[^2]) && joined[^1] is '-' or '\u2010' or '\u2011' or '\u00AD';
            if (hyphenated && char.IsLower(line[0]))
            {
                joined.Length--;
                joined.Append(line);
            }
            else if (hyphenated && (char.IsUpper(line[0]) || char.IsDigit(line[0])))
            {
                if (joined[^1] is '\u00AD') joined.Length--; else if (joined[^1] is not '-') joined[^1] = '-';
                joined.Append(line);
            }
            else
            {
                joined.Append(' ').Append(line);
            }
        }
        return joined.ToString();
    }

    private static bool IsFooterLine(string source, LineSpan line)
    {
        (int Start, int End)? text = TrimSpan(source, line.Start, line.End);
        return text is not null && text.Value.End - text.Value.Start <= 60
            && FooterLine().IsMatch(source[text.Value.Start..text.Value.End]);
    }

    /// <summary>One line of an unwrapped paragraph: indent and list marker stay literal; the text is translated whole or by runs.</summary>
    private static void AddLine(PlanBuilder builder, string source, LineSpan line, PinTranslationDirection direction)
    {
        (int Start, int End)? span = TrimSpan(source, line.Start, line.End);
        if (span is null) return;
        int start = span.Value.Start, end = span.Value.End;
        string text = source[start..end];
        Match marker = ListMarker().Match(text);
        if (marker.Success && marker.Length < text.Length)
        {
            start += marker.Length;
            text = text[marker.Length..];
        }
        if (LooksLikeCodeLine(text)) return;
        AddProseSpan(builder, text, start, direction);
    }

    private const double MixedShare = 0.15;

    /// <summary>
    /// Translates the text whole when it is clean source-language text, otherwise only the source-language runs.
    /// English to Korean: a line with at least 15% Hangul is mixed, so only runs of three or more English words are translated.
    /// Korean to English: a Hangul-majority line is translated whole; in other lines only runs of two or more Korean words.
    /// </summary>
    private static void AddProseSpan(PlanBuilder builder, string text, int offset, PinTranslationDirection direction)
    {
        string masked = MaskProtected(text);
        bool translatable = PinScript.IsTranslatable(masked, direction);
        bool english = direction == PinTranslationDirection.EnglishToKorean;
        if (translatable && (!english || HangulShare(masked) < MixedShare))
        {
            builder.Piece(offset, offset + text.Length, PlanKind.Prose);
            return;
        }
        foreach ((int runStart, int runEnd) in english ? EnglishRuns(masked) : KoreanRuns(text, masked))
        {
            string run = text[runStart..runEnd];
            if (PinScript.IsTranslatable(MaskProtected(run), direction))
                builder.Piece(offset + runStart, offset + runEnd, PlanKind.Prose);
        }
    }

    /// <summary>Runs of at least three English words (letters, apostrophes and hyphens, numbers allowed between them).</summary>
    private static IEnumerable<(int Start, int End)> EnglishRuns(string masked)
    {
        foreach (Match match in EnglishRun().Matches(masked))
        {
            int words = 0;
            foreach (Match word in AsciiWord().Matches(match.Value)) { _ = word; words++; }
            if (words >= 3) yield return (match.Index, match.Index + match.Length);
        }
    }

    /// <summary>Runs of at least two consecutive space-separated Korean words (tokens containing Hangul).</summary>
    private static IEnumerable<(int Start, int End)> KoreanRuns(string text, string masked)
    {
        int index = 0, runStart = -1, runEnd = -1, tokens = 0;
        while (index <= masked.Length)
        {
            while (index < masked.Length && masked[index] is ' ' or '\t') index++;
            if (index >= masked.Length) break;
            int tokenStart = index, firstHangul = -1;
            while (index < masked.Length && !char.IsWhiteSpace(masked[index]))
            {
                if (firstHangul < 0 && IsHangul(masked[index])) firstHangul = index;
                index++;
            }
            if (firstHangul >= 0)
            {
                // Text before the Hangul that closes a quotation or sentence ("...10.") belongs to the previous context.
                bool closing = firstHangul > tokenStart && masked[firstHangul - 1] is '"' or '\'' or '”' or '’' or ')' or ']' or '.';
                if (runStart < 0) runStart = closing ? firstHangul : tokenStart;
                runEnd = index;
                tokens++;
                continue;
            }
            if (tokens >= 2 && TrimRun(text, runStart, runEnd) is { } trimmed) yield return trimmed;
            runStart = -1;
            tokens = 0;
        }
        if (tokens >= 2 && TrimRun(text, runStart, runEnd) is { } last) yield return last;
    }

    private static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    /// <summary>Leaves opening brackets and quotes before a run, and closing ones after it, outside the run.</summary>
    private static (int Start, int End)? TrimRun(string text, int start, int end)
    {
        while (start < end && text[start] is '(' or '[' or '{' or '"' or '\'' or '“' or '‘' or '<') start++;
        while (end > start && text[end - 1] is ')' or ']' or '}' or '"' or '\'' or '”' or '’' or '>') end--;
        return end > start ? (start, end) : null;
    }

    [GeneratedRegex(@"^(?:page\s+\d+(?:\s*(?:of|/)\s*\d+)?|p\.\s*\d+|\d+\s*/\s*\d+|\d+\s+of\s+\d+|[-–—]\s*\d+\s*[-–—]|\d{1,4}|confidential|internal use only|all rights reserved\.?|(?:페이지|쪽)\s*\d+(?:\s*/\s*\d+)?|\d+\s*쪽|대외비|기밀|사내\s*한정)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FooterLine();

    [GeneratedRegex(@"^(?:[-*+•‣◦▪●○■□▶►–—]|\d{1,3}[.)]|\(\d{1,3}\)|\d+(?:\.\d+)+[.)]?|[A-Za-z][.)]|[ivxlcdmIVXLCDM]{1,6}[.)]|\[[ xX]\])\s+",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"(?<![A-Za-z0-9_@/\\#$%-])(?:[A-Za-z]+(?:['’\-][A-Za-z]+)*|\d+(?:[.,]\d+)*%?)(?:[,;:.!?]*[ \t]+(?:[A-Za-z]+(?:['’\-][A-Za-z]+)*|\d+(?:[.,]\d+)*%?))+[.!?…]*(?!\.?[A-Za-z0-9_@/\\])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex EnglishRun();

    [GeneratedRegex(@"[A-Za-z]+(?:['’\-][A-Za-z]+)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AsciiWord();
}
