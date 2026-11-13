using System.Text.RegularExpressions;
using System.Text;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed record PinTranslationRecoveryDecision(string Source, string InitialText, string Issue,
    int PieceCount, string? AcceptedText)
{
    public bool Recovered => Issue.Length > 0 && AcceptedText is not null;
    public bool SourceFallback => AcceptedText is null;
}

internal sealed partial class PinTranslationService
{
    internal Action<PinTranslationRecoveryDecision>? ObserveRecovery { get; init; }

    private static string? RestoreJoinedSource(string original, string joined)
    {
        string repaired = OcrLatinTextRepair.Repair(original);
        var restored = new StringBuilder(joined.Length);
        int raw = 0;
        foreach (char character in joined)
        {
            while (raw < repaired.Length && char.IsWhiteSpace(repaired[raw])) raw++;
            if (char.IsWhiteSpace(character))
            {
                restored.Append(character);
                continue;
            }
            // The prose planner removes a wrapping hyphen before a lowercase letter,
            // and normalizes retained Unicode hyphens before capitals or digits.
            if (raw + 1 < repaired.Length && repaired[raw] is '-' or '\u2010' or '\u2011' or '\u00AD'
                && char.IsWhiteSpace(repaired[raw + 1]))
            {
                if (character == '-')
                {
                    restored.Append(character);
                    raw++;
                    continue;
                }
                if (char.IsLower(character) || repaired[raw] == '\u00AD')
                {
                    raw++;
                    while (raw < repaired.Length && char.IsWhiteSpace(repaired[raw])) raw++;
                }
            }
            if (raw >= repaired.Length || repaired[raw] != character) return null;
            restored.Append(original[raw++]);
        }
        while (raw < repaired.Length && char.IsWhiteSpace(repaired[raw])) raw++;
        return raw == repaired.Length ? restored.ToString() : null;
    }

    // c3 corpus, source >= 40 characters: en->ko p01=.274, p50=.500 (403 pairs);
    // ko->en p01=.200, p05=1.078, p50=1.764 (88 pairs). Complete English noun
    // phrases legitimately compress to .267, so they must not share the Korean bound.
    internal static string ContentLossReason(string source, string text, PinTranslationDirection direction)
    {
        if (HasNovelRepetition(source, text)) return "repetition";
        double minimumRatio = direction == PinTranslationDirection.EnglishToKorean ? .25 : .90;
        return source.Trim().Length >= 40 && text.Trim().Length < source.Trim().Length * minimumRatio
            ? "truncated" : string.Empty;
    }

