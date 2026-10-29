using MyCapture.App.Threading;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinTranslationService
{
    /// <summary>Translates all image blocks in one batch; unsafe blocks retain their exact source pixels.</summary>
    internal async Task<PinTranslationResult?> TranslateRecognizedAsync(OcrResult recognized, PinContent picture,
        CancellationToken cancellationToken, Action<PinOverlayBlock, string>? observeDecision = null)
    {
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(picture);
        cancellationToken.ThrowIfCancellationRequested();
        if (!recognized.HasText) return null;
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.CanOverlay(recognized)
            ? PinTranslationOverlay.BuildBlocks(recognized, picture.Image).Where(block => block.Lines.Any(line =>
                OcrGeometry.IsValid(PinTranslationOverlay.Clamp(line.Bounds, picture.Image.PixelWidth, picture.Image.PixelHeight)))).ToArray()
            : [];
        if (blocks.Count == 0)
        {
            string text = OcrExtractionAnalyzer.Analyze(recognized).Text(joinLines: false);
            return await TranslateRecognizedAsync(text, picture, cancellationToken).ConfigureAwait(false);
        }

        var units = new List<string>();
        var counts = new int[blocks.Count];
        var ineligible = new HashSet<PinOverlayBlock>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].PreservePixels) continue;
            string source = OcrLatinTextRepair.Repair(blocks[i].Text);
            if (Evaluate(source, Direction == PinTranslationDirection.EnglishToKorean).Availability
                != PinTranslationAvailability.Translatable)
            { ineligible.Add(blocks[i]); continue; }
            string[] sentences = [.. TranslationSegmenter.Sentences(source)];
            if (sentences.Length == 0) sentences = [source];
            counts[i] = sentences.Length;
            units.AddRange(sentences);
        }
        if (units.Count == 0)
        {
            foreach (PinOverlayBlock block in blocks)
                observeDecision?.Invoke(block, block.PreservePixels ? "protected" : "not-eligible");
            return null;
        }
        IReadOnlyList<TranslatedText> translated = await _translator.TranslateAsync(units, null, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (translated.Count != units.Count)
            throw new InvalidOperationException("The translator returned an incomplete pin result.");

        int cursor = 0;
        bool needsReview = false;
        var overlays = new List<PinOverlayTranslation>(blocks.Count);
        var paragraphs = new List<string>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            // As in a text pin, a sentence that fails the value checks (for example a dropped
            // identifier) stays in the source language while its neighbours translate. Only a block
            // with no accepted sentence keeps its original pixels.
            var parts = new List<string>(counts[i]);
            bool anyAccepted = false;
            for (int sentence = 0; sentence < counts[i]; sentence++, cursor++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TranslatedText result = translated[cursor];
                if (!string.Equals(result.Source, units[cursor], StringComparison.Ordinal))
                    throw new InvalidOperationException("The translator returned a missing or mismatched pin result.");
                string text = (result.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
                bool safe = text.Length > 0 && !result.NeedsReview && PreservesProtectedValues(units[cursor], text);
                needsReview |= !safe;
                anyAccepted |= safe;
                parts.Add(safe ? text : units[cursor].Trim());
            }
            string? accepted = anyAccepted ? string.Join(" ", parts) : null;
            overlays.Add(new(blocks[i], accepted));
            paragraphs.Add(accepted ?? blocks[i].Text);
        }

        string output = string.Join("\n\n", paragraphs);
        return await StaThreadTask.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            PinOverlayComposition composed = PinTranslationOverlay.Compose(picture.Image, overlays,
                observeDecision: (block, decision) => observeDecision?.Invoke(block,
                    decision == "all-rejected" && ineligible.Contains(block) ? "not-eligible" : decision));
            PinContent document = PinContent.FromText(ClipboardTextRenderer.RenderTranslation(output), output, false, picture.Provenance)
                with { BorderlessText = true, PendingProvenance = picture.PendingProvenance };
            PinContent overlay = PinContent.FromTranslationOverlay(composed.Image, output, picture);
            return new PinTranslationResult(overlay, needsReview || composed.NeedsReview, document);
        }, "MyCapture translated picture render").ConfigureAwait(false);
    }
}
