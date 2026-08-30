using System.Windows.Media.Imaging;

namespace MyCapture.Ocr;

/// <summary>
/// The raw output of a recognizer over a prepared (already-scaled) bitmap, in the prepared
/// bitmap's pixel coordinates.
/// </summary>
/// <remarks>
/// This is the seam that lets the service be tested without the OS engine: the WinRT
/// recognizer returns this, and everything after it — unscaling boxes back to original pixels,
/// normalising and joining text, deciding the outcome — is pure and covered by tests through a
/// fake recognizer.
/// </remarks>
public sealed record RecognizedText(string LanguageTag, IReadOnlyList<RecognizedLine> Lines);

/// <summary>A recognizer line in prepared-bitmap pixel coordinates.</summary>
public sealed record RecognizedLine(string Text, IReadOnlyList<RecognizedWord> Words);

/// <summary>A recognizer word with its box in prepared-bitmap pixel coordinates.</summary>
public sealed record RecognizedWord(string Text, OcrRect Bounds);

/// <summary>
/// Abstracts the operating-system OCR engine so the service's preparation, coordinate mapping
/// and outcome logic can be exercised deterministically without invoking real OS OCR.
/// </summary>
internal interface IOcrRecognizer
{
    /// <summary>Whether a recognizer can be created (a supported language exists).</summary>
    bool IsAvailable { get; }

    /// <summary>Languages the recognizer reports it supports, as BCP-47 tags.</summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>The engine's maximum image dimension, used to cap preparation.</summary>
    int MaxImageDimension { get; }

    /// <summary>
    /// Recognises text over an already-prepared bitmap using <paramref name="languageTag"/>
    /// (or the user-profile default when it is <see langword="null"/>). Returns
    /// <see langword="null"/> when no engine could be created for the request.
    /// </summary>
    Task<RecognizedText?> RecognizeAsync(
        BitmapSource preparedBitmap,
        string? languageTag,
        CancellationToken cancellationToken);
}
