using System.Text.RegularExpressions;

namespace MyCapture.App.Pinning;

// Code: only the text of comments is translated. Markers, string literals and code stay.
internal static partial class PinTranslationPlanner
{
    private enum CommentKind { Slash, Hash, Dash, Block }

    private readonly record struct Comment(int Start, int End, CommentKind Kind);

    private sealed class DocState
    {
        internal bool InCode;
    }

    private static PinTranslationPlan CreateCode(string source, PinTranslationDirection direction)
    {
        var builder = new PlanBuilder(source);
        foreach ((int start, int end) in CollectCommentSpans(source, InferCommentStyles(source)))
        {
            if (IsTranslatableText(source[start..end], direction)) builder.Piece(start, end, PlanKind.Comment);
        }
        return new PinTranslationPlan(PinTextFormat.Code, builder.Finish());
    }

    /// <summary>
    /// Text spans inside comments, in source order and without overlap: one per comment line, without markers,
    /// XML/HTML doc tags, doc-tag names, TODO prefixes, directives and commented-out code.
    /// </summary>
    private static List<(int Start, int End)> CollectCommentSpans(string source, CommentStyles styles)
    {
        var spans = new List<(int Start, int End)>();
        var state = new DocState();
        foreach (Comment comment in ScanComments(source, styles))
        {
            if (comment.Kind == CommentKind.Block) AddBlockComment(spans, source, comment, state);
            else AddLineComment(spans, source, comment, state);
        }
        return spans;
    }

    private static void AddLineComment(List<(int Start, int End)> spans, string source, Comment comment, DocState state)
    {
        int index = comment.Start;
        char marker = comment.Kind switch { CommentKind.Slash => '/', CommentKind.Hash => '#', _ => '-' };
        if (comment.Kind == CommentKind.Hash && comment.Start == 0 && comment.End > 1 && source[1] == '!') return;
        while (index < comment.End && source[index] == marker) index++;
        int markerLength = index - comment.Start;
        bool bang = comment.Kind == CommentKind.Slash && markerLength == 2 && index < comment.End && source[index] == '!';
        if (bang) index++;
        bool doc = comment.Kind == CommentKind.Slash && (markerLength == 3 || bang);
        bool tight = index < comment.End && !char.IsWhiteSpace(source[index]);
        (int Start, int End)? text = TrimSpan(source, index, comment.End);
        if (text is null) return;
        AddCommentText(spans, source, text.Value.Start, text.Value.End, doc, comment.Kind == CommentKind.Hash && tight, state);
    }

    /// <summary>Block comment: the opening marker, the leading '*' of continuation lines and the closing marker stay literal.</summary>
    private static void AddBlockComment(List<(int Start, int End)> spans, string source, Comment comment, DocState state)
    {
        bool closed = comment.End - comment.Start >= 4 && source[comment.End - 1] == '/' && source[comment.End - 2] == '*';
        int limit = closed ? comment.End - 2 : comment.End;
        bool doc = comment.Start + 2 < limit && source[comment.Start + 2] is '*' or '!';
        bool first = true;
        foreach (LineSpan line in SplitLines(source, comment.Start, comment.End))
        {
            int index = first ? comment.Start + 2 : line.Start;
            int end = Math.Min(line.End, limit);
            if (first)
            {
                if (doc && index < end) index++;
            }
            else
            {
                while (index < end && char.IsWhiteSpace(source[index])) index++;
                while (index < end && source[index] == '*') index++;
            }
            first = false;
            if (index >= end) continue;
            (int Start, int End)? text = TrimSpan(source, index, end);
            if (text is not null) AddCommentText(spans, source, text.Value.Start, text.Value.End, doc, false, state);
        }
    }

