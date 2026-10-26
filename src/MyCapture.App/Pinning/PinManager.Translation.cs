using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Localization;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    private Func<PinTranslationDirection, ITextTranslator?>? _translators;
    private Func<BitmapSource, CancellationToken, Task<string?>>? _recognizeText;
    private readonly Dictionary<PinWindow, CancellationTokenSource> _translationOperations = [];

    /// <summary>One translator for both directions; tests and single-model hosts.</summary>
    internal void ConfigureTranslation(ITextTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ConfigureTranslation(_ => translator);
    }

    /// <summary>
    /// The translator of each direction, or null while that direction's model is not offered. The target
    /// language is the application display language; the direction follows from it.
    /// </summary>
    internal void ConfigureTranslation(Func<PinTranslationDirection, ITextTranslator?> translators) =>
        _translators = translators ?? throw new ArgumentNullException(nameof(translators));

    /// <summary>Recognizes the text of a picture pin on this PC; the pin then translates the recognized lines.</summary>
    internal void ConfigureImageTranslation(Func<BitmapSource, CancellationToken, Task<string?>> recognizeText) =>
        _recognizeText = recognizeText ?? throw new ArgumentNullException(nameof(recognizeText));

    private static bool TargetIsKorean => UiText.Culture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase);

    private static PinTranslationDirection DirectionFor(bool targetIsKorean) =>
        targetIsKorean ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish;

    private void AttachTranslation(PinWindow pin)
    {
        pin.TranslationRequested += download => StartPinTranslation(pin, download);
        pin.CloseStarted += (_, _) => CancelPinTranslation(pin);
        if (_translators is null) return;
        // Offering the command is a decision about the text alone. No model check, download or
        // inference runs because a pin opens or a saved pin is restored; the language of the
        // pin decides only whether the command is on, off with a reason, or absent.
        bool korean = TargetIsKorean;
        pin.SetTranslationDirection(korean);
        PinContent source = pin.TranslationSourceContent;
        if (source.Kind == PinContentKind.Image)
        {
            // The language of a picture is known only after its text is read, when the command runs.
            if (_recognizeText is not null) pin.SetTranslationState(PinTranslationState.Available);
            return;
        }
        PinTranslationDecision decision = PinTranslationService.Evaluate(source, korean);
        switch (decision.Availability)
        {
            case PinTranslationAvailability.Translatable:
                if (_translators(decision.Direction) is null)
                    pin.SetTranslationDisabled(UiText.Get("Pin_TranslationModelPreparing"));
                else pin.SetTranslationState(PinTranslationState.Available);
                break;
            case PinTranslationAvailability.AlreadyTargetLanguage:
                pin.SetTranslationDisabled(UiText.Get(korean ? "Pin_AlreadyKorean" : "Pin_AlreadyEnglish"));
                break;
            case PinTranslationAvailability.UnsupportedLanguage:
                pin.SetTranslationDisabled(UiText.Get("Pin_TranslationUnsupportedLanguage"));
                break;
        }
    }

    private void CancelPinTranslation(PinWindow pin)
    {
        if (_translationOperations.Remove(pin, out CancellationTokenSource? operation)) operation.Cancel();
    }

    internal void StartPinTranslation(PinWindow pin, bool download)
    {
        if (_translators is null || pin.IsClosed) return;
        PinContent source = pin.TranslationSourceContent;
        bool korean = TargetIsKorean;
        PinTranslationDirection direction = DirectionFor(korean);
        bool picture = source.Kind == PinContentKind.Image;
        if (picture ? _recognizeText is null
            : PinTranslationService.Evaluate(source, korean).Availability != PinTranslationAvailability.Translatable) return;
        ITextTranslator? translator = _translators(direction);
        if (translator is null)
        {
            pin.SetTranslationDisabled(UiText.Get("Pin_TranslationModelPreparing"));
            return;
        }
        CancelPinTranslation(pin);
        var operation = new CancellationTokenSource();
        _translationOperations[pin] = operation;
        pin.SetTranslationDirection(korean);
        pin.SetTranslationState(PinTranslationState.Checking);
        // Dispatcher scheduling makes every UI continuation stay on the owning thread,
        // including test hosts without an ambient synchronization context.
        pin.TranslationWork = pin.Dispatcher.InvokeAsync(() => RunPinTranslationAsync(
            pin, source, translator, direction, korean, operation)).Task.Unwrap();
    }

    private async Task RunPinTranslationAsync(PinWindow pin, PinContent source, ITextTranslator translator,
        PinTranslationDirection direction, bool korean, CancellationTokenSource operation)
    {
        CancellationToken token = operation.Token;
        bool Current() => !pin.IsClosed && !token.IsCancellationRequested
            && _translationOperations.TryGetValue(pin, out CancellationTokenSource? active) && ReferenceEquals(active, operation);
        try
        {
            token.ThrowIfCancellationRequested();
            string? recognized = null;
            if (source.Kind == PinContentKind.Image)
            {
                // Read the picture first: a picture that is already in the target language
                // must not ask for a model it will never use.
                pin.SetTranslationState(PinTranslationState.Translating);
                recognized = await _recognizeText!(source.Image, token);
                if (!Current()) return;
                if (string.IsNullOrWhiteSpace(recognized))
                {
                    pin.SetTranslationState(PinTranslationState.Available);
                    pin.ReportTranslationNote(UiText.Get("Pin_TranslationNoText"));
                    return;
                }
                PinTranslationDecision decision = PinTranslationService.Evaluate(recognized, korean);
                if (decision.Availability != PinTranslationAvailability.Translatable)
                {
                    pin.SetTranslationDisabled(decision.Availability == PinTranslationAvailability.UnsupportedLanguage
                        ? UiText.Get("Pin_TranslationUnsupportedLanguage")
                        : UiText.Get(korean ? "Pin_AlreadyKorean" : "Pin_AlreadyEnglish"));
                    pin.ReportTranslationNote(pin.TranslationDisabledHelp ?? string.Empty);
                    return;
                }
                pin.SetTranslationState(PinTranslationState.Checking);
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
            var service = new PinTranslationService(translator, direction);
            PinTranslationResult? result = recognized is null
                ? await service.TranslateAsync(source, token)
                : await service.TranslateRecognizedAsync(recognized, source, token);
            if (!Current()) return;
            if (result is not null) pin.ApplyTranslation(result.Content, result.NeedsReview, korean);
            else pin.SetTranslationDisabled(UiText.Get(korean ? "Pin_AlreadyKorean" : "Pin_AlreadyEnglish"));
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
