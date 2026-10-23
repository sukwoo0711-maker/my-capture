using System.Globalization;
using System.Text;

namespace MyCapture.Ocr;

/// <summary>Combines two recognitions of the same source image without modifying either input.</summary>
internal static class OcrResultMerger
{
    internal static OcrResult Merge(OcrResult windows, OcrResult neural, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(neural);

        if (windows.Status == OcrStatus.Cancelled || neural.Status == OcrStatus.Cancelled)
            return OcrResult.Cancelled();

        // Windows records its winning rotation; PP-OCR currently returns source boxes without
        // orientation metadata. Use the explicit reading axes while leaving every box untouched.
        OcrReadingOrientation orientation = windows.HasText && windows.ReadingOrientation.OriginalWidth > 0
            ? windows.ReadingOrientation : neural.ReadingOrientation;
        OcrLine[] first = OrderLines(PlausibleLines(windows), orientation);
        OcrLine[] second = OrderLines(PlausibleLines(neural), orientation);
        var parents = Enumerable.Range(0, first.Length + second.Length).ToArray();
        OcrLine[] allLines = [.. first, .. second];
        OcrRect[] readingBounds = allLines.Select(line => orientation.ToReading(line.Bounds)).ToArray();
        string[] compact = allLines.Select(line => Profile.Create(line.Text).Compact).ToArray();
        var members = parents.Select(index => new List<int> { index }).ToArray();

        int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }

        // Bipartite components allow a Windows row to align with several neural text blocks
        // (notably table cells). Horizontal intersection is mandatory: nearby columns alone
        // are not duplicates. Pairwise overlap is insufficient: a box between adjacent rows
        // can overlap both. Each joined component must also remain consistent across ALL of
        // its row boxes. Compact agreement and center distance resolve competing edges before
        // stable source order, so reversing engine enumeration cannot change the assignment.
        var edges = new List<(int Left, int Right)>();
        for (int left = 0; left < first.Length; left++)
        for (int right = 0; right < second.Length; right++)
        {
            int other = first.Length + right;
            if (Aligns(readingBounds[left], readingBounds[other])) edges.Add((left, other));
        }
        foreach (var edge in edges.OrderByDescending(edge => compact[edge.Left] == compact[edge.Right])
                     .ThenBy(edge => Math.Abs(CenterY(readingBounds[edge.Left]) - CenterY(readingBounds[edge.Right])))
                     .ThenBy(edge => edge.Left).ThenBy(edge => edge.Right))
        {
            int left = Root(edge.Left), right = Root(edge.Right);
            if (left == right || !members[left].All(a => members[right].All(b =>
                    SameRow(readingBounds[a], readingBounds[b])))) continue;
            parents[right] = left;
            members[left].AddRange(members[right]);
        }

        var groups = Enumerable.Range(0, parents.Length).GroupBy(Root);
        var selected = new List<OcrLine>();
        int windowsCharacters = 0, neuralCharacters = 0;
        foreach (var group in groups)
        {
            OcrLine[] left = OrderLines(group.Where(index => index < first.Length)
                .Select(index => first[index]), orientation);
            OcrLine[] right = OrderLines(group.Where(index => index >= first.Length)
                .Select(index => second[index - first.Length]), orientation);
            bool useWindows = right.Length == 0 || (left.Length > 0 &&
                PreferWindows(left, right, windows.LanguageTag, orientation));
            OcrLine[] chosen = useWindows ? left : right;
            selected.AddRange(chosen);
            int count = chosen.Sum(line => Profile.Create(line.Text).Meaningful);
            if (useWindows) windowsCharacters += count;
            else neuralCharacters += count;
        }

        OcrLine[] ordered = OrderLines(selected, orientation);
        string language = (windowsCharacters > neuralCharacters ? windows.LanguageTag : neural.LanguageTag)
            ?? windows.LanguageTag ?? string.Empty;
        if (ordered.Length > 0)
        {
            // Do not use BuildBlockText here: it removes repeated adjacent text even when it
            // occupies distinct source rows (e.g. two spreadsheet cells with the same value).
            return OcrResult.Success(string.Join('\n', ordered.Select(line => line.Text)),
                language, ordered, elapsed, orientation);
        }

