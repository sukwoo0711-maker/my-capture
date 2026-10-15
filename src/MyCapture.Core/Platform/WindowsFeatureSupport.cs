namespace MyCapture.Core.Platform;

/// <summary>Optional Windows features, independent of the supported-host floor.</summary>
public static class WindowsFeatureSupport
{
    /// <summary>First Windows 11 build (21H2).</summary>
    public const int Windows11Build = 22000;
    private const int Windows11BackdropBuild = 22621;

    public static bool HasRoundedWindowCorners(int build) => build >= Windows11Build;

    public static bool HasDwmCaptionColors(int build) => build >= Windows11Build;

    /// <summary>Windows 11 renamed Settings > Ease of Access to Accessibility.</summary>
    public static bool HasAccessibilitySettingsName(int build) => build >= Windows11Build;

    public static bool HasSystemBackdrop(int build) => build >= Windows11BackdropBuild;

    /// <summary>Runtime callers must also probe the WinRT type and members with ApiInformation.</summary>
    public static bool HasBorderlessCapture(int build) => build >= Windows11Build;

    public static double WindowCornerRadiusDip(int build) => HasRoundedWindowCorners(build) ? 8.0 : 0.0;
}
