using Microsoft.Extensions.Logging;
using MyCapture.Ocr.Translation;
using MyCapture.Ocr;
using MyCapture.App.Threading;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    private Func<string, ITextTranslator?>? _translatorForTarget;
    private Func<string> _translationTarget = static () => UiText.Culture.TwoLetterISOLanguageName;
    private Func<PinContent, string, CancellationToken, Task<OcrResult>>? _recognizeForTranslation;
    private readonly Dictionary<PinWindow, CancellationTokenSource> _translationOperations = [];

    internal void ConfigureTranslation(ITextTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ConfigureTranslation(_ => translator);
    }

    internal void ConfigureTranslation(Func<string, ITextTranslator?> translators,
        Func<string>? targetLanguage = null,
        Func<PinContent, string, CancellationToken, Task<OcrResult>>? recognize = null)
    {
        _translatorForTarget = translators ?? throw new ArgumentNullException(nameof(translators));
        _translationTarget = targetLanguage ?? (static () => UiText.Culture.TwoLetterISOLanguageName);
        _recognizeForTranslation = recognize;
        RefreshTranslationAvailability();
    }

    internal void RefreshTranslationAvailability()
    {
        foreach (PinWindow pin in _pins)
        {
            CancelPinTranslation(pin);
            OfferTranslation(pin);
        }
    }

    private void AttachTranslation(PinWindow pin)
    {
        pin.TranslationRequested += download => StartPinTranslation(pin, download);
        pin.CloseStarted += (_, _) => CancelPinTranslation(pin);
        // Offering the command is a text-only decision. No model checks, downloads or
        // inference run merely because a pin opens or a saved pin is restored.
        OfferTranslation(pin);
    }

    private void OfferTranslation(PinWindow pin)
    {
        if (_translatorForTarget is null || pin.IsClosed) return;
        string target = _translationTarget();
        pin.SetTranslationTarget(target);
        if (pin.TranslationState == PinTranslationState.Translated) return;
        PinContent source = pin.TranslationSourceContent;
        if (source.OriginalText is null)
        {
            if (_recognizeForTranslation is not null) pin.SetTranslationState(PinTranslationState.Available);
            return;
        }
        if (PinTranslationService.CanTranslate(source, target)) pin.SetTranslationState(PinTranslationState.Available);
        else SetLanguageDisabled(pin, source.OriginalText, target);
    }

    private static void SetLanguageDisabled(PinWindow pin, string source, string target)
    {
        PinLanguageAssessment language = PinLanguagePolicy.Assess(source, target);
        pin.SetTranslationDisabled(UiText.Get(language.Reason switch
        {
            PinLanguageReason.AlreadyTarget => target.StartsWith("ko", StringComparison.OrdinalIgnoreCase)
                ? "Pin_TranslationAlreadyKorean" : "Pin_TranslationAlreadyEnglish",
            PinLanguageReason.Unsupported => "Pin_TranslationUnsupportedLanguage",
            _ => "Pin_TranslationNoText",
        }));
    }

    private void CancelPinTranslation(PinWindow pin)
    {
        if (_translationOperations.Remove(pin, out CancellationTokenSource? operation)) operation.Cancel();
    }

    internal void StartPinTranslation(PinWindow pin, bool download)
    {
        if (_translatorForTarget is null || pin.IsClosed) return;
        string target = _translationTarget();
        PinContent source = pin.TranslationSourceContent;
        if (source.OriginalText is not null && !PinTranslationService.CanTranslate(source, target))
        {
            SetLanguageDisabled(pin, source.OriginalText, target);
            return;
        }
        if (source.OriginalText is null && _recognizeForTranslation is null) return;
        CancelPinTranslation(pin);
        var operation = new CancellationTokenSource();
        _translationOperations[pin] = operation;
        pin.SetTranslationTarget(target);
        pin.SetTranslationState(PinTranslationState.Checking);
        // Dispatcher scheduling makes every UI continuation stay on the owning thread,
        // including test hosts without an ambient synchronization context.
        pin.TranslationWork = pin.Dispatcher.InvokeAsync(() => RunPinTranslationAsync(
            pin, source, target, operation)).Task.Unwrap();
    }

    private async Task RunPinTranslationAsync(PinWindow pin, PinContent source, string target,
        CancellationTokenSource operation)
    {
        CancellationToken token = operation.Token;
        bool Current() => !pin.IsClosed && !token.IsCancellationRequested
            && _translationOperations.TryGetValue(pin, out CancellationTokenSource? active) && ReferenceEquals(active, operation);
        try
        {
            token.ThrowIfCancellationRequested();
            bool sourceWasImage = source.OriginalText is null;
            bool ocrNeedsLanguageReview = false;
            if (source.OriginalText is null)
            {
                pin.SetTranslationState(PinTranslationState.Recognizing);
                OcrResult recognized = await _recognizeForTranslation!(source, target, token);
                if (!Current()) return;
                if (!recognized.HasText)
                {
                    pin.SetTranslationDisabled(UiText.Get(recognized.Status == OcrStatus.NoText
                        ? "Pin_TranslationNoText" : "Pin_TranslationOcrUnavailable"));
                    return;
                }
                ocrNeedsLanguageReview = PinTranslationOcr.NeedsLanguageReview(recognized, target);
                pin.RetainRecognizedOriginal(recognized.Text);
                var recognizedImage = await StaThreadTask.RunAsync(
                    () => ClipboardTextRenderer.Render(recognized.Text, false), "MyCapture recognized pin text");
                if (!Current()) return;
                source = PinContent.FromText(recognizedImage, recognized.Text, false, source.Provenance);
                if (!PinTranslationService.CanTranslate(source, target))
                {
                    SetLanguageDisabled(pin, recognized.Text, target);
                    return;
                }
            }
            ITextTranslator? translator = _translatorForTarget!(target);
            if (translator is null)
            {
                pin.SetTranslationDisabled(UiText.Get("Pin_TranslationUnsupportedLanguage"));
                return;
            }
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
            PinTranslationResult? result = await new PinTranslationService(translator, target,
                preserveLineBreaks: sourceWasImage).TranslateAsync(source, token);
            if (!Current()) return;
            if (result is not null) pin.ApplyTranslation(result.Content, result.NeedsReview || ocrNeedsLanguageReview,
                ocrNeedsLanguageReview ? UiText.Get("Pin_TranslationOcrLanguageReview") : null);
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
