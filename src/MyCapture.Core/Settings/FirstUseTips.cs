namespace MyCapture.Core.Settings;

public enum PinFirstUseTip { Size, HideAll }

/// <summary>Record a tip only after its presentation succeeds.</summary>
public static class FirstUseTips
{
    /// <summary>At most one tip per created pin; a refused presentation leaves it for a later pin.</summary>
    public static bool ShowNextPinTip(GeneralSettings settings, Func<PinFirstUseTip, bool> show)
    {
        if (!settings.PinSizeTipShown)
        {
            if (!show(PinFirstUseTip.Size)) return false;
            settings.PinSizeTipShown = true;
            return true;
        }
        if (settings.FirstPinTipShown || !show(PinFirstUseTip.HideAll)) return false;
        settings.FirstPinTipShown = true;
        return true;
    }

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
