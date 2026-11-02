using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

/// <summary>
/// The translator of each language direction. A direction without a published model has none, so its
/// command shows that the model is being prepared instead of offering an install it cannot complete.
/// </summary>
internal sealed class PinTranslationEngines(TextTranslationService englishToKorean, TextTranslationService? koreanToEnglish) : IDisposable
{
    internal ITextTranslator? For(PinTranslationDirection direction) => direction == PinTranslationDirection.EnglishToKorean
        ? englishToKorean : koreanToEnglish;

    /// <summary>Raised on the releasing thread after either direction's model has left.</summary>
    internal event Action? ModelReleased
    {
        add
        {
            englishToKorean.EngineReleased += value;
            if (koreanToEnglish is not null) koreanToEnglish.EngineReleased += value;
        }
        remove
        {
            englishToKorean.EngineReleased -= value;
            if (koreanToEnglish is not null) koreanToEnglish.EngineReleased -= value;
        }
    }

    /// <summary>Reads a picture's text with the app's recognizer; paragraphs are separated by blank lines.</summary>
    internal static async Task<string?> RecognizeAsync(IOcrService? ocr, BitmapSource image,
        MyCapture.Core.Settings.OcrSettings settings, CancellationToken cancellationToken)
    {
        if (ocr is null) return null;
        OcrResult result = await ocr.RecognizeAsync(OcrRequestFactory.FromBitmap(image, settings), cancellationToken);
        if (!result.HasText) return null;
        string text = OcrExtractionAnalyzer.Analyze(result).Text(joinLines: false);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Keeps the original-pixel geometry and angle for an in-place translation. <paramref name="windowsReading"/>
    /// receives the Windows reading before the Latin recovery reads its suspect rows again.
    /// </summary>
    internal static Task<OcrResult> RecognizeResultAsync(IOcrService? ocr, BitmapSource image,
        MyCapture.Core.Settings.OcrSettings settings, CancellationToken cancellationToken,
        Action<OcrResult>? windowsReading = null) =>
        ocr is null ? Task.FromResult(OcrResult.Unavailable())
            : ocr.RecognizeAsync(OcrRequestFactory.FromBitmap(image, settings).WithLatinRecovery(windowsReading), cancellationToken);

    public void Dispose()
    {
        englishToKorean.Dispose();
        koreanToEnglish?.Dispose();
    }
}
