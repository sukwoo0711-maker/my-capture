using System.Text.RegularExpressions;

namespace MyCapture.App.Pinning;

// Detection of code and Mermaid text, and the comment styles a code text uses.
internal static partial class PinTranslationPlanner
{
    private const int ProbeLineLimit = 4000;

    private sealed class CodeProbe
    {
        internal int NonBlank, Semicolon, Brace, Keyword, Python, Arrow, Decorator;
        internal int SlashMarks, HashMarks, DashMarks, Trailing, CodeLike, KeyValue;
        internal bool Shebang, Sql, Php;

        internal int Signals()
        {
            int signals = 0;
            if (Semicolon >= 1 && Brace >= 1) signals++;
            if (Keyword >= 1) signals++;
            if (Arrow >= 1) signals++;
            if (Shebang) signals++;
            if (Decorator >= 1) signals++;
            if (Trailing >= 1 || (SlashMarks + HashMarks >= 1 && (CodeLike >= 1 || Semicolon >= 1 || Brace >= 1)) || (Shebang && HashMarks >= 1)) signals++;
            if (Sql && DashMarks >= 1) signals++;
            if (KeyValue >= 2 && HashMarks >= 1) signals++;
            if (CodeLike >= 2 && CodeLike * 3 >= NonBlank) signals++;
            return signals;
        }
    }

    /// <summary>Code needs at least two structural signals; a stray '#' or a URL in prose is only one comment-like mark.</summary>
    private static bool IsCode(string source) => ProbeCode(source).Signals() >= 2;

