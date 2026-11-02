using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Threading;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinTranslationService
{
    /// <summary>
    /// Translates all image blocks in one batch; unsafe blocks retain their exact source pixels. The block scan
    /// uses <paramref name="sourcePixels"/>, the Bgra32 copy of the frozen picture, when the caller is already making it.
    /// </summary>
    internal async Task<PinTranslationResult?> TranslateRecognizedAsync(OcrResult recognized, PinContent picture,
        CancellationToken cancellationToken, Action<PinOverlayBlock, string>? observeDecision = null,
        Task<byte[]>? sourcePixels = null)
    {
        ArgumentNullException.ThrowIfNull(recognized);
        ArgumentNullException.ThrowIfNull(picture);
        cancellationToken.ThrowIfCancellationRequested();
        if (!recognized.HasText) return null;
        BitmapSource image = await FreezeOverlaySourceAsync(picture.Image, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<PinOverlayBlock> blocks, byte[]? pixels) = PinTranslationOverlay.CanOverlay(recognized)
            ? await BuildOverlayBlocksAsync(recognized, image, cancellationToken, sourcePixels: sourcePixels).ConfigureAwait(false)
            : ([], null);
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
        // Surfaces, colours and sizes do not depend on the translated text. Sample them while the
        // translator runs, from the pixels the block scan already converted; placement and the render
        // wait for the accepted sentences.
        using var preparing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken preparingToken = preparing.Token;
        Task<PinOverlayPreparation> preparation = StaThreadTask.RunAsync(() => PinTranslationOverlay.Prepare(image, blocks,
            index => counts[index] > 0, preparingToken, pixels), "MyCapture translated picture layout");
        bool prepared = false;
        try
        {
            IReadOnlyList<TranslatedText> translated = await TranslateUnitsAsync(units, cancellationToken).ConfigureAwait(false);
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
            using PinOverlayPreparation layout = await preparation.ConfigureAwait(false);
            prepared = true;
            // Placement measures text away from the UI thread. It records a frozen layer, not a bitmap: the pin
            // draws it over the source picture, and the document view renders when it is first opened.
            PinOverlayComposition composed = await StaThreadTask.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return PinTranslationOverlay.Compose(layout, overlays,
                    observeDecision: (block, decision) => observeDecision?.Invoke(block,
                        decision == "all-rejected" && ineligible.Contains(block) ? "not-eligible" : decision));
            }, "MyCapture translated picture render").ConfigureAwait(false);
            var document = new Lazy<PinContent>(() => PinContent.FromText(ClipboardTextRenderer.RenderTranslation(output), output,
                false, picture.Provenance) with { BorderlessText = true, PendingProvenance = picture.PendingProvenance });
            return new PinTranslationResult(PinContent.FromTranslationOverlay(composed, output, picture),
                needsReview || composed.NeedsReview, document);
        }
        finally
        {
            if (!prepared)
            {
                preparing.Cancel();
                ReleaseWhenPrepared(preparation);
            }
        }
    }

    /// <summary>A preparation abandoned by a failed or cancelled translation releases its pixels when it ends.</summary>
    private static void ReleaseWhenPrepared(Task<PinOverlayPreparation> preparation) =>
        _ = preparation.ContinueWith(static task =>
        {
            if (task.IsCompletedSuccessfully) task.Result.Dispose();
            else _ = task.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>The blocks, and the Bgra32 pixels the scan converted (or was given) for the layout step to take over.</summary>
    internal static Task<(IReadOnlyList<PinOverlayBlock> Blocks, byte[]? Pixels)> BuildOverlayBlocksAsync(OcrResult recognized,
        BitmapSource image, CancellationToken cancellationToken, Action? observeBuild = null, Task<byte[]>? sourcePixels = null)
    {
        if (!image.IsFrozen) throw new ArgumentException("Block building requires a frozen image.", nameof(image));
        if (sourcePixels is not null)
            return BuildOverlayBlocksAsync(recognized, image.PixelWidth, image.PixelHeight, sourcePixels, cancellationToken, observeBuild);
        return StaThreadTask.RunAsync<(IReadOnlyList<PinOverlayBlock> Blocks, byte[]? Pixels)>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            observeBuild?.Invoke();
            PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, image, out byte[]? pixels)
                .Where(block => block.Lines.Any(line => OcrGeometry.IsValid(
                    PinTranslationOverlay.Clamp(line.Bounds, image.PixelWidth, image.PixelHeight)))).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return (blocks, pixels);
        }, "MyCapture picture translation blocks");
    }

    /// <summary>The scan on pixels converted beforehand; it needs no STA, so it runs where the pixels arrive.</summary>
    private static async Task<(IReadOnlyList<PinOverlayBlock> Blocks, byte[]? Pixels)> BuildOverlayBlocksAsync(OcrResult recognized,
        int width, int height, Task<byte[]> sourcePixels, CancellationToken cancellationToken, Action? observeBuild)
    {
        byte[] pixels = await sourcePixels.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        observeBuild?.Invoke();
        PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, pixels, width, height)
            .Where(block => block.Lines.Any(line => OcrGeometry.IsValid(PinTranslationOverlay.Clamp(line.Bounds, width, height)))).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return (blocks, pixels);
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
