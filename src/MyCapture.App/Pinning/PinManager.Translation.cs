using Microsoft.Extensions.Logging;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    private ITextTranslator? _translator;
    private readonly Dictionary<PinWindow, CancellationTokenSource> _translationOperations = [];

    internal void ConfigureTranslation(ITextTranslator translator) =>
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));

    private void AttachTranslation(PinWindow pin)
    {
        pin.TranslationRequested += download => StartPinTranslation(pin, download);
        pin.CloseStarted += (_, _) => CancelPinTranslation(pin);
        // Offering the command is a text-only decision. No model checks, downloads or
        // inference run merely because a pin opens or a saved pin is restored.
        if (_translator is not null && PinTranslationService.CanTranslate(pin.TranslationSourceContent))
            pin.SetTranslationState(PinTranslationState.Available);
    }

    private void CancelPinTranslation(PinWindow pin)
    {
        if (_translationOperations.Remove(pin, out CancellationTokenSource? operation)) operation.Cancel();
    }

    internal void StartPinTranslation(PinWindow pin, bool download)
    {
        if (_translator is null || pin.IsClosed || !PinTranslationService.CanTranslate(pin.TranslationSourceContent)) return;
        CancelPinTranslation(pin);
        var operation = new CancellationTokenSource();
        _translationOperations[pin] = operation;
        ITextTranslator translator = _translator;
        PinContent source = pin.TranslationSourceContent;
        pin.SetTranslationState(PinTranslationState.Checking);
        // Dispatcher scheduling makes every UI continuation stay on the owning thread,
        // including test hosts without an ambient synchronization context.
        pin.TranslationWork = pin.Dispatcher.InvokeAsync(() => RunPinTranslationAsync(
            pin, source, translator, operation)).Task.Unwrap();
    }

    private async Task RunPinTranslationAsync(PinWindow pin, PinContent source, ITextTranslator translator,
        CancellationTokenSource operation)
    {
        CancellationToken token = operation.Token;
        bool Current() => !pin.IsClosed && !token.IsCancellationRequested
            && _translationOperations.TryGetValue(pin, out CancellationTokenSource? active) && ReferenceEquals(active, operation);
        try
        {
            token.ThrowIfCancellationRequested();
            bool installed = await translator.IsModelInstalledAsync(token);
            if (!Current()) return;
            if (!installed)
            {
                pin.SetTranslationState(PinTranslationState.ModelNeeded, translator.ModelDownloadBytes);
                // Only the user's explicit installation response starts a download, including
                // a retry after cancellation/failure. A pin's initial creation never reaches here.
                bool confirmed = pin.ConfirmTranslationModelInstallation(translator.ModelDownloadBytes);
                if (!Current() || !confirmed) return;
                pin.SetTranslationState(PinTranslationState.Downloading, translator.ModelDownloadBytes);
                await translator.DownloadModelAsync(progress: null, token);
                if (!Current()) return;
                if (!await translator.IsModelInstalledAsync(token))
                    throw new InvalidOperationException("Translation model verification failed.");
                if (!Current()) return;
            }
            pin.SetTranslationState(PinTranslationState.Translating);
            PinTranslationResult? result = await new PinTranslationService(translator).TranslateAsync(source, token);
            if (!Current()) return;
            if (result is not null) pin.ApplyTranslation(result.Content, result.NeedsReview);
            else pin.SetTranslationState(PinTranslationState.Unavailable);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Source text and model output never enter the diagnostic log.
            _log.LogWarning("Pin translation failed ({ErrorType})", ex.GetType().Name);
            if (Current()) pin.SetTranslationState(PinTranslationState.Failed);
        }
        finally
        {
            if (_translationOperations.TryGetValue(pin, out CancellationTokenSource? active) && ReferenceEquals(active, operation))
                _translationOperations.Remove(pin);
            operation.Dispose();
        }
    }
}
