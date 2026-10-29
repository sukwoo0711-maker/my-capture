using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        BitmapSource image = await FreezeOverlaySourceAsync(picture.Image, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.CanOverlay(recognized)
            ? await BuildOverlayBlocksAsync(recognized, image, cancellationToken).ConfigureAwait(false)
            : [];
        if (blocks.Count == 0)
        {
            string text = OcrExtractionAnalyzer.Analyze(recognized).Text(joinLines: false);
            PinContent snapshot = PinContent.FromImage(image, picture.SourcePageUrl, picture.Provenance)
                with { PendingProvenance = picture.PendingProvenance };
            return await TranslateRecognizedAsync(text, snapshot, cancellationToken).ConfigureAwait(false);
        }

        var units = new List<string>();
        var originalUnits = new List<string>();
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
            int sourceOffset = 0;
            foreach (string sentence in sentences)
            {
                int start = source.IndexOf(sentence, sourceOffset, StringComparison.Ordinal);
                originalUnits.Add(blocks[i].Text.Substring(start, sentence.Length));
                sourceOffset = start + sentence.Length;
            }
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
                string? text = await AcceptOrRecoverAsync(units[cursor], translated[cursor], false,
                    blocks[i].Lines.Select(line => OcrLatinTextRepair.Repair(line.Text)), cancellationToken).ConfigureAwait(false);
                bool safe = text is not null;
                needsReview |= !safe;
                anyAccepted |= safe;
                parts.Add(text ?? originalUnits[cursor]);
            }
            string? accepted = anyAccepted ? string.Join(" ", parts) : null;
            overlays.Add(new(blocks[i], accepted));
            paragraphs.Add(accepted ?? blocks[i].Text);
        }

        string output = string.Join("\n\n", paragraphs);
        return await StaThreadTask.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            PinOverlayComposition composed = PinTranslationOverlay.Compose(image, overlays,
                observeDecision: (block, decision) => observeDecision?.Invoke(block,
                    decision == "all-rejected" && ineligible.Contains(block) ? "not-eligible" : decision));
            PinContent document = PinContent.FromText(ClipboardTextRenderer.RenderTranslation(output), output, false, picture.Provenance)
                with { BorderlessText = true, PendingProvenance = picture.PendingProvenance };
            PinContent overlay = PinContent.FromTranslationOverlay(composed.Image, output, picture);
            return new PinTranslationResult(overlay, needsReview || composed.NeedsReview, document);
        }, "MyCapture translated picture render").ConfigureAwait(false);
    }

    internal static Task<IReadOnlyList<PinOverlayBlock>> BuildOverlayBlocksAsync(OcrResult recognized,
        BitmapSource image, CancellationToken cancellationToken, Action? observeBuild = null)
    {
        if (!image.IsFrozen) throw new ArgumentException("Block building requires a frozen image.", nameof(image));
        return StaThreadTask.RunAsync<IReadOnlyList<PinOverlayBlock>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            observeBuild?.Invoke();
            PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, image)
                .Where(block => block.Lines.Any(line => OcrGeometry.IsValid(
                    PinTranslationOverlay.Clamp(line.Bounds, image.PixelWidth, image.PixelHeight)))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return blocks;
        }, "MyCapture picture translation blocks");
    }

    internal static async Task<BitmapSource> FreezeOverlaySourceAsync(BitmapSource source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // IsFrozen itself verifies dispatcher access on mutable Freezables. Frozen
        // objects are detached, so CheckAccess is safe and true on every thread.
        if (source.CheckAccess() && source.IsFrozen) return source;
        // Captures and decoded session/clipboard images normally arrive frozen. PinContent
        // also accepts callers' mutable bitmaps; take an owned snapshot on their dispatcher
        // before handing either the scan or the later composition to a background thread.
        BitmapSource Snapshot()
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapSource snapshot = source.CloneCurrentValue();
            snapshot.Freeze();
            return snapshot;
        }
        return source.CheckAccess() ? Snapshot()
            : await source.Dispatcher.InvokeAsync(Snapshot, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }
}
