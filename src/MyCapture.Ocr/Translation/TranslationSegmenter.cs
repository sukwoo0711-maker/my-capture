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

    // A Korean sentence also ends at "다. " followed by Hangul, so a Korean paragraph splits like an English one.
    [GeneratedRegex(@"[.!?][""')\]]*\s+(?=[""'(\[]?[A-Z0-9가-힯])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"(?<word>[A-Za-z]+(?:\.[A-Za-z]+)*)[""')\]]*\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingWord();

    internal static IReadOnlyList<string> Split(string text, Func<string, int> countTokens, int maxTokens, int hardLimit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(countTokens);
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return [];
        if (countTokens(trimmed) <= maxTokens) return [trimmed];

        var segments = new List<string>();
        string pending = "";
        foreach (string sentence in Sentences(trimmed))
        {
            foreach (string piece in countTokens(sentence) <= hardLimit ? [sentence] : WordChunks(sentence, countTokens, hardLimit))
            {
                string joined = pending.Length == 0 ? piece : pending + " " + piece;
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
            string before = text[start..end.Index];
            Match word = TrailingWord().Match(before);
            if (word.Success)
            {
                // "e.g." and initials such as "J." do not end a sentence; a unit such as "1.8 V." does.
                string value = word.Groups["word"].Value;
                bool unit = value.Length == 1 && before[..word.Index].TrimEnd() is { Length: > 0 } prefix
                    && char.IsAsciiDigit(prefix[^1]);
                if (Abbreviations.Contains(value) || (value.Length == 1 && !unit)) continue;
            }
            string sentence = text[start..(end.Index + end.Length)].Trim();
            if (sentence.Length > 0) yield return sentence;
            start = end.Index + end.Length;
        }
        if (start < text.Length && text[start..].Trim() is { Length: > 0 } rest) yield return rest;
    }

    private static IEnumerable<string> WordChunks(string sentence, Func<string, int> countTokens, int hardLimit)
    {
        string current = "";
        foreach (string word in sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string joined = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && countTokens(joined) > hardLimit)
            {
                yield return current;
                current = word;
            }
            else
            {
                current = joined;
            }
        }
        if (current.Length > 0) yield return current;
    }
}
