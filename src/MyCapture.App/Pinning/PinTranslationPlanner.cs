using System.Text.Json;

namespace MyCapture.App.Pinning;

/// <summary>
/// Decides which substrings of a pinned text are translated. The plan keeps every other character
/// (structure, code, keys, numbers) byte-identical: piece Raw strings concatenate back to the source.
/// Markdown and Mermaid are planned by <c>PinTranslationPlanner.Markdown.cs</c>; the rest is in this partial class.
/// </summary>
internal static partial class PinTranslationPlanner
{
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 1024 };

    /// <summary>
    /// Chooses the structure. <paramref name="isMarkdown"/> and <paramref name="isGrid"/> come from the pin
    /// kind; JSON, Mermaid, tab-separated tables and code are detected from the text, everything else is prose.
    /// </summary>
    internal static PinTextFormat Classify(string source, bool isMarkdown, bool isGrid)
    {
        source ??= string.Empty;
        if (isMarkdown) return PinTextFormat.Markdown;
        if (isGrid) return PinTextFormat.Table;
        try
        {
            if (IsJson(source)) return PinTextFormat.Json;
            if (StartsWithMermaidFence(source)) return PinTextFormat.Markdown;
            if (IsMermaid(source)) return PinTextFormat.Mermaid;
            if (IsTabular(source)) return PinTextFormat.Table;
            if (IsCode(source)) return PinTextFormat.Code;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return PinTextFormat.Prose;
    }

    /// <summary>
    /// Builds the plan. Never throws: when a part cannot be parsed, or a plan fails to reproduce the source,
    /// the result has no translatable piece.
    /// </summary>
    internal static PinTranslationPlan Create(string source, PinTextFormat format, PinTranslationDirection direction)
    {
        source ??= string.Empty;
        try
        {
            PinTranslationPlan plan = format switch
            {
                PinTextFormat.Json => CreateJson(source, direction),
                PinTextFormat.Code => CreateCode(source, direction),
                PinTextFormat.Table => CreateTable(source, direction),
                PinTextFormat.Markdown => CreateMarkdown(source, direction),
                PinTextFormat.Mermaid => CreateMermaid(source, direction),
                _ => CreateProse(source, direction),
            };
            return string.Equals(plan.Original, source, StringComparison.Ordinal) ? plan : Unchanged(source, format);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Unchanged(source, format);
        }
    }

    /// <summary>The text a person wrote inside a structure (JSON string values, code comments, table cells), for language detection.</summary>
    internal static string ExtractHumanText(string source, PinTextFormat format)
    {
        source ??= string.Empty;
        try
        {
            IEnumerable<string> parts = format switch
            {
                PinTextFormat.Json => ScanJsonValues(source)?.Select(static value => value.Decoded) ?? [],
                PinTextFormat.Code => CollectCommentSpans(source, InferCommentStyles(source)).Select(span => source[span.Start..span.End]),
                PinTextFormat.Table => ScanTable(source).SelectMany(static row => row).Select(static cell => cell.Text),
                _ => [source],
            };
            return string.Join('\n', parts);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private static PinTranslationPlan Unchanged(string source, PinTextFormat format) =>
        new(format, source.Length == 0 ? [] : [new PlanPiece(source)]);

    private static bool IsJson(string source)
    {
        ReadOnlySpan<char> trimmed = source.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] is not ('{' or '[')) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(source, JsonOptions);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private readonly record struct LineSpan(int Start, int End, int Next);

    /// <summary>Lines with their terminators (\r\n, \n or \r). End excludes the terminator; Next is the next line start.</summary>
    private static List<LineSpan> SplitLines(string text, int from = 0, int to = -1)
    {
        if (to < 0) to = text.Length;
        var lines = new List<LineSpan>();
        int start = from;
        while (start < to)
        {
            int end = start;
            while (end < to && text[end] is not ('\r' or '\n')) end++;
            int next = end;
            if (next < to && text[next] == '\r') next++;
            if (next < to && text[next] == '\n') next++;
            lines.Add(new LineSpan(start, end, next));
            start = next;
        }
        return lines;
    }

    private static bool IsBlank(string text, LineSpan line)
    {
        for (int index = line.Start; index < line.End; index++)
        {
            if (!char.IsWhiteSpace(text[index])) return false;
        }
        return true;
    }

    /// <summary>Accumulates translatable pieces; everything between them becomes literal pieces automatically.</summary>
    private sealed class PlanBuilder(string source)
    {
        private readonly List<PlanPiece> _pieces = [];
        private int _cursor;
        private int _literalStart;

        internal int Cursor => _cursor;

        /// <summary>Adds a translatable piece covering source[start..end]. Pieces must be added in source order without overlap.</summary>
        internal void Piece(int start, int end, PlanKind kind, string? content = null)
        {
            if (start < _cursor || end <= start || end > source.Length)
                throw new InvalidOperationException("Plan pieces must be ordered and must not overlap.");
            _cursor = start;
            FlushLiteral();
            _pieces.Add(new PlanPiece(source[start..end], kind, true, content));
            _cursor = _literalStart = end;
        }

        internal List<PlanPiece> Finish()
        {
            _cursor = source.Length;
            FlushLiteral();
            return _pieces;
        }

        private void FlushLiteral()
        {
            if (_cursor > _literalStart) _pieces.Add(new PlanPiece(source[_literalStart.._cursor]));
            _literalStart = _cursor;
        }
    }

    /// <summary>The span of <paramref name="text"/> without leading and trailing whitespace; null when it is blank.</summary>
    private static (int Start, int End)? TrimSpan(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end > start ? (start, end) : null;
    }
}