    private static void AddCommentText(List<(int Start, int End)> spans, string source, int start, int end, bool doc, bool tightHash, DocState state)
    {
        if (!doc)
        {
            AddSegment(spans, source, start, end, tightHash);
            return;
        }

        string text = source[start..end];
        if (text[0] == '@')
        {
            Match tag = JavaDocParamTag().Match(text);
            if (!tag.Success) tag = JavaDocTextTag().Match(text);
            if (!tag.Success) return;
            start += tag.Length;
            if (start >= end) return;
            text = source[start..end];
        }

        int cursor = 0;
        foreach (Match markup in DocMarkup().Matches(text))
        {
            if (!state.InCode) AddSegment(spans, source, start + cursor, start + markup.Index, false);
            string name = markup.Value.TrimStart('<', '/').Split([' ', '>', '/', '\t'], 2)[0];
            if (name.Equals("code", StringComparison.OrdinalIgnoreCase) || name.Equals("pre", StringComparison.OrdinalIgnoreCase))
                state.InCode = !markup.Value.StartsWith("</", StringComparison.Ordinal) && !markup.Value.EndsWith("/>", StringComparison.Ordinal);
            cursor = markup.Index + markup.Length;
        }
        if (!state.InCode) AddSegment(spans, source, start + cursor, end, false);
    }

    /// <summary>One run of comment text: skips directives and commented-out code, and keeps TODO(x): style prefixes literal.</summary>
    private static void AddSegment(List<(int Start, int End)> spans, string source, int start, int end, bool tightHash)
    {
        (int Start, int End)? trimmed = TrimSpan(source, start, end);
        if (trimmed is null) return;
        start = trimmed.Value.Start;
        end = trimmed.Value.End;
        string text = source[start..end];
        if (CommentDirective().IsMatch(text) || (tightHash && PreprocessorWord().IsMatch(text)) || LooksLikeCodeText(text)) return;
        Match prefix = TodoPrefix().Match(text);
        if (prefix.Success)
        {
            start += prefix.Length;
            if (start >= end) return;
        }
        spans.Add((start, end));
    }

    /// <summary>A comment that is itself a statement: ends with ; { }, or is an assignment, a call or a one-line control statement.</summary>
    private static bool LooksLikeCodeText(string text)
    {
        if (ProseWords().IsMatch(text)) return false;
        if (text.Length > 1 && text[^1] is ';' or '{' or '}') return !text.EndsWith('}') || !Placeholder().IsMatch(text);
        return CommentedCode().IsMatch(text);
    }

