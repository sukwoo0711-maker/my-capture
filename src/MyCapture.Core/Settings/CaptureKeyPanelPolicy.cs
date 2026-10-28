namespace MyCapture.Core.Settings;

/// <summary>The ten selectors following the initial three start guides.</summary>
public static class CaptureKeyPanelPolicy
{
    public const int ShowCount = 10;
    public static int Normalize(int count) => Math.Clamp(count, 0, ShowCount);

    public static bool TryClaim(CaptureSettings capture, bool startGuideShown)
    {
        ArgumentNullException.ThrowIfNull(capture);
        capture.KeyPanelShownCount = Normalize(capture.KeyPanelShownCount);
        if (startGuideShown || CaptureStartGuidePolicy.ShouldShow(capture.StartGuideShownCount))
            return capture.AlwaysShowKeyPanel;
        if (capture.KeyPanelShownCount >= ShowCount) return capture.AlwaysShowKeyPanel;
        capture.KeyPanelShownCount++;
        return true;
    }
}
