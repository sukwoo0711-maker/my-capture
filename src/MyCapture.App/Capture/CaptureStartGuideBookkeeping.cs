using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Capture;

/// <summary>
/// Decides, once per capture selector, whether it opens with the start guide, and persists the count
/// the way the Store rating bookkeeping is persisted: later, when the shell is idle, and never before
/// a first run's welcome card has created the settings file.
/// </summary>
internal sealed class CaptureStartGuideBookkeeping(
    Func<AppSettings?> current,
    Action<AppSettings> save,
    Action<Action> scheduleWrite,
    ILogger log)
{
    /// <summary>True when this selector shows the guide; the count has then advanced in memory.</summary>
    internal bool Claim()
    {
        if (current() is not { } settings || !CaptureStartGuidePolicy.TryClaim(settings.Capture)) return false;
        scheduleWrite(Persist);
        return true;
    }

    private void Persist()
    {
        // A first run writes the settings file only after the welcome card closes; that write
        // carries the count with it.
        if (current() is not { } settings || settings.General.IsFirstRun) return;
        try
        {
            save(settings);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not save the capture start guide count");
        }
    }
}