    /// <summary>
    /// Finds comment spans with a state machine that knows string literals: "..." and '...' with backslash escapes,
    /// C# verbatim @"..." and raw """...""", Python triple quotes, JS/Go backtick strings. Comment markers inside them are text.
    /// </summary>
    private static List<Comment> ScanComments(string text, CommentStyles styles)
    {
        var comments = new List<Comment>();
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (c is '"' or '\'')
            {
                index = SkipQuote(text, index, styles);
                continue;
            }
            if (c == '`')
            {
                index = SkipEscaped(text, index, '`', multiLine: true, out _);
                continue;
            }
            if (styles.Slash && c == '/' && next == '/' && (index == 0 || text[index - 1] != ':'))
            {
                int end = LineEnd(text, index);
                comments.Add(new Comment(index, end, CommentKind.Slash));
                index = end;
                continue;
            }
            if (styles.Block && c == '/' && next == '*')
            {
                int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + 2;
                comments.Add(new Comment(index, end, CommentKind.Block));
                index = end;
                continue;
            }
            if (styles.Hash && c == '#' && next != '[' && (index == 0 || char.IsWhiteSpace(text[index - 1])))
            {
                int end = LineEnd(text, index);
                comments.Add(new Comment(index, end, CommentKind.Hash));
                index = end;
                continue;
            }
            if (styles.Dashes && c == '-' && next == '-' && (index == 0 || char.IsWhiteSpace(text[index - 1]) || text[index - 1] is ';' or ')' or ','))
            {
                int end = LineEnd(text, index);
                comments.Add(new Comment(index, end, CommentKind.Dash));
                index = end;
                continue;
            }
            index++;
        }
        return comments;
    }

    private static int LineEnd(string text, int index)
    {
        while (index < text.Length && text[index] is not ('\r' or '\n')) index++;
        return index;
    }

    /// <summary>Index after the string literal that starts at <paramref name="index"/> (a quote character).</summary>
    private static int SkipQuote(string text, int index, CommentStyles styles)
    {
        char quote = text[index];
        int run = 1;
        while (index + run < text.Length && text[index + run] == quote) run++;
        if (run >= 3) return SkipTriple(text, index, quote, run);
        if (run == 2) return index + 2;

        bool verbatim = quote == '"' && styles.Slash && index > 0
            && (text[index - 1] == '@' || (text[index - 1] == '$' && index > 1 && text[index - 2] == '@'));
        if (verbatim)
        {
            for (int cursor = index + 1; cursor < text.Length; cursor++)
            {
                if (text[cursor] != '"') continue;
                if (cursor + 1 < text.Length && text[cursor + 1] == '"') { cursor++; continue; }
                return cursor + 1;
            }
            return text.Length;
        }

        int after = SkipEscaped(text, index, quote, multiLine: false, out bool closed);
        // An apostrophe without a closing one on the line is not a string (Rust lifetime, plain text).
        return closed || quote == '"' ? after : index + 1;
    }

    private static int SkipTriple(string text, int index, char quote, int openingRun)
    {
        for (int cursor = index + openingRun; cursor < text.Length; cursor++)
        {
            if (text[cursor] == '\\') { cursor++; continue; }
            if (text[cursor] != quote) continue;
            int run = 1;
            while (cursor + run < text.Length && text[cursor + run] == quote) run++;
            if (run >= openingRun) return cursor + run;
            cursor += run - 1;
        }
        return text.Length;
    }

    private static int SkipEscaped(string text, int index, char quote, bool multiLine, out bool closed)
    {
        closed = false;
        for (int cursor = index + 1; cursor < text.Length; cursor++)
        {
            char c = text[cursor];
            if (c == '\\')
            {
                if (cursor + 2 < text.Length && text[cursor + 1] == '\r' && text[cursor + 2] == '\n') cursor += 2;
                else cursor++;
                continue;
            }
            if (c == quote)
            {
                closed = true;
                return cursor + 1;
            }
            if (!multiLine && c is '\r' or '\n') return cursor;
        }
        return text.Length;
    }

    [GeneratedRegex(@"^(?:-\*-|coding[:=]|type:|noqa\b|pylint:|pyright:|mypy:|isort:|fmt:|flake8|nolint|NOLINT|nosec|NOSONAR|pragma\b|eslint\b|eslint-|@ts-|prettier-ignore|istanbul\b|jshint|jscs|tslint|noinspection|ReSharper|StyleCop|rubocop:|swiftlint:|frozen_string_literal|encoding:|vim?:|SPDX-License-Identifier|MARK:|<editor-fold|</editor-fold|\[SuppressMessage|lint:|go:)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CommentDirective();

    // '#if', '#region' and similar written without a space after the marker are preprocessor lines, not comments.
    [GeneratedRegex(@"^(?:include|define|undef|ifdef|ifndef|if|elif|else|endif|pragma|region|endregion|line|error|warning|nullable|load)\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PreprocessorWord();

    [GeneratedRegex(@"^(?:(?:TODO|FIXME|HACK|XXX|BUG)\b(?:\([^)]*\))?:?|(?:NOTE|WARNING|WARN|TEMP)(?:\([^)]*\))?:)[ \t]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TodoPrefix();

    [GeneratedRegex(
        @"^(?:[\w.$\[\]]+\s*(?:[-+*/%&|^]?=|:=)\s*\S[^\r\n]*"
        + @"|[\w.$:]+\([^\r\n]*\)\s*[;{]?"
        + @"|(?:if|for|while|foreach|switch|catch)\s*\(.*\)\s*\{?"
        + @"|(?:import|from)\s+[\w.{}*,\s]+\s(?:from|import)\b[^\r\n]*"
        + @"|[^\r\n]*\w\s*=>\s*[\w({\[][^\r\n]*)$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CommentedCode();

    [GeneratedRegex(@"^@(?:param|arg|argument|property|prop|throws|exception|template|typeparam|type|typedef|callback|member|var)\b(?:\s*\{[^}]*\})?(?:\s+(?:\[[^\]]*\]|[\w.$<>]+))?(?:\s+-)?[ \t]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex JavaDocParamTag();

    [GeneratedRegex(@"^@(?:returns?|deprecated|description|summary|remarks|example|note|todo)\b(?:\s*\{[^}]*\})?[ \t]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex JavaDocTextTag();

    // Three plain words in a row: the text is a sentence, not a statement.
    [GeneratedRegex(@"\p{L}{2,}[ 	]+\p{L}{2,}[ 	]+\p{L}{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProseWords();

    [GeneratedRegex(@"\s\{[\w.:\-]*\}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"</?[A-Za-z][^<>]*>|\{@\w+[^{}]*\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DocMarkup();
}
