using System.Text;
using System.Text.RegularExpressions;

namespace MyCapture.App.Pinning;

internal static partial class PinTranslationPlanner
{
    /// <summary>
    /// Plans a Markdown document. Syntax, code, URLs and structure are Literal; headings, paragraphs, list
    /// items, quotes and table cells are translatable pieces; the body of a mermaid fence is planned as a diagram.
    /// </summary>
    internal static PinTranslationPlan CreateMarkdown(string source, PinTranslationDirection direction)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<MdPlanning.Cut> cuts;
        try
        {
            cuts = MdPlanning.IsDiagramText(source)
                ? MdPlanning.ScanDiagram(source, direction)
                : MdPlanning.ScanMarkdown(source, direction);
        }
        catch (RegexMatchTimeoutException)
        {
            cuts = [];
        }
        return new PinTranslationPlan(PinTextFormat.Markdown, MdPlanning.Pieces(source, cuts));
    }

    /// <summary>
    /// All private helpers of the Markdown and Mermaid planners live in this one nested type so that they
    /// cannot collide with the other partial files. The Mermaid part is in PinTranslationPlanner.Mermaid.cs.
    /// </summary>
    private static partial class MdPlanning
    {
        /// <summary>A translatable source range. <see cref="Content"/> replaces the raw text as the translation source.</summary>
        internal readonly record struct Cut(int Start, int End, PlanKind Kind, string? Content = null);

        internal readonly record struct Line(int Start, int End, int Next);

        private readonly record struct Prefix(int ContentStart, bool Quote, bool List, bool Heading, bool Footnote, int ListContent);

        private readonly record struct Para(int Start, int End, bool Hard);

        private static Regex Rx(string pattern, RegexOptions extra = RegexOptions.None) =>
            new(pattern, RegexOptions.CultureInvariant | extra, TimeSpan.FromMilliseconds(250));

        private static readonly Regex ListMarker = Rx(@"\G(?:[-+*]|\d{1,9}[.)])(?=[ \t]|$)");
        private static readonly Regex TaskBox = Rx(@"\G\[[ xX]\](?=[ \t]|$)");
        private static readonly Regex HeadingMarker = Rx(@"\G#{1,6}(?=[ \t]|$)");
        private static readonly Regex FootnoteMarker = Rx(@"\G\[\^[^\]\s]+\]:(?=[ \t]|$)");
        private static readonly Regex ClosingHashes = Rx(@"[ \t]+#+$");
        private static readonly Regex ThematicOrSetext = Rx(@"^(?:(?:[-*_][ \t]*){3,}|=+[ \t]*|-+[ \t]*)$");
        private static readonly Regex StraySeparator = Rx(@"^\|?[ \t]*:?-+:?[ \t]*(?:\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$");
        private static readonly Regex SeparatorCell = Rx(@"^:?-+:?$");
        private static readonly Regex ReferenceDefinition = Rx(@"^\[(?!\^)[^\]]+\]:[ \t]*\S");
        private static readonly Regex AlertMarker = Rx(@"^\[![A-Za-z]+\][ \t]*$");
        private static readonly Regex HtmlBlockStart = Rx(
            @"^</?(?:address|article|aside|base|basefont|blockquote|body|caption|center|col|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|frame|frameset|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|menuitem|nav|noframes|ol|optgroup|option|p|param|search|section|summary|table|tbody|td|tfoot|th|thead|title|tr|track|ul)(?:[ \t]|/?>|$)",
            RegexOptions.IgnoreCase);
        private static readonly Regex HtmlRawStart = Rx(@"^<(pre|script|style|textarea)(?:[ \t>]|$)", RegexOptions.IgnoreCase);
        private static readonly Regex YamlLine = Rx(@"^(?:[ \t#].*|-(?:[ \t].*)?|[^:]*:.*)$");

        private static readonly Regex UrlLine = Rx(@"^(?:(?:https?|ftp)://\S+|www\.\S+|mailto:\S+)$", RegexOptions.IgnoreCase);
        private static readonly Regex AutolinkLine = Rx(@"^<[^<>\s]+>$");
        private static readonly Regex EmailLine = Rx(@"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$");
        private static readonly Regex PathLine = Rx(@"^(?:[A-Za-z]:[\\/][^\r\n]*|\\\\\S[^\r\n]*|(?:(?:~|\.{1,2})[\\/]|/)\S+)$");
        private const string ImageToken = @"(?:\[!\[[^\]]*\]\([^)]*\)\](?:\([^)]*\)|\[[^\]]*\])|!\[[^\]]*\](?:\([^)]*\)|\[[^\]]*\]))";
        private static readonly Regex ImageLine = Rx(@"^(?:" + ImageToken + @"[ \t]*)+$");

        private static readonly Regex LinkDestination = Rx(@"\]\([^)]*\)|\]\[[^\]]*\]");
        private static readonly Regex HtmlTag = Rx(@"</?[A-Za-z][^<>\r\n]*>|<!--.*?-->|<[A-Za-z][A-Za-z0-9+.\-]*:[^\s<>]*>|<[^\s<>@]+@[^\s<>]+>");
        private static readonly Regex Entity = Rx(@"&#?[A-Za-z0-9]+;");
        private static readonly Regex AdditionalLiteral = Rx(
            @"(?:\b(?:https?|ftp)://|\bwww\.|\bmailto:)[^\s<>]+|(?<![\w])(?:[A-Za-z]:[\\/]|\\\\|(?:\.{1,2})?/)[^\s<>]+|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|\S{129,}");

        private static readonly Regex DiagramDeclaration = Rx(
            @"^(?:(?:flowchart|graph)[ \t]+(?:TB|TD|BT|RL|LR)(?:[ \t]*;.*)?[ \t]*|(?:sequenceDiagram|classDiagram|stateDiagram(?:-v2)?|erDiagram|gantt|mindmap|timeline|journey|gitGraph|quadrantChart|requirementDiagram|C4Context|C4Container|C4Component|C4Dynamic|C4Deployment|block-beta|packet-beta|architecture-beta|sankey-beta|xychart-beta)[ \t]*|pie(?:[ \t]+(?:showData(?:[ \t]+title.*)?|title.*))?[ \t]*)$");

        // ---- shared helpers -------------------------------------------------------------------------

        internal static List<Line> ReadLines(string source)
        {
            var lines = new List<Line>();
            int start = 0;
            for (int index = 0; index < source.Length; index++)
            {
                if (source[index] is not ('\r' or '\n')) continue;
                int next = index + 1;
                if (source[index] == '\r' && next < source.Length && source[next] == '\n') next++;
                lines.Add(new Line(start, index, next));
                index = next - 1;
                start = next;
            }
            if (start < source.Length) lines.Add(new Line(start, source.Length, source.Length));
            return lines;
        }

        internal static List<PlanPiece> Pieces(string source, List<Cut> cuts)
        {
            var pieces = new List<PlanPiece>();
            int position = 0;
            foreach (Cut cut in cuts.OrderBy(static cut => cut.Start))
            {
                if (cut.Start < position || cut.End <= cut.Start || cut.End > source.Length) continue;
                if (cut.Start > position) pieces.Add(new PlanPiece(source[position..cut.Start]));
                pieces.Add(new PlanPiece(source[cut.Start..cut.End], cut.Kind, true, cut.Content));
                position = cut.End;
            }
            if (position < source.Length) pieces.Add(new PlanPiece(source[position..]));
            return pieces;
        }

        internal static bool CanTranslate(string text, PinTranslationDirection direction) =>
            PinScript.IsTranslatable(Mask(text), direction);

        /// <summary>Blanks inline syntax that must never count as translatable words.</summary>
        internal static string Mask(string text)
        {
            text = MaskCode(text);
            text = LinkDestination.Replace(text, " ");
            text = HtmlTag.Replace(text, " ");
            text = Entity.Replace(text, " ");
            return AdditionalLiteral.Replace(text, " ");
        }

        private static string MaskCode(string text)
        {
            if (text.IndexOf('`') < 0) return text;
            var masked = new StringBuilder(text);
            for (int index = 0; index < text.Length;)
            {
                if (text[index] != '`') { index++; continue; }
                int run = 1;
                while (index + run < text.Length && text[index + run] == '`') run++;
                int close = FindCodeClose(text, index + run, text.Length, run);
                if (close < 0) { index += run; continue; }
                for (int fill = index; fill < close + run; fill++) masked[fill] = ' ';
                index = close + run;
            }
            return masked.ToString();
        }

        private static int FindCodeClose(string text, int start, int end, int run)
        {
            for (int index = start; index < end; index++)
            {
                if (text[index] != '`') continue;
                int length = 1;
                while (index + length < end && text[index + length] == '`') length++;
                if (length == run) return index;
                index += length - 1;
            }
            return -1;
        }

        internal static int IndentWidth(string text)
        {
            int width = 0;
            foreach (char c in text)
            {
                if (c == ' ') width++;
                else if (c == '\t') width += 4;
                else break;
            }
            return width;
        }

        private static bool IsBlank(char c) => c is ' ' or '\t';

        // ---- markdown -------------------------------------------------------------------------------

        /// <summary>The renderer treats a text as one diagram when its first meaningful line is a diagram declaration.</summary>
        internal static bool IsDiagramText(string source)
        {
            foreach (Line line in ReadLines(source))
            {
                string text = source[line.Start..line.End].Trim().TrimStart('﻿');
                if (text.Length == 0 || text.StartsWith("%%", StringComparison.Ordinal)) continue;
                return DiagramDeclaration.IsMatch(text);
            }
            return false;
        }

        internal static List<Cut> ScanMarkdown(string source, PinTranslationDirection direction) =>
            new Walker(source, direction).Run();

        private static Prefix ParsePrefix(string text)
        {
            int position = 0, listContent = -1;
            bool quote = false, list = false, heading = false, footnote = false;
            for (int guard = 0; guard < 32; guard++)
            {
                int start = position;
                while (start < text.Length && IsBlank(text[start])) start++;
                if (start >= text.Length) { position = start; break; }
                if (text[start] == '>')
                {
                    quote = true;
                    position = start + 1;
                    if (position < text.Length && IsBlank(text[position])) position++;
                    continue;
                }
                Match marker = ListMarker.Match(text, start);
                if (marker.Success)
                {
                    list = true;
                    position = start + marker.Length;
                    while (position < text.Length && IsBlank(text[position])) position++;
                    Match task = TaskBox.Match(text, position);
                    if (task.Success)
                    {
                        position += task.Length;
                        while (position < text.Length && IsBlank(text[position])) position++;
                    }
                    listContent = VisualWidth(text, position);
                    continue;
                }
                Match hash = HeadingMarker.Match(text, start);
                if (hash.Success)
                {
                    heading = true;
                    position = start + hash.Length;
                    while (position < text.Length && IsBlank(text[position])) position++;
                    break;
                }
                Match note = FootnoteMarker.Match(text, start);
                if (note.Success)
                {
                    footnote = true;
                    position = start + note.Length;
                    while (position < text.Length && IsBlank(text[position])) position++;
                    break;
                }
                position = start;
                break;
            }
            return new Prefix(position, quote, list, heading, footnote, listContent);
        }

        private static int VisualWidth(string text, int length)
        {
            int width = 0;
            for (int index = 0; index < length && index < text.Length; index++) width += text[index] == '\t' ? 4 : 1;
            return width;
        }

        private static int FenceRun(string rest)
        {
            if (rest.Length < 3 || rest[0] is not ('`' or '~')) return 0;
            int count = 1;
            while (count < rest.Length && rest[count] == rest[0]) count++;
            return count >= 3 ? count : 0;
        }

        private static bool IsProtectedLine(string rest) =>
            UrlLine.IsMatch(rest) || AutolinkLine.IsMatch(rest) || EmailLine.IsMatch(rest)
            || PathLine.IsMatch(rest) || ImageLine.IsMatch(rest) || AlertMarker.IsMatch(rest);

        /// <summary>Trimmed (start, end) ranges of the cells of a table row; pipes inside code spans or escaped stay in the cell.</summary>
        private static List<(int Start, int End)> CellRanges(string text, int from)
        {
            var cells = new List<(int, int)>();
            int end = text.Length;
            while (end > from && IsBlank(text[end - 1])) end--;
            int index = from;
            while (index < end && IsBlank(text[index])) index++;
            if (index < end && text[index] == '|') index++;
            int cellStart = index;
            while (index < end)
            {
                char c = text[index];
                if (c == '\\' && index + 1 < end) { index += 2; continue; }
                if (c == '`')
                {
                    int run = 1;
                    while (index + run < end && text[index + run] == '`') run++;
                    int close = FindCodeClose(text, index + run, end, run);
                    index = close >= 0 ? close + run : index + run;
                    continue;
                }
                if (c == '|')
                {
                    cells.Add(Trim(text, cellStart, index));
                    cellStart = index + 1;
                }
                index++;
            }
            if (cellStart < end) cells.Add(Trim(text, cellStart, end));
            return cells;
        }

        private static (int, int) Trim(string text, int start, int end)
        {
            while (start < end && IsBlank(text[start])) start++;
            while (end > start && IsBlank(text[end - 1])) end--;
            return (start, end);
        }

        private static bool EndsSentence(string text)
        {
            int end = text.Length;
            while (end > 0 && text[end - 1] is ')' or ']' or '}' or '"' or '\'' or '”' or '’' or '»' or '」' or '』') end--;
            return end > 0 && text[end - 1] is '.' or '!' or '?' or ':' or ';' or '。' or '！' or '？' or '…';
        }

        private static int DisplayWidth(string text)
        {
            int width = 0;
            foreach (char c in text) width += c >= 'ᄀ' ? 2 : 1;
            return width;
        }

        /// <summary>True when two consecutive paragraph lines look like one hard-wrapped sentence.</summary>
        private static bool LooksWrapped(string left, string right)
        {
            if (left.Length == 0 || right.Length == 0 || EndsSentence(left)) return false;
            if (left.EndsWith(',')) return true;
            if (char.IsLower(right[0]) || char.IsDigit(right[0])) return true;
            return DisplayWidth(left) >= 40;
        }

        private sealed class Walker(string source, PinTranslationDirection direction)
        {
            private readonly List<Line> _lines = ReadLines(source);
            private readonly List<Cut> _cuts = [];
            private readonly List<Para> _pending = [];
            private bool _inList;
            private int _listContent;
            private bool _lastParagraph;

            private string Text(Line line) => source[line.Start..line.End];

            internal List<Cut> Run()
            {
                int index = FrontMatterEnd();
                while (index < _lines.Count) index = Block(index);
                Flush();
                return _cuts;
            }

            private int FrontMatterEnd()
            {
                if (_lines.Count < 2 || Text(_lines[0]).TrimStart('﻿').TrimEnd() != "---") return 0;
                for (int index = 1; index < _lines.Count; index++)
                {
                    string text = Text(_lines[index]);
                    string trimmed = text.TrimEnd();
                    if (trimmed is "---" or "...") return index + 1;
                    if (trimmed.Length > 0 && !YamlLine.IsMatch(trimmed)) return 0;
                }
                return 0;
            }

            private int Block(int i)
            {
                string text = Text(_lines[i]);
                if (string.IsNullOrWhiteSpace(text))
                {
                    Flush();
                    _lastParagraph = false;
                    return i + 1;
                }
                int width = IndentWidth(text);
                Prefix p = ParsePrefix(text);
                bool marked = p.Quote || p.List || p.Heading || p.Footnote;
                if (width < 2 && !p.List && !_lastParagraph) _inList = false;
                if (width >= (_inList ? _listContent + 4 : 4) && !_lastParagraph)
                {
                    Flush();
                    return i + 1;
                }

                string rest = text[p.ContentStart..].TrimEnd();
                if (rest.Length == 0)
                {
                    Flush();
                    if (p.List) { _inList = true; _listContent = p.ListContent; }
                    _lastParagraph = false;
                    return i + 1;
                }

                if (FenceRun(rest) > 0 && (marked || width <= 3 || _inList))
                {
                    Flush();
                    _lastParagraph = false;
                    return Fence(i, p, rest, width, marked);
                }
                if (rest.StartsWith("<!--", StringComparison.Ordinal)) return Skip(i, marked, "<!--", "-->");
                Match raw = HtmlRawStart.Match(rest);
                if (raw.Success) return Skip(i, marked, "<" + raw.Groups[1].Value, "</" + raw.Groups[1].Value + ">");
                if (HtmlBlockStart.IsMatch(rest))
                {
                    Flush();
                    _lastParagraph = false;
                    if (marked) return i + 1;
                    int j = i + 1;
                    while (j < _lines.Count && !string.IsNullOrWhiteSpace(Text(_lines[j]))) j++;
                    return j;
                }
                if (rest.StartsWith("$$", StringComparison.Ordinal)) return Skip(i, marked, "$$", "$$");
                if (ThematicOrSetext.IsMatch(rest)
                    || (rest.Contains('|') && StraySeparator.IsMatch(rest))
                    || ReferenceDefinition.IsMatch(rest))
                {
                    Flush();
                    _lastParagraph = false;
                    return i + 1;
                }

                if (!p.List && !p.Heading && !p.Footnote && rest.Contains('|') && i + 1 < _lines.Count
                    && TryTable(i, text, p, out int tableEnd))
                {
                    Flush();
                    _lastParagraph = false;
                    return tableEnd;
                }

                if (marked)
                {
                    Flush();
                    if (p.List) { _inList = true; _listContent = p.ListContent; }
                    if (!IsProtectedLine(rest)) AddStandalone(i, text, p);
                    _lastParagraph = !p.Heading;
                    return i + 1;
                }

                if (IsProtectedLine(rest))
                {
                    Flush();
                    _lastParagraph = true;
                    return i + 1;
                }
                AddPending(i, text, p.ContentStart);
                _lastParagraph = true;
                return i + 1;
            }

            /// <summary>Skips a literal region that ends at the first line containing <paramref name="closer"/> after the opener.</summary>
            private int Skip(int i, bool marked, string opener, string closer)
            {
                Flush();
                _lastParagraph = false;
                string first = Text(_lines[i]);
                int open = first.IndexOf(opener, StringComparison.OrdinalIgnoreCase);
                int after = open < 0 ? first.Length : open + opener.Length;
                if (marked || first.IndexOf(closer, after, StringComparison.OrdinalIgnoreCase) >= 0) return i + 1;
                for (int j = i + 1; j < _lines.Count; j++)
                    if (Text(_lines[j]).Contains(closer, StringComparison.OrdinalIgnoreCase)) return j + 1;
                return _lines.Count;
            }

            private int Fence(int i, Prefix p, string rest, int width, bool marked)
            {
                char marker = rest[0];
                int length = FenceRun(rest);
                bool top = !marked && width <= 3;
                bool diagram = top && rest[length..].Trim().Equals("mermaid", StringComparison.OrdinalIgnoreCase);
                int j = i + 1;
                while (j < _lines.Count && !IsClosingFence(Text(_lines[j]), marker, length, top)) j++;
                if (diagram)
                {
                    int bodyStart = _lines[i].Next;
                    int bodyEnd = j < _lines.Count ? _lines[j].Start : source.Length;
                    if (bodyEnd > bodyStart)
                        foreach (Cut cut in ScanDiagram(source[bodyStart..bodyEnd], direction))
                            _cuts.Add(cut with { Start = cut.Start + bodyStart, End = cut.End + bodyStart });
                }
                return j < _lines.Count ? j + 1 : _lines.Count;
            }

            private static bool IsClosingFence(string text, char marker, int length, bool strict)
            {
                if (!strict)
                {
                    Prefix prefix = ParsePrefix(text);
                    if (prefix.List || prefix.Heading || prefix.Footnote) return false;
                    text = text[prefix.ContentStart..];
                }
                int start = 0;
                while (start < text.Length && text[start] == ' ') start++;
                if (strict && start > 3) return false;
                int run = 0;
                while (start + run < text.Length && text[start + run] == marker) run++;
                return run >= length && string.IsNullOrWhiteSpace(text[(start + run)..]);
            }

            private bool TryTable(int i, string text, Prefix p, out int next)
            {
                next = i + 1;
                string separator = Text(_lines[i + 1]);
                Prefix sp = ParsePrefix(separator);
                if (sp.List || sp.Heading || sp.Footnote || sp.Quote != p.Quote) return false;
                if (!separator[sp.ContentStart..].Contains('|')) return false;
                List<(int Start, int End)> separatorCells = CellRanges(separator, sp.ContentStart);
                if (separatorCells.Count == 0 || separatorCells.Count > 64) return false;
                foreach ((int start, int end) in separatorCells)
                    if (!SeparatorCell.IsMatch(separator[start..end])) return false;
                List<(int Start, int End)> headerCells = CellRanges(text, p.ContentStart);
                if (headerCells.Count != separatorCells.Count) return false;

                AddCells(_lines[i].Start, headerCells);
                int j = i + 2;
                while (j < _lines.Count)
                {
                    string row = Text(_lines[j]);
                    Prefix rp = ParsePrefix(row);
                    if (rp.List || rp.Heading || rp.Footnote || rp.Quote != p.Quote) break;
                    string content = row[rp.ContentStart..];
                    if (string.IsNullOrWhiteSpace(content) || !content.Contains('|') || FenceRun(content.TrimEnd()) > 0) break;
                    AddCells(_lines[j].Start, CellRanges(row, rp.ContentStart));
                    j++;
                }
                next = j;
                return true;
            }

            private void AddCells(int lineStart, List<(int Start, int End)> cells)
            {
                foreach ((int start, int end) in cells)
                    if (end > start) AddCut(lineStart + start, lineStart + end, PlanKind.TableCell);
            }

            private void AddStandalone(int i, string text, Prefix p)
            {
                int start = p.ContentStart;
                int end = TrimEndIndex(text, start);
                if (p.Heading)
                {
                    string cleaned = ClosingHashes.Replace(text[start..end], string.Empty);
                    end = start + cleaned.Length;
                }
                end = StripBreakBackslash(text, start, end, out _);
                if (end > start) AddCut(_lines[i].Start + start, _lines[i].Start + end, PlanKind.MarkdownLine);
            }

            private static int TrimEndIndex(string text, int floor)
            {
                int end = text.Length;
                while (end > floor && IsBlank(text[end - 1])) end--;
                return end;
            }

            /// <summary>An odd run of trailing backslashes is a hard line break; the break stays outside the piece.</summary>
            private static int StripBreakBackslash(string text, int start, int end, out bool hard)
            {
                int slashes = 0;
                while (end - 1 - slashes >= start && text[end - 1 - slashes] == '\\') slashes++;
                hard = slashes % 2 == 1;
                if (!hard) return end;
                end--;
                while (end > start && IsBlank(text[end - 1])) end--;
                return end;
            }

            private void AddPending(int i, string text, int contentStart)
            {
                int end = TrimEndIndex(text, contentStart);
                bool hard = text.Length - end >= 2;
                end = StripBreakBackslash(text, contentStart, end, out bool slash);
                hard |= slash;
                if (end <= contentStart) { Flush(); return; }
                _pending.Add(new Para(_lines[i].Start + contentStart, _lines[i].Start + end, hard));
            }

            private void Flush()
            {
                int count = _pending.Count;
                int index = 0;
                while (index < count)
                {
                    int group = index + 1;
                    while (group < count && !_pending[group - 1].Hard
                        && LooksWrapped(ParaText(_pending[group - 1]), ParaText(_pending[group]))) group++;
                    if (group - index == 1)
                    {
                        AddCut(_pending[index].Start, _pending[index].End, PlanKind.MarkdownLine);
                    }
                    else
                    {
                        string joined = string.Join(' ', _pending.Skip(index).Take(group - index).Select(ParaText));
                        AddCut(_pending[index].Start, _pending[group - 1].End, PlanKind.MarkdownLine, joined);
                    }
                    index = group;
                }
                _pending.Clear();
            }

            private string ParaText(Para para) => source[para.Start..para.End];

            private void AddCut(int start, int end, PlanKind kind, string? content = null)
            {
                string text = content ?? source[start..end];
                if (CanTranslate(text, direction)) _cuts.Add(new Cut(start, end, kind, content));
            }
        }
    }
}
