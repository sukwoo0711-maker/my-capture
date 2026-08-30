using System.Windows.Media.Imaging;

namespace MyCapture.Ocr;

/// <summary>
/// One recognition request: an image source plus how it should be prepared.
/// </summary>
/// <remarks>
/// The source is deliberately one of three shapes — encoded bytes, a file path, or an
/// already-decoded <see cref="BitmapSource"/> — so the gallery can point at <c>rendered.png</c>
/// on disk while a pin can hand over freshly encoded PNG bytes without ever writing the user's
/// transient image to a file.
/// </remarks>
public sealed class OcrRequest
{
    private OcrRequest(byte[]? encodedImage, string? filePath, BitmapSource? bitmap)
    {
        EncodedImage = encodedImage;
        FilePath = filePath;
        Bitmap = bitmap;
    }

    /// <summary>Encoded image bytes (PNG/JPEG/etc.), when the source is in memory.</summary>
    public byte[]? EncodedImage { get; }

    /// <summary>Path to an image file, when the source is on disk.</summary>
    public string? FilePath { get; }

    /// <summary>An already-decoded, frozen bitmap, when the caller already has one.</summary>
    public BitmapSource? Bitmap { get; }

    /// <summary>
    /// Requested upscale factor applied to small UI text before recognition, 1–4×.
    /// </summary>
    /// <remarks>
    /// The engine clamps this to the range and further caps it so the prepared bitmap never
    /// exceeds <c>OcrEngine.MaxImageDimension</c>; the caller need not know that limit.
    /// </remarks>
    public double UpscaleFactor { get; init; } = 1.0;

    /// <summary>
    /// Preferred BCP-47 tags in priority order, or empty to defer to the user profile.
    /// </summary>
    public IReadOnlyList<string> PreferredLanguages { get; init; } = [];

    public static OcrRequest FromBytes(
        byte[] encodedImage,
        double upscaleFactor = 1.0,
        IReadOnlyList<string>? preferredLanguages = null)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);
        return new OcrRequest(encodedImage, filePath: null, bitmap: null)
        {
            UpscaleFactor = upscaleFactor,
            PreferredLanguages = preferredLanguages ?? [],
        };
    }

    public static OcrRequest FromFile(
        string filePath,
        double upscaleFactor = 1.0,
        IReadOnlyList<string>? preferredLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return new OcrRequest(encodedImage: null, filePath, bitmap: null)
        {
            UpscaleFactor = upscaleFactor,
            PreferredLanguages = preferredLanguages ?? [],
        };
    }

    public static OcrRequest FromBitmap(
        BitmapSource bitmap,
        double upscaleFactor = 1.0,
        IReadOnlyList<string>? preferredLanguages = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new OcrRequest(encodedImage: null, filePath: null, bitmap)
        {
            UpscaleFactor = upscaleFactor,
            PreferredLanguages = preferredLanguages ?? [],
        };
    }
}

/// <summary>
/// Recognises text in an image using the operating system's OCR engine.
/// </summary>
/// <remarks>
/// The one implementation, <see cref="WindowsOcrService"/>, wraps
/// <c>Windows.Media.Ocr.OcrEngine</c>. The interface exists so the app can hold the service by
/// contract and so tests can substitute a fake; the recognition-boundary logic that decides
/// language, scaling and coordinate mapping is factored out separately and tested directly.
/// </remarks>
public interface IOcrService
{
    /// <summary>
    /// Whether an OS OCR engine can be created at all on this machine (a supported language is
    /// installed and the WinRT surface is reachable). Cheap; safe to call for UI enablement.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>BCP-47 tags the OS reports it can recognise, empty when unavailable.</summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>
    /// Recognises text in the requested image. Never throws for a recognition failure: an
    /// unavailable engine, an undecodable image or an API fault is reported through
    /// <see cref="OcrResult.Status"/>. Honours <paramref name="cancellationToken"/>.
    /// </summary>
    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default);
}
