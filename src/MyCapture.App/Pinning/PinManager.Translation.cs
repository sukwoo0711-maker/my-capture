using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    private Func<PinTranslationDirection, ITextTranslator?>? _translators;
    private Func<BitmapSource, CancellationToken, Task<string?>>? _recognizeText;
    private Func<BitmapSource, Action<OcrResult>?, CancellationToken, Task<OcrResult>>? _recognizeImage;
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

    internal void ConfigureImageTranslation(Func<BitmapSource, CancellationToken, Task<OcrResult>> recognizeImage)
    {
        ArgumentNullException.ThrowIfNull(recognizeImage);
        _recognizeImage = (image, _, token) => recognizeImage(image, token);
    }

    /// <summary>A recognizer that hands over its first reading when a slower second reading of some rows follows.</summary>
    internal void ConfigureImageTranslation(Func<BitmapSource, Action<OcrResult>?, CancellationToken, Task<OcrResult>> recognizeImage) =>
        _recognizeImage = recognizeImage ?? throw new ArgumentNullException(nameof(recognizeImage));

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
            if (_recognizeText is not null || _recognizeImage is not null) pin.SetTranslationState(PinTranslationState.Available);
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
        if (picture ? _recognizeText is null && _recognizeImage is null
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
        // The sentences done reach the pin from the translator's threads until this operation ends or is replaced.
        var progress = new PinTranslationProgress(pin, operation.Token);
        // A frozen picture starts its reading before the pin redraws its command, which takes the UI thread a moment.
        Task<PictureReading>? reading = picture && source.Image.IsFrozen
            ? StartPictureReading(source, source.Image, translator, direction, korean, progress, operation.Token) : null;
        pin.SetTranslationDirection(korean);
        // A picture is read first, which already takes a moment: it shows "translating" from the command on.
        pin.SetTranslationState(picture ? PinTranslationState.Translating : PinTranslationState.Checking);
        // Dispatcher scheduling makes every UI continuation stay on the owning thread,
        // including test hosts without an ambient synchronization context.
        pin.TranslationWork = pin.Dispatcher.InvokeAsync(() => RunPinTranslationAsync(
            pin, source, translator, direction, korean, operation, progress, reading)).Task.Unwrap();
    }

    /// <summary>
    /// What the thread pool learned from a picture: its recognition (when the overlay can use it), its text,
    /// whether that text translates toward the target language and, only for a picture to translate, whether the
    /// model is installed. With an installed model the overlay translation has run there too.
    /// </summary>
    private sealed record PictureReading(OcrResult? Recognition, string? Text, PinTranslationDecision? Decision,
        bool? Installed, bool Translated, PinTranslationResult? Result);

    /// <summary>
    /// Reads a frozen picture on the thread pool and, when nothing needs the user, translates it there as well,
    /// so the UI thread shows only the states and the result. An installed model loads once the text needs it.
    /// </summary>
    private Task<PictureReading> StartPictureReading(PinContent source, BitmapSource picture, ITextTranslator translator,
        PinTranslationDirection direction, bool korean, IProgress<TranslationProgress> progress, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            // The overlay samples the picture as Bgra32 rows; the copy is made while the text is read.
            Task<byte[]>? pixels = _recognizeImage is not null && ReferenceEquals(picture, source.Image)
                ? Observed(StaThreadTask.RunAsync(() => PinTranslationOverlay.Bgra32Pixels(picture), "MyCapture picture pixels"))
                : null;
            OcrResult? recognition = null;
            string? text;
            bool warming = false;
            if (_recognizeImage is not null)
            {
                // A first reading that already translates starts the model's load while its suspect rows are read
                // again. That second reading only turns Hangul misreadings into Latin words, which changes the
                // language it settles on only for a mostly English picture misread as Korean.
                recognition = await _recognizeImage(picture, first =>
                {
                    if (!Translatable(first, korean)) return;
                    warming = true;
                    _ = translator.WarmUpAsync(token);
                }, token).ConfigureAwait(false);
                text = recognition.HasText ? OcrExtractionAnalyzer.Analyze(recognition).Text(joinLines: false) : null;
            }
            else text = await _recognizeText!(picture, token).ConfigureAwait(false);
            PinTranslationDecision? decision = string.IsNullOrWhiteSpace(text) ? null : PinTranslationService.Evaluate(text, korean);
            if (decision?.Availability != PinTranslationAvailability.Translatable)
                return new PictureReading(recognition, text, decision, null, false, null);
            // Only a picture to translate checks or loads the model. The load overlaps the
            // installation check and the overlay's block scan and layout.
            if (!warming) _ = translator.WarmUpAsync(token);
            bool installed = await translator.IsModelInstalledAsync(token).ConfigureAwait(false);
            if (!installed || recognition is null) return new PictureReading(recognition, text, decision, installed, false, null);
            PinTranslationResult? result = await new PinTranslationService(translator, direction) { Progress = progress }
                .TranslateRecognizedAsync(recognition, source, token, sourcePixels: pixels).ConfigureAwait(false);
            return new PictureReading(recognition, text, decision, installed, true, result);
        }, token);
    }

    private static bool Translatable(OcrResult reading, bool korean) => reading.HasText
        && OcrExtractionAnalyzer.Analyze(reading).Text(joinLines: false) is { } text && !string.IsNullOrWhiteSpace(text)
        && PinTranslationService.Evaluate(text, korean).Availability == PinTranslationAvailability.Translatable;

    /// <summary>A copy nobody may end up waiting for still has its failure observed.</summary>
    private static Task<T> Observed<T>(Task<T> task)
    {
        _ = task.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task RunPinTranslationAsync(PinWindow pin, PinContent source, ITextTranslator translator,
        PinTranslationDirection direction, bool korean, CancellationTokenSource operation,
        IProgress<TranslationProgress> progress, Task<PictureReading>? reading)
    {
        CancellationToken token = operation.Token;
        bool Current() => !pin.IsClosed && !token.IsCancellationRequested
            && _translationOperations.TryGetValue(pin, out CancellationTokenSource? active) && ReferenceEquals(active, operation);
        try
        {
            token.ThrowIfCancellationRequested();
            string? recognized = null;
            OcrResult? recognition = null;
            PictureReading? read = null;
            if (source.Kind == PinContentKind.Image)
            {
                // Read the picture first: a picture that is already in the target language
                // must not ask for a model it will never use. A picture to translate checks the
                // installation right after the reading, on the same worker, so an installed model
                // leaves the pin "translating" from the command to the result.
                reading ??= StartPictureReading(source, await PinTranslationService.FreezeOverlaySourceAsync(source.Image, token),
                    translator, direction, korean, progress, token);
                read = await reading;
                if (!Current()) return;
                (recognition, recognized) = (read.Recognition, read.Text);
                if (string.IsNullOrWhiteSpace(recognized) || read.Decision is not { } decision)
                {
                    pin.SetTranslationState(PinTranslationState.Available);
                    pin.ReportTranslationNote(UiText.Get("Pin_TranslationNoText"));
                    return;
                }
                if (decision.Availability != PinTranslationAvailability.Translatable)
                {
                    pin.SetTranslationDisabled(decision.Availability == PinTranslationAvailability.UnsupportedLanguage
                        ? UiText.Get("Pin_TranslationUnsupportedLanguage")
                        : UiText.Get(korean ? "Pin_AlreadyKorean" : "Pin_AlreadyEnglish"));
                    pin.ReportTranslationNote(pin.TranslationDisabledHelp ?? string.Empty);
                    return;
                }
            }

            PinTranslationResult? result;
            if (read is { Translated: true }) result = read.Result;
            else
            {
                bool installed = read?.Installed ?? await translator.IsModelInstalledAsync(token);
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
                if (pin.TranslationState != PinTranslationState.Translating) pin.SetTranslationState(PinTranslationState.Translating);
                var service = new PinTranslationService(translator, direction) { Progress = progress };
                // The overlay steps run on worker threads; starting them from the pool keeps their set-up there too.
                result = recognition is not null
                    ? await Task.Run(() => service.TranslateRecognizedAsync(recognition, source, token), token)
                    : recognized is null
                    ? await service.TranslateAsync(source, token)
                    : await service.TranslateRecognizedAsync(recognized, source, token);
                if (!Current()) return;
            }
            if (result is not null) pin.ApplyTranslation(result.Content, result.NeedsReview, korean, result.DocumentView);
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
            // A reading the translation stopped waiting for keeps its token until it ends, and its failure is observed.
            if (reading is null) operation.Dispose();
            else _ = reading.ContinueWith(task => { _ = task.Exception; operation.Dispose(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
