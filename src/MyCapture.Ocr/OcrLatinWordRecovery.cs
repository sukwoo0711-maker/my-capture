using System.Text;

namespace MyCapture.Ocr;

/// <summary>Uses a second spatial reading to repair short foreign-script intrusions, never a dictionary guess.</summary>
internal static class OcrLatinWordRecovery
{
    internal static bool HasSuspects(OcrResult result) => result.HasText && result.Lines.Any(line =>
        IsLatinLine(line) && line.Words.Where((word, index) => IsSuspect(word.Text) && !IsQuoted(line, index)).Any());

    internal static int CountSuspects(OcrResult result) => result.Lines.Where(IsLatinLine)
        .Sum(line => line.Words.Where((word, index) => IsSuspect(word.Text) && !IsQuoted(line, index)).Count());

    internal static OcrResult Recover(OcrResult source, OcrResult second, TimeSpan elapsed)
    {
        if (!source.HasText || !second.HasText) return source;
        bool changed = false;
        var lines = new OcrLine[source.Lines.Count];
        for (int index = 0; index < lines.Length; index++)
        {
            OcrLine original = source.Lines[index];
            lines[index] = RecoverLine(original, second.Lines, source.ReadingOrientation);
            changed |= !ReferenceEquals(original, lines[index]);
        }
        return changed ? OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)),
            source.LanguageTag ?? string.Empty, lines, elapsed, source.ReadingOrientation, source.TextAngle) : source;
    }

    private static OcrLine RecoverLine(OcrLine source, IReadOnlyList<OcrLine> second, OcrReadingOrientation orientation)
    {
        if (!IsLatinLine(source) || !source.Words.Any(word => IsSuspect(word.Text))) return source;
        OcrWord[] alternatives = second.SelectMany(line => line.Words)
            .Where(word => SameRow(orientation.ToReading(source.Bounds), orientation.ToReading(word.Bounds))).ToArray();
        if (alternatives.Length == 0) return source;

        // Text and word boxes must describe the same token sequence. Retain the exact original
        // separators and punctuation outside each replaced token instead of rebuilding a line.
        var spans = new (int Start, int Length)[source.Words.Count];
        int cursor = 0;
        for (int index = 0; index < source.Words.Count; index++)
        {
            string token = source.Words[index].Text;
            int start = source.Text.IndexOf(token, cursor, StringComparison.Ordinal);
            if (token.Length == 0 || start < 0 || source.Text[cursor..start].Any(character => !char.IsWhiteSpace(character)))
                return source;
            spans[index] = (start, token.Length);
            cursor = start + token.Length;
        }
        if (source.Text[cursor..].Any(character => !char.IsWhiteSpace(character))) return source;

        OcrWord[]? changed = null;
        for (int index = 0; index < source.Words.Count; index++)
        {
            OcrWord suspect = source.Words[index];
            if (!IsSuspect(suspect.Text) || IsQuoted(source, index)) continue;
            OcrWord[] matches = alternatives.Where(candidate => IsLatinReading(candidate.Text) &&
                SameWord(orientation.ToReading(suspect.Bounds), orientation.ToReading(candidate.Bounds)) &&
                !source.Words.Where((_, other) => other != index).Any(other =>
                    SameRow(orientation.ToReading(other.Bounds), orientation.ToReading(candidate.Bounds)) &&
                    HorizontalOverlap(orientation.ToReading(other.Bounds), orientation.ToReading(candidate.Bounds)) >
                        orientation.ToReading(other.Bounds).Width * .35)).ToArray();
            if (matches.Length != 1) continue;

            // Two nearby Latin words must be independently corroborated at their source boxes.
            // A Korean label next to an unrelated English line cannot borrow that line's reading.
            int anchors = 0;
            for (int neighbour = Math.Max(0, index - 3); neighbour <= Math.Min(source.Words.Count - 1, index + 3); neighbour++)
            {
                if (neighbour == index) continue;
                OcrWord word = source.Words[neighbour];
                string expected = Anchor(word.Text);
                if (expected.Length < 2 || !expected.All(char.IsAsciiLetter)) continue;
                if (alternatives.Any(candidate => Anchor(candidate.Text) == expected &&
                    SameWord(orientation.ToReading(word.Bounds), orientation.ToReading(candidate.Bounds)))) anchors++;
            }
            if (anchors < 2) continue;
            changed ??= source.Words.ToArray();
            changed[index] = suspect with { Text = matches[0].Text };
        }
        if (changed is null) return source;
        var text = new StringBuilder(source.Text.Length);
        cursor = 0;
        for (int index = 0; index < changed.Length; index++)
        {
            text.Append(source.Text, cursor, spans[index].Start - cursor);
            text.Append(changed[index].Text);
            cursor = spans[index].Start + spans[index].Length;
        }
        text.Append(source.Text, cursor, source.Text.Length - cursor);
        return source with { Text = text.ToString(), Words = changed };
    }

    /// <summary>
    /// The words a second reading needs around each suspect: the Latin anchors that Recover
    /// requires on both sides, kept to the suspect's own visual row. Windows sometimes joins a
    /// caption at another height into the line, and a crop across both reads neither; words
    /// far apart on one row, such as a menu bar's, read well together.
    /// </summary>
    internal static IEnumerable<OcrLine> SuspectRows(OcrResult result)
    {
        foreach (OcrLine line in result.Lines.Where(IsLatinLine))
        {
            (int First, int Last)? pending = null;
            // The first word no yielded row holds: rows stay disjoint, so no word is read twice.
            int unread = 0;
            for (int index = 0; index < line.Words.Count; index++)
            {
                if (!IsSuspect(line.Words[index].Text) || IsQuoted(line, index)) continue;
                OcrRect suspect = line.Words[index].Bounds;
                int first = index, last = index;
                while (first > Math.Max(0, index - 3) && SameRow(line.Words[first - 1].Bounds, suspect)) first--;
                while (last < Math.Min(line.Words.Count - 1, index + 3) && SameRow(line.Words[last + 1].Bounds, suspect)) last++;
                if (pending is { } open && first <= open.Last)
                {
                    // Boxes of different heights make SameRow intransitive: a later suspect's row can reach
                    // further left than an earlier one's, and those words are its anchors too.
                    pending = (Math.Max(unread, Math.Min(open.First, first)), Math.Max(open.Last, last));
                    continue;
                }
                if (pending is { } done)
                {
                    yield return Row(line, done.First, done.Last);
                    unread = done.Last + 1;
                }
                pending = (first, last);
            }
            if (pending is { } final) yield return Row(line, final.First, final.Last);
        }

        static OcrLine Row(OcrLine line, int first, int last)
        {
            OcrWord[] words = [.. line.Words.Skip(first).Take(last - first + 1)];
            return new OcrLine(string.Join(' ', words.Select(word => word.Text)),
                words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);
        }
    }

    private static bool IsLatinLine(OcrLine line)
    {
        int latin = line.Text.Count(char.IsAsciiLetter), hangul = line.Text.Count(IsHangul);
        return latin >= 12 && hangul > 0 && latin >= hangul * 6 &&
            line.Words.Count(word => word.Text.Count(char.IsAsciiLetter) >= 2 && !word.Text.Any(IsHangul)) >= 3;
    }

    private static bool IsSuspect(string word)
    {
        int hangul = word.Count(IsHangul);
        // An unadorned two-syllable Korean word is common legitimate bilingual content.
        // Two syllables need a mixed ASCII atom, or a broken parenthesized abbreviation,
        // before an independent Latin reading may be consulted.
        bool mixed = word.Any(char.IsAsciiLetterOrDigit) || word.StartsWith('(') && word.EndsWith('.') && !word.Contains(')');
        return hangul is >= 1 and <= 2 && (hangul == 1 || mixed) && word.Length <= 10 &&
            word.All(character => IsHangul(character) || char.IsAscii(character) && !char.IsControl(character));
    }

    private static bool IsQuoted(OcrLine line, int index)
    {
        string token = line.Words[index].Text;
        if (token.IndexOfAny(['\"', '\'', '`', '\u201C', '\u201D', '\u2018', '\u2019']) >= 0) return true;
        return index > 0 && line.Words[index - 1].Text is "\"" or "'" or "`" or "\u201C" or "\u2018" ||
            index + 1 < line.Words.Count && line.Words[index + 1].Text is "\"" or "'" or "`" or "\u201D" or "\u2019";
    }

    private static bool IsLatinReading(string text) => text.Length is > 0 and <= 12 &&
        text.Any(char.IsAsciiLetterOrDigit) && text.All(character => char.IsAscii(character) &&
            !char.IsWhiteSpace(character) && !char.IsControl(character));

    private static string Anchor(string text) => new string(OcrLatinTextRepair.Repair(text)
        .Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsHangul(char value) => value is >= '\uAC00' and <= '\uD7A3';

    private static bool SameWord(OcrRect first, OcrRect second) => SameRow(first, second) &&
        HorizontalOverlap(first, second) >= Math.Max(first.Width, second.Width) * .45 &&
        Math.Abs(first.X + first.Width / 2 - second.X - second.Width / 2) <= Math.Max(first.Width, second.Width) * .4;

    private static bool SameRow(OcrRect first, OcrRect second) => OcrGeometry.IsValid(first) && OcrGeometry.IsValid(second) &&
        Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y) >= Math.Min(first.Height, second.Height) * .5 &&
        Math.Abs(first.Y + first.Height / 2 - second.Y - second.Height / 2) <= Math.Max(first.Height, second.Height) * .6;

    private static double HorizontalOverlap(OcrRect first, OcrRect second) =>
        Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.X, second.X));
}
