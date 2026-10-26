using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed record PinTranslationResult(PinContent Content, bool NeedsReview);

/// <summary>
/// Translates complete prose lines with the existing offline translator. Values and inline
/// syntax retain sentence context; a result is applied only after their preservation is checked.
/// The caller retains the original pin and owns model preparation and operation cancellation.
/// </summary>
internal sealed partial class PinTranslationService(ITextTranslator translator, string targetLanguage = "ko", bool preserveLineBreaks = false)
{
    private static readonly JsonSerializerOptions JsonTextOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
    private readonly ITextTranslator _translator = translator ?? throw new ArgumentNullException(nameof(translator));
    private readonly string _targetLanguage = PinLanguagePolicy.NormalizeTarget(targetLanguage);
    private readonly bool _preserveLineBreaks = preserveLineBreaks;

    internal static bool CanTranslate(PinContent content, string targetLanguage = "ko")
    {
        ArgumentNullException.ThrowIfNull(content);
        return CreatePlan(content, PinLanguagePolicy.NormalizeTarget(targetLanguage)).Any(piece => piece.Translate);
    }

    internal async Task<PinTranslationResult?> TranslateAsync(PinContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        List<Piece> pieces = CreatePlan(content, _targetLanguage, _preserveLineBreaks);
        string[] sources = pieces.Where(piece => piece.Translate).Select(piece => piece.Text).ToArray();
        if (sources.Length == 0) return null;

        IReadOnlyList<TranslatedText> translated = await _translator.TranslateAsync(sources, null, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (translated.Count != sources.Length)
            throw new InvalidOperationException("The translator returned an incomplete pin result.");

        var output = new StringBuilder(content.OriginalText!.Length);
        int index = 0;
        bool needsReview = false;
        var runs = new List<PinCodeRun>();
        void Append(Piece piece, string text)
        {
            int start = output.Length;
            output.Append(text);
            if (piece.Color is { } color && text.Length > 0)
            {
                if (runs.LastOrDefault() is { } previous && previous.Start + previous.Length == start && previous.Color == color)
                    runs[^1] = previous with { Length = previous.Length + text.Length };
                else runs.Add(new PinCodeRun(start, text.Length, color));
            }
        }
        foreach (Piece piece in pieces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!piece.Translate)
            {
                Append(piece, piece.Text);
                continue;
            }

            TranslatedText result = translated[index++];
            if (!string.Equals(result.Source, piece.Text, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(result.Text))
            {
                throw new InvalidOperationException("The translator returned a missing or mismatched pin result.");
            }
            // Preserve the complete sentence context, including values and inline formatting.
            // The model's value checker does not detect broken Markdown; validate both here.
            string text = result.Text.ReplaceLineEndings(" ").Trim();
            ProtectedTokenCheck beforeRepair = ProtectedTokens.Check(piece.Text, text);
            (text, _) = ProtectedTokens.Repair(piece.Text, text);
            bool safe = !result.NeedsReview && beforeRepair.Altered.Count == 0 && beforeRepair.Invented.Count == 0
                && PreservesProtectedValues(piece.Text, text)
                && (!piece.Markdown || PreservesMarkdown(piece.Text, text))
                && (!piece.Comment || !text.Contains("*/", StringComparison.Ordinal));
            needsReview |= !safe;
            string value = safe ? text : piece.Text;
            Append(piece, piece.JsonString ? JsonSerializer.Serialize(value, JsonTextOptions)[1..^1]
                : piece.MermaidLabel && safe ? EscapeMermaidLabel(value) : value);
        }

        string renderedText = output.ToString();
        if (IsJson(content.OriginalText!))
        {
            using JsonDocument validated = JsonDocument.Parse(renderedText);
        }
        bool isCode = content.TextStyle == PinTextStyle.Code || pieces.Any(piece => piece.Color is not null);
        PinCodeDocument? code = isCode && renderedText.Length <= PinCodeDocument.MaxOriginalTextLength
            ? new PinCodeDocument(renderedText, runs.Count <= PinCodeDocument.MaxRunCount ? runs : [],
                content.CodeDocument?.Background ?? "#1E1E1E", content.CodeDocument?.Foreground ?? "#DCDCDC") : null;
        bool isTable = content.Kind == PinContentKind.Table;
        bool sameDiagrams = ClipboardMarkdownRenderer.ExtractMermaidBlocks(content.OriginalText!).Select(block => block.Source)
            .SequenceEqual(ClipboardMarkdownRenderer.ExtractMermaidBlocks(renderedText).Select(block => block.Source), StringComparer.Ordinal);
        var diagrams = sameDiagrams ? content.MermaidResults : null;
        cancellationToken.ThrowIfCancellationRequested();
        var image = await StaThreadTask.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return code is not null ? ClipboardCodeRenderer.Render(code)
                : content.IsMarkdown ? ClipboardMarkdownRenderer.Render(renderedText, 1, diagrams)
                : isTable ? ClipboardTextRenderer.Render(renderedText, true)
                : ClipboardTextRenderer.RenderTranslation(renderedText);
        }, "MyCapture translated pin render").ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        PinContent translatedContent = code is not null ? PinContent.FromCode(image, renderedText, code, content.Provenance)
            : content.IsMarkdown
            ? PinContent.FromMarkdown(image, renderedText, content.Provenance)
            : PinContent.FromText(image, renderedText, isTable, content.Provenance) with { BorderlessText = !isTable };
        return new PinTranslationResult(translatedContent with
        {
            PendingProvenance = content.PendingProvenance,
            MermaidResults = diagrams,
        }, needsReview);
    }

    private static List<Piece> CreatePlan(PinContent content, string target, bool preserveLines = false)
    {
        var pieces = new List<Piece>();
        if (content.Kind == PinContentKind.Image || target is not ("ko" or "en")
            || string.IsNullOrWhiteSpace(content.OriginalText)) return pieces;
        string source = content.OriginalText;
        if (IsJson(source)) return JsonPlan(source, target);
        if (content.TextStyle == PinTextStyle.Code || (!content.IsMarkdown && source.Split(['\r', '\n']).Any(IsCodeLine)))
            return CodePlan(content, target);
        if (content.Kind == PinContentKind.Table) return TablePlan(source, target);
        if (ClipboardMarkdownRenderer.ExtractMermaidBlocks(source) is [var diagram]
            && string.Equals(diagram.Source, source, StringComparison.Ordinal)) return MermaidPlan(source, target);
        if (!source.Contains('\r') && !source.Contains('\n') && StandalonePath().IsMatch(source)) return pieces;
        if (!content.IsMarkdown && !preserveLines) source = JoinPdfLines(source);

        char fence = '\0';
        int fenceLength = 0;
        bool mermaid = false;
        int start = 0;
        while (start < source.Length)
        {
            int end = start;
            while (end < source.Length && source[end] is not ('\r' or '\n')) end++;
            string line = source[start..end];
            Match prefix = content.IsMarkdown ? MarkdownPrefix().Match(line) : Match.Empty;
            string trimmed = content.IsMarkdown ? line[prefix.Length..].TrimStart() : line.TrimStart();
            int run = LeadingFenceLength(trimmed);
            if (content.IsMarkdown && run >= 3)
            {
                if (fence == '\0')
                {
                    fence = trimmed[0];
                    fenceLength = run;
                    mermaid = trimmed[run..].Trim().Equals("mermaid", StringComparison.OrdinalIgnoreCase);
                }
                else if (trimmed[0] == fence && run >= fenceLength && string.IsNullOrWhiteSpace(trimmed[run..]))
                {
                    fence = '\0';
                }
                Literal(pieces, line);
            }
            else if (fence != '\0' && mermaid) AddMermaidLine(pieces, line, target);
            else if (fence != '\0' || IsCodeLine(line)
                || (content.IsMarkdown && (IsMarkdownLiteralLine(line) || IsIndentedCode(line))))
            {
                Literal(pieces, line);
            }
            else if (content.IsMarkdown)
            {
                Literal(pieces, prefix.Value);
                string body = line[prefix.Length..];
                if (body.Contains('|')) AddMarkdownCells(pieces, body, target);
                else AddTranslatable(pieces, body, target, markdown: true);
            }
            else
            {
                AddPlainProse(pieces, line, target);
            }

            int next = end;
            if (next < source.Length && source[next] == '\r') next++;
            if (next < source.Length && source[next] == '\n') next++;
            Literal(pieces, source[end..next]);
            start = next;
        }
        return pieces;
    }

    private static bool PreservesProtectedValues(string source, string translated)
    {
        // Keep exact source values even when the engine considers a spelling or unit change
        // harmless. Count occurrences so duplicated/dropped values cannot pass this check.
        IReadOnlyList<ProtectedSpan> protectedSpans = ProtectedTokens.Find(source);
        IEnumerable<string> values = protectedSpans.Select(span => span.Text)
            .Concat(AdditionalLiteral().Matches(source)
                .Select(match => match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}')))
            .Concat(PinLanguagePolicy.AddressesAndPlaceholders().Matches(source)
                .Select(match => match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}')))
            .Where(value => value.Length > 0);
        foreach (string value in values.Distinct(StringComparer.Ordinal))
        {
            if (CountOccurrences(source, value) != CountOccurrences(translated, value)) return false;
        }
        ProtectedTokenCheck check = ProtectedTokens.Check(source, translated);
        return check.Altered.Count == 0 && check.Invented.Count == 0;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static bool PreservesMarkdown(string source, string translated)
    {
        // Korean word order can move an intact code span or link. Compare complete syntax
        // units as a multiset, while checking emphasis nesting in each sentence separately.
        var expected = new List<string>();
        var actual = new List<string>();
        return MarkdownPrefix().Match(translated).Length == 0
            && TryCollectMarkdown(source, expected)
            && TryCollectMarkdown(translated, actual)
            && expected.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool TryCollectMarkdown(string text, List<string> syntax, int depth = 0)
    {
        if (depth > 32) return false;
        var emphasis = new Stack<string>();
        for (int index = 0; index < text.Length;)
        {
            char c = text[index];
            if (c == '\\' && index + 1 < text.Length)
            {
                syntax.Add("escape:" + text.Substring(index, 2));
                index += 2;
                continue;
            }
            if (c == '`')
            {
                int count = 1;
                while (index + count < text.Length && text[index + count] == '`') count++;
                int close = FindBacktickClose(text, index + count, count);
                if (close < 0) return false;
                syntax.Add("code:" + text[index..(close + count)]);
                index = close + count;
                continue;
            }
            if (c == '<')
            {
                int close = text.IndexOf('>', index + 1);
                if (close >= 0)
                {
                    syntax.Add("angle:" + text[index..(close + 1)]);
                    index = close + 1;
                    continue;
                }
            }
            if (c == '[' || (c == '!' && index + 1 < text.Length && text[index + 1] == '['))
            {
                int labelStart = c == '!' ? index + 1 : index;
                int close = FindClose(text, labelStart, '[', ']');
                if (close >= 0 && close + 1 < text.Length && text[close + 1] is '(' or '[')
                {
                    char open = text[close + 1];
                    int destinationEnd = FindClose(text, close + 1, open, open == '(' ? ')' : ']');
                    if (destinationEnd >= 0)
                    {
                        if (c == '!') syntax.Add("image:" + text[index..(destinationEnd + 1)]);
                        else if (open == '[' && destinationEnd == close + 2)
                        {
                            // A collapsed reference uses the visible label as its destination ID.
                            syntax.Add("reference:" + text[index..(destinationEnd + 1)]);
                        }
                        else
                        {
                            syntax.Add("link:" + text[close..(destinationEnd + 1)]);
                            if (!TryCollectMarkdown(text[(index + 1)..close], syntax, depth + 1)) return false;
                        }
                        index = destinationEnd + 1;
                        continue;
                    }
                }
                if (close >= 0)
                {
                    // Shortcut reference labels are IDs too. Preserve ambiguous bracket text
                    // conservatively instead of accepting a translation that disconnects a link.
                    syntax.Add("reference:" + text[index..(close + 1)]);
                    index = close + 1;
                    continue;
                }
            }
            if (c is '*' or '_' or '~')
            {
                int end = index + 1;
                while (end < text.Length && text[end] == c) end++;
                string marker = text[index..end];
                if ((c == '_' && index > 0 && end < text.Length
                        && char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[end]))
                    || (c == '~' && marker.Length == 1))
                {
                    syntax.Add("literal:" + marker);
                    index = end;
                    continue;
                }
                syntax.Add("style:" + marker);
                bool canOpen = end < text.Length && !char.IsWhiteSpace(text[end]);
                bool canClose = index > 0 && !char.IsWhiteSpace(text[index - 1]);
                if (canClose && emphasis.TryPeek(out string? previous) && previous == marker) emphasis.Pop();
                else if (canOpen) emphasis.Push(marker);
                else return false;
                index = end;
                continue;
            }
            if (c is '|' or '[' or ']' or '<' or '>' or '#') syntax.Add("char:" + c);
            index++;
        }
        return emphasis.Count == 0;
    }

    private static void AddTranslatable(List<Piece> pieces, string text, string target,
        bool markdown = false, bool json = false, bool comment = false, bool mermaid = false, string? color = null)
    {
        int start = 0, end = text.Length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        AddLiteral(pieces, text[..start], json, color);
        string prose = text[start..end];
        string languageText = PinLanguagePolicy.WithoutProtectedText(prose);
        if (languageText.Any(PinLanguagePolicy.IsKorean) && languageText.Any(PinLanguagePolicy.IsLatin))
        {
            int segment = 0, language = 0;
            for (int position = 0; position < languageText.Length; position++)
            {
                int current = PinLanguagePolicy.IsKorean(languageText[position]) ? 1 : PinLanguagePolicy.IsLatin(languageText[position]) ? 2 : 0;
                if (current == 0) continue;
                if (language != 0 && current != language)
                {
                    AddTranslatable(pieces, prose[segment..position], target, markdown, json, comment, mermaid, color);
                    segment = position;
                }
                language = current;
            }
            AddTranslatable(pieces, prose[segment..], target, markdown, json, comment, mermaid, color);
        }
        else if (PinLanguagePolicy.Assess(prose, target).CanTranslate)
            pieces.Add(new Piece(prose, true, json, markdown, comment, mermaid, color));
        else AddLiteral(pieces, prose, json, color);
        AddLiteral(pieces, text[end..], json, color);
    }

    private static void AddLiteral(List<Piece> pieces, string text, bool json = false, string? color = null)
    {
        if (text.Length > 0) pieces.Add(new Piece(json ? JsonSerializer.Serialize(text, JsonTextOptions)[1..^1] : text, false, Color: color));
    }

    private static void Literal(List<Piece> pieces, string text)
    {
        if (text.Length > 0) pieces.Add(new Piece(text, false));
    }

    private static bool IsJson(string source)
    {
        string trimmed = source.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[')) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsCodeLine(string line) => CodeLine().IsMatch(line);
    private static bool IsIndentedCode(string line) => line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t');
    private static bool IsMarkdownLiteralLine(string line) => MarkdownLiteralLine().IsMatch(line);

    private static int LeadingFenceLength(string text)
    {
        if (text.Length == 0 || text[0] is not ('`' or '~')) return 0;
        int count = 1;
        while (count < text.Length && text[count] == text[0]) count++;
        return count;
    }

    private static int FindBacktickClose(string text, int start, int count)
    {
        for (int index = start; index < text.Length; index++)
        {
            if (text[index] != '`') continue;
            int end = index;
            while (end < text.Length && text[end] == '`') end++;
            if (end - index == count) return index;
            index = end - 1;
        }
        return -1;
    }

    private static int FindClose(string text, int start, char open, char close)
    {
        int depth = 0;
        for (int index = start; index < text.Length; index++)
        {
            if (text[index] == '\\') { index++; continue; }
            if (text[index] == open) depth++;
            else if (text[index] == close && --depth == 0) return index;
        }
        return -1;
    }

    private sealed record Piece(string Text, bool Translate, bool JsonString = false, bool Markdown = false,
        bool Comment = false, bool MermaidLabel = false, string? Color = null);

    [GeneratedRegex(@"^\s*(?:(?:(?:using|import|from|namespace|class|struct|interface|public|private|protected|internal|const|let|var|def|function|return)\b[^\r\n]*[;{}=])|(?:[\w.$]+\([^\r\n]*\)\s*;?)|(?:[\w.$]+\s*(?:=>|:=|=(?!=))[^\r\n]*)|(?:[{}\[\],;]+))\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeLine();

    [GeneratedRegex(@"^\s*(?:(?:>[ \t]*)*(?:(?:#{1,6}[ \t]+)|(?:(?:[-+*]|\d+[.)])[ \t]+(?:\[[ xX]\][ \t]+)?))?)(?:>[ \t]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownPrefix();

    [GeneratedRegex(@"^\s*(?:(?:\|?\s*:?-{3,}:?\s*)+\|?|[-*_](?:\s*[-*_]){2,}|=+|\[[^\]]+\]:\s*\S.*|<!--.*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLiteralLine();

    [GeneratedRegex(@"(?:\b(?:https?|ftp)://|\bwww\.|\bmailto:)[^\s<>]+|(?<![\w])(?:[A-Za-z]:[\\/]|\\\\|(?:\.{1,2})?/)[^\s<>]+|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|\S{129,}", RegexOptions.CultureInvariant)]
    private static partial Regex AdditionalLiteral();

    [GeneratedRegex(@"^\s*(?:[A-Za-z]:[\\/]|\\\\|(?:\.{1,2})?/)[^\r\n]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex StandalonePath();
}
