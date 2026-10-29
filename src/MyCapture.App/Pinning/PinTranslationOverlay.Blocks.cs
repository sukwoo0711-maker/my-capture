using System.Buffers;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static partial class PinTranslationOverlay
{
    /// <summary>Reading order with protected syntax, table cells and source-supported paragraph gaps.</summary>
    internal static IReadOnlyList<PinOverlayBlock> BuildBlocks(OcrResult result, BitmapSource? source = null)
    {
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        if (!extraction.HasGeometry) return [];
        byte[]? pixels = null;
        int width = source?.PixelWidth ?? 0, height = source?.PixelHeight ?? 0;
        try
        {
            if (source is not null)
            {
                BitmapSource converted = source.Format == PixelFormats.Bgra32 ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                pixels = ArrayPool<byte>.Shared.Rent(checked(width * height * 4));
                converted.CopyPixels(pixels, checked(width * 4), 0);
            }
            return BuildBlocksCore(result, extraction, pixels, width, height);
        }
        finally { if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels); }
    }

    private static IReadOnlyList<PinOverlayBlock> BuildBlocksCore(OcrResult result, OcrExtraction extraction,
        byte[]? pixels, int width, int height)
    {
        var blocks = new List<PinOverlayBlock>();
        OcrRect[] keycaps = pixels is null ? [] : FindKeyboardKeycaps(result.Lines, pixels, width, height);
        OcrRect[] codeLines = result.Lines.Where(line => FindComments(line.Text).Count == 0
                && IsCodeLine(WithoutRowNumber(line), extraction.Code.IsMonospaced == true && extraction.Code.Language is not null))
            .Select(line => line.Bounds).ToArray();
        OcrTable table = extraction.Table;
        bool useTable = table.HasStructure && OcrGeometry.IsValid(table.Bounds)
            && (table.Columns.Count >= 3 || table.HasHeader || table.Columns.Any(column => column.IsNumeric))
            && HasStableTableLanes(table);
        bool tableAdded = false;
        foreach (OcrParagraph paragraph in extraction.Paragraphs)
        {
            if (useTable && OcrGeometry.Contains(table.Bounds,
                    paragraph.Bounds.X + paragraph.Bounds.Width / 2, paragraph.Bounds.Y + paragraph.Bounds.Height / 2))
            {
                if (!tableAdded)
                {
                    foreach (OcrTableCell cell in table.Rows.SelectMany(row => row.Cells))
                    {
                        if (string.IsNullOrWhiteSpace(cell.Text) || !OcrGeometry.IsValid(cell.Bounds)) continue;
                        var lines = OcrExtractionAnalyzer.Analyze(result, cell.Bounds).Paragraphs
                            .SelectMany(item => item.SourceLines).ToArray();
                        AddParagraph(lines.Length > 0 ? lines : [new(cell.Text, cell.Bounds, [])], blocks,
                            extraction.Code, result.Lines, codeLines, pixels, width, height, keycaps);
                    }
                    tableAdded = true;
                }
            }
            else AddParagraph(paragraph.SourceLines, blocks, extraction.Code, result.Lines, codeLines, pixels, width, height, keycaps);
        }
        var unique = WithoutDuplicateWords(blocks).Select(block => block with
        {
            ProtectedRegions = block.ProtectedRegions.Concat(keycaps.Where(cap => Intersects(cap, block.Bounds))).Distinct().ToArray(),
        }).ToList();
        foreach (OcrRect cap in keycaps)
        {
            string text = string.Join(" ", result.Lines.SelectMany(line => line.Words)
                .Where(word => Intersects(word.Bounds, cap)).Select(word => word.Text));
            unique.Add(new(text, cap, [new(text, cap, [])]) { PreservePixels = true });
        }
        return unique;
    }

    private static bool HasStableTableLanes(OcrTable table)
    {
        OcrTableCell[] cells = table.Rows.SelectMany(row => row.Cells)
            .Where(cell => cell.Text.Length > 0 && OcrGeometry.IsValid(cell.Bounds)).ToArray();
        if (cells.Length == 0) return false;
        double tolerance = Math.Max(2, Median(cells.Select(cell => cell.Bounds.Height)) * .25);
        // An inferred prose table commonly assigns wide sentence fragments across several
        // column lanes. Real cells fit their lane, even with numeric right alignment.
        if (cells.Any(cell => cell.Bounds.X < cell.Region.X - tolerance
            || cell.Bounds.Right > cell.Region.Right + tolerance)) return false;
        return table.Rows.Count(row => row.Cells.Count(cell => cell.Text.Length > 0) >= 2)
            >= Math.Max(2, table.Rows.Count * .6);
    }

    private static OcrRect[] FindKeyboardKeycaps(IReadOnlyList<OcrLine> lines, byte[] pixels, int width, int height)
    {
        var capsules = new List<OcrRect>();
        int stride = width * 4;
        bool Neutral(int x, int y, int low, int high)
        {
            int offset = y * stride + x * 4;
            int minimum = Math.Min(pixels[offset], Math.Min(pixels[offset + 1], pixels[offset + 2]));
            int maximum = Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2]));
            return minimum >= low && maximum <= high && maximum - minimum <= 16;
        }
        foreach (OcrLine line in lines)
        {
            bool keyboard = KeyboardContext().IsMatch(line.Text)
                || KeyboardInstruction().IsMatch(line.Text) && AmbiguousKeyName().IsMatch(line.Text);
            if (!keyboard || !OcrGeometry.IsValid(line.Bounds)) continue;
            double lineHeight = line.Bounds.Height;
            int margin = Math.Max(4, (int)Math.Ceiling(lineHeight * .8));
            int left = Math.Max(0, (int)line.Bounds.X - margin), right = Math.Min(width, (int)Math.Ceiling(line.Bounds.Right) + margin);
            int top = Math.Max(0, (int)line.Bounds.Y - margin), bottom = Math.Min(height, (int)Math.Ceiling(line.Bounds.Bottom) + margin);
            int columns = right - left;
            if (columns <= 0 || bottom <= top) continue;
            var visited = new bool[columns * (bottom - top)];
            var component = new List<int>();
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int offset = (y - top) * columns + x - left;
                    if (visited[offset] || !Neutral(x, y, 96, 224)) continue;
                    component.Clear(); component.Add(offset); visited[offset] = true;
                    int minX = x, maxX = x, minY = y, maxY = y;
                    bool clipped = false;
                    for (int cursor = 0; cursor < component.Count; cursor++)
                    {
                        int xx = component[cursor] % columns + left, yy = component[cursor] / columns + top;
                        minX = Math.Min(minX, xx); maxX = Math.Max(maxX, xx);
                        minY = Math.Min(minY, yy); maxY = Math.Max(maxY, yy);
                        clipped |= xx == left || xx == right - 1 || yy == top || yy == bottom - 1;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nextX = xx + dx, nextY = yy + dy;
                                if (nextX < left || nextX >= right || nextY < top || nextY >= bottom) continue;
                                int next = (nextY - top) * columns + nextX - left;
                                if (visited[next] || !Neutral(nextX, nextY, 96, 224)) continue;
                                visited[next] = true; component.Add(next);
                            }
                    }
                    int capWidth = maxX - minX + 1, capHeight = maxY - minY + 1;
                    if (clipped || capWidth < lineHeight * .45 || capWidth > lineHeight * 12
                        || capHeight < lineHeight * .65 || capHeight > lineHeight * 2.2) continue;
                    // A keycap has four connected outline edges and a light neutral interior.
                    // Glyph counters, link underlines, code highlights and table rules do not.
                    int edge = Math.Max(2, (int)Math.Ceiling(capHeight * .08));
                    int upper = 0, lower = 0, first = 0, last = 0;
                    for (int xx = minX; xx <= maxX; xx++)
                    {
                        bool a = false, b = false;
                        for (int inset = 0; inset < edge; inset++)
                        { a |= Neutral(xx, minY + inset, 96, 224); b |= Neutral(xx, maxY - inset, 96, 224); }
                        if (a) upper++; if (b) lower++;
                    }
                    for (int yy = minY; yy <= maxY; yy++)
                    {
                        bool a = false, b = false;
                        for (int inset = 0; inset < edge; inset++)
                        { a |= Neutral(minX + inset, yy, 96, 224); b |= Neutral(maxX - inset, yy, 96, 224); }
                        if (a) first++; if (b) last++;
                    }
                    if (Math.Min(upper, lower) < capWidth * .65 || Math.Min(first, last) < capHeight * .65) continue;
                    int interior = 0, fill = 0;
                    for (int yy = minY + edge; yy <= maxY - edge; yy++)
                        for (int xx = minX + edge; xx <= maxX - edge; xx++)
                        { interior++; if (Neutral(xx, yy, 228, 252)) fill++; }
                    if (interior == 0 || fill < interior * .5) continue;
                    OcrRect capsule = Inflate(new(minX, minY, capWidth, capHeight), Math.Max(2, Math.Ceiling(lineHeight * .12)), width, height);
                    bool duplicate = false;
                    foreach (OcrRect other in capsules)
                    {
                        double overlap = Math.Max(0, Math.Min(other.Right, capsule.Right) - Math.Max(other.X, capsule.X))
                            * Math.Max(0, Math.Min(other.Bottom, capsule.Bottom) - Math.Max(other.Y, capsule.Y));
                        if (overlap >= Math.Min(other.Width * other.Height, capsule.Width * capsule.Height) * .7)
                        { duplicate = true; break; }
                    }
                    if (!duplicate) capsules.Add(capsule);
                }
        }
        // Preserve the connector between neighboring keys as part of the shortcut.
        // This also protects keys that OCR skipped entirely beside a recognized key.
        var shortcuts = new List<OcrRect>();
        foreach (OcrRect capsule in capsules.OrderBy(cap => cap.Y).ThenBy(cap => cap.X))
        {
            int neighbor = shortcuts.FindIndex(other => Math.Min(other.Bottom, capsule.Bottom) - Math.Max(other.Y, capsule.Y)
                    >= Math.Min(other.Height, capsule.Height) * .6
                && Math.Max(other.X, capsule.X) - Math.Min(other.Right, capsule.Right) <= Math.Min(other.Height, capsule.Height) * .5);
            if (neighbor >= 0) shortcuts[neighbor] = OcrRect.Union(shortcuts[neighbor], capsule);
            else shortcuts.Add(capsule);
        }
        // OCR can join a key label, connector and following suffix into one word.
        // Removing that word transfers its complete footprint to the protected span.
        for (int index = 0; index < shortcuts.Count; index++)
        {
            OcrRect shortcut = shortcuts[index];
            foreach (OcrWord word in lines.SelectMany(line => line.Words))
                if (Intersects(shortcut, word.Bounds)) shortcuts[index] = OcrRect.Union(shortcuts[index], word.Bounds);
        }
        return shortcuts.ToArray();
    }

    [GeneratedRegex(@"(?:\+|\b(?:cmd|command|ctrl|control|alt|ait|shift|print\s*screen|prtsc)(?![a-z])|커맨드|컨트롤|시프트)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex KeyboardContext();

    [GeneratedRegex(@"(?:\b(?:press|key|keyboard|shortcut|hold)\b|키|누르)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex KeyboardInstruction();

    [GeneratedRegex(@"(?:\b(?:space|enter|return|escape|esc|tab|win|windows|backspace|delete|del|home|end|page\s*(?:up|down)|[a-z0-9])\b|스페이스|엔터)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex AmbiguousKeyName();

    private static IReadOnlyList<PinOverlayBlock> WithoutDuplicateWords(IReadOnlyList<PinOverlayBlock> blocks)
    {
        var seen = new HashSet<(string Text, OcrRect Bounds)>();
        var unique = new Dictionary<int, PinOverlayBlock>();
        // Prefer the complete paragraph when a table selection and a boundary-crossing
        // paragraph both contain the same source word. Emit that pixel region only once.
        foreach (int index in Enumerable.Range(0, blocks.Count).OrderByDescending(index => blocks[index].Lines.Count)
            .ThenByDescending(index => blocks[index].Text.Length))
        {
            PinOverlayBlock block = blocks[index];
            var lines = new List<OcrLine>();
            bool changed = false;
            foreach (OcrLine line in block.Lines)
            {
                OcrWord[] words = line.Words.Where(word => seen.Add((word.Text, word.Bounds))).ToArray();
                changed |= words.Length != line.Words.Count;
                if (words.Length > 0) lines.Add(words.Length == line.Words.Count ? line : MakeLine(words));
                else if (line.Words.Count == 0 && seen.Add((line.Text, line.Bounds))) lines.Add(line);
            }
            if (lines.Count == 0) continue;
            unique.Add(index, changed ? block with
            {
                Text = string.Join(" ", lines.Select(line => line.Text)),
                Lines = lines.ToArray(),
                Bounds = lines.Select(line => line.Bounds).Aggregate(default(OcrRect), OcrRect.Union),
            } : block);
        }
        return unique.OrderBy(item => item.Key).Select(item => item.Value).ToArray();
    }

    private enum OverlayLineKind { Prose, Comment, Code }
    private sealed record OverlayLine(OcrLine Line, OverlayLineKind Kind, IReadOnlyList<OcrRect> Protected)
    {
        internal bool StartsListItem { get; init; }
    }

    private readonly record struct OverlayInkStyle(double Height, double FullHeight, double Stroke, Color Background);

    private static void AddParagraph(IReadOnlyList<OcrLine> lines, List<PinOverlayBlock> output,
        OcrCode code, IReadOnlyList<OcrLine> sourceLines, IReadOnlyList<OcrRect> codeLines,
        byte[]? pixels, int width, int height, IReadOnlyList<OcrRect> keycaps)
    {
        var groups = new List<List<OverlayLine>>();
        var styles = new Dictionary<OcrLine, OverlayInkStyle>();
        double ordinaryGap = TypicalParagraphGap(lines);
        foreach (OcrLine line in lines)
        {
            foreach (OverlayLine part in SplitLine(line, code, sourceLines, codeLines, pixels, width, height, keycaps))
            {
                styles[part.Line] = ReadInkStyle(part.Line, pixels, width, height);
                // Rejoin only vertically adjacent lines with horizontal overlap; never bridge a column gutter.
                // Comments have a marker per line, and code must never join any translated run.
                List<OverlayLine>? group = part.Kind != OverlayLineKind.Prose || part.StartsListItem ? null : groups.LastOrDefault(items =>
                    items[^1].Kind == OverlayLineKind.Prose
                    && !LooksLikeHeadingThenBody(items, part)
                    && !DifferentInkStyle(styles[items[^1].Line], styles[part.Line])
                    && part.Line.Bounds.Y - items[^1].Line.Bounds.Bottom
                        <= Math.Max(ordinaryGap * 1.6, Math.Max(part.Line.Bounds.Height, items[^1].Line.Bounds.Height) * .5)
                    && part.Line.Bounds.Y >= items[^1].Line.Bounds.Bottom - Math.Min(part.Line.Bounds.Height, items[^1].Line.Bounds.Height) * .2
                    && part.Line.Bounds.Y - items[^1].Line.Bounds.Bottom <= Math.Max(part.Line.Bounds.Height, items[^1].Line.Bounds.Height) * .85
                    && Math.Min(part.Line.Bounds.Right, items[^1].Line.Bounds.Right) - Math.Max(part.Line.Bounds.X, items[^1].Line.Bounds.X)
                        > Math.Min(part.Line.Bounds.Width, items[^1].Line.Bounds.Width) * .25
                    && !part.Protected.Concat(items.SelectMany(item => item.Protected)).Any(rect =>
                        Intersects(rect, OcrRect.Union(part.Line.Bounds, items[^1].Line.Bounds))));
                if (group is null) { group = []; groups.Add(group); }
                group.Add(part);
            }
        }
        foreach (List<OverlayLine> group in groups)
            output.Add(new(string.Join(" ", group.Select(item => item.Line.Text.Trim())),
                group.Select(item => item.Line.Bounds).Aggregate(default(OcrRect), OcrRect.Union), group.Select(item => item.Line).ToArray())
            {
                PreservePixels = group[0].Kind == OverlayLineKind.Code,
                ProtectedRegions = group.SelectMany(item => item.Protected).Distinct().ToArray(),
            });
    }

    private static double TypicalParagraphGap(IReadOnlyList<OcrLine> lines)
    {
        OcrLine[] ordered = lines.OrderBy(line => line.Bounds.Y).ToArray();
        var gaps = new List<double>();
        for (int index = 1; index < ordered.Length; index++)
        {
            OcrRect first = ordered[index - 1].Bounds, next = ordered[index].Bounds;
            double gap = next.Y - first.Bottom;
            if (gap <= 0 || gap > Math.Max(first.Height, next.Height) * .85) continue;
            if (Math.Min(first.Right, next.Right) - Math.Max(first.X, next.X) < Math.Min(first.Width, next.Width) * .25) continue;
            gaps.Add(gap);
        }
        return gaps.Count > 0 ? Median(gaps) : double.PositiveInfinity;
    }

    private static bool DifferentInkStyle(OverlayInkStyle first, OverlayInkStyle second)
    {
        double small = Math.Min(first.Height, second.Height), large = Math.Max(first.Height, second.Height);
        double smallFull = Math.Min(first.FullHeight, second.FullHeight), largeFull = Math.Max(first.FullHeight, second.FullHeight);
        // A sparse ascender can change either the full extent or the dominant band
        // at the same font size. A size boundary needs both independent extents to
        // change in the same direction; font weight and surface are checked below.
        if (small > 0 && large > small * 1.21 && smallFull > 0 && largeFull > smallFull * 1.21
            && (first.Height - second.Height) * (first.FullHeight - second.FullHeight) > 0) return true;
        if (DifferentSurface(first.Background, second.Background)) return true;
        // Area divided by the foreground perimeter estimates stroke thickness without
        // confusing a short heading's word spacing with a change in font weight.
        double thin = Math.Min(first.Stroke, second.Stroke), thick = Math.Max(first.Stroke, second.Stroke);
        return thin > 0 && thick > thin * 1.42 && thick - thin > .45;
    }

    private static bool DifferentSurface(Color first, Color second) =>
        Math.Max(Math.Abs(first.R - second.R), Math.Max(Math.Abs(first.G - second.G), Math.Abs(first.B - second.B))) > 48;

    private static OverlayInkStyle ReadInkStyle(OcrLine line, byte[]? pixels, int width, int height)
    {
        // OCR extents alone cannot distinguish font size from descenders. Without
        // source pixels neither ink-height measurement nor stroke weight is known.
        if (pixels is null) return new(0, 0, 0, Colors.White);
        int stride = width * 4;
        Color background = EstimateBackground(pixels, width, height, stride, line.Bounds).Color;
        int topInk = int.MaxValue, bottomInk = -1, area = 0, edges = 0;
        int rowTop = Math.Max(0, (int)Math.Floor(line.Bounds.Y));
        int[] rowInk = new int[Math.Max(1, Math.Min(height, (int)Math.Ceiling(line.Bounds.Bottom)) - rowTop)];
        bool Ink(int x, int y) => x >= 0 && x < width && y >= 0 && y < height
            && PixelVariance(pixels, y * stride + x * 4, background) >= 40 * 40;
        foreach (OcrRect word in line.Words.Select(word => Clamp(word.Bounds, width, height)))
            for (int y = (int)word.Y; y < (int)Math.Ceiling(word.Bottom); y++)
                for (int x = (int)word.X; x < (int)Math.Ceiling(word.Right); x++)
                {
                    if (!Ink(x, y)) continue;
                    topInk = Math.Min(topInk, y); bottomInk = Math.Max(bottomInk, y); area++;
                    rowInk[y - rowTop]++;
                    if (!Ink(x - 1, y)) edges++;
                    if (!Ink(x + 1, y)) edges++;
                    if (!Ink(x, y - 1)) edges++;
                    if (!Ink(x, y + 1)) edges++;
                }
        // A line with a descender is taller than a line without one at exactly the
        // same font size. Compare the dominant ink band; sparse ascenders, dots and
        // descenders must not turn an ordinary paragraph or subtitle into headings.
        int threshold = Math.Max(2, (int)Math.Ceiling(rowInk.Max() * .2));
        int firstBody = Array.FindIndex(rowInk, count => count >= threshold);
        int lastBody = Array.FindLastIndex(rowInk, count => count >= threshold);
        double inkHeight = firstBody >= 0 ? lastBody - firstBody + 1
            : bottomInk >= topInk ? bottomInk - topInk + 1 : line.Bounds.Height;
        return new(inkHeight, bottomInk >= topInk ? bottomInk - topInk + 1 : line.Bounds.Height,
            edges > 0 ? 2d * area / edges : 0, background);
    }

    private static bool LooksLikeHeadingThenBody(IReadOnlyList<OverlayLine> group, OverlayLine next)
    {
        if (group.Count != 1) return false;
        OcrLine first = group[0].Line, second = next.Line;
        return first.Bounds.Width < second.Bounds.Width * .55
            && first.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6
            && first.Text.Length > 0 && first.Text[^1] is not ('.' or '!' or '?' or ',' or ':' or ';')
            && Math.Abs(first.Bounds.X - second.Bounds.X) <= Math.Max(first.Bounds.Height, second.Bounds.Height) * .5;
    }

    private static IEnumerable<OverlayLine> SplitLine(OcrLine line, OcrCode code,
        IReadOnlyList<OcrLine> sourceLines, IReadOnlyList<OcrRect> codeLines, byte[]? pixels, int width, int height,
        IReadOnlyList<OcrRect> keycaps)
    {
        if (keycaps.Count > 0 && line.Words.Count > 0)
        {
            OcrWord[] prose = line.Words.Where(word => !keycaps.Any(cap => Intersects(cap, word.Bounds))).ToArray();
            if (prose.Length == 0) yield break;
            if (prose.Length != line.Words.Count) line = MakeLine(prose);
        }
        // Extraction can turn a single code row into several runs around a long string-literal
        // gap. The original OCR row still supplies the syntax that protects every fragment.
        if (codeLines.Any(bounds => OcrGeometry.Contains(bounds,
                line.Bounds.X + line.Bounds.Width / 2, line.Bounds.Y + line.Bounds.Height / 2)))
        {
            yield return new(line, OverlayLineKind.Code, []);
            yield break;
        }
        OcrWord[] words = line.Words.Where(word => OcrGeometry.IsValid(word.Bounds)
            && !string.IsNullOrWhiteSpace(word.Text)).OrderBy(word => word.Bounds.X).ToArray();
        if (words.Length == 0)
        {
            if (!OcrGeometry.IsValid(line.Bounds) || string.IsNullOrWhiteSpace(line.Text)) yield break;
            words = [new(line.Text, line.Bounds)];
            line = line with { Words = words };
        }
        double wordHeight = Median(words.Select(word => word.Bounds.Height));
        double gap = Math.Max(6, wordHeight * 2);
        double[] spaces = words.Zip(words.Skip(1), (first, next) => next.Bounds.X - first.Bounds.Right)
            .Where(space => space > 0).ToArray();
        double[] wordSpaces = spaces.Where(space => space <= wordHeight * .65).ToArray();
        double navigationGap = Math.Max(6, Math.Max(wordHeight * 1.15,
            wordSpaces.Length > 0 ? Median(wordSpaces) * 2.5 : gap));
        // Repeated inter-item gaps establish a navigation row. Its ordinary word spaces
        // stay together, including two-word labels and product names with a number.
        if (words.Length >= 5 && wordSpaces.Length >= 2 && spaces.Count(space => space >= navigationGap) >= 3)
            gap = navigationGap - .01;
        var run = new List<OcrWord>();
        var separated = new List<OcrLine>();
        var rules = new List<OcrRect>();
        bool menuRow = words.Count(word => IsChevron(word.Text)) >= 2;
        Color? previousBackground = null;
        foreach (OcrWord word in words)
        {
            // Repeated dropdown controls divide a navigation row even though their
            // gaps are narrower than a column gutter. Their pixels remain controls.
            if (menuRow && (IsChevron(word.Text) || word.Text.Length <= 3
                && word.Text.All(character => char.IsPunctuation(character) || char.IsSymbol(character))))
            {
                if (run.Count > 0) { separated.Add(MakeLine(run)); run.Clear(); }
                rules.Add(word.Bounds);
                previousBackground = null;
                continue;
            }
            Color? background = pixels is null ? null : EstimateBackground(pixels, width, height, width * 4, word.Bounds).Color;
            OcrRect? rule = run.Count == 0 || pixels is null ? null
                : FindVerticalRule(pixels, width, height, line.Bounds, run[^1].Bounds.Right, word.Bounds.X);
            bool wideGap = run.Count > 0 && word.Bounds.X - run[^1].Bounds.Right > gap
                && !SpannedByAdjacentLine(line, run[^1].Bounds.Right, word.Bounds.X, sourceLines);
            bool changedSurface = previousBackground is { } previous && background is { } current
                && DifferentSurface(previous, current);
            if (run.Count > 0 && (wideGap || rule is not null || changedSurface))
            {
                separated.Add(MakeLine(run));
                if (rule is not null) rules.Add(rule.Value);
                run.Clear();
            }
            run.Add(word);
            previousBackground = background;
        }
        if (run.Count > 0) separated.Add(MakeLine(run));
        foreach (OcrLine separatedLine in separated)
        {
            OcrLine withoutMarker = TrimListMarker(separatedLine, pixels, width, height, out OcrRect? marker);
            OcrLine content = TrimEdgeIcons(withoutMarker, out IReadOnlyList<OcrRect> icons);
            IEnumerable<OcrRect> protectedRegions = rules.Concat(icons);
            if (marker is { } bounds) protectedRegions = protectedRegions.Append(bounds);
            foreach (OverlayLine part in ClassifyLine(content, code, protectedRegions.ToArray()))
                yield return part with { StartsListItem = marker is not null };
        }
    }

    private static bool IsChevron(string text) => text is "v" or "V" or ">" or "›" or "∨" or "⌄" or "▾" or "▼";

    private static OcrLine TrimListMarker(OcrLine line, byte[]? pixels, int width, int height, out OcrRect? marker)
    {
        marker = null;
        Match match = OverlayListMarker().Match(line.Text);
        if (!match.Success) return TrimRaisedFootnote(line, pixels, width, height, out marker);
        OcrLine? prefix = SliceLine(line, 0, match.Length), body = SliceLine(line, match.Length, line.Text.Length);
        if (prefix is null || body is null) return line;
        marker = prefix.Bounds;
        return body;
    }

    [GeneratedRegex(@"^(?:[†‡§⁰¹²³⁴⁵⁶⁷⁸⁹]+\s*|\*(?=\S)\s*|(?:[•●◦▪▫‣⁃*−–—-]|\(?\d+(?:\.\d+)*\s*[.)]|\(?[A-Za-z][.)])\s+)", RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex OverlayListMarker();

    private static OcrLine TrimRaisedFootnote(OcrLine line, byte[]? pixels, int width, int height, out OcrRect? marker)
    {
        marker = null;
        if (pixels is null || line.Words.Count < 3) return line;
        OcrWord first = line.Words[0];
        if (first.Text.Length < 2 || first.Text[0] is not ('t' or 'T') || !char.IsAsciiLetter(first.Text[1])) return line;
        double bodyTop = Median(line.Words.Skip(1).Select(word => word.Bounds.Y));
        double bodyHeight = Median(line.Words.Skip(1).Select(word => word.Bounds.Height));
        if (bodyTop - first.Bounds.Y < Math.Max(2, bodyHeight * .15)) return line;
        OcrRect area = Clamp(first.Bounds, width, height);
        Color background = EstimateBackground(pixels, width, height, width * 4, area).Color;
        int left = -1, right = -1, top = int.MaxValue, bottom = -1, next = -1;
        for (int x = (int)area.X; x < (int)Math.Ceiling(area.Right); x++)
        {
            int columnTop = int.MaxValue, columnBottom = -1;
            for (int y = (int)area.Y; y < (int)Math.Ceiling(area.Bottom); y++)
            {
                if (PixelVariance(pixels, (y * width + x) * 4, background) < 40 * 40) continue;
                columnTop = Math.Min(columnTop, y); columnBottom = Math.Max(columnBottom, y);
            }
            if (columnBottom < columnTop)
            {
                if (left >= 0 && right < 0) right = x;
                continue;
            }
            if (right >= 0) { next = x; break; }
            if (left < 0) left = x;
            top = Math.Min(top, columnTop); bottom = Math.Max(bottom, columnBottom);
        }
        // Korean Windows OCR reads raised daggers as a leading 't'. Only an isolated,
        // raised narrow glyph qualifies; ordinary words beginning with t stay untouched.
        if (left < 0 || next < 0 || right - left > bodyHeight * .65 || right - left < 1
            || bodyTop - top < Math.Max(2, bodyHeight * .15)
            || bottom - top + 1 > bodyHeight * 1.15 || next - right < 1
            || bottom + 1 > first.Bounds.Bottom - Math.Max(2, bodyHeight * .2)) return line;
        int textTop = int.MaxValue, textBottom = -1;
        for (int y = (int)area.Y; y < (int)Math.Ceiling(area.Bottom); y++)
            for (int x = next; x < (int)Math.Ceiling(area.Right); x++)
            {
                if (PixelVariance(pixels, (y * width + x) * 4, background) < 40 * 40) continue;
                textTop = Math.Min(textTop, y); textBottom = Math.Max(textBottom, y);
            }
        // Compare against the remaining glyphs of the same word, not just other words'
        // OCR tops: an ordinary capital T beside lowercase/descending words is not raised.
        if (textBottom < textTop || (textTop + textBottom - top - bottom) / 2d < bodyHeight * .3) return line;
        marker = new(left, top, right - left, bottom - top + 1);
        OcrWord body = first with { Text = first.Text[1..], Bounds = first.Bounds with
            { X = next, Width = first.Bounds.Right - next, Y = Math.Max(first.Bounds.Y, bodyTop),
                Height = first.Bounds.Bottom - Math.Max(first.Bounds.Y, bodyTop) } };
        return MakeLine([body, .. line.Words.Skip(1)]);
    }

    private static OcrLine MakeLine(IReadOnlyList<OcrWord> source) => new(string.Join(" ", source.Select(word => word.Text)),
        source.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), source.ToArray());

    private static OcrLine TrimEdgeIcons(OcrLine line, out IReadOnlyList<OcrRect> protectedRegions)
    {
        var words = line.Words.ToList();
        var icons = new List<OcrRect>();
        double separation = Median(words.Select(word => word.Bounds.Height)) * .4;
        // Code/comment syntax is classified before its symbols can be mistaken for controls.
        if (!IsCodeLine(line.Text, false) && FindComments(line.Text).Count == 0)
        {
            if (words.Count > 1 && IsIconToken(words[0])
                && words[1].Bounds.X - words[0].Bounds.Right >= separation)
            {
                icons.Add(words[0].Bounds);
                words.RemoveAt(0);
            }
            if (words.Count > 1 && IsIconToken(words[^1])
                && words[^1].Bounds.X - words[^2].Bounds.Right >= separation)
            {
                icons.Add(words[^1].Bounds);
                words.RemoveAt(words.Count - 1);
            }
        }
        protectedRegions = icons;
        return MakeLine(words);
    }

    private static bool IsIconToken(OcrWord word)
    {
        string text = word.Text.Trim();
        if (text.Length is < 1 or > 2 || text is "a" or "A" or "I") return false;
        // A square icon can be misread as a bracket plus a Hangul letter by the Korean OCR
        // pack (for example "[애"). A normal bracketed text run is wider than one glyph.
        if (text.Length == 2 && text.Any(character => "[](){}<>".Contains(character))
            && word.Bounds.Width <= word.Bounds.Height * 1.3) return true;
        return text.Count(char.IsAsciiLetter) <= 1
            && text.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetter(character)
            || char.IsSymbol(character) || char.IsPunctuation(character));
    }

    private static IEnumerable<OverlayLine> ClassifyLine(OcrLine line, OcrCode code, IReadOnlyList<OcrRect> protectedRegions)
    {
        List<(int Start, int End)> comments = FindComments(line.Text);
        if (comments.Count > 0)
        {
            var translatedRanges = comments.Where(span => !IsCodeLine(line.Text[span.Start..span.End], false)).ToArray();
            int cursor = 0;
            foreach ((int start, int end) in translatedRanges)
            {
                OcrLine? literal = SliceLine(line, cursor, start);
                if (literal is not null) yield return new(literal, OverlayLineKind.Code, protectedRegions);
                OcrLine? comment = SliceLine(line, start, end);
                if (comment is not null)
                {
                    OcrLine? prefix = SliceLine(line, 0, start), suffix = SliceLine(line, end, line.Text.Length);
                    yield return new(comment, OverlayLineKind.Comment, protectedRegions
                        .Concat(new[] { prefix, suffix }.OfType<OcrLine>().Select(item => item.Bounds)).ToArray());
                }
                cursor = end;
            }
            OcrLine? tail = SliceLine(line, cursor, line.Text.Length);
            if (tail is not null) yield return new(tail, OverlayLineKind.Code, protectedRegions);
            yield break;
        }
        bool monospaced = code.IsMonospaced == true && code.Language is not null
            && code.Regions.Any(region => OcrGeometry.Contains(region,
                line.Bounds.X + line.Bounds.Width / 2, line.Bounds.Y + line.Bounds.Height / 2));
        yield return new(line, IsCodeLine(line.Text, monospaced) ? OverlayLineKind.Code : OverlayLineKind.Prose, protectedRegions);
    }

    private static bool IsCodeLine(string text, bool monospaced)
    {
        text = text.Trim();
        if (PinTranslationPlanner.LooksLikeCodeLine(text) || OverlayDeclaration().IsMatch(text)
            || OverlayCall().IsMatch(text) || OverlayPrimitiveDeclaration().IsMatch(text)
            || OverlaySqlOrBasicStatement().IsMatch(text)) return true;
        int syntax = text.Count(character => ";{}()=<>".Contains(character));
        int visible = text.Count(character => !char.IsWhiteSpace(character));
        return monospaced && syntax >= 2 && syntax >= visible * .12;
    }

    [GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|async|override|virtual|abstract|sealed|readonly)\s+.*[()<>{};=]|(?:await|return|throw|yield|break|continue)\b.*;|#(?:include|define|if|ifdef|ifndef|else|elif|endif|pragma|region|endregion|nullable)\b.*)$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex OverlayDeclaration();

    // OCR sometimes puts spaces inside a call's name. Closing ');' still identifies the
    // complete statement without relying on a language label or on the damaged identifier.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.$\s]*\([^\r\n]*\)\s*;$", RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex OverlayCall();

    [GeneratedRegex(@"^(?:(?:const|static|readonly|volatile)\s+)*(?:bool|byte|sbyte|char|short|ushort|int|uint|long|ulong|float|double|decimal|string|object|var|void)\s*[?*\[\]]*\s+[A-Za-z_]\w*\s*(?:,\s*[A-Za-z_]\w*\s*)*;$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex OverlayPrimitiveDeclaration();

    [GeneratedRegex(@"^(?:SELECT\b.+\bFROM\b|INSERT\s+INTO\b|UPDATE\s+\w+\s+SET\b|DELETE\s+FROM\b|CREATE\s+(?:TABLE|INDEX|VIEW)\b|(?:Dim|Const)\s+\w+\s*(?:=|As\b)|Call\s+\w+\s*\().*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex OverlaySqlOrBasicStatement();

    private static string WithoutRowNumber(OcrLine line)
    {
        if (line.Words.Count > 1 && line.Words[0].Text.All(char.IsAsciiDigit)
            && line.Words[1].Bounds.X - line.Words[0].Bounds.Right >= line.Bounds.Height * .4)
            return string.Join(" ", line.Words.Skip(1).Select(word => word.Text));
        return line.Text;
    }

    private static bool SpannedByAdjacentLine(OcrLine line, double left, double right, IReadOnlyList<OcrLine> sourceLines)
    {
        double middle = (left + right) / 2, tolerance = line.Bounds.Height * .25;
        return sourceLines.Any(other => Math.Abs(other.Bounds.Y - line.Bounds.Y) > line.Bounds.Height * .5
            && Math.Abs(other.Bounds.Y - line.Bounds.Y) <= line.Bounds.Height * 1.75
            // An OCR row can include a sidebar before the body column. Use the
            // words at this column's indent, not the row's unrelated left edge.
            && other.Words.Any(word => Math.Abs(word.Bounds.X - line.Bounds.X) <= line.Bounds.Height * 2)
            && other.Bounds.Right >= right
            && other.Words.Any(word => word.Bounds.X < middle + tolerance && word.Bounds.Right > middle - tolerance));
    }

    /// <summary>Comment bodies only; quoted strings, URLs and apostrophes inside words stay literal.</summary>
    private static List<(int Start, int End)> FindComments(string text)
    {
        var comments = new List<(int Start, int End)>();
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            bool atStart = string.IsNullOrWhiteSpace(text[..index]);
            if ((index == 0 || char.IsWhiteSpace(text[index - 1]))
                && (text.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || text.AsSpan(index).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || text.AsSpan(index).StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)))
            {
                while (index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1])) index++;
                continue;
            }
            if (current == '\'' && index > 0 && char.IsLetterOrDigit(text[index - 1])) continue;
            int quoteEnd = current == '\'' ? ClosingQuote(text, index, '\'') : -1;
            string before = text[..index].TrimEnd();
            bool singleQuotedLiteral = quoteEnd >= 0 && (atStart || before.Length == 0
                || "=(:,[{".Contains(before[^1]) || (quoteEnd + 1 < text.Length && ";,)]}".Contains(text[quoteEnd + 1])));
            if (current == '"' || current == '`' || singleQuotedLiteral)
            {
                int closingQuote = current == '\'' ? quoteEnd : ClosingQuote(text, index, current);
                index = closingQuote < 0 ? text.Length : closingQuote;
                continue;
            }
            bool slash = current == '/' && next == '/' && (index == 0 || text[index - 1] != ':');
            bool block = current == '/' && next == '*';
            bool hash = current == '#' && (atStart || char.IsWhiteSpace(text[index - 1]) && IsInlineHashPrefix(text[..index]))
                && !OverlayDeclaration().IsMatch(text[index..]);
            bool dash = current == '-' && next == '-' && (atStart || IsCodeLine(text[..index], false));
            bool apostrophe = current == '\'' && (atStart || IsCodeLine(text[..index], false));
            if (!slash && !block && !hash && !dash && !apostrophe) continue;
            int start = index + (slash || block || dash ? 2 : 1);
            if (slash) while (start < text.Length && text[start] == '/') start++;
            int close = block ? text.IndexOf("*/", start, StringComparison.Ordinal) : -1;
            int end = close < 0 ? text.Length : close;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end > start) comments.Add((start, end));
            if (!block || close < 0) break;
            index = close + 1;
        }
        return comments;
    }

    private static bool IsInlineHashPrefix(string text) => IsCodeLine(text, false)
        // OCR can lose an assignment's '=' while retaining its numeric operands.
        // This narrow numeric expression case does not classify prose mentions of '#'.
        || text.Any(char.IsAsciiDigit) && text.All(character => char.IsAsciiDigit(character)
            || char.IsWhiteSpace(character) || "=+-*/%().[]".Contains(character));

    private static int ClosingQuote(string text, int start, char quote)
    {
        bool verbatim = quote == '"' && start > 0 && text[start - 1] == '@';
        for (int index = start + 1; index < text.Length; index++)
        {
            if (!verbatim && text[index] == '\\') { index++; continue; }
            if (text[index] != quote) continue;
            if (verbatim && index + 1 < text.Length && text[index + 1] == quote) { index++; continue; }
            return index;
        }
        return -1;
    }

    private static OcrLine? SliceLine(OcrLine line, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(line.Text[start])) start++;
        while (end > start && char.IsWhiteSpace(line.Text[end - 1])) end--;
        if (start == end) return null;
        var words = new List<OcrWord>();
        int cursor = 0;
        foreach (OcrWord word in line.Words)
        {
            int from = Math.Max(0, start - cursor), to = Math.Min(word.Text.Length, end - cursor);
            if (to > from)
            {
                double columns = OcrGeometry.CharacterColumns(word.Text);
                double left = from == 0 ? 0 : OcrGeometry.CharacterColumns(word.Text[..from]) / columns;
                double right = to == word.Text.Length ? 1 : OcrGeometry.CharacterColumns(word.Text[..to]) / columns;
                words.Add(new(word.Text[from..to], word.Bounds with
                { X = word.Bounds.X + word.Bounds.Width * left, Width = word.Bounds.Width * (right - left) }));
            }
            cursor += word.Text.Length + 1;
        }
        return words.Count == 0 ? null : MakeLine(words);
    }

    private static OcrRect? FindVerticalRule(byte[] pixels, int width, int height, OcrRect line, double left, double right)
    {
        int first = Math.Max(1, (int)Math.Ceiling(left)), last = Math.Min(width - 2, (int)Math.Floor(right) - 1);
        int extension = Math.Max(2, (int)Math.Ceiling(line.Height * .25));
        int top = (int)Math.Floor(line.Y) - extension, bottom = (int)Math.Ceiling(line.Bottom) + extension;
        if (last < first || line.Height < 4 || top < 0 || bottom > height) return null;
        Color ring = EstimateBackground(pixels, width, height, width * 4, line).Color;
        const int maxThickness = 2;
        bool Similar(Color a, Color b, int tolerance) => Math.Abs(a.R - b.R) <= tolerance
            && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;
        bool Uniform(int x, Color reference)
        {
            for (int y = top; y < bottom; y++)
                if (!Similar(Pixel(pixels, width * 4, x, y), reference, 8)) return false;
            return true;
        }
        for (int x = first; x <= last; x++)
        {
            Color color = Pixel(pixels, width * 4, x, top);
            if (Similar(color, ring, 12) || !Uniform(x, color)) continue;
            int finish = x + 1;
            while (finish <= last && Uniform(finish, color)) finish++;
            int thickness = finish - x;
            if (thickness <= maxThickness && !Similar(Pixel(pixels, width * 4, x - 1, top), color, 8)
                && !Similar(Pixel(pixels, width * 4, finish, top), color, 8))
                return new(x, top, thickness, bottom - top);
            x = finish - 1;
        }
        return null;
    }

}
