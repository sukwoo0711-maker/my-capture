namespace MyCapture.App.Updates;

/// <summary>
/// Result of querying the release repository for newer application versions.
/// </summary>
public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; }
    public UpdatePackageInfo? PackageInfo { get; }
    public UpdateVersion? CurrentVersion { get; }
    public UpdateVersion? LatestVersion { get; }
    public UpdateErrorKind ErrorKind { get; }
    public string? ErrorMessage { get; }

    private UpdateCheckResult(
        bool isUpdateAvailable,
        UpdatePackageInfo? packageInfo,
        UpdateVersion? currentVersion,
        UpdateVersion? latestVersion,
        UpdateErrorKind errorKind,
        string? errorMessage)
    {
        IsUpdateAvailable = isUpdateAvailable;
        PackageInfo = packageInfo;
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
    }

    public static UpdateCheckResult Available(UpdateVersion current, UpdatePackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new UpdateCheckResult(
            isUpdateAvailable: true,
            packageInfo: package,
            currentVersion: current,
            latestVersion: package.Version,
            errorKind: UpdateErrorKind.None,
            errorMessage: null);
    }

    public static UpdateCheckResult UpToDate(UpdateVersion current, UpdateVersion latest) =>
        new(
            isUpdateAvailable: false,
            packageInfo: null,
            currentVersion: current,
            latestVersion: latest,
            errorKind: UpdateErrorKind.AlreadyUpToDate,
            errorMessage: null);

    public static UpdateCheckResult Failed(
        UpdateErrorKind errorKind,
        string message,
        UpdateVersion? current = null,
        UpdateVersion? latest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new UpdateCheckResult(
            isUpdateAvailable: false,
            packageInfo: null,
            currentVersion: current,
            latestVersion: latest,
            errorKind: errorKind,
            errorMessage: message);
    }
}

/// <summary>
/// Result of downloading, checking integrity, and staging an update package.
/// </summary>
public sealed class StagedUpdateResult
{
    public bool Succeeded { get; }
    public VerifiedUpdatePackage? VerifiedPackage { get; }
    public UpdateErrorKind ErrorKind { get; }
    public string? ErrorMessage { get; }

    private StagedUpdateResult(
        bool succeeded,
        VerifiedUpdatePackage? verifiedPackage,
        UpdateErrorKind errorKind,
        string? errorMessage)
    {
        Succeeded = succeeded;
        VerifiedPackage = verifiedPackage;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
    }

    public static StagedUpdateResult Success(VerifiedUpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new StagedUpdateResult(
            succeeded: true,
            verifiedPackage: package,
            errorKind: UpdateErrorKind.None,
            errorMessage: null);
    }

    public static StagedUpdateResult Failed(UpdateErrorKind errorKind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new StagedUpdateResult(
            succeeded: false,
            verifiedPackage: null,
            errorKind: errorKind,
            errorMessage: message);
    }
}