    private async Task<string?> AcceptOrRecoverAsync(string source, TranslatedText result, bool markdown,
        IEnumerable<string>? sourceLines, CancellationToken cancellationToken)
    {
        if (!string.Equals(result.Source, source, StringComparison.Ordinal))
            throw new InvalidOperationException("The translator returned a missing or mismatched pin result.");
        string initial = (result.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        string issue = ContentLossReason(source, initial, Direction);
        string? accepted = null;
        int pieceCount = 0;
        if (issue.Length == 0)
        {
            if (Accepts(source, result, initial, markdown)) accepted = initial;
        }
        else
        {
            // One bounded retry. Pieces are slices of the entire sentence, never selected
            // clauses; any failed piece sends the whole sentence back to its source text.
            string[] pieces = RecoveryPieces(source, sourceLines);
            pieceCount = pieces.Length;
            if (pieces.Length >= 2)
            {
                IReadOnlyList<TranslatedText> retry = await _translator.TranslateAsync(pieces, null, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (retry.Count != pieces.Length)
                    throw new InvalidOperationException("The translator returned an incomplete pin recovery result.");
                var texts = new List<string>(pieces.Length);
                bool safe = true;
                for (int index = 0; index < pieces.Length; index++)
                {
                    if (!string.Equals(retry[index].Source, pieces[index], StringComparison.Ordinal))
                        throw new InvalidOperationException("The translator returned a mismatched pin recovery result.");
                    string text = (retry[index].Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
                    safe &= Accepts(pieces[index], retry[index], text, markdown)
                        && ContentLossReason(pieces[index], text, Direction).Length == 0;
                    texts.Add(text);
                }
                string joined = string.Join(" ", texts);
                if (safe && PreservesProtectedValues(source, joined)
                    && (!markdown || PreservesMarkdown(source, joined))
                    && ContentLossReason(source, joined, Direction).Length == 0)
                    accepted = joined;
            }
        }
        ObserveRecovery?.Invoke(new(source, initial, issue, pieceCount, accepted));
        return accepted;
    }

    /// <summary>
    /// An English paragraph of two or more sentences that surely fits one model segment, so the translator keeps
    /// it whole. The estimate counts at least one token per two letters of a word and one per any other character;
    /// the model's own pieces are fewer for everyday English.
    /// </summary>
    private bool TranslatesWhole(PlanPiece piece, int sentences) =>
        Direction == PinTranslationDirection.EnglishToKorean && piece.Kind == PlanKind.Prose && sentences >= 2
        && EstimateTokens(piece.Source) <= TextTranslationService.MaxSegmentTokens;

    internal static int EstimateTokens(string text)
    {
        int tokens = 1, letters = 0;
        foreach (char c in text)
        {
            if (char.IsAsciiLetter(c))
            {
                letters++;
                continue;
            }
            tokens += (letters + 1) / 2;
            letters = 0;
            if (!char.IsWhiteSpace(c)) tokens++;
        }
        return tokens + (letters + 1) / 2;
    }

    /// <summary>
    /// The whole translation of a short paragraph, when it passes the same checks as a sentence and keeps a sentence
    /// end for every source sentence; otherwise null and the paragraph is translated sentence by sentence.
    /// </summary>
    private string? AcceptWhole(string source, TranslatedText result, int sentences)
    {
        if (!string.Equals(result.Source, source, StringComparison.Ordinal))
            throw new InvalidOperationException("The translator returned a missing or mismatched pin result.");
        string text = (result.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return SentenceEnd().Count(text) >= sentences && ContentLossReason(source, text, Direction).Length == 0
            && Accepts(source, result, text, false) ? text : null;
    }

    [GeneratedRegex(@"[.!?。？！](?=[""')\]]*(?:\s|$))", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceEnd();

    private static bool Accepts(string source, TranslatedText result, string text, bool markdown) =>
        text.Length > 0 && !result.NeedsReview && PreservesProtectedValues(source, text)
        && (!markdown || PreservesMarkdown(source, text));

    internal static string[] RecoveryPieces(string source, IEnumerable<string>? sourceLines = null)
    {
        var boundaries = new SortedSet<int>();
        IReadOnlyList<ProtectedSpan> protectedSpans = ProtectedTokens.Find(source);
        void AddBoundary(int end)
        {
            if (end <= 0 || end >= source.Length || protectedSpans.Any(span => end > span.Start && end < span.Start + span.Length)) return;
            boundaries.Add(end);
        }
        foreach (Match clause in RecoveryClauseBoundary().Matches(source)) AddBoundary(clause.Index + clause.Length);
        if (sourceLines is not null)
        {
            // OCR lines are only hints. A hint must match this sentence exactly, so
            // adjacent sentences and repeated words cannot silently remove source text.
            int cursor = 0;
            foreach (string raw in sourceLines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int start = source.IndexOf(line, cursor, StringComparison.Ordinal);
                if (start < 0) continue;
                AddBoundary(start);
                cursor = start + line.Length;
                AddBoundary(cursor);
            }
        }
        var pieces = new List<string>();
        int previous = 0;
        foreach (int boundary in boundaries.Append(source.Length))
        {
            string piece = source[previous..boundary].Trim();
            if (piece.Length > 0) pieces.Add(piece);
            previous = boundary;
        }
        return pieces.ToArray();
    }

    internal static bool HasNovelRepetition(string source, string text)
    {
        // Adjacent word n-grams catch both repeated words and repeated phrases despite
        // varying separators; character n-grams also catch an unspaced decoder loop.
        string[] words = RepetitionWord().Matches(text).Select(match => match.Value).ToArray();
        string[] sourceWords = RepetitionWord().Matches(source).Select(match => match.Value).ToArray();
        for (int size = 1; size <= 5 && size * 3 <= words.Length; size++)
            for (int start = 0; start + size * 3 <= words.Length; start++)
            {
                bool repeated = true;
                for (int offset = size; offset < size * 3; offset++)
                    repeated &= string.Equals(words[start + offset], words[start + offset % size], StringComparison.OrdinalIgnoreCase);
                if (!repeated || words.Skip(start).Take(size * 3).Sum(word => word.Length) < 12) continue;
                string run = string.Join(" ", words.Skip(start).Take(size * 3));
                if (!string.Join(" ", sourceWords).Contains(run, StringComparison.OrdinalIgnoreCase)) return true;
            }
        for (int size = 1; size <= 32 && size * 3 <= text.Length; size++)
            for (int start = 0; start + Math.Max(12, size * 3) <= text.Length; start++)
            {
                int length = size;
                while (start + length < text.Length && text[start + length] == text[start + length % size]) length++;
                if (length < Math.Max(12, size * 3) || !text.Substring(start, size).Any(char.IsLetterOrDigit)) continue;
                if (!source.Contains(text.Substring(start, length), StringComparison.Ordinal)) return true;
            }
        return false;
    }

    [GeneratedRegex(@"[,;，；]\s+|[가-힣](?:지만|으며|며|고)\s+|\r?\n+", RegexOptions.CultureInvariant)]
    private static partial Regex RecoveryClauseBoundary();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex RepetitionWord();
}
