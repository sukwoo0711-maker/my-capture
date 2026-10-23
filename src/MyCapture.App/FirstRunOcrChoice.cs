using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App;

internal static class FirstRunOcrChoice
{
    internal const ulong MinimumMemoryBytes = 8UL * 1024 * 1024 * 1024;

    internal static bool ShouldPreselect(IOcrDownloadEnvironment environment)
    {
        try
        {
            return !environment.ShouldDeferBackgroundDownload()
                && environment.IsBatterySaverOn == false
                && environment.ProcessorCount >= 4
                && environment.InstalledMemoryBytes >= MinimumMemoryBytes;
        }
        catch (Exception) { return false; }
    }

    internal static string Description => UiText.Format("Welcome_AccurateOcrHelp",
        OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Accurate));

    // Every welcome close uses this path, including dismissal by a capture shortcut.
    internal static void Complete(AppSettings settings, bool selected,
        Action<AppSettings> completeFirstRun, Action startExplicitDownload)
    {
        OcrQuality previous = settings.Ocr.Quality;
        double previousUpscale = settings.Ocr.UpscaleFactor;
        settings.Ocr.Quality = OcrQualityNames.Normalize(previous);
        if (selected && previous == OcrQuality.Fast) settings.Ocr.Quality = OcrQuality.Accurate;
        if (settings.Ocr.Quality != previous) OcrQualityProfile.Synchronize(settings.Ocr);
        try
        {
            completeFirstRun(settings);
        }
        catch
        {
            settings.Ocr.Quality = previous;
            settings.Ocr.UpscaleFactor = previousUpscale;
            throw;
        }
        if (selected && OcrQualityProfile.UseNeuralModel(settings.Ocr.Quality)) startExplicitDownload();
    }
}
