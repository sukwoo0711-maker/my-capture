using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinTranslationService
{
    /// <summary>
    /// One translator call for the sentences of a pin. With <see cref="Progress"/> the pin learns how many there are
    /// before the model is ready, then each sentence the translator finishes, then all of them once it returns.
    /// Recovery retries afterwards report nothing: they belong to sentences that are already counted.
    /// </summary>
    private async Task<IReadOnlyList<TranslatedText>> TranslateUnitsAsync(IReadOnlyList<string> units,
        CancellationToken cancellationToken, ITranslatedBlockSink? blockSink = null)
    {
        IProgress<TranslationProgress>? progress = blockSink is not null ? new TranslatedUnitProgress(Progress, units.Count, blockSink)
            : Progress is { } target ? new UnitProgress(target, units.Count) : null;
        progress?.Report(new TranslationProgress(0, units.Count));
        IReadOnlyList<TranslatedText> translated = await _translator.TranslateAsync(units, progress, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new TranslationProgress(units.Count, units.Count));
        return translated;
    }

    /// <summary>
    /// Passes on only counts of this call that grow: a translator may repeat a count, report from its own threads or
    /// count past what it was sent, and none of that may move the pin's bar backwards or beyond the end.
    /// </summary>
    private sealed class UnitProgress(IProgress<TranslationProgress>? target, int total) : IProgress<TranslationProgress>
    {
        private readonly object _gate = new();
        private int _completed = -1;

        public void Report(TranslationProgress value)
        {
            if (value.Total != total) return;
            int completed = Math.Clamp(value.Completed, 0, total);
            lock (_gate)
            {
                if (completed <= _completed) return;
                _completed = completed;
                // Inside the lock, so two threads cannot hand the counts on out of order.
                target?.Report(new TranslationProgress(completed, total));
            }
        }
    }

    /// <summary>
    /// The counts as <see cref="UnitProgress"/> passes them on, and each sentence's translation as its model call ends, for
    /// a pin that shows its picture changing block by block.
    /// </summary>
    private sealed class TranslatedUnitProgress(IProgress<TranslationProgress>? target, int total, ITranslatedBlockSink translated)
        : IProgress<TranslationProgress>, ITranslatedBlockSink
    {
        private readonly UnitProgress _counts = new(target, total);

        public void Report(TranslationProgress value) => _counts.Report(value);

        public void Translated(IReadOnlyList<(int Index, TranslatedText Result)> blocks)
        {
            if (blocks.Any(block => block.Index < 0 || block.Index >= total)) return;
            translated.Translated(blocks);
        }
    }
}
