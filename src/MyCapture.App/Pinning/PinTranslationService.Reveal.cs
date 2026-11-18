using System.Windows;
using System.Windows.Media;
using MyCapture.App.Threading;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

/// <summary>A picture block about to change: where it is in native pixels, its own background, and whether it has several lines.</summary>
internal sealed record PinRevealBlock(int Index, Rect Bounds, Color Background, int LineCount);

/// <summary>
/// What a pin needs to show its picture changing while the translation runs. Calls come from worker threads; the receiver
/// hands them to its UI thread.
/// </summary>
internal interface IPinTranslationRevealSink
{
    /// <summary>The blocks that will be translated, once the picture's text and backgrounds are known.</summary>
    void BlocksKnown(int pixelWidth, int pixelHeight, IReadOnlyList<PinRevealBlock> blocks);

    /// <summary>Blocks placed from the sentences translated so far, before the final placement.</summary>
    void BlocksReady(IReadOnlyList<PinOverlayBlockLayer> layers);
}

internal sealed partial class PinTranslationService
{
    /// <summary>A pin that shows a picture translation changing block by block; null when nothing shows it.</summary>
    internal IPinTranslationRevealSink? Reveal { get; init; }

    /// <summary>
    /// The acceptance of a sentence that needs no recovery retry, exactly as <see cref="AcceptOrRecoverAsync"/> decides it;
    /// false when a retry or a mismatch leaves it to that method.
    /// </summary>
    private bool TryAcceptWithoutRetry(string source, TranslatedText result, bool markdown, out string? accepted)
    {
        accepted = null;
        if (!string.Equals(result.Source, source, StringComparison.Ordinal)) return false;
        string initial = (result.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (ContentLossReason(source, initial, Direction).Length != 0) return false;
        if (Accepts(source, result, initial, markdown)) accepted = initial;
        return true;
    }

    /// <summary>
    /// Places the blocks of a picture translation as their sentences come back from the model, before the whole
    /// translation is in. A block is placed with the blocks known so far; one placed before an earlier block was known
    /// may come out differently in the final placement, which the pin then shows instead. Nothing here feeds the final
    /// result: the translator's calls, the accepted texts and the final composition are made as without it.
    /// </summary>
    private sealed class RevealFeed : ITranslatedBlockSink
    {
        private readonly PinTranslationService _service;
        private readonly IPinTranslationRevealSink _sink;
        private readonly IReadOnlyList<PinOverlayBlock> _blocks;
        private readonly int[] _counts;
        private readonly int[] _firstUnit;
        private readonly IReadOnlyList<string> _units;
        private readonly IReadOnlyList<string> _originalUnits;
        private readonly Task<PinOverlayPreparation> _preparation;
        private readonly CancellationToken _token;
        private readonly object _gate = new();
        private readonly TranslatedText?[] _translated;
        // The text each block shows, once decided without a retry (null: kept as source).
        private readonly (bool Known, string? Text)[] _decided;
        private readonly bool[] _delivered;
        private bool _dirty;
        private bool _stopped;
        private bool _running;
        private Task _placing = Task.CompletedTask;

        internal RevealFeed(PinTranslationService service, IPinTranslationRevealSink sink, IReadOnlyList<PinOverlayBlock> blocks,
            int[] counts, IReadOnlyList<string> units, IReadOnlyList<string> originalUnits, Task<PinOverlayPreparation> preparation,
            CancellationToken token)
        {
            _originalUnits = originalUnits;
            _service = service;
            _sink = sink;
            _blocks = blocks;
            _counts = counts;
            _units = units;
            _preparation = preparation;
            _token = token;
            _translated = new TranslatedText?[units.Count];
            _decided = new (bool, string?)[blocks.Count];
            _delivered = new bool[blocks.Count];
            _firstUnit = new int[blocks.Count];
            for (int index = 0, unit = 0; index < blocks.Count; unit += counts[index], index++) _firstUnit[index] = unit;
            for (int index = 0; index < blocks.Count; index++)
                if (counts[index] == 0) _decided[index] = (true, null);
            _ = preparation.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully && !token.IsCancellationRequested) AnnounceBlocks(task.Result);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void AnnounceBlocks(PinOverlayPreparation preparation)
        {
            var known = new List<PinRevealBlock>();
            for (int index = 0; index < _blocks.Count; index++)
            {
                if (_counts[index] == 0 || preparation.Layouts[index] is not { } layout) continue;
                PinOverlayBlock block = _blocks[index];
                Rect bounds = Rect.Empty;
                foreach (OcrLine line in block.Lines)
                {
                    OcrRect clamped = PinTranslationOverlay.Clamp(line.Bounds, preparation.Width, preparation.Height);
                    if (!OcrGeometry.IsValid(clamped)) continue;
                    bounds.Union(new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height));
                }
                if (!bounds.IsEmpty) known.Add(new(index, bounds, layout.Background, block.Lines.Count));
            }
            try
            {
                _sink.BlocksKnown(preparation.Width, preparation.Height, known);
            }
            catch (Exception)
            {
                // Showing the change never decides the translation.
            }
        }

        /// <summary>The model's call for some sentences has ended; it only stores them, on the translating thread.</summary>
        public void Translated(IReadOnlyList<(int Index, TranslatedText Result)> units)
        {
            lock (_gate)
            {
                if (_stopped) return;
                foreach ((int index, TranslatedText result) in units) _translated[index] = result;
                for (int block = 0; block < _blocks.Count; block++)
                {
                    if (_decided[block].Known) continue;
                    if (Decide(block) is { } decided)
                    {
                        _decided[block] = decided;
                        _dirty = true;
                    }
                }
                if (!_dirty || _running) return;
                _running = true;
                _placing = PlaceAsync();
            }
        }

        /// <summary>The block's accepted text as the final step will join it, or null while a sentence is missing or needs a retry.</summary>
        private (bool, string?)? Decide(int block)
        {
            int first = _firstUnit[block];
            var parts = new List<string>(_counts[block]);
            bool anyAccepted = false;
            for (int sentence = 0; sentence < _counts[block]; sentence++)
            {
                if (_translated[first + sentence] is not { } result) return null;
                if (!_service.TryAcceptWithoutRetry(_units[first + sentence], result, false, out string? text)) return null;
                anyAccepted |= text is not null;
                // The source of an unaccepted sentence, as the final step keeps it.
                parts.Add(text ?? _originalUnits[first + sentence]);
            }
            return (true, anyAccepted ? string.Join(" ", parts) : null);
        }

        private async Task PlaceAsync()
        {
            try
            {
                PinOverlayPreparation preparation = await _preparation.ConfigureAwait(false);
                while (true)
                {
                    PinOverlayTranslation[] translations;
                    bool[] wanted;
                    lock (_gate)
                    {
                        if (_stopped || !_dirty || _token.IsCancellationRequested)
                        {
                            _running = false;
                            return;
                        }
                        _dirty = false;
                        translations = new PinOverlayTranslation[_blocks.Count];
                        wanted = new bool[_blocks.Count];
                        for (int index = 0; index < _blocks.Count; index++)
                        {
                            translations[index] = new(_blocks[index], _decided[index].Known ? _decided[index].Text : null);
                            wanted[index] = _decided[index].Known && _decided[index].Text is not null && !_delivered[index];
                        }
                    }
                    var layers = new List<PinOverlayBlockLayer>();
                    await StaThreadTask.RunAsync(() =>
                    {
                        _token.ThrowIfCancellationRequested();
                        PinTranslationOverlay.Compose(preparation, translations, observeBlockLayer: layer =>
                        {
                            if (wanted[layer.Index]) layers.Add(layer);
                        });
                        return true;
                    }, "MyCapture translated picture preview").ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_stopped || _token.IsCancellationRequested)
                        {
                            _running = false;
                            return;
                        }
                        foreach (PinOverlayBlockLayer layer in layers) _delivered[layer.Index] = true;
                    }
                    if (layers.Count > 0) _sink.BlocksReady(layers);
                }
            }
            catch (Exception)
            {
                // A preview that fails or is cancelled leaves the blocks to the final placement.
                lock (_gate) _running = false;
            }
        }

        /// <summary>The model has returned: no new preview starts, and the one running ends before the final placement.</summary>
        internal Task StopAsync()
        {
            Task placing;
            lock (_gate)
            {
                _stopped = true;
                placing = _placing;
            }
            return placing;
        }

        /// <summary>The translation has ended, however it ended.</summary>
        internal void Abandon()
        {
            lock (_gate) _stopped = true;
        }
    }
}
