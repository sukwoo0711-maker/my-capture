using System.Globalization;

namespace MyCapture.Core.Platform;

/// <summary>
/// The single source of truth for the host operating system MyCapture supports.
/// </summary>
/// <remarks>
/// MyCapture 1.0.0 ships for Windows 11 only. Older Windows releases are out of support, so the
/// gate is expressed as a build-number floor: build 22000 is the first Windows 11 release, and
/// every earlier build is rejected regardless of its marketing name. The floor is duplicated in
/// three places that cannot reference this assembly - the MSBuild
/// <c>SupportedOSPlatformVersion</c>, the installer preflight and the release manifest - so the
/// constants below are asserted against those artefacts by the contract tests.
/// </remarks>
public static class WindowsSupportPolicy
{
    /// <summary>First Windows 11 build (21H2). Anything lower is unsupported.</summary>
    public const int MinimumBuild = 22000;

    /// <summary>Human-readable name of the minimum supported release.</summary>
    public const string MinimumReleaseName = "Windows 11 version 21H2";

    /// <summary>Value mirrored by the MSBuild <c>SupportedOSPlatformVersion</c> property.</summary>
    public const string SupportedOSPlatformVersion = "10.0.22000.0";

    /// <summary>Major version reported by every Windows 11 build.</summary>
    private const int WindowsMajorVersion = 10;

    /// <summary>True when <paramref name="build"/> is at or above the Windows 11 floor.</summary>
    public static bool IsSupportedBuild(int build) => build >= MinimumBuild;

    /// <summary>
    /// True when <paramref name="osVersion"/> describes a supported Windows 11 host. The major
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
        "{0} (build {1}) or later, x64, is required.",
        MinimumReleaseName,
        MinimumBuild);

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
