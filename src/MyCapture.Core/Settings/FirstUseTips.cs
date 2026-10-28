namespace MyCapture.Core.Settings;

/// <summary>Record a tip only after its presentation succeeds.</summary>
public static class FirstUseTips
{
    public const int SavedLocationLimit = 5;

    public static bool ShowSavedLocation(GeneralSettings settings, Func<bool> show)
    {
        if (settings.SavedLocationTipCount >= SavedLocationLimit || !show()) return false;
        settings.SavedLocationTipCount++;
        return true;
    }

    public static bool ShowCaptureOnce(GeneralSettings settings, Action show)
    {
        if (settings.FirstCaptureTipShown) return false;
        show();
        settings.FirstCaptureTipShown = true;
        return true;
    }

    public static bool ShowPinOnce(GeneralSettings settings, Action show)
    {
        if (settings.FirstPinTipShown) return false;
        show();
        settings.FirstPinTipShown = true;
        return true;
    }
}
