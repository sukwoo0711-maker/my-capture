using MyCapture.Core.Platform;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Startup gate that stops MyCapture on hosts below the supported Windows baseline.
/// </summary>
/// <remarks>
/// The installer refuses unsupported hosts, but the portable ZIP has no installer, so the process
/// itself must enforce the same floor. The gate is written as a pure function over the reported OS
/// version plus two callbacks so it can be unit-tested without a message loop.
/// </remarks>
internal static class HostRequirementGate
{
    /// <summary>Matches the installer's unsupported-OS exit code.</summary>
    internal const int UnsupportedHostExitCode = 10;

    /// <summary>
    /// Returns true when startup must stop. When it returns true the error text has already been
    /// handed to <paramref name="showError"/> and <paramref name="shutdown"/> has been called with
    /// <see cref="UnsupportedHostExitCode"/>.
    /// </summary>
    internal static bool BlockUnsupportedHost(
        Version? osVersion,
        Action<string> showError,
        Action<int> shutdown)
    {
        ArgumentNullException.ThrowIfNull(showError);
        ArgumentNullException.ThrowIfNull(shutdown);

        if (WindowsSupportPolicy.IsSupportedHost(osVersion))
        {
            return false;
        }

        showError(BuildMessage(osVersion));
        shutdown(UnsupportedHostExitCode);
        return true;
    }

    /// <summary>Korean user-facing text followed by the invariant technical detail.</summary>
    internal static string BuildMessage(Version? osVersion) =>
        "MyCapture는 Windows 11 이상에서만 실행됩니다."
        + Environment.NewLine
        + Environment.NewLine
        + WindowsSupportPolicy.DescribeUnsupportedHost(osVersion);
}
