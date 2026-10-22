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
        if (selected && previous == OcrQuality.Fast) settings.Ocr.Quality = OcrQuality.Accurate;
        try
        {
            completeFirstRun(settings);
        }
        catch
        {
            settings.Ocr.Quality = previous;
            throw;
        }
        if (selected && OcrQualityProfile.UseNeuralModel(settings.Ocr.Quality)) startExplicitDownload();
    }
}
