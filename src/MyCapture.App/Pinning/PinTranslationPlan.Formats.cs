using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyCapture.Core.Pin;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinTranslationService
{
    private static void AddPlainProse(List<Piece> pieces, string line, string target)
    {
        int copied = 0;
        foreach (string sentence in TranslationSegmenter.Sentences(line))
        {
            int start = line.IndexOf(sentence, copied, StringComparison.Ordinal);
            // Sentences returns exact trimmed substrings; retain the original gaps separately.
            if (start < 0) { AddTranslatable(pieces, line[copied..], target); return; }
            Literal(pieces, line[copied..start]);
            AddTranslatable(pieces, sentence, target);
            copied = start + sentence.Length;
        }
        Literal(pieces, line[copied..]);
    }

    private static void AddMarkdownCells(List<Piece> pieces, string line, string target)
    {
        int start = 0;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '\\') { index++; continue; }
            if (line[index] == '`')
            {
                int count = 1;
                while (index + count < line.Length && line[index + count] == '`') count++;
                int close = FindBacktickClose(line, index + count, count);
                if (close >= 0) { index = close + count - 1; continue; }
            }
            if (line[index] != '|') continue;
            AddTranslatable(pieces, line[start..index], target, markdown: true);
            Literal(pieces, "|");
            start = index + 1;
        }
        AddTranslatable(pieces, line[start..], target, markdown: true);
    }

    private static List<Piece> JsonPlan(string source, string target)
    {
        var pieces = new List<Piece>();
        int copied = 0;
        for (int index = 0; index < source.Length; index++)
        {
            if (source[index] != '"') continue;
            int start = index++;
            while (index < source.Length)
            {
                if (source[index] == '\\') { index += 2; continue; }
                if (source[index] == '"') break;
                index++;
            }
            int end = index + 1, next = end;
            while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
            if (next < source.Length && source[next] == ':') continue; // Property names are immutable.
            string value = JsonSerializer.Deserialize<string>(source[start..end])!;
            if (!PinLanguagePolicy.Assess(value, target).CanTranslate) continue;
            Literal(pieces, source[copied..start]);
            Literal(pieces, "\"");
            AddLines(pieces, value, (line, _) => AddTranslatable(pieces, line, target, json: true), json: true);
            Literal(pieces, "\"");
            copied = end;
        }
        Literal(pieces, source[copied..]);
        return pieces;
    }

    private static List<Piece> TablePlan(string source, string target)
    {
        var pieces = new List<Piece>();
        int start = 0;
        for (int index = 0; index <= source.Length; index++)
        {
            if (index < source.Length && source[index] is not ('\t' or '\r' or '\n')) continue;
            string cell = source[start..index];
            if (DateTime.TryParse(cell.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) Literal(pieces, cell);
            else AddTranslatable(pieces, cell, target);
            if (index < source.Length) Literal(pieces, source[index].ToString());
            start = index + 1;
        }
        return pieces;
    }

    private static string JoinPdfLines(string source)
    {
        var output = new StringBuilder(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            char character = source[index];
            if (character is not ('\r' or '\n')) { output.Append(character); continue; }
            int end = index + 1;
            if (character == '\r' && end < source.Length && source[end] == '\n') end++;
            int next = end;
            while (next < source.Length && source[next] is ' ' or '\t') next++;
            int previous = output.Length - 1;
            while (previous >= 0 && output[previous] is ' ' or '\t') previous--;
            bool joins = previous >= 0 && output[previous] is not ('\r' or '\n' or '.' or '!' or '?' or ':' or ';')
                && next < source.Length && source[next] is not ('\r' or '\n' or '-' or '*' or '#' or '>');
            if (joins)
            {
                output.Length = previous + 1;
                bool hyphen = output[^1] == '-' && output.Length > 1 && char.IsLetter(output[^2]) && char.IsLower(source[next]);
                if (hyphen) output.Length--;
                else output.Append(' ');
                index = next - 1;
            }
            else
            {
                output.Append(source.AsSpan(index, end - index));
                index = end - 1;
            }
        }
        return output.ToString();
    }

    private static List<Piece> CodePlan(PinContent content, string target)
    {
        string source = content.OriginalText!;
        var pieces = new List<Piece>();
        string[] colors = OriginalColors(content);
        bool csharpDirectives = CSharpDirective().IsMatch(source);
        bool python = !csharpDirectives && Regex.IsMatch(source, @"(?m)^\s*(?:#!.*python|(?:from|import|def)\s|class\s+[^\r\n]*:|#)", RegexOptions.CultureInvariant);
        int copied = 0, position = 0;
        void LiteralCode(int start, int end)
        {
            while (start < end)
            {
                int next = start + 1;
                while (next < end && colors[next] == colors[start]) next++;
                AddLiteral(pieces, source[start..next], color: colors[start]);
                start = next;
            }
        }
        while (position < source.Length)
        {
            if (source[position] is '"' or '\'') { position = SkipString(source, position, python); continue; }
            if (!python && source[position] == '#' && IsLinePrefixWhitespace(source, position))
            {
                // Directive text (including quoted diagnostics and trailing comments) is code.
                while (position < source.Length && source[position] is not ('\r' or '\n')) position++;
                continue;
            }
            bool line = python ? source[position] == '#' : source.AsSpan(position).StartsWith("//");
            bool block = !python && source.AsSpan(position).StartsWith("/*");
            if (!line && !block) { position++; continue; }
            int markerLength = python ? 1 : 2;
            int bodyStart = position + markerLength;
            int bodyEnd = bodyStart;
            if (line)
                while (bodyEnd < source.Length && source[bodyEnd] is not ('\r' or '\n')) bodyEnd++;
            else
            {
                bodyEnd = source.IndexOf("*/", bodyStart, StringComparison.Ordinal);
                if (bodyEnd < 0) bodyEnd = source.Length;
            }
            LiteralCode(copied, bodyStart);
            string color = colors[Math.Min(position, colors.Length - 1)];
            AddLines(pieces, source[bodyStart..bodyEnd], (text, _) => AddTranslatable(pieces, text, target, comment: true, color: color), color: color);
            copied = bodyEnd;
            position = block && bodyEnd < source.Length ? bodyEnd + 2 : bodyEnd;
        }
        LiteralCode(copied, source.Length);
        return pieces;
    }

    private static bool IsLinePrefixWhitespace(string source, int position)
    {
        while (--position >= 0 && source[position] is not ('\r' or '\n'))
            if (!char.IsWhiteSpace(source[position])) return false;
        return true;
    }

    [GeneratedRegex(@"(?m)^[\t ]*#[\t ]*(?:if|elif|else|endif|define|undef|warning|error|line|region|endregion|pragma|nullable|include)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpDirective();

    private static int SkipString(string source, int start, bool python = false, int depth = 0)
    {
        // An uncertain/deep literal is retained to the end rather than exposing its contents as comments.
        if (depth > 32) return source.Length;
        char quote = source[start];
        int count = 1;
        while (start + count < source.Length && source[start + count] == quote) count++;
        bool triple = count >= 3;
        bool verbatim = start > 0 && source[start - 1] == '@'
            || start > 1 && source[start - 2] == '@' && source[start - 1] == '$';
        bool interpolated = start > 0 && source[start - 1] == '$'
            || start > 1 && source[start - 2] == '$' && source[start - 1] == '@'
            || python && start > 0 && source[start - 1] is 'f' or 'F';
        // Raw interpolated C# strings use a variable brace delimiter; retain the remainder
        // until a dedicated raw-string lexer can distinguish nested raw expression literals.
        if (triple && interpolated && !python) return source.Length;
        int position = start + (triple ? count : 1);
        while (position < source.Length)
        {
            if (interpolated && source[position] == '{')
            {
                if (position + 1 < source.Length && source[position + 1] == '{') position += 2;
                else position = SkipInterpolation(source, position + 1, python, depth + 1);
                continue;
            }
            if (!verbatim && source[position] == '\\') { position = Math.Min(source.Length, position + 2); continue; }
            if (source[position] != quote) { position++; continue; }
            if (triple)
            {
                int run = 0;
                while (position + run < source.Length && source[position + run] == quote) run++;
                if (run >= count) return position + count;
                position += run;
            }
            else if (verbatim && position + 1 < source.Length && source[position + 1] == quote) position += 2;
            else return position + 1;
        }
        return position;
    }

    private static int SkipInterpolation(string source, int position, bool python, int depth)
    {
        int braces = 1;
        while (position < source.Length)
        {
            if (source[position] is '"' or '\'') { position = SkipString(source, position, python, depth); continue; }
            if ((python && source[position] == '#') || (!python && source.AsSpan(position).StartsWith("//")))
            {
                while (position < source.Length && source[position] is not ('\r' or '\n')) position++;
                continue;
            }
            if (!python && source.AsSpan(position).StartsWith("/*"))
            {
                int close = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (close < 0) return source.Length;
                position = close + 2;
                continue;
            }
            if (source[position] == '{') braces++;
            if (source[position++] == '}' && --braces == 0) return position;
        }
        return position;
    }

    private static string[] OriginalColors(PinContent content)
    {
        string source = content.OriginalText!;
        string fallback = content.CodeDocument?.Foreground ?? "#DCDCDC";
        var colors = Enumerable.Repeat(fallback, source.Length).ToArray();
        if (content.CodeDocument is not { } document || !document.MatchesOriginalText(source)) return colors;
        int display = 0, runIndex = 0;
        for (int index = 0; index < source.Length; index++)
        {
            if (!char.IsWhiteSpace(source[index]))
                while (display < document.DisplayText.Length && char.IsWhiteSpace(document.DisplayText[display])) display++;
            while (runIndex < document.Runs.Count && display >= document.Runs[runIndex].Start + document.Runs[runIndex].Length) runIndex++;
            if (runIndex < document.Runs.Count && display >= document.Runs[runIndex].Start) colors[index] = document.Runs[runIndex].Color;
            if (display < document.DisplayText.Length && (document.DisplayText[display] == source[index] || !char.IsWhiteSpace(source[index]))) display++;
        }
        return colors;
    }

    private static List<Piece> MermaidPlan(string source, string target)
    {
        var pieces = new List<Piece>();
        AddLines(pieces, source, (line, _) => AddMermaidLine(pieces, line, target));
        return pieces;
    }

    private static void AddMermaidLine(List<Piece> pieces, string line, string target)
    {
        var ranges = new List<(int Start, int End)>();
        Match alias = Regex.Match(line, @"^\s*(?:participant|actor)\s+\S+\s+as\s+(?<label>.+)$", RegexOptions.CultureInvariant);
        Match message = Regex.Match(line, @"^\s*[\w-]+\s*[-<>=xo]+\s*[\w-]+\s*:(?<label>.*)$", RegexOptions.CultureInvariant);
        if (alias.Success) ranges.Add((alias.Groups["label"].Index, line.Length));
        else if (message.Success) ranges.Add((message.Groups["label"].Index, line.Length));
        else if (!line.TrimStart().StartsWith("%%", StringComparison.Ordinal))
        {
            for (int index = 0; index < line.Length; index++)
            {
                if (line[index] == '|' && index > 0 && line[index - 1] is '-' or '>')
                {
                    int close = line.IndexOf('|', index + 1);
                    if (close > index) { ranges.Add((index + 1, close)); index = close; }
                    continue;
                }
                if (line[index] is not ('[' or '(' or '{') || index == 0
                    || !(char.IsLetterOrDigit(line[index - 1]) || line[index - 1] == '_')) continue;
                int first = index;
                var stack = new Stack<char>();
                int end = index;
                bool quoted = false;
                for (; end < line.Length; end++)
                {
                    char character = line[end];
                    if (character == '\\') { end++; continue; }
                    if (character == '"') { quoted = !quoted; continue; }
                    if (quoted) continue;
                    if (character is '[' or '(' or '{') stack.Push(character);
                    else if (character is ']' or ')' or '}')
                    {
                        if (stack.Count == 0) break;
                        stack.Pop();
                        if (stack.Count == 0) break;
                    }
                }
                if (end >= line.Length || stack.Count != 0) continue;
                int bodyStart = first + 1, bodyEnd = end;
                while (bodyStart < bodyEnd && line[bodyStart] is '[' or '(' or '{') bodyStart++;
                while (bodyEnd > bodyStart && line[bodyEnd - 1] is ']' or ')' or '}') bodyEnd--;
                if (bodyStart < bodyEnd && line[bodyStart] == '"' && line[bodyEnd - 1] == '"') { bodyStart++; bodyEnd--; }
                if (bodyStart < bodyEnd) ranges.Add((bodyStart, bodyEnd));
                index = end;
            }
        }
        int copied = 0;
        foreach ((int start, int end) in ranges.OrderBy(range => range.Start))
        {
            if (start < copied) continue;
            Literal(pieces, line[copied..start]);
            AddTranslatable(pieces, line[start..end], target, mermaid: true);
            copied = end;
        }
        Literal(pieces, line[copied..]);
    }

    private static string EscapeMermaidLabel(string value) => WebUtility.HtmlEncode(WebUtility.HtmlDecode(value))
        .Replace("[", "&#91;", StringComparison.Ordinal).Replace("]", "&#93;", StringComparison.Ordinal)
        .Replace("{", "&#123;", StringComparison.Ordinal).Replace("}", "&#125;", StringComparison.Ordinal)
        .Replace("(", "&#40;", StringComparison.Ordinal).Replace(")", "&#41;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal);

    private static void AddLines(List<Piece> pieces, string text, Action<string, int> line, bool json = false, string? color = null)
    {
        int start = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] is not ('\r' or '\n')) continue;
            line(text[start..index], start);
            int end = index;
            if (end < text.Length && text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
            AddLiteral(pieces, text[index..end], json, color);
            index = end - 1;
            start = end;
            if (end == text.Length) break;
        }
    }
}
