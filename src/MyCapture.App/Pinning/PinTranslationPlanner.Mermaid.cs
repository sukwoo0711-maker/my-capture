using System.Text.RegularExpressions;

namespace MyCapture.App.Pinning;

internal static partial class PinTranslationPlanner
{
    /// <summary>
    /// Plans a bare Mermaid diagram (no fences). Only human labels become pieces; node ids, arrows, keywords,
    /// styling and unsupported diagram types stay Literal.
    /// </summary>
    internal static PinTranslationPlan CreateMermaid(string source, PinTranslationDirection direction)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<MdPlanning.Cut> cuts;
        try
        {
            cuts = MdPlanning.ScanDiagram(source, direction);
        }
        catch (RegexMatchTimeoutException)
        {
            cuts = [];
        }
        return new PinTranslationPlan(PinTextFormat.Mermaid, MdPlanning.Pieces(source, cuts));
    }

    private static partial class MdPlanning
    {
        internal static List<Cut> ScanDiagram(string source, PinTranslationDirection direction) =>
            new DiagramScanner(source, direction).Run();

        private static readonly Regex DiagramType = Rx(@"^(?<t>flowchart(?:-elk)?|graph|sequenceDiagram|classDiagram(?:-v2)?|stateDiagram(?:-v2)?|erDiagram|gantt|pie|mindmap|timeline|journey)(?![\w-])");
        private static readonly Regex FlowHeader = Rx(@"^(?:flowchart(?:-elk)?|graph)(?:[ \t]+(?:TB|TD|BT|RL|LR))?");
        private static readonly Regex FlowSkip =Rx(@"^(?:classDef|class|style|linkStyle|click|direction|callback|href|end)(?=[ \t]|$)");
        private static readonly Regex InlineEdge = Rx(@"\G(?<open>(?:<)?(?:--|==|-\.))(?<ws1>[ \t]+)(?<text>\S(?:.*?\S)?)(?<ws2>[ \t]+)(?<close>(?:-{2,}|={2,}|\.+-+)[>xo]?)(?=[ \t;&|]|[A-Za-z_]|$|[^\x00-\x7F])");
        private static readonly Regex PlainEdge = Rx(@"\G(?:<)?(?:-{2,}|={2,}|-\.+-|\.+-)[>xo]?|\G~{3,}");
        private static readonly Regex AtLabel = Rx(@"label[ \t]*:[ \t]*""(?<t>[^""]*)""");
        private static readonly Regex AccLine = Rx(@"^(?:accTitle|accDescr)[ \t]*:[ \t]*(?<t>.+)$");
        private static readonly Regex AccBlock = Rx(@"^accDescr[ \t]*\{(?<rest>.*)$");
        private static readonly Regex TitleLine = Rx(@"^(?:title|section)(?:[ \t]*:[ \t]*|[ \t]+)(?<t>.+)$");

        private static readonly Regex SeqParticipant = Rx(@"^(?:participant|actor)[ \t]+.+?[ \t]+as[ \t]+(?<t>.+)$");
        private static readonly Regex SeqBlock = Rx(@"^(?:loop|alt|else|opt|par|and|critical|option|break)(?:[ \t]+(?<t>.+))?$");
        private static readonly Regex SeqNote = Rx(@"^[Nn]ote[ \t]+(?:left[ \t]+of|right[ \t]+of|over)[ \t]+[^:]+:[ \t]*(?<t>.*)$");
        private static readonly Regex SeqArrow = Rx(@"<<-->>|<<->>|-->>|->>|-->|--x|--\)|-x|-\)|->");
        private static readonly Regex SeqBox = Rx(@"^box(?:[ \t]+(?<t>.+))?$");

        private static readonly Regex StateDescription = Rx(@"^state[ \t]+""(?<t>[^""]*)""[ \t]+as[ \t]+\S+");
        private static readonly Regex StateNote = Rx(@"^note[ \t]+(?:left|right)[ \t]+of[ \t]+\S+(?:[ \t]*:[ \t]*(?<t>.*))?$", RegexOptions.IgnoreCase);
        private static readonly Regex StateKeyword = Rx(@"^(?:state|note|end|direction|class|classDef|style|click|hide|scale|accTitle|accDescr|title)$");

        private static readonly Regex ClassLabel = Rx(@"^class[ \t]+\S+?\[(?:""(?<t>[^""]*)""|(?<b>[^\]""]*))\]");
        private static readonly Regex ClassNote = Rx(@"^note(?:[ \t]+for[ \t]+\S+)?[ \t]+""(?<t>[^""]*)""");
        private static readonly Regex ClassSkip = Rx(@"^(?:(?:class|namespace|note|direction|link|callback|click|style|classDef|cssClass)(?=[ \t{\[]|$)|<<)");
        private static readonly Regex RelationOp = Rx(@"<\|--|--\|>|<\|\.\.|\.\.\|>|\*--|--\*|o--|--o|-->|<--|\.\.>|<\.\.|--|\.\.");
        private static readonly Regex QuotedString = Rx(@"""[^""]*""");

        private static readonly Regex ErRelation = Rx(@"^(?:""[^""]+""|\S+)[ \t]+\S*(?:--|\.\.)\S*[ \t]+(?:""[^""]+""|\S+)[ \t]*:[ \t]*(?<t>.+)$");

        private static readonly Regex PieHeader = Rx(@"^pie(?:[ \t]+showData)?[ \t]+title[ \t]+(?<t>.+)$");
        private static readonly Regex PieEntry = Rx(@"^""(?<t>[^""]*)""[ \t]*:");
        private static readonly Regex GanttKeyword = Rx(@"^(?:dateFormat|axisFormat|tickInterval|excludes|includes|todayMarker|weekday|weekend|topAxis|inclusiveEndDates|click|accTitle|accDescr)(?![\w-])");

        private static readonly (string Open, string[] Close)[] FlowShapes =
        [
            ("(((", [")))"]), ("((", ["))"]), ("([", ["])"]), ("(", [")"]),
            ("[[", ["]]"]), ("[(", [")]"]), ("[/", ["/]", "\\]"]), ("[\\", ["\\]", "/]"]), ("[", ["]"]),
            ("{{", ["}}"]), ("{", ["}"]), (">", ["]"]),
        ];

        private static readonly (string Open, string[] Close)[] MindmapShapes =
        [
            ("((", ["))"]), ("))", ["(("]), ("{{", ["}}"]), ("(", [")"]), (")", ["("]), ("[", ["]"]),
        ];

        private static readonly HashSet<string> CssColors = new(StringComparer.OrdinalIgnoreCase)
        {
            "aqua", "black", "blue", "fuchsia", "gray", "grey", "green", "lime", "maroon", "navy", "olive", "orange",
            "purple", "red", "silver", "teal", "white", "yellow", "transparent", "pink", "brown", "cyan", "magenta",
            "gold", "indigo", "violet", "coral", "crimson", "khaki", "lavender", "salmon", "tan",
        };

        private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int Count(string text, string value)
        {
            int count = 0, start = 0;
            while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
            {
                count++;
                start += value.Length;
            }
            return count;
        }

        private sealed class DiagramScanner(string source, PinTranslationDirection direction)
        {
            private readonly List<Line> _lines = ReadLines(source);
            private readonly List<Cut> _cuts = [];
            private bool _accBlock;
            private bool _noteBlock;

            internal List<Cut> Run()
            {
                int index = FindHeader(out string kind);
                if (index < 0) return _cuts;
                for (int i = index; i < _lines.Count; i++)
                {
                    int abs = _lines[i].Start;
                    string t = source[abs.._lines[i].End];
                    int lead = 0;
                    while (lead < t.Length && IsBlank(t[lead])) lead++;
                    string s = t[lead..].TrimEnd();

                    if (_accBlock)
                    {
                        if (s.StartsWith('}')) _accBlock = false;
                        else if (s.Length > 0) AddRange(abs, t, lead, lead + s.Length, PlanKind.Prose);
                        continue;
                    }
                    if (s.Length == 0 || s.StartsWith("%%", StringComparison.Ordinal)) continue;
                    if (TryAccessibility(abs, t, lead, s)) continue;

                    switch (kind)
                    {
                        case "flow": Flow(abs, t, lead, s, i == index); break;
                        case "sequence": Sequence(abs, t, lead, s); break;
                        case "state": State(abs, t, lead, s); break;
                        case "class": Class(abs, t, lead, s); break;
                        case "er": Er(abs, t, lead, s); break;
                        case "pie": Pie(abs, t, lead, s); break;
                        case "gantt": Gantt(abs, t, lead, s); break;
                        case "timeline": Timeline(abs, t, lead, s); break;
                        case "journey": Journey(abs, t, lead, s); break;
                        case "mindmap": if (i != index) Mindmap(abs, t, lead, s); break;
                    }
                }
                return _cuts;
            }

            private int FindHeader(out string kind)
            {
                kind = "";
                int start = 0;
                if (_lines.Count > 1 && source[_lines[0].Start.._lines[0].End].TrimStart('﻿').TrimEnd() == "---")
                {
                    for (int i = 1; i < _lines.Count; i++)
                        if (source[_lines[i].Start.._lines[i].End].TrimEnd() == "---") { start = i + 1; break; }
                }
                for (int i = start; i < _lines.Count; i++)
                {
                    string s = source[_lines[i].Start.._lines[i].End].Trim().TrimStart('﻿');
                    if (s.Length == 0 || s.StartsWith("%%", StringComparison.Ordinal)) continue;
                    Match m = DiagramType.Match(s);
                    if (!m.Success) return -1;
                    string type = m.Groups["t"].Value;
                    kind = type.StartsWith("flowchart", StringComparison.Ordinal) || type == "graph" ? "flow"
                        : type.StartsWith("sequence", StringComparison.Ordinal) ? "sequence"
                        : type.StartsWith("state", StringComparison.Ordinal) ? "state"
                        : type.StartsWith("class", StringComparison.Ordinal) ? "class"
                        : type == "erDiagram" ? "er"
                        : type;
                    return i;
                }
                return -1;
            }

            // ---- adding pieces --------------------------------------------------------------------

            private bool Translatable(string text)
            {
                // Mermaid markdown strings use backticks for text; they are not code spans here.
                string masked = text.Replace('`', ' ');
                masked = HtmlTag.Replace(masked, " ");
                masked = Entity.Replace(masked, " ");
                masked = AdditionalLiteral.Replace(masked, " ");
                return PinScript.IsTranslatable(masked, direction);
            }

            private void Add(int start, int end, PlanKind kind, string? content = null)
            {
                if (end <= start) return;
                if (Translatable(content ?? source[start..end])) _cuts.Add(new Cut(start, end, kind, content));
            }

            /// <summary>Adds the trimmed range [from, to) of line text <paramref name="t"/> as a piece of <paramref name="kind"/>.</summary>
            private void AddRange(int abs, string t, int from, int to, PlanKind kind)
            {
                (int start, int end) = Trim(t, from, to);
                Add(abs + start, abs + end, kind);
            }

            private void AddRest(int abs, string t, int from) => AddRange(abs, t, from, t.Length, PlanKind.Prose);

            private void AddGroup(int abs, Group group)
            {
                if (group.Success) Add(abs + group.Index, abs + group.Index + group.Length, PlanKind.Prose);
            }

            /// <summary>
            /// A label whose quotes are optional. A quoted label is replaced together with its quotes (Content is the
            /// inner text) so that PlanPiece.Emit decides whether the translation needs quotes and never doubles them.
            /// </summary>
            private void AddLabel(int abs, string t, int from, int to)
            {
                (int start, int end) = Trim(t, from, to);
                if (end <= start) return;
                if (end - start >= 2 && t[start] == '"' && t[end - 1] == '"')
                    Add(abs + start, abs + end, PlanKind.MermaidLabel, t[(start + 1)..(end - 1)]);
                else
                    Add(abs + start, abs + end, PlanKind.MermaidLabel);
            }

            private bool TryAccessibility(int abs, string t, int lead, string s)
            {
                Match acc = AccLine.Match(s);
                if (acc.Success)
                {
                    AddGroup(abs + lead, acc.Groups["t"]);
                    return true;
                }
                Match block = AccBlock.Match(s);
                if (!block.Success) return false;
                string rest = block.Groups["rest"].Value;
                int close = rest.IndexOf('}');
                if (close < 0)
                {
                    _accBlock = true;
                    (int start, int end) = Trim(rest, 0, rest.Length);
                    Add(abs + lead + block.Groups["rest"].Index + start, abs + lead + block.Groups["rest"].Index + end, PlanKind.Prose);
                }
                return true;
            }

            // ---- flowchart ------------------------------------------------------------------------

            private void Flow(int abs, string t, int lead, string s, bool header)
            {
                if (header)
                {
                    Match head = FlowHeader.Match(s);
                    ScanFlow(abs, t, lead + head.Length);
                    return;
                }
                if (FlowSkip.IsMatch(s)) return;
                if (s.StartsWith("subgraph", StringComparison.Ordinal) && (s.Length == 8 || IsBlank(s[8])))
                {
                    Subgraph(abs, t, lead);
                    return;
                }
                ScanFlow(abs, t, lead);
            }

            private void Subgraph(int abs, string t, int lead)
            {
                int k = lead + "subgraph".Length;
                while (k < t.Length && IsBlank(t[k])) k++;
                if (k >= t.Length) return;
                if (t[k] == '"')
                {
                    int quote = t.IndexOf('"', k + 1);
                    if (quote > 0) AddLabel(abs, t, k, quote + 1);
                    return;
                }
                int idEnd = k;
                while (idEnd < t.Length && !IsBlank(t[idEnd]) && t[idEnd] != '[') idEnd++;
                int after = idEnd;
                while (after < t.Length && IsBlank(t[after])) after++;
                if (after < t.Length && t[after] == '[')
                {
                    Shape(abs, t, after, FlowShapes);
                    return;
                }
                (int start, int end) = Trim(t, k, t.Length);
                string title = t[start..end];
                // A single word is also the subgraph id; edges elsewhere may refer to it.
                bool single = title.IndexOfAny([' ', '\t']) < 0;
                if (single && Count(source, title) > 1) return;
                AddLabel(abs, t, start, end);
            }

            private void ScanFlow(int abs, string t, int i)
            {
                int n = t.Length;
                while (i < n)
                {
                    char c = t[i];
                    if (IsBlank(c) || c is ';' or '&') { i++; continue; }
                    if (IsIdChar(c))
                    {
                        while (i < n && IsIdChar(t[i])) i++;
                        if (i + 1 < n && t[i] == '@' && t[i + 1] == '{') i = AtShape(abs, t, i);
                        else if (i < n && t[i] is '[' or '(' or '{' or '>') i = Shape(abs, t, i, FlowShapes);
                        continue;
                    }
                    if (c is '-' or '=' or '.' or '<' or '~') { i = Edge(abs, t, i); continue; }
                    i++;
                }
            }

            private int AtShape(int abs, string t, int at)
            {
                int close = t.IndexOf('}', at);
                if (close < 0) return t.Length;
                Match m = AtLabel.Match(t, at, close - at);
                if (m.Success) AddGroup(abs, m.Groups["t"]);
                return close + 1;
            }

            private int Edge(int abs, string t, int i)
            {
                Match inline = InlineEdge.Match(t, i);
                if (inline.Success)
                {
                    Group text = inline.Groups["text"];
                    AddLabel(abs, t, text.Index, text.Index + text.Length);
                    return inline.Index + inline.Length;
                }
                Match plain = PlainEdge.Match(t, i);
                if (!plain.Success) return i + 1;
                int end = plain.Index + plain.Length;
                int k = end;
                while (k < t.Length && IsBlank(t[k])) k++;
                if (k < t.Length && t[k] == '|')
                {
                    int close = t.IndexOf('|', k + 1);
                    if (close > 0)
                    {
                        AddLabel(abs, t, k + 1, close);
                        return close + 1;
                    }
                }
                return end;
            }

            /// <summary>Parses a node shape that opens at <paramref name="at"/> and adds its label; returns the index after the shape.</summary>
            private int Shape(int abs, string t, int at, (string Open, string[] Close)[] shapes)
            {
                foreach ((string open, string[] closers) in shapes)
                {
                    if (!t.AsSpan(at).StartsWith(open, StringComparison.Ordinal)) continue;
                    int j = at + open.Length;
                    int k = j;
                    while (k < t.Length && IsBlank(t[k])) k++;
                    if (k < t.Length && t[k] == '"')
                    {
                        int quote = t.IndexOf('"', k + 1);
                        if (quote < 0) return t.Length;
                        int after = quote + 1;
                        while (after < t.Length && IsBlank(t[after])) after++;
                        foreach (string closer in closers)
                        {
                            if (!t.AsSpan(after).StartsWith(closer, StringComparison.Ordinal)) continue;
                            AddLabel(abs, t, k, quote + 1);
                            return after + closer.Length;
                        }
                        return quote + 1;
                    }
                    int best = -1;
                    string bestCloser = "";
                    foreach (string closer in closers)
                    {
                        int found = t.IndexOf(closer, j, StringComparison.Ordinal);
                        if (found >= 0 && (best < 0 || found < best)) { best = found; bestCloser = closer; }
                    }
                    if (best < 0) return t.Length;
                    AddLabel(abs, t, j, best);
                    return best + bestCloser.Length;
                }
                return at + 1;
            }

            // ---- sequence diagram -----------------------------------------------------------------

            private void Sequence(int abs, string t, int lead, string s)
            {
                int origin = abs + lead;
                Match m = SeqParticipant.Match(s);
                if (m.Success) { AddGroup(origin, m.Groups["t"]); return; }
                m = TitleLine.Match(s);
                if (m.Success && s.StartsWith("title", StringComparison.Ordinal)) { AddGroup(origin, m.Groups["t"]); return; }
                m = SeqNote.Match(s);
                if (m.Success) { AddGroup(origin, m.Groups["t"]); return; }
                m = SeqBox.Match(s);
                if (m.Success)
                {
                    Group title = m.Groups["t"];
                    if (!title.Success) return;
                    int skip = BoxTitleOffset(title.Value);
                    if (skip >= 0) Add(origin + title.Index + skip, origin + title.Index + title.Length, PlanKind.Prose);
                    return;
                }
                m = SeqBlock.Match(s);
                if (m.Success) { AddGroup(origin, m.Groups["t"]); return; }
                int colon = s.IndexOf(':');
                if (colon > 0 && SeqArrow.IsMatch(s[..colon])) AddRest(abs, t, lead + colon + 1);
            }

            /// <summary>Offset of the box name after an optional colour; -1 when the box has only a colour.</summary>
            private static int BoxTitleOffset(string text)
            {
                if (Regex.IsMatch(text, @"^(?:rgba?|hsla?)\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    int close = text.IndexOf(')');
                    if (close < 0) return -1;
                    int next = close + 1;
                    while (next < text.Length && IsBlank(text[next])) next++;
                    return next < text.Length ? next : -1;
                }
                int space = text.IndexOfAny([' ', '\t']);
                if (text.StartsWith('#') || CssColors.Contains(space < 0 ? text : text[..space]))
                {
                    if (space < 0) return -1;
                    int next = space;
                    while (next < text.Length && IsBlank(text[next])) next++;
                    return next;
                }
                return space < 0 ? -1 : 0;
            }

            // ---- state diagram --------------------------------------------------------------------

            private void State(int abs, string t, int lead, string s)
            {
                int origin = abs + lead;
                if (_noteBlock)
                {
                    if (s.StartsWith("end note", StringComparison.OrdinalIgnoreCase)) _noteBlock = false;
                    else AddRange(abs, t, lead, lead + s.Length, PlanKind.Prose);
                    return;
                }
                Match m = StateDescription.Match(s);
                if (m.Success) { AddGroup(origin, m.Groups["t"]); return; }
                m = StateNote.Match(s);
                if (m.Success)
                {
                    if (m.Groups["t"].Success) AddGroup(origin, m.Groups["t"]);
                    else _noteBlock = true;
                    return;
                }
                int colon = FirstColon(s);
                if (colon <= 0) return;
                string head = s[..colon].TrimEnd();
                int space = head.IndexOfAny([' ', '\t']);
                if (StateKeyword.IsMatch(space < 0 ? head : head[..space])) return;
                if (head.Contains("-->", StringComparison.Ordinal) || space < 0) AddRest(abs, t, lead + colon + 1);
            }

            private static int FirstColon(string s)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] != ':') continue;
                    if (i + 2 < s.Length && s[i + 1] == ':' && s[i + 2] == ':') { i += 2; continue; }
                    return i;
                }
                return -1;
            }

            // ---- class diagram --------------------------------------------------------------------

            private void Class(int abs, string t, int lead, string s)
            {
                int origin = abs + lead;
                Match m = ClassLabel.Match(s);
                if (m.Success)
                {
                    AddGroup(origin, m.Groups["t"].Success ? m.Groups["t"] : m.Groups["b"]);
                    return;
                }
                m = ClassNote.Match(s);
                if (m.Success) { AddGroup(origin, m.Groups["t"]); return; }
                if (ClassSkip.IsMatch(s)) return;
                int colon = FirstColon(s);
                if (colon <= 0) return;
                string head = QuotedString.Replace(s[..colon], " ").Trim();
                if (head.IndexOfAny([' ', '\t']) > 0 && RelationOp.IsMatch(head)) AddRest(abs, t, lead + colon + 1);
            }

            // ---- other diagram types --------------------------------------------------------------

            private void Er(int abs, string t, int lead, string s)
            {
                Match m = ErRelation.Match(s);
                if (!m.Success) return;
                Group label = m.Groups["t"];
                string text = label.Value.TrimEnd();
                // Only a quoted label can safely take a multi-word translation.
                if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                    Add(abs + lead + label.Index + 1, abs + lead + label.Index + text.Length - 1, PlanKind.Prose);
            }

            private void Pie(int abs, string t, int lead, string s)
            {
                int origin = abs + lead;
                Match m = PieHeader.Match(s);
                if (m.Success) { AddGroup(origin, m.Groups["t"]); return; }
                m = TitleLine.Match(s);
                if (m.Success && s.StartsWith("title", StringComparison.Ordinal)) { AddGroup(origin, m.Groups["t"]); return; }
                m = PieEntry.Match(s);
                if (m.Success) AddGroup(origin, m.Groups["t"]);
            }

            private void Gantt(int abs, string t, int lead, string s)
            {
                if (GanttKeyword.IsMatch(s)) return;
                Match m = TitleLine.Match(s);
                if (m.Success) { AddGroup(abs + lead, m.Groups["t"]); return; }
                int colon = s.IndexOf(':');
                if (colon > 0) AddRange(abs, t, lead, lead + colon, PlanKind.Prose);
            }

            private void Timeline(int abs, string t, int lead, string s)
            {
                Match m = TitleLine.Match(s);
                if (m.Success) { AddGroup(abs + lead, m.Groups["t"]); return; }
                int colon = s.IndexOf(':');
                while (colon >= 0)
                {
                    int next = s.IndexOf(':', colon + 1);
                    AddRange(abs, t, lead + colon + 1, lead + (next < 0 ? s.Length : next), PlanKind.Prose);
                    colon = next;
                }
            }

            private void Journey(int abs, string t, int lead, string s)
            {
                Match m = TitleLine.Match(s);
                if (m.Success) { AddGroup(abs + lead, m.Groups["t"]); return; }
                int colon = s.IndexOf(':');
                if (colon > 0) AddRange(abs, t, lead, lead + colon, PlanKind.Prose);
            }

            private void Mindmap(int abs, string t, int lead, string s)
            {
                if (s.StartsWith("::", StringComparison.Ordinal)) return;
                for (int p = 0; p < s.Length; p++)
                {
                    foreach ((string open, string[] closers) in MindmapShapes)
                    {
                        if (!s.AsSpan(p).StartsWith(open, StringComparison.Ordinal)) continue;
                        if (closers.Any(closer => s.IndexOf(closer, p + open.Length, StringComparison.Ordinal) >= 0))
                        {
                            Shape(abs, t, lead + p, [(open, closers)]);
                            return;
                        }
                    }
                }
                // A plain node cannot be quoted when it contains syntax characters (the parser rejects it), so its
                // translation is inserted as free text; only an already quoted node keeps the quoting rule.
                if (s[0] == '"') AddLabel(abs, t, lead, lead + s.Length);
                else AddRange(abs, t, lead, lead + s.Length, PlanKind.Prose);
            }
        }
    }
}
