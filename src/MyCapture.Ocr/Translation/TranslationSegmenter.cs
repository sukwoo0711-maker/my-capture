using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyCapture.Ocr.Translation;

/// <summary>
/// Splits a block into the segments the model translates. A short block stays whole, which
/// keeps register descriptions such as "0: Counter disabled. 1: Counter enabled." together;
/// a long paragraph is split at sentence ends and the sentences are packed back up to a token
/// budget, because Marian drops content from long multi-sentence inputs.
/// </summary>
internal static partial class TranslationSegmenter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "cf", "al", "approx", "fig", "figs", "no", "nos", "vol", "ch", "sec",
        "ref", "refs", "eq", "pp", "p", "max", "min", "typ", "mr", "mrs", "ms", "dr", "inc", "ltd", "co",
        "corp", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    private static readonly int LongestAbbreviation = Abbreviations.Max(value => value.Length);

    // A Korean sentence also ends at "다. " followed by Hangul, so a Korean paragraph splits like an English one.
    [GeneratedRegex(@"[.!?][""')\]]*\s+(?=[""'(\[]?[A-Z0-9가-힯])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();

    internal static IReadOnlyList<string> Split(string text, Func<string, int> countTokens, int maxTokens, int hardLimit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(countTokens);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTokens, hardLimit);
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return [];
        if (countTokens(trimmed) <= maxTokens) return [trimmed];

        var segments = new List<string>();
        string pending = "";
        foreach (string sentence in Sentences(trimmed))
        {
            IEnumerable<(string Text, bool Glued)> pieces = countTokens(sentence) <= hardLimit
                ? [(sentence, false)] : WordChunks(sentence, countTokens, hardLimit);
            foreach ((string piece, bool glued) in pieces)
            {
                string joined = pending.Length == 0 ? piece : pending + (glued ? "" : " ") + piece;
                if (pending.Length > 0 && countTokens(joined) > maxTokens)
                {
                    segments.Add(pending);
                    pending = piece;
                }
                else
                {
                    pending = joined;
                }
            }
        }
        if (pending.Length > 0) segments.Add(pending);
        return segments;
    }

    internal static IEnumerable<string> Sentences(string text)
    {
        int start = 0;
        foreach (Match end in SentenceEnd().Matches(text))
        {
            int wordEnd = end.Index;
            while (wordEnd > start && char.IsWhiteSpace(text[wordEnd - 1])) wordEnd--;
            while (wordEnd > start && text[wordEnd - 1] is '"' or '\'' or ')' or ']') wordEnd--;
            if (wordEnd > start && char.IsAsciiLetter(text[wordEnd - 1]))
            {
                int wordStart = wordEnd;
                do
                {
                    while (wordStart > start && char.IsAsciiLetter(text[wordStart - 1])) wordStart--;
                    if (wordStart - 2 < start || text[wordStart - 1] != '.' || !char.IsAsciiLetter(text[wordStart - 2])) break;
                    wordStart--;
                } while (true);

                // "e.g." and initials such as "J." do not end a sentence; a unit such as "1.8 V." does.
                int length = wordEnd - wordStart;
                int prefixEnd = wordStart;
                if (length == 1)
                    while (prefixEnd > start && char.IsWhiteSpace(text[prefixEnd - 1])) prefixEnd--;
                bool unit = length == 1 && prefixEnd > start && char.IsAsciiDigit(text[prefixEnd - 1]);
                if ((length <= LongestAbbreviation && Abbreviations.Contains(text[wordStart..wordEnd]))
                    || (length == 1 && !unit)) continue;
            }
            string sentence = text[start..(end.Index + end.Length)].Trim();
            if (sentence.Length > 0) yield return sentence;
            start = end.Index + end.Length;
        }
        if (start < text.Length && text[start..].Trim() is { Length: > 0 } rest) yield return rest;
    }

    /// <summary>Packs words greedily, remembering when a cut continues the same source word.</summary>
    private static IEnumerable<(string Text, bool Glued)> WordChunks(string sentence, Func<string, int> countTokens, int hardLimit)
    {
        string current = "";
        bool glued = false;
        foreach (string word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0)
            {
                string joined = current + " " + word;
                if (countTokens(joined) <= hardLimit)
                {
                    current = joined;
                    continue;
                }
                yield return (current, glued);
                current = "";
            }
            glued = false;
            if (countTokens(word) <= hardLimit) current = word;
            else
            {
                bool continuation = false;
                foreach (string piece in CutWord(word, countTokens, hardLimit))
                {
                    if (current.Length > 0) yield return (current, glued);
                    current = piece;
                    glued = continuation;
                    continuation = true;
                }
            }
        }
        if (current.Length > 0) yield return (current, glued);
    }

    /// <summary>
    /// Cuts an oversized word at text elements, keeping every source character. Exponential
    /// probing bounds the text passed to the tokenizer near the size of the next piece.
    /// </summary>
    private static IEnumerable<string> CutWord(string word, Func<string, int> countTokens, int hardLimit)
    {
        var boundaries = new List<int> { 0 };
        for (int offset = 0; offset < word.Length;)
        {
            offset += StringInfo.GetNextTextElementLength(word.AsSpan(offset));
            boundaries.Add(offset);
        }
        for (int start = 0; start < boundaries.Count - 1;)
        {
            int end = FittingEnd(word, boundaries, start, countTokens, hardLimit);
            if (end == start)
            {
                // An unusually large text element may need several pieces, but a surrogate pair stays whole.
                var scalars = new List<int> { boundaries[start] };
                for (int offset = boundaries[start]; offset < boundaries[start + 1];)
                {
                    Rune.DecodeFromUtf16(word.AsSpan(offset, boundaries[start + 1] - offset), out _, out int consumed);
                    offset += consumed;
                    scalars.Add(offset);
                }
                for (int scalar = 0; scalar < scalars.Count - 1;)
                {
                    int next = FittingEnd(word, scalars, scalar, countTokens, hardLimit);
                    // A single code point is indivisible even when the supplied counter puts it over the limit.
                    if (next == scalar) next++;
                    yield return word[scalars[scalar]..scalars[next]];
                    scalar = next;
                }
                start++;
                continue;
            }
            end = PreferWhitespace(word, boundaries, start, end, countTokens, hardLimit);
            yield return word[boundaries[start]..boundaries[end]];
            start = end;
        }
    }

    /// <summary>
    /// Gallops to a failing boundary and searches the bracket. Token counts may fall as text
    /// grows, so every returned nonempty piece must have its own successful count.
    /// </summary>
    private static int FittingEnd(string text, List<int> boundaries, int start, Func<string, int> countTokens, int hardLimit)
    {
        int last = boundaries.Count - 1;
        int fit = start;
        int probe = start + 1;
        while (true)
        {
            if (countTokens(text[boundaries[start]..boundaries[probe]]) > hardLimit)
            {
                int fail = probe;
                while (fail - fit > 1)
                {
                    int middle = fit + (fail - fit) / 2;
                    if (countTokens(text[boundaries[start]..boundaries[middle]]) <= hardLimit) fit = middle;
                    else fail = middle;
                }
                return fit;
            }
            fit = probe;
            if (fit == last) return fit;
            probe = start + (int)Math.Min((long)(fit - start) * 2, last - start);
        }
    }

    /// <summary>Prefers a complete whitespace-delimited word when a checked shorter piece also fits.</summary>
    private static int PreferWhitespace(string text, List<int> boundaries, int start, int end,
        Func<string, int> countTokens, int hardLimit)
    {
        if (end == boundaries.Count - 1) return end;
        int firstContent = boundaries[start];
        while (firstContent < boundaries[end] && char.IsWhiteSpace(text[firstContent])) firstContent++;
        for (int candidate = end; candidate > start; candidate--)
        {
            int offset = boundaries[candidate];
            if (firstContent < offset && char.IsWhiteSpace(text[offset - 1]) && !char.IsWhiteSpace(text[offset]))
                return candidate == end || countTokens(text[boundaries[start]..offset]) <= hardLimit ? candidate : end;
        }
        return end;
    }
}
