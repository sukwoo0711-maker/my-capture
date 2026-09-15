using System.Windows.Media.Imaging;
using MyCapture.Core.Settings;

namespace MyCapture.Ocr;

/// <summary>Builds recognition requests from the user's OCR quality stage.</summary>
public static class OcrRequestFactory
{
    public static OcrRequest FromFile(
        string filePath,
        OcrSettings settings,
        bool? searchRotatedOrientations = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return OcrRequest.FromFile(
            filePath,
            settings.UpscaleFactor,
            settings.PreferredLanguages,
            searchRotatedOrientations ?? OcrQualityProfile.SearchRotatedOrientations(settings.Quality),
            OcrQualityProfile.EnhanceContrast(settings.Quality),
            OcrQualityProfile.UseNeuralModel(settings.Quality),
            OcrQualityProfile.LocalCorrection(settings.Quality));
    }

    public static OcrRequest FromBitmap(
        BitmapSource bitmap,
        OcrSettings settings,
        bool? searchRotatedOrientations = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return OcrRequest.FromBitmap(
            bitmap,
            settings.UpscaleFactor,
            settings.PreferredLanguages,
            searchRotatedOrientations ?? OcrQualityProfile.SearchRotatedOrientations(settings.Quality),
            OcrQualityProfile.EnhanceContrast(settings.Quality),
            OcrQualityProfile.UseNeuralModel(settings.Quality),
            OcrQualityProfile.LocalCorrection(settings.Quality));
    }
}