        if (windows.Status is OcrStatus.Success or OcrStatus.NoText ||
            neural.Status is OcrStatus.Success or OcrStatus.NoText)
            return OcrResult.NoText(language, elapsed);
        if (neural.Status == OcrStatus.Failed || windows.Status == OcrStatus.Failed)
            return OcrResult.Failed((neural.Status == OcrStatus.Failed ? neural.Message : windows.Message)
                ?? string.Empty, elapsed);
        return OcrResult.Unavailable(neural.Message ?? windows.Message);
    }

    private static OcrLine[] PlausibleLines(OcrResult result) => result.HasText
        ? result.Lines.Where(line => Profile.Create(line.Text).Plausible)
            .Select(line => line with { Text = line.Text.Trim() }).ToArray()
        : [];

    private static bool PreferWindows(OcrLine[] windows, OcrLine[] neural,
        string? windowsLanguage, OcrReadingOrientation orientation)
    {
        Profile first = Profile.Create(string.Concat(windows.Select(line => line.Text)));
        Profile second = Profile.Create(string.Concat(neural.Select(line => line.Text)));

        // Neither engine exposes confidence in OcrResult. Never invent comparable confidence
        // scores. Exact compact agreement is strong evidence for retaining Windows spacing,
        // together with its real word boxes, so downstream text/table extraction agrees.
        if (string.Equals(first.Compact, second.Compact, StringComparison.Ordinal)) return true;

        // A detector may find the outer cells but miss a short interior quantity. Retain the
        // Windows row only when every neural cell agrees exactly with its spatially matching
        // Windows word(s), and the only extra words occupy unobserved interior gaps. This is
        // specific independent spatial/text evidence, not a preference for a longer string.
        if (HasSupportedInteriorWords(windows, neural, orientation)) return true;

        // The installed neural recognizer is Korean/Latin. A Windows language pass can read
        // other scripts; do not let the Korean model's plausible substitute overwrite them.
        string primaryLanguage = windowsLanguage?.Split('-')[0] ?? string.Empty;
        if (first.OtherScript > 0 && !primaryLanguage.Equals("ko", StringComparison.OrdinalIgnoreCase)
            && !primaryLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)) return true;

        // Replacement/private/control characters and isolated Hangul fragments are evidence
        // against a candidate, unlike ordinary punctuation, currency, and code operators.
        if (first.DefectRatio + .15 < second.DefectRatio) return true;
        if (second.DefectRatio + .15 < first.DefectRatio) return false;

        // Preserve observed operators when both engines agree on all letters/digits and one
        // simply omits syntax. No characters or word boxes are fabricated or transplanted.
        if (first.Meaningful > 0 && first.LettersAndDigits == second.LettersAndDigits)
        {
            if (IsSyntaxSuperset(first.Compact, second.Compact)) return true;
            if (IsSyntaxSuperset(second.Compact, first.Compact)) return false;
        }

        // A longer string alone is not better. Accept containment only when source geometry
        // also extends beyond the shorter recognition, indicating actual truncation.
        OcrRect firstBounds = ReadingUnion(windows, orientation);
        OcrRect secondBounds = ReadingUnion(neural, orientation);
        if (Extends(first.Compact, second.Compact, firstBounds, secondBounds)) return true;
        if (Extends(second.Compact, first.Compact, secondBounds, firstBounds)) return false;

        // Ambiguous disagreements retain the existing Accurate engine. These dictionary-free
        // rules cannot establish the truth of two plausible alternatives; the benchmark gate
        // determines whether the dual-engine path may become the product default.
        return false;
    }

    private static bool HasSupportedInteriorWords(OcrLine[] windows, OcrLine[] neural,
        OcrReadingOrientation orientation)
    {
        if (neural.Length < 2) return false;
        var words = windows.SelectMany(line => line.Words)
            .Select(word => (Word: word, Bounds: orientation.ToReading(word.Bounds)))
            .OrderBy(entry => entry.Bounds.X).ThenBy(entry => entry.Bounds.Y).ToArray();
        OcrRect[] cells = neural.Select(line => orientation.ToReading(line.Bounds)).ToArray();
        if (words.Length < 3 || words.Any(entry => !HasArea(entry.Bounds) ||
                !Profile.Create(entry.Word.Text).Plausible) || cells.Any(cell => !HasArea(cell))) return false;
        if (Profile.Create(string.Concat(windows.Select(line => line.Text))).Compact !=
            Profile.Create(string.Concat(words.Select(entry => entry.Word.Text))).Compact) return false;

        var matched = new bool[words.Length];
        for (int cell = 0; cell < cells.Length; cell++)
        {
            int[] matching = Enumerable.Range(0, words.Length)
                .Where(index => Aligns(words[index].Bounds, cells[cell])).ToArray();
            if (matching.Length == 0 || matching.Any(index => matched[index]) ||
                Profile.Create(string.Concat(matching.Select(index => words[index].Word.Text))).Compact !=
                Profile.Create(neural[cell].Text).Compact) return false;
            foreach (int index in matching) matched[index] = true;
        }

        int[] extras = Enumerable.Range(0, words.Length).Where(index => !matched[index]).ToArray();
        return extras.Length > 0 && extras.All(index =>
        {
            OcrRect word = words[index].Bounds;
            return cells.All(cell => word.Right <= cell.X || word.X >= cell.Right) &&
                cells.Any(cell => cell.Right <= word.X) && cells.Any(cell => cell.X >= word.Right);
        });
    }

    private static bool Extends(string longer, string shorter, OcrRect large, OcrRect small) =>
        shorter.Length > 0 && longer.Length >= shorter.Length + 2 &&
        longer.Contains(shorter, StringComparison.Ordinal) && HasArea(large) && HasArea(small) &&
        large.Width >= small.Width * 1.15;

    private static bool IsSyntaxSuperset(string richer, string poorer)
    {
        if (richer.Length <= poorer.Length || !richer.Any(IsCodeOperator)) return false;
        int index = 0;
        foreach (char character in richer)
        {
            if (index < poorer.Length && character == poorer[index]) index++;
            else if (!IsCodeOperator(character)) return false;
        }
        return index == poorer.Length;
    }

    private static bool IsCodeOperator(char character) => "{}[]();=<>!&|+-*/%:^~".Contains(character);

    private static OcrRect ReadingUnion(IEnumerable<OcrLine> lines, OcrReadingOrientation orientation) =>
        lines.Select(line => orientation.ToReading(line.Bounds)).Aggregate(default(OcrRect), OcrRect.Union);

    private static bool Aligns(OcrRect left, OcrRect right)
    {
        if (!SameRow(left, right)) return false;
        double width = Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X);
        if (width <= 0) return false;
        double height = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y);
        double intersection = width * height;
        double minimumArea = Math.Min(left.Width * left.Height, right.Width * right.Height);
        double unionArea = left.Width * left.Height + right.Width * right.Height - intersection;
        return intersection / minimumArea >= .5 || intersection / unionArea >= .3;
    }

    private static bool SameRow(OcrRect left, OcrRect right)
    {
        if (!HasArea(left) || !HasArea(right)) return false;
        double minimumHeight = Math.Min(left.Height, right.Height);
        double overlap = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y);
        double centers = Math.Abs(CenterY(left) - CenterY(right));
        // PP-OCR line boxes include padding while Windows bounds tightly fit glyphs.
        // Their height ratio is not row evidence; overlap and center distance must use
        // the smaller glyph height. Component-wide checks above still prevent bridging.
        return overlap >= minimumHeight * .55 && centers <= minimumHeight * .45;
    }

    private static double CenterY(OcrRect bounds) => bounds.Y + bounds.Height / 2;

    private static bool HasArea(OcrRect bounds) => bounds.Width > 0 && bounds.Height > 0 &&
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Right) && double.IsFinite(bounds.Bottom);

    private static OcrLine[] OrderLines(IEnumerable<OcrLine> source, OcrReadingOrientation orientation)
    {
        var rows = new List<List<(OcrLine Line, OcrRect Bounds)>>();
        foreach (var entry in source.Select(line => (Line: line, Bounds: orientation.ToReading(line.Bounds)))
                     .OrderBy(entry => entry.Bounds.Y + entry.Bounds.Height / 2)
                     .ThenBy(entry => entry.Bounds.X).ThenBy(entry => entry.Line.Text, StringComparer.Ordinal))
        {
            // Compare to the fixed row anchor, not an expanding union, to avoid transitive
            // drift into the next row. Within a row small detector Y jitter must not swap cells.
            var row = rows.LastOrDefault(candidate => SameRow(candidate[0].Bounds, entry.Bounds));
            if (row is null) rows.Add([entry]);
            else row.Add(entry);
        }
        return rows.SelectMany(row => row.OrderBy(entry => entry.Bounds.X)
            .ThenBy(entry => entry.Bounds.Y).ThenBy(entry => entry.Line.Text, StringComparer.Ordinal))
            .Select(entry => entry.Line).ToArray();
    }

    private sealed record Profile(string Compact, string LettersAndDigits, int Meaningful,
        int OtherScript, double DefectRatio, bool Plausible)
    {
        internal static Profile Create(string text)
        {
            var compact = new StringBuilder(text.Length);
            var letters = new StringBuilder(text.Length);
            int count = 0, meaningful = 0, defects = 0, fragments = 0, otherScript = 0;
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune)) continue;
                compact.Append(rune.ToString());
                count++;
                UnicodeCategory category = Rune.GetUnicodeCategory(rune);
                if (rune.Value == 0xFFFD || category is UnicodeCategory.Control or
                    UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Format)
                    defects++;
                if (Rune.IsLetterOrDigit(rune))
                {
                    meaningful++;
                    letters.Append(rune.ToString());
                    if (Rune.IsLetter(rune) && !(rune.Value <= 0x024F ||
                        rune.Value is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or
                            >= 0x3130 and <= 0x318F)) otherScript++;
                }
                if (rune.Value is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) fragments++;
            }
            string value = compact.ToString();
            // Single letters/digits and closing braces are valid titles/cells/code lines.
            // Pure repeated punctuation such as "???" is not useful OCR evidence.
            bool syntaxOnly = count is > 0 and <= 12 && value.All(IsCodeOperator) &&
                value.Any(character => "{}[]();".Contains(character));
            bool plausible = count > 0 && defects == 0 &&
                (meaningful >= Math.Max(1, count / 5.0) || syntaxOnly);
            return new(value, letters.ToString(), meaningful, otherScript,
                count == 0 ? 1 : (defects + fragments * .5) / count, plausible);
        }
    }
}
