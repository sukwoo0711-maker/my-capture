using System.Windows.Media.Imaging;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Pinning;

internal static class PinTranslationOcr
{
    // The requested first language is a preference, not a measured source language.
    // Windows and the dual recognizer report one winning language for a merged result.
    // Require matching prose evidence before calling that result a language fallback.
    internal static bool NeedsLanguageReview(OcrResult result, string target)
    {
        if (!result.HasText) return false;
        string desired = PinLanguagePolicy.NormalizeTarget(target) switch
        {
            "ko" => "en",
            "en" => "ko",
            _ => string.Empty,
        };
        string actual = result.LanguageTag?.Split('-')[0].ToLowerInvariant() ?? string.Empty;
        if (desired.Length == 0 || actual is not ("en" or "ko") || actual == desired) return false;
        string prose = PinLanguagePolicy.WithoutProtectedText(result.Text);
        bool korean = prose.Any(PinLanguagePolicy.IsKorean);
        bool latin = prose.Any(PinLanguagePolicy.IsLatin);
        return desired == "en" ? latin && !korean : korean && !latin;
    }

    internal static OcrRequest CreateRequest(BitmapSource image, string target, OcrSettings settings) =>
        OcrRequest.FromBitmap(image, settings.UpscaleFactor,
            target.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? ["ko-KR", "en-US"] : ["en-US", "ko-KR"],
            OcrQualityProfile.SearchRotatedOrientations(settings.Quality),
            OcrQualityProfile.EnhanceContrast(settings.Quality),
            AppArchitecture.SupportsNeuralOcr && OcrQualityProfile.UseNeuralModel(settings.Quality),
            OcrQualityProfile.LocalCorrection(settings.Quality),
            AppArchitecture.SupportsNeuralOcr && OcrQualityProfile.UseSuperResolution(settings.Quality));
}
