using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed record PinTranslationResult(PinContent Content, bool NeedsReview, PinContent? Document = null);

/// <summary>Whether a pin can be translated into the target language, and in which direction.</summary>
internal readonly record struct PinTranslationDecision(PinTranslationAvailability Availability, PinTranslationDirection Direction);

/// <summary>
/// Translates the human text of a pin with the offline translator and leaves its structure alone.
/// A plan splits the text into translatable pieces (prose, JSON string values, code comments, table
/// cells, Markdown lines, diagram labels) and literal syntax; each translated piece is accepted only
/// after its numbers, URLs, paths, code and inline Markdown are checked against the source piece.
/// The caller retains the original pin and owns model preparation and operation cancellation.
/// </summary>
internal sealed partial class PinTranslationService(ITextTranslator translator,
    PinTranslationDirection direction = PinTranslationDirection.EnglishToKorean)
{
    private readonly ITextTranslator _translator = translator ?? throw new ArgumentNullException(nameof(translator));

    internal PinTranslationDirection Direction { get; } = direction;

    internal static bool CanTranslate(PinContent content, PinTranslationDirection direction = PinTranslationDirection.EnglishToKorean)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Evaluate(content, direction == PinTranslationDirection.EnglishToKorean).Availability == PinTranslationAvailability.Translatable;
    }

    /// <summary>The text a plan is built from: a colored code pin plans on its display text so spans map onto its colors.</summary>
    private static string? SourceText(PinContent content) => content.CodeDocument?.DisplayText ?? content.OriginalText;

    private static bool IsGrid(PinContent content) => content.Kind == PinContentKind.Table || content.TextStyle == PinTextStyle.Grid;

    /// <summary>Decides, from the text alone, whether the translation command is on, off with a reason, or absent.</summary>
    internal static PinTranslationDecision Evaluate(PinContent content, bool targetIsKorean)
    {
        ArgumentNullException.ThrowIfNull(content);
        PinTranslationDirection fallback = targetIsKorean ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish;
        if (content.Kind == PinContentKind.Image) return new(PinTranslationAvailability.Translatable, fallback);
        string? text = SourceText(content);
        if (string.IsNullOrWhiteSpace(text)) return new(PinTranslationAvailability.NothingToTranslate, fallback);
        PinTranslationAvailability availability = PinTextLanguageDecision.Decide(text, targetIsKorean, out PinTranslationDirection direction);
        if (availability == PinTranslationAvailability.Translatable)
        {
            // The plan has the last word: a Markdown pin whose only English sits in code fences has nothing to translate.
            PinTextFormat format = PinTranslationPlanner.Classify(text, content.IsMarkdown, IsGrid(content));
            if (!PinTranslationPlanner.Create(text, format, direction).HasTranslatable)
                availability = PinTranslationAvailability.AlreadyTargetLanguage;
        }
        return new(availability, direction);
    }

    /// <summary>The same decision for text read from a picture.</summary>
    internal static PinTranslationDecision Evaluate(string text, bool targetIsKorean)
    {
        ArgumentNullException.ThrowIfNull(text);
        PinTranslationAvailability availability = PinTextLanguageDecision.Decide(text, targetIsKorean, out PinTranslationDirection direction);
        return new(availability, direction);
    }

    internal async Task<PinTranslationResult?> TranslateAsync(PinContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Kind == PinContentKind.Image || string.IsNullOrWhiteSpace(SourceText(content))) return null;
        string text = SourceText(content)!;
        PinTextFormat format = PinTranslationPlanner.Classify(text, content.IsMarkdown, IsGrid(content));
        PinTranslationPlan plan = PinTranslationPlanner.Create(text, format, Direction);
        return plan.HasTranslatable ? await TranslatePlanAsync(plan, content, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Translates the text recognized from a picture pin into a document that replaces the picture.</summary>
    internal async Task<PinTranslationResult?> TranslateRecognizedAsync(string recognized, PinContent picture, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recognized);
        ArgumentNullException.ThrowIfNull(picture);
        cancellationToken.ThrowIfCancellationRequested();
        PinTextFormat format = PinTranslationPlanner.Classify(recognized, false, false);
        PinTranslationPlan repaired = PinTranslationPlanner.Create(OcrLatinTextRepair.Repair(recognized), format, Direction);
        // Every repair replaces exactly one character. Plan on the repaired text so a label such
        // as "SeIect" is eligible, then put original bytes back into all literal and fallback spans.
        // Code, comment markers, JSON keys and fenced text consequently keep their source spelling.
        int offset = 0;
        var pieces = new List<PlanPiece>(repaired.Pieces.Count);
        foreach (PlanPiece piece in repaired.Pieces)
        {
            string raw = recognized.Substring(offset, piece.Raw.Length);
            pieces.Add(piece with { Raw = raw, Content = piece.Translate ? piece.Source : null });
            offset += piece.Raw.Length;
        }
        PinTranslationPlan plan = new(repaired.Format, pieces);
        if (!plan.HasTranslatable) return null;
        // The recognized text has no source style of its own: it becomes a plain translated document.
        PinContent template = PinContent.FromText(picture.Image, recognized, false, picture.Provenance)
            with { PendingProvenance = picture.PendingProvenance };
        return await TranslatePlanAsync(plan, template, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PinTranslationResult?> TranslatePlanAsync(PinTranslationPlan plan, PinContent content, CancellationToken cancellationToken)
    {
        // A piece is translated sentence by sentence, so one sentence that fails the value checks (a date
        // the model rewrote, a dropped number) stays in the source language while its neighbours translate.
        // Markdown lines keep their inline syntax together and are translated whole.
        PlanPiece[] pieces = [.. plan.Pieces.Where(static piece => piece.Translate)];
        var units = new List<string>();
        var unitCounts = new int[pieces.Length];
        for (int index = 0; index < pieces.Length; index++)
        {
            string[] sentences = pieces[index].Kind == PlanKind.MarkdownLine ? [pieces[index].Source]
                : [.. TranslationSegmenter.Sentences(pieces[index].Source)];
            if (sentences.Length == 0) sentences = [pieces[index].Source];
            unitCounts[index] = sentences.Length;
            units.AddRange(sentences);
        }

        IReadOnlyList<TranslatedText> translated = await _translator.TranslateAsync(units, null, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (translated.Count != units.Count || translated.All(static result => string.IsNullOrWhiteSpace(result.Text)))
            throw new InvalidOperationException("The translator returned an incomplete pin result.");

        var accepted = new string?[pieces.Length];
        bool needsReview = false;
        int unit = 0;
        for (int index = 0; index < pieces.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = new List<string>(unitCounts[index]);
            bool any = false;
            for (int offset = 0; offset < unitCounts[index]; offset++, unit++)
            {
                TranslatedText result = translated[unit];
                if (!string.Equals(result.Source, units[unit], StringComparison.Ordinal))
                    throw new InvalidOperationException("The translator returned a missing or mismatched pin result.");
                // Keep the sentence context, including values and inline syntax, and check them afterwards.
                // The model's value checker does not detect broken Markdown or a changed diagram label.
                // An empty answer is not a translation: that sentence keeps its source text.
                string text = (result.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
                bool safe = text.Length > 0 && !result.NeedsReview && PreservesProtectedValues(units[unit], text)
                    && (pieces[index].Kind != PlanKind.MarkdownLine || PreservesMarkdown(units[unit], text));
                needsReview |= !safe;
                any |= safe;
                parts.Add(safe ? text : units[unit]);
            }
            accepted[index] = any ? string.Join(" ", parts) : null;
        }

        string output = plan.Assemble(accepted);
        if (!IsStructurePreserved(plan, output))
        {
            // A result that breaks the structure (invalid JSON, a shifted table) is never shown.
            output = plan.Original;
            Array.Clear(accepted);
            needsReview = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PinContent rendered = await RenderAsync(content, plan, accepted, output, cancellationToken).ConfigureAwait(false);
        return new PinTranslationResult(rendered with { PendingProvenance = content.PendingProvenance }, needsReview);
    }

    private static bool IsStructurePreserved(PinTranslationPlan plan, string output)
    {
        switch (plan.Format)
        {
            case PinTextFormat.Json:
                try
                {
                    using JsonDocument _ = JsonDocument.Parse(output);
                    return true;
                }
                catch (JsonException) { return false; }
            case PinTextFormat.Table:
                return TabsPerLine(plan.Original).SequenceEqual(TabsPerLine(output));
            default:
                return true;
        }

        static IEnumerable<int> TabsPerLine(string text) => text.Split('\n').Select(static line => line.Count(static c => c == '\t'));
    }

    private static async Task<PinContent> RenderAsync(PinContent content, PinTranslationPlan plan, string?[] accepted,
        string output, CancellationToken cancellationToken)
    {
        PinCodeDocument? code = content.CodeDocument is { } source && plan.Original == source.DisplayText
            ? TranslatedCode(source, plan, accepted, output) : null;
        return await StaThreadTask.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content.IsMarkdown)
                return PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(output, 1, null), output, content.Provenance);
            if (code is not null)
                return PinContent.FromCode(ClipboardCodeRenderer.Render(code), output, code, content.Provenance);
            if (plan.Format == PinTextFormat.Table || IsGrid(content))
                return PinContent.FromText(ClipboardTextRenderer.Render(output, true), output, true, content.Provenance);
            return PinContent.FromText(ClipboardTextRenderer.RenderTranslation(output), output, false, content.Provenance)
                with { BorderlessText = true };
        }, "MyCapture translated pin render").ConfigureAwait(false);
    }

    /// <summary>Rebuilds a colored code document around the translated comments, keeping every other color run.</summary>
    private static PinCodeDocument? TranslatedCode(PinCodeDocument source, PinTranslationPlan plan, string?[] accepted, string output)
    {
        // Each piece maps an old span to a new span; a position inside a replaced piece clamps to its edges.
        var spans = new List<(int OldStart, int OldLength, int NewStart, int NewLength)>();
        int oldOffset = 0, newOffset = 0, index = 0;
        foreach (PlanPiece piece in plan.Pieces)
        {
            string emitted = piece.Raw;
            if (piece.Translate)
            {
                string? translation = accepted[index++];
                if (translation is not null) emitted = piece.Emit(translation);
            }
            spans.Add((oldOffset, piece.Raw.Length, newOffset, emitted.Length));
            oldOffset += piece.Raw.Length;
            newOffset += emitted.Length;
        }
        if (newOffset != output.Length) return null;

        int Map(int position)
        {
            foreach ((int oldStart, int oldLength, int newStart, int newLength) in spans)
            {
                if (position > oldStart + oldLength) continue;
                if (position <= oldStart) return newStart + (position - oldStart);
                // Inside or at the end of this piece.
                return oldLength == newLength ? newStart + (position - oldStart)
                    : position == oldStart + oldLength ? newStart + newLength
                    : newStart + (int)((long)(position - oldStart) * newLength / Math.Max(1, oldLength));
            }
            return output.Length;
        }

        var runs = new List<PinCodeRun>();
        int end = 0;
        foreach (PinCodeRun run in source.Runs)
        {
            int start = Math.Max(end, Map(run.Start));
            int stop = Math.Min(output.Length, Map(run.Start + run.Length));
            if (stop <= start) continue;
            runs.Add(run with { Start = start, Length = stop - start });
            end = stop;
        }
        var document = new PinCodeDocument(output, runs, source.Background, source.Foreground);
        return document.IsValid() ? document : null;
    }

    private static bool PreservesProtectedValues(string source, string translated)
    {
        // Keep exact source values even when the engine considers a spelling or unit change
        // harmless. Count occurrences so duplicated/dropped values cannot pass this check.
        IReadOnlyList<ProtectedSpan> protectedSpans = ProtectedTokens.Find(source);
        IEnumerable<string> values = protectedSpans.Select(span => span.Text)
            .Concat(AdditionalLiteral().Matches(source)
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
                        else
                        {
                            syntax.Add("link:" + text[close..(destinationEnd + 1)]);
                            if (!TryCollectMarkdown(text[(index + 1)..close], syntax, depth + 1)) return false;
                        }
                        index = destinationEnd + 1;
                        continue;
                    }
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

    [GeneratedRegex(@"^\s*(?:(?:>[ \t]*)*(?:(?:#{1,6}[ \t]+)|(?:(?:[-+*]|\d+[.)])[ \t]+(?:\[[ xX]\][ \t]+)?))?)(?:>[ \t]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownPrefix();

    [GeneratedRegex(@"(?:\b(?:https?|ftp)://|\bwww\.|\bmailto:)[^\s<>]+|(?<![\w])(?:[A-Za-z]:[\\/]|\\\\|(?:\.{1,2})?/)[^\s<>]+|[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}|\S{129,}", RegexOptions.CultureInvariant)]
    private static partial Regex AdditionalLiteral();
}
