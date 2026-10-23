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
        bool[,] crossRows = CrossEngineRows(readingBounds, compact, first.Length);
        var members = parents.Select(index => new List<int> { index }).ToArray();
        var consumed = new bool[allLines.Length];
        var selected = new List<OcrLine>();
        int windowsCharacters = 0, neuralCharacters = 0;

        // A padded narrow cell and a tight wide row can have little area overlap despite
        // representing the same row. Match that layout separately, using horizontal cell
        // coverage and corroborated text, without relaxing the ordinary row-bridge checks.
        foreach (RowMatch match in FindRowMatches(allLines, first.Length, readingBounds, compact, orientation))
        {
            if (consumed[match.Row] || match.Cells.Any(index => consumed[index])) continue;
            consumed[match.Row] = true;
            foreach (int index in match.Cells) consumed[index] = true;
            selected.Add(match.Line);
            bool fromWindows = (match.UseCells ? match.Cells[0] : match.Row) < first.Length;
            int count = Profile.Create(match.Line.Text).Meaningful;
            if (fromWindows) windowsCharacters += count;
            else neuralCharacters += count;
        }

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
        // its row boxes. Compact agreement, a shared row center and center distance resolve
        // competing edges before stable source order, so reversing engine enumeration cannot
        // change the assignment.
        var edges = new List<(int Left, int Right)>();
        for (int left = 0; left < first.Length; left++)
        for (int right = 0; right < second.Length; right++)
        {
            int other = first.Length + right;
            if (!consumed[left] && !consumed[other] && crossRows[left, other] &&
                Aligns(readingBounds[left], readingBounds[other]))
                edges.Add((left, other));
        }
        // Lines of one engine must share a glyph row; a cross-engine pair uses the row
        // relation above, which also accepts a padded box offset from a tight glyph box.
        bool Compatible(int a, int b) => a < first.Length == b < first.Length
            ? SameRow(readingBounds[a], readingBounds[b]) : crossRows[a, b];
        foreach (var edge in edges.OrderByDescending(edge => compact[edge.Left] == compact[edge.Right])
                     .ThenByDescending(edge => SameRow(readingBounds[edge.Left], readingBounds[edge.Right]))
                     .ThenBy(edge => Math.Abs(CenterY(readingBounds[edge.Left]) - CenterY(readingBounds[edge.Right])))
                     .ThenBy(edge => edge.Left).ThenBy(edge => edge.Right))
        {
            int left = Root(edge.Left), right = Root(edge.Right);
            if (left == right || !members[left].All(a => members[right].All(b => Compatible(a, b)))) continue;
            parents[right] = left;
            members[left].AddRange(members[right]);
        }

        // An engine can split one glyph row into two stacked partial lines (upper and lower
        // stroke halves of a bold heading). Only one half shares the row center with the
        // other engine's box, so the other half would be emitted as an extra line. Join a
        // remaining line to that component only when both halves lie inside the same box of
        // the other engine and touch vertically; separate rows inside a tall box do not.
        for (int index = 0; index < allLines.Length; index++)
        {
            if (consumed[index] || members[Root(index)].Count != 1) continue;
            bool windowsLine = index < first.Length;
            int start = windowsLine ? first.Length : 0, end = windowsLine ? allLines.Length : first.Length;
            for (int outer = start; outer < end; outer++)
            {
                int root = Root(outer);
                if (consumed[outer] || !Contains(readingBounds[outer], readingBounds[index])) continue;
                if (!members[root].Any(half => half != index && half < first.Length == windowsLine &&
                        Contains(readingBounds[outer], readingBounds[half]) &&
                        StackedHalves(readingBounds[half], readingBounds[index], readingBounds[outer]))) continue;
                parents[index] = root;
                members[root].Add(index);
                break;
            }
        }

        var groups = Enumerable.Range(0, parents.Length).Where(index => !consumed[index]).GroupBy(Root);
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

    private static IEnumerable<RowMatch> FindRowMatches(OcrLine[] lines, int windowsCount,
        OcrRect[] bounds, string[] compact, OcrReadingOrientation orientation)
    {
        var matches = new List<RowMatch>();
        for (int row = 0; row < lines.Length; row++)
        {
            int start = row < windowsCount ? windowsCount : 0;
            int count = row < windowsCount ? lines.Length - windowsCount : windowsCount;
            int[] cells = Enumerable.Range(start, count)
                .Where(index => SameRow(bounds[row], bounds[index]) &&
                    HorizontalOverlap(bounds[row], bounds[index]) >= bounds[index].Width * .65)
                .OrderBy(index => bounds[index].X).ThenBy(index => bounds[index].Y)
                .ThenBy(index => compact[index], StringComparer.Ordinal).ToArray();
            if (cells.Length < 2) continue;

            // Distinct cell regions must share a row. In particular, a spanning box cannot
            // connect two adjacent rows or count two overlapping alternatives as two cells.
            bool separateCells = true;
            for (int first = 0; first < cells.Length && separateCells; first++)
            for (int second = first + 1; second < cells.Length; second++)
            {
                OcrRect left = bounds[cells[first]], right = bounds[cells[second]];
                if (!SameRow(left, right) || HorizontalOverlap(left, right) > Math.Min(left.Width, right.Width) * .15)
                {
                    separateCells = false;
                    break;
                }
            }
            if (!separateCells) continue;

            OcrLine[] cellLines = cells.Select(index => lines[index]).ToArray();
            string cellText = string.Concat(cells.Select(index => compact[index]));
            int distance = CompactAgreementDistance(compact[row], cellText);
            if (distance < 0)
            {
                // One engine can read a header row as one line and garble one short cell
                // ("구분 거 人" for "구분 건수") while the other reads separate cells. Whole-row
                // edit distance then rejects the row although its words corroborate cells.
                if (!CellsCorroborateRow(lines[row], cellLines, orientation)) continue;
                distance = 3; // ordered after every whole-row agreement (at most two edits)
            }
            // Approximate agreement must not erase a corroborated interior quantity. This
            // existing spatial evidence is stronger than a small aggregate edit distance.
            bool useCells = distance == 0 || !HasSupportedInteriorWords([lines[row]], cellLines, orientation);
            OcrLine chosen = useCells ? AssembleCells(cellLines) : lines[row];
            matches.Add(new(row, cells, distance, chosen, useCells));
        }
        return matches.OrderBy(match => match.Distance).ThenByDescending(match => match.Cells.Length)
            .ThenByDescending(match => bounds[match.Row].Width)
            .ThenBy(match => CenterY(bounds[match.Row])).ThenBy(match => bounds[match.Row].X)
            .ThenBy(match => match.Row);
    }

    private static bool CellsCorroborateRow(OcrLine row, OcrLine[] cells, OcrReadingOrientation orientation)
    {
        // Every word of the row must fall in exactly one cell by its horizontal center, and
        // every cell must receive a word. At least half of the cells must then agree exactly
        // with their words. A disagreeing cell may not contain a digit in either reading: a
        // conflicting quantity is never resolved by layout, so both readings stay visible.
        OcrRect[] cellBounds = cells.Select(cell => orientation.ToReading(cell.Bounds)).ToArray();
        var assigned = new List<string>[cells.Length];
        for (int cell = 0; cell < cells.Length; cell++) assigned[cell] = [];
        if (row.Words.Count < cells.Length ||
            Profile.Create(row.Text).Compact != Profile.Create(string.Concat(row.Words.Select(word => word.Text))).Compact)
            return false;
        foreach (OcrWord word in row.Words)
        {
            OcrRect bounds = orientation.ToReading(word.Bounds);
            double center = bounds.X + bounds.Width / 2;
            int[] owners = Enumerable.Range(0, cells.Length)
                .Where(cell => center >= cellBounds[cell].X && center <= cellBounds[cell].Right).ToArray();
            if (!HasArea(bounds) || owners.Length != 1) return false;
            assigned[owners[0]].Add(word.Text);
        }
        int agreeing = 0;
        for (int cell = 0; cell < cells.Length; cell++)
        {
            if (assigned[cell].Count == 0) return false;
            string observed = Profile.Create(string.Concat(assigned[cell])).Compact;
            string read = Profile.Create(cells[cell].Text).Compact;
            if (observed == read || WithoutIntegerGrouping(observed) == WithoutIntegerGrouping(read)) agreeing++;
            else if (observed.Any(char.IsDigit) || read.Any(char.IsDigit)) return false;
        }
        return agreeing > 0 && agreeing * 2 >= cells.Length;
    }

    private static OcrLine AssembleCells(OcrLine[] cells)
    {
        // Emit one visual row, taking its text from the cell-level representation. Keep every
        // actual cell word and source box so text reconstruction and table column inference
        // use the cell geometry. A detector's whole-row text is never emitted a second time.
        OcrWord[] words = cells.SelectMany(line => line.Words.Count > 0
            ? line.Words : new[] { new OcrWord(line.Text, line.Bounds) }).ToArray();
        return new OcrLine(string.Join(' ', cells.Select(line => line.Text)),
            cells.Select(line => line.Bounds).Aggregate(default(OcrRect), OcrRect.Union), words);
    }

    private static int CompactAgreementDistance(string first, string second)
    {
        if (first == second) return 0;
        // Short numeric cells can differ only by a thousands separator ("23 400" vs
        // "23,400"). Require exact equality after removing valid integer-grouping commas;
        // signs, decimal points, digits and malformed grouping remain significant. This
        // does not permit arbitrary substitutions in short labels or change emitted text.
        if (WithoutIntegerGrouping(first) == WithoutIntegerGrouping(second)) return 0;
        // At most two edits, at most 10% of the shorter text, and therefore close length
        // agreement. Short labels require exact agreement; this is not a dictionary rule.
        int limit = Math.Min(2, Math.Min(first.Length, second.Length) / 10);
        if (limit == 0 || Math.Abs(first.Length - second.Length) > limit) return -1;
        int[] previous = Enumerable.Range(0, second.Length + 1).Select(value => Math.Min(value, limit + 1)).ToArray();
        var current = new int[previous.Length];
        for (int row = 1; row <= first.Length; row++)
        {
            current[0] = Math.Min(row, limit + 1);
            int start = Math.Max(1, row - limit), end = Math.Min(second.Length, row + limit);
            if (start > 1) current[start - 1] = limit + 1;
            if (end < second.Length) current[end + 1] = limit + 1;
            int minimum = limit + 1;
            for (int column = start; column <= end; column++)
            {
                current[column] = Math.Min(previous[column] + 1, Math.Min(current[column - 1] + 1,
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1)));
                minimum = Math.Min(minimum, current[column]);
            }
            if (minimum > limit) return -1;
            (previous, current) = (current, previous);
        }
        return previous[second.Length] <= limit ? previous[second.Length] : -1;
    }

    private static string WithoutIntegerGrouping(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length;)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                result.Append(text[index++]);
                continue;
            }
            int end = index + 1;
            while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == ',')) end++;
            string run = text[index..end];
            string[] groups = run.Split(',');
            bool integerGrouping = (index == 0 || text[index - 1] != '.') && groups.Length > 1 &&
                groups[0].Length is >= 1 and <= 3 && groups[0].All(char.IsAsciiDigit) &&
                groups.Skip(1).All(group => group.Length == 3 && group.All(char.IsAsciiDigit));
            result.Append(integerGrouping ? string.Concat(groups) : run);
            index = end;
        }
        return result.ToString();
    }

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

        // Both recognizers read Korean and Latin. Misread Hangul strokes can come out as Latin
        // letters: PP-OCR read the bold heading "내보내기" as "Ichah". Across 180 benchmark
        // images neither engine read a Latin word as Hangul syllables, which need a full
        // initial/vowel structure. A box read as only Hangul syllables by one engine and only
        // as unrelated Latin letters by the other therefore keeps the Hangul reading.
        if (first.Hangul >= 2 && first.Hangul == first.Letters && second.Latin >= 2 && second.Latin == second.Letters &&
            CommonSubsequence(first.LettersAndDigits, second.LettersAndDigits) == 0) return true;
        if (second.Hangul >= 2 && second.Hangul == second.Letters && first.Latin >= 2 && first.Latin == first.Letters &&
            CommonSubsequence(first.LettersAndDigits, second.LettersAndDigits) == 0) return false;

        // When both engines agree on every letter and digit, the candidates differ only in
        // spacing and marks. Small marks (commas, periods, quotes, operators) are dropped far
        // more often than they are invented at the right place, so keep the reading whose
        // marks contain all marks of the other in order. Substituted marks ("-" versus "_",
        // "•" versus ";") are not ordered by this evidence and fall through.
        if (first.Meaningful > 0 && first.LettersAndDigits == second.LettersAndDigits)
        {
            if (IsPunctuationSuperset(first.Compact, second.Compact)) return true;
            if (IsPunctuationSuperset(second.Compact, first.Compact)) return false;
        }

        // A longer string alone is not better. Accept containment only when source geometry
        // also extends beyond the shorter recognition, indicating actual truncation.
        OcrRect firstBounds = ReadingUnion(windows, orientation);
        OcrRect secondBounds = ReadingUnion(neural, orientation);
        if (IsBroadFragment(second, first, secondBounds, firstBounds)) return true;
        if (IsBroadFragment(first, second, firstBounds, secondBounds)) return false;
        if (Extends(first.Compact, second.Compact, firstBounds, secondBounds)) return true;
        if (Extends(second.Compact, first.Compact, secondBounds, firstBounds)) return false;

        // Ambiguous disagreements retain the existing Accurate engine. These dictionary-free
        // rules cannot establish the truth of two plausible alternatives; any rule change must
        // pass the dual-engine benchmark gate on every corpus before it ships.
        return false;
    }

    private static bool IsBroadFragment(Profile fragment, Profile longer, OcrRect fragmentBounds, OcrRect longerBounds)
    {
        // A tiny string assigned to almost the same broad source strip has much less text
        // coverage. Require a large length/density discrepancy and strong geometric coverage;
        // an actual short label with its own tight box must not trigger this rule. The fragment
        // need not be a substring: the few surviving characters can themselves be misrecognized.
        if (!HasArea(fragmentBounds) || !HasArea(longerBounds) || fragment.Compact.Length == 0 ||
            longer.Meaningful < 4 || longer.Compact.Length < fragment.Compact.Length * 4) return false;
        double verticalOverlap = Math.Min(fragmentBounds.Bottom, longerBounds.Bottom) -
            Math.Max(fragmentBounds.Y, longerBounds.Y);
        return SameRow(fragmentBounds, longerBounds) &&
            fragmentBounds.Width >= fragment.Compact.Length * fragmentBounds.Height * 1.5 &&
            HorizontalOverlap(fragmentBounds, longerBounds) >= Math.Max(fragmentBounds.Width, longerBounds.Width) * .8 &&
            verticalOverlap >= Math.Min(fragmentBounds.Height, longerBounds.Height) * .8;
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
            // Exact text corroboration below permits padded/narrower cell boxes to match
            // tight words by horizontal containment rather than their incompatible areas.
            // A word assigned to two cells is rejected, even when both cell texts agree.
            int[] matching = Enumerable.Range(0, words.Length)
                .Where(index => SameRow(words[index].Bounds, cells[cell]) &&
                    HorizontalOverlap(words[index].Bounds, cells[cell]) >=
                    Math.Min(words[index].Bounds.Width, cells[cell].Width) * .65).ToArray();
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

    private static bool IsPunctuationSuperset(string richer, string poorer)
    {
        if (richer.Length <= poorer.Length) return false;
        int index = 0;
        foreach (char character in richer)
        {
            if (index < poorer.Length && character == poorer[index]) index++;
            else if (char.IsLetterOrDigit(character) || char.IsSurrogate(character)) return false;
        }
        return index == poorer.Length;
    }

    private static bool IsCodeOperator(char character) => "{}[]();=<>!&|+-*/%:^~".Contains(character);

    private static OcrRect ReadingUnion(IEnumerable<OcrLine> lines, OcrReadingOrientation orientation) =>
        lines.Select(line => orientation.ToReading(line.Bounds)).Aggregate(default(OcrRect), OcrRect.Union);

    private static bool[,] CrossEngineRows(OcrRect[] bounds, string[] compact, int windowsCount)
    {
        var rows = new bool[bounds.Length, bounds.Length];
        for (int windows = 0; windows < windowsCount; windows++)
        for (int neural = windowsCount; neural < bounds.Length; neural++)
            rows[windows, neural] = rows[neural, windows] = SameRow(bounds[windows], bounds[neural]) ||
                OffsetRow(windows, neural, bounds, compact, windowsCount);
        return rows;
    }

    private static bool OffsetRow(int first, int second, OcrRect[] bounds, string[] compact, int windowsCount)
    {
        // PP-OCR boxes are padded by the unclip ratio and their center is often several
        // pixels above or below the tight Windows glyph box of the same text (observed up to
        // 0.7 of the glyph height). The smaller box must still lie mostly inside the larger
        // box with its center inside. That geometry identifies the row when the two texts
        // agree, or when the larger box holds no other row of the smaller box's engine.
        OcrRect a = bounds[first], b = bounds[second];
        if (!HasArea(a) || !HasArea(b)) return false;
        (int small, OcrRect inner, OcrRect outer) = a.Height <= b.Height ? (first, a, b) : (second, b, a);
        double overlap = VerticalOverlap(inner, outer);
        double center = CenterY(inner);
        if (overlap < inner.Height * .5 || center < outer.Y || center > outer.Bottom) return false;
        if (TextSupports(compact[first], compact[second])) return true;
        if (overlap < inner.Height * .8) return false;
        int start = small < windowsCount ? 0 : windowsCount, end = small < windowsCount ? windowsCount : bounds.Length;
        for (int other = start; other < end; other++)
        {
            if (other != small && HasArea(bounds[other]) && HorizontalOverlap(bounds[other], outer) > 0 &&
                VerticalOverlap(bounds[other], outer) >= bounds[other].Height * .8 && !SameRow(bounds[other], inner))
                return false;
        }
        return true;
    }

    private static bool TextSupports(string first, string second)
    {
        // Agreement on most characters in order (a common subsequence of at least 60% of
        // the longer text) is evidence that both boxes read the same text; a one-glyph
        // substitution in a four-syllable label still qualifies, unrelated text does not.
        if (first.Length == 0 || second.Length == 0) return false;
        if (string.Equals(first, second, StringComparison.Ordinal)) return true;
        if (Math.Min(first.Length, second.Length) < 2) return false;
        return CommonSubsequence(first, second) >= Math.Max(first.Length, second.Length) * .6;
    }

    private static int CommonSubsequence(string first, string second)
    {
        var previous = new int[second.Length + 1];
        var current = new int[second.Length + 1];
        for (int row = 1; row <= first.Length; row++)
        {
            for (int column = 1; column <= second.Length; column++)
                current[column] = first[row - 1] == second[column - 1] ? previous[column - 1] + 1
                    : Math.Max(previous[column], current[column - 1]);
            (previous, current) = (current, previous);
        }
        return previous[second.Length];
    }

    private static bool Contains(OcrRect outer, OcrRect inner) => HasArea(outer) && HasArea(inner) &&
        HorizontalOverlap(outer, inner) >= inner.Width * .8 && VerticalOverlap(outer, inner) >= inner.Height * .8;

    private static bool StackedHalves(OcrRect upper, OcrRect lower, OcrRect outer)
    {
        double top = Math.Min(upper.Y, lower.Y), bottom = Math.Max(upper.Bottom, lower.Bottom);
        double gap = Math.Max(upper.Y, lower.Y) - Math.Min(upper.Bottom, lower.Bottom);
        return !SameRow(upper, lower) && gap <= Math.Min(upper.Height, lower.Height) * .25 &&
            bottom - top <= outer.Height;
    }

    private static double VerticalOverlap(OcrRect left, OcrRect right) =>
        Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));

    // Region agreement of two boxes the caller has already placed on one row.
    private static bool Aligns(OcrRect left, OcrRect right)
    {
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

    private static double HorizontalOverlap(OcrRect left, OcrRect right) =>
        Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X));

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
        int OtherScript, double DefectRatio, bool Plausible, int Letters, int Hangul, int Latin)
    {
        internal static Profile Create(string text)
        {
            var compact = new StringBuilder(text.Length);
            var letters = new StringBuilder(text.Length);
            int count = 0, meaningful = 0, defects = 0, fragments = 0, otherScript = 0;
            int letterCount = 0, hangul = 0, latin = 0;
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
                    if (Rune.IsLetter(rune))
                    {
                        letterCount++;
                        if (rune.Value is >= 0xAC00 and <= 0xD7A3) hangul++;
                        else if (rune.Value is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
                    }
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
                count == 0 ? 1 : (defects + fragments * .5) / count, plausible, letterCount, hangul, latin);
        }
    }

    private sealed record RowMatch(int Row, int[] Cells, int Distance, OcrLine Line, bool UseCells);
}
