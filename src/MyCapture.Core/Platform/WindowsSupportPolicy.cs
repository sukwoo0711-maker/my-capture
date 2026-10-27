using System.Globalization;

namespace MyCapture.Core.Platform;

/// <summary>
/// The single source of truth for the host operating system MyCapture supports.
/// </summary>
/// <remarks>
/// Windows 10 version 22H2 is the x64 floor. Its API contract is shared with build 19041,
/// while the host gate and package manifests enforce build 19045. ARM64 x64 emulation needs
/// Windows 11. Contract tests keep MSBuild, installer and Store metadata aligned with this
/// policy; optional API availability is handled separately by WindowsFeatureSupport.
/// </remarks>
public static class WindowsSupportPolicy
{
    /// <summary>Windows 10 version 22H2. Anything lower is unsupported.</summary>
    public const int MinimumBuild = 19045;

    /// <summary>Windows 11 introduced x64 emulation on ARM64.</summary>
    public const int MinimumArm64EmulationBuild = 22000;

    /// <summary>Human-readable name of the minimum supported release.</summary>
    public const string MinimumReleaseName = "Windows 10 version 22H2";

    /// <summary>Value mirrored by the MSBuild <c>SupportedOSPlatformVersion</c> property.</summary>
    public const string SupportedOSPlatformVersion = "10.0.19041.0";

    /// <summary>Major version reported by Windows 10 and Windows 11.</summary>
    private const int WindowsMajorVersion = 10;

    /// <summary>True when <paramref name="build"/> is at or above the x64 floor.</summary>
    public static bool IsSupportedBuild(int build) => build >= MinimumBuild;

    public static bool IsArm64EmulationSupportedBuild(int build) => build >= MinimumArm64EmulationBuild;

    /// <summary>
    /// True when <paramref name="osVersion"/> describes a supported x64 Windows host. The major
    /// version is checked as well, because pre-Windows-10 releases report major 6 with build
    /// numbers that would otherwise look plausible.
    /// </summary>
    public static bool IsSupportedHost(Version? osVersion) =>
        osVersion is not null
        && osVersion.Major >= WindowsMajorVersion
        && IsSupportedBuild(osVersion.Build);

    /// <summary>True when the process is running on a supported host.</summary>
    public static bool IsCurrentHostSupported() =>
        OperatingSystem.IsWindows() && IsSupportedHost(Environment.OSVersion.Version);

    /// <summary>One-line statement of the requirement, used by installer and UI messages.</summary>
    public static string DescribeRequirement() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} (build {1}) or later, {2}, is required.",
        MinimumReleaseName,
        MinimumBuild,
        AppArchitecture.Name);

    /// <summary>Message for a host that does not meet the requirement.</summary>
    public static string DescribeUnsupportedHost(Version? osVersion)
    {
        string detected = osVersion is null
            ? "unknown"
            : osVersion.ToString();

        return string.Format(
            CultureInfo.InvariantCulture,
            "This computer reports Windows version {0} (build {1}). {2}",
            detected,
            osVersion?.Build.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            DescribeRequirement());
    }
}
