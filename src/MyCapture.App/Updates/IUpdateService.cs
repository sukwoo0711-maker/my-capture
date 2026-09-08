namespace MyCapture.App.Updates;

/// <summary>
/// Service contract for querying releases, downloading and verifying update packages,
/// and staging them in an isolated directory without executing installers.
/// </summary>
public interface IUpdateService : IDisposable
{
    /// <summary>
    /// Queries the latest GitHub release, rejecting prerelease/draft/malformed versions
    /// and verifying that both the exact setup asset and SHA256SUMS.txt exist.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(
        UpdateVersion currentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience overload taking a standard <see cref="Version"/>.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and stages the update package into a unique subdirectory under <paramref name="stagingRoot"/>,
    /// enforcing URL safety, timeouts, size limits, and SHA-256 integrity verification.
    /// </summary>
    Task<StagedUpdateResult> DownloadAndStageAsync(
        UpdatePackageInfo package,
        string stagingRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Single-step check and stage: checks for an update and, if available, downloads and verifies it.
    /// </summary>
    Task<StagedUpdateResult> CheckAndStageAsync(
        UpdateVersion currentVersion,
        string stagingRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience single-step overload taking a standard <see cref="Version"/>.
    /// </summary>
    Task<StagedUpdateResult> CheckAndStageAsync(
        Version currentVersion,
        string stagingRoot,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