    private static CodeProbe ProbeCode(string source)
    {
        var probe = new CodeProbe { Shebang = source.StartsWith("#!/", StringComparison.Ordinal), Php = source.Contains("<?php", StringComparison.Ordinal) };
        int seen = 0;
        foreach (LineSpan span in SplitLines(source))
        {
            if (++seen > ProbeLineLimit) break;
            (int Start, int End)? trimmedSpan = TrimSpan(source, span.Start, span.End);
            if (trimmedSpan is null) continue;
            string line = source[trimmedSpan.Value.Start..trimmedSpan.Value.End];
            probe.NonBlank++;
            bool comment = line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal);
            bool hashComment = line[0] == '#' && !line.StartsWith("#!", StringComparison.Ordinal) && !line.StartsWith("#[", StringComparison.Ordinal)
                && !PreprocessorLine().IsMatch(line);
            bool dashComment = line.StartsWith("-- ", StringComparison.Ordinal) || line == "--";
            if (comment) probe.SlashMarks++;
            else if (hashComment) probe.HashMarks++;
            else if (dashComment) probe.DashMarks++;
            else if (TrailingDash().IsMatch(line)) probe.DashMarks++;
            else if (HasTrailingComment(line, out bool slash))
            {
                probe.Trailing++;
                if (slash) probe.SlashMarks++; else probe.HashMarks++;
            }
            if (comment || hashComment || dashComment) continue;

            if (line[^1] == ';') probe.Semicolon++;
            if (line[^1] is '{' or '}' || line[0] == '}') probe.Brace++;
            if (CodeKeyword().IsMatch(line)) probe.Keyword++;
            if (PythonLine().IsMatch(line)) probe.Python++;
            if (line.Contains("=>", StringComparison.Ordinal) && ArrowFunction().IsMatch(line)) probe.Arrow++;
            if (DecoratorLine().IsMatch(line)) probe.Decorator++;
            if (SqlStrong().IsMatch(line)) probe.Sql = true;
            if (KeyValueLine().IsMatch(line)) probe.KeyValue++;
            if (LooksLikeCodeLine(line)) probe.CodeLike++;
        }
        return probe;
    }

    /// <summary>A comment after code on the same line: '//' after a statement end, or ' # ' after an assignment.</summary>
    private static bool HasTrailingComment(string line, out bool slash)
    {
        slash = true;
        int index = line.IndexOf("//", StringComparison.Ordinal);
        while (index > 0)
        {
            if (line[index - 1] != ':')
            {
                string before = line[..index].TrimEnd();
                if (before.Length > 0 && (before[^1] is ';' or '{' or '}' or ')' or ',' or ']' || AssignmentPrefix().IsMatch(before))) return true;
            }
            index = line.IndexOf("//", index + 2, StringComparison.Ordinal);
        }
        slash = false;
        return HashTrailing().IsMatch(line);
    }

    /// <summary>The comment markers a code text uses. Python-like text uses only '#', because '//' is floor division there.</summary>
    private readonly record struct CommentStyles(bool Slash, bool Block, bool Hash, bool Dashes);

    private static CommentStyles InferCommentStyles(string source)
    {
        CodeProbe probe = ProbeCode(source);
        bool braces = probe.Semicolon >= 1 && probe.Brace >= 1;
        bool pythonLike = probe.Shebang || probe.Python >= 1 || (probe.KeyValue >= 2 && probe.HashMarks >= 1);
        bool slash = probe.SlashMarks > 0 || braces || probe.Php;
        bool hash = probe.HashMarks > 0 || probe.Shebang || probe.Php;
        if (pythonLike && !braces && probe.SlashMarks == 0) slash = false;
        if (!slash && !hash && !probe.Sql)
        {
            slash = source.Contains("//", StringComparison.Ordinal) || source.Contains("/*", StringComparison.Ordinal);
            hash = !slash && source.Contains('#');
        }
        return new CommentStyles(slash, slash || probe.Sql, hash, probe.Sql);
    }

    private static bool StartsWithMermaidFence(string source)
    {
        foreach (LineSpan line in SplitLines(source))
        {
            (int Start, int End)? text = TrimSpan(source, line.Start, line.End);
            if (text is null) continue;
            string trimmed = source[text.Value.Start..text.Value.End];
            return trimmed.StartsWith("```mermaid", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("~~~mermaid", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>A diagram: optional front matter and %% comments, then a Mermaid header line and at least one more line.</summary>
    private static bool IsMermaid(string source)
    {
        List<LineSpan> lines = SplitLines(source);
        int index = 0;
        string Text(int at) => source[lines[at].Start..lines[at].End].Trim();
        while (index < lines.Count && Text(index).Length == 0) index++;
        if (index < lines.Count && Text(index) == "---")
        {
            index++;
            while (index < lines.Count && Text(index) != "---") index++;
            index++;
        }
        while (index < lines.Count && (Text(index).Length == 0 || Text(index).StartsWith("%%", StringComparison.Ordinal))) index++;
        if (index >= lines.Count || !MermaidHeader().IsMatch(Text(index))) return false;
        for (int next = index + 1; next < lines.Count; next++)
        {
            if (Text(next).Length > 0) return true;
        }
        return false;
    }

    [GeneratedRegex(@"^(?:(?:graph|flowchart)(?:\s+(?:TB|TD|BT|RL|LR))?|sequenceDiagram|classDiagram(?:-v2)?|stateDiagram(?:-v2)?|erDiagram|gantt|pie(?:\s+(?:showData|title\b.*))*|journey|gitGraph.*|mindmap|timeline|quadrantChart|requirementDiagram|C4(?:Context|Container|Component|Dynamic|Deployment)|xychart-beta.*|sankey-beta|block-beta|architecture-beta|packet-beta|kanban)\s*;?$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MermaidHeader();

    [GeneratedRegex(@"^#(?:include|define|undef|ifdef|ifndef|if|elif|else|endif|pragma|region|endregion|line|error|warning|nullable|load|import|using)\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PreprocessorLine();

    [GeneratedRegex(
        @"^(?:def\s+\w+\s*\(|class\s+\w+(?:\s*\([^)]*\))?\s*(?::|\{|extends\b|implements\b|<)|import\s+[\w.*{},\s]+(?:\s+as\s+\w+)?\s*;?$|import\s.+\sfrom\s|from\s+[\w.]+\s+import\s"
        + @"|using\s+[\w.=\s]+;|function\s*\*?\s*\w*\s*\(|(?:const|let|var)\s+[\w{}\[\],\s]+=|(?:public|private|protected|internal|static)\s+(?:(?:static|async|final|readonly|override|virtual|abstract|sealed)\s+)*[\w<>\[\],.?]+\s+\w+"
        + @"|return\b[^.\r\n]*;|namespace\s+[\w.]+|package\s+[\w.]+;?|fn\s+\w+|func\s+\w+|#include\b|#define\b|elif\s"
        + @"|(?:SELECT\b.*\bFROM|INSERT\s+INTO|CREATE\s+(?:TABLE|INDEX|VIEW)|UPDATE\s+\w+\s+SET|DELETE\s+FROM|ALTER\s+TABLE|DROP\s+TABLE)\b)",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CodeKeyword();

    [GeneratedRegex(@"^(?:def\s+\w+\s*\(.*\)\s*(?:->\s*[^:]+)?:|class\s+\w+.*:|elif\s.+:|else:|try:|except\b.*:|with\s.+:|for\s.+\sin\s.+:|while\s.+:|import\s+\w|from\s+[\w.]+\s+import\s|print\()",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PythonLine();

    [GeneratedRegex(@"\)\s*=>|\b\w+\s*=>\s*[\w({\[]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ArrowFunction();

    [GeneratedRegex(@"^@[A-Za-z_][\w.]*(?:\(.*\))?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DecoratorLine();

    [GeneratedRegex(@"\b(?:SELECT\b.*\bFROM|INSERT\s+INTO|CREATE\s+(?:TABLE|INDEX|VIEW)|UPDATE\s+\w+\s+SET|DELETE\s+FROM|ALTER\s+TABLE|DROP\s+TABLE)\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqlStrong();

    [GeneratedRegex(@"^(?:[A-Za-z_][\w.-]*\s*[:=]\s*\S|\[[\w.-]+\]$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex KeyValueLine();

    [GeneratedRegex(@"^[\w.\[\]$]+\s*(?:[-+*/%&|^]?=|:=)\s*\S", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AssignmentPrefix();

    [GeneratedRegex(@"^(?:[\w.\[\]$-]+\s*[:=]\s*[^#""']*?|(?:return|print|import|from|pass|break|continue|raise|yield|assert|del|global|echo|export|source)\b[^#""']*?|[^#""']*[)\]:])\s+#\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HashTrailing();

    [GeneratedRegex(@"[;)]\s+--\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TrailingDash();
}
