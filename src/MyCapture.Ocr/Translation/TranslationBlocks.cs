namespace MyCapture.Ocr.Translation;

/// <summary>A numbered block of recognized text and where it is in the original image.</summary>
internal sealed record TranslationBlock(int Number, string Text, OcrRect Region)
{
    internal bool IsCodePayload { get; init; }
}

/// <summary>
/// The blocks translated side by side with the original: the paragraphs from
/// <see cref="OcrTextReconstructor"/> and, when the capture has a table, its cells in place of
/// the paragraphs inside it. Blocks without English words (identifiers, numbers, access codes
/// such as "rw", Korean or other scripts) keep their original text and get no number.
/// </summary>
internal static class TranslationBlocks
{
    internal static IReadOnlyList<TranslationBlock> Build(OcrExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        OcrTable table = extraction.Table;
        bool hasTable = extraction.HasGeometry && table.HasStructure && OcrGeometry.IsValid(table.Bounds);
        var candidates = new List<(string Text, OcrRect Region, bool Payload)>();
        bool tableAdded = false;
        foreach (OcrParagraph paragraph in extraction.Paragraphs)
        {
            if (paragraph.SourceLines.Any(line => line.IsCodePayload))
            {
                candidates.Add((string.Join("\n", paragraph.Lines), paragraph.Bounds, true));
                continue;
            }
            OcrRect bounds = paragraph.Bounds;
            if (hasTable && OcrGeometry.IsValid(bounds) && OcrGeometry.Contains(table.Bounds,
                    bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))
            {
                if (!tableAdded)
                {
                    candidates.AddRange(table.Rows.SelectMany(row => row.Cells).Where(cell => !cell.IsCodePayload)
                        .Select(cell => (cell.Text, OcrGeometry.IsValid(cell.Bounds) ? cell.Bounds : cell.Region, false)));
                    tableAdded = true;
                }
                continue;
            }
            candidates.Add((string.Join(" ", paragraph.Lines.Select(line => line.Trim()).Where(line => line.Length > 0)), bounds, false));
        }

        var blocks = new List<TranslationBlock>();
        foreach ((string text, OcrRect region, bool payload) in candidates)
        {
            if (payload || IsEnglishToTranslate(text))
                blocks.Add(new TranslationBlock(blocks.Count + 1, text, region) { IsCodePayload = payload });
        }
        return blocks;
    }

    internal static async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<TranslationBlock> blocks,
        ITextTranslator translator, IProgress<TranslationProgress>? progress, CancellationToken token)
    {
        string[] sources = blocks.Where(block => !block.IsCodePayload).Select(block => block.Text).ToArray();
        IReadOnlyList<TranslatedText> translated = sources.Length == 0 ? []
            : await translator.TranslateAsync(sources, progress, token).ConfigureAwait(false);
        if (translated.Count != sources.Length) throw new InvalidOperationException("Incomplete translation result.");
        int index = 0;
        return blocks.Select(block => block.IsCodePayload
            ? new TranslatedText(block.Text, block.Text, [], []) : translated[index++]).ToArray();
    }

    /// <summary>English words to translate, and not mostly Korean or another script.</summary>
    internal static bool IsEnglishToTranslate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int latin = 0, other = 0;
        foreach (char c in text)
        {
            if (char.IsAsciiLetter(c)) latin++;
            else if (char.IsLetter(c)) other++;
        }
        return latin > other && ProtectedTokens.HasTranslatableWords(text);
    }
}
