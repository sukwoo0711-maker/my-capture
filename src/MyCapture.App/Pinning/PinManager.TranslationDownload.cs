using System.Windows.Threading;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

internal sealed partial class PinManager
{
    /// <summary>
    /// Installs the model while the pin shows how much of it has arrived. The bar is determinate from the start:
    /// the size comes from the catalog.
    /// </summary>
    private static Task DownloadTranslationModelAsync(PinWindow pin, ITextTranslator translator, CancellationToken token)
    {
        long total = translator.ModelDownloadBytes;
        pin.SetTranslationState(PinTranslationState.Downloading, total);
        pin.ShowTranslationDownloadProgress(0, total);
        return translator.DownloadModelAsync(new PinDownloadProgress(pin, token), token);
    }

    /// <summary>
    /// The command while a check, download or translation runs: that work stops and the pin returns to what it
    /// offered before, the install when the model is still missing.
    /// </summary>
    private void CancelPinTranslationByUser(PinWindow pin)
    {
        if (!pin.IsTranslationRunning) return;
        bool modelMissing = pin.TranslationState == PinTranslationState.Downloading;
        CancelPinTranslation(pin);
        long bytes = modelMissing && _translators?.Invoke(DirectionFor(TargetIsKorean)) is { } translator
            ? translator.ModelDownloadBytes : 0;
        pin.SetTranslationState(modelMissing ? PinTranslationState.ModelNeeded : PinTranslationState.Available, bytes);
        pin.ReportTranslationNote(UiText.Get("Pin_TranslationCancelled"));
    }

    /// <summary>
    /// Nothing came back to show. A text pin's plan found nothing in the other language; a picture's lines were all
    /// code, numbers, symbols or already in the target language. The command turns off with that reason, said once.
    /// </summary>
    internal static void ReportNothingTranslated(PinWindow pin, PinContent source, bool korean)
    {
        pin.SetTranslationDisabled(source.Kind == PinContentKind.Image ? UiText.Get("Pin_TranslationNothing")
            : UiText.Get(korean ? "Pin_AlreadyKorean" : "Pin_AlreadyEnglish"));
        pin.ReportTranslationNote(pin.TranslationDisabledHelp ?? string.Empty);
    }

    /// <summary>
    /// Why a picture's read text is not translated: an unsupported script, already the target language, or nothing
    /// in a language at all (only code, identifiers, numbers or symbols), which was reported as "already" before.
    /// </summary>
    private static string NotTranslatableReason(PinTranslationAvailability availability, bool korean) => availability switch
    {
        PinTranslationAvailability.UnsupportedLanguage => UiText.Get("Pin_TranslationUnsupportedLanguage"),
        PinTranslationAvailability.NothingToTranslate => UiText.Get("Pin_TranslationNothing"),
        _ => UiText.Get(korean ? "Pin_AlreadyKorean" : "Pin_AlreadyEnglish"),
    };

    /// <summary>
    /// Why a translation stopped, in a sentence that says what to do. Model download failures use the OCR window's
    /// wording where it fits a pin; anything else keeps the general sentence.
    /// </summary>
    internal static string TranslationFailureMessage(Exception error, long downloadBytes) => error switch
    {
        TranslationModelException { Error: TranslationModelError.Network } => UiText.Get("Pin_TranslationErrorNetwork"),
        TranslationModelException { Error: TranslationModelError.Stalled } => UiText.Get("Translate_ErrorStalled"),
        TranslationModelException { Error: TranslationModelError.DiskFull } =>
            UiText.Format("Translate_ErrorDiskFull", Math.Ceiling(downloadBytes / 1_000_000d)),
        TranslationModelException { Error: TranslationModelError.Disk } => UiText.Get("Translate_ErrorDisk"),
        TranslationModelException { Error: TranslationModelError.Corrupt } => UiText.Get("Translate_ErrorCorrupt"),
        _ => UiText.Get("Pin_TranslationFailed"),
    };

    /// <summary>The check after a completed download did not find the whole model.</summary>
    private static TranslationModelException ModelVerificationFailed() =>
        new(TranslationModelError.Corrupt, "Translation model verification failed.");
}

/// <summary>
/// Carries the model download's progress from the store's worker to the pin. A report that arrives while an earlier
/// one is still on its way replaces it, and nothing reaches the pin once its translation is cancelled, superseded or
/// the pin closes. The pin itself shows at most ten updates a second.
/// </summary>
internal sealed class PinDownloadProgress(PinWindow pin, CancellationToken cancellation) : IProgress<TranslationDownloadProgress>
{
    private readonly object _gate = new();
    private TranslationDownloadProgress _latest;
    private bool _queued;

    public void Report(TranslationDownloadProgress value)
    {
        if (cancellation.IsCancellationRequested) return;
        lock (_gate)
        {
            _latest = value;
            if (_queued) return;
            _queued = true;
        }
        _ = pin.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Deliver));
    }

    private void Deliver()
    {
        TranslationDownloadProgress value;
        lock (_gate)
        {
            value = _latest;
            _queued = false;
        }
        if (!cancellation.IsCancellationRequested) pin.ShowTranslationDownloadProgress(value.ReceivedBytes, value.TotalBytes);
    }
}
