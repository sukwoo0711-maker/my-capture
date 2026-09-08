namespace MyCapture.App.Updates;

/// <summary>
/// Immutable metadata describing an available update package discovered from the remote repository.
/// </summary>
public sealed class UpdatePackageInfo
{
    public UpdateVersion Version { get; }
    public string ReleaseTitle { get; }
    public string ReleaseNotes { get; }
    public Uri ReleaseUrl { get; }
    public DateTimeOffset PublishedAt { get; }
    public string SetupAssetName { get; }
    public Uri SetupDownloadUrl { get; }
    public long SetupSizeBytes { get; }
    public Uri ChecksumDownloadUrl { get; }

    public UpdatePackageInfo(
        UpdateVersion version,
        string releaseTitle,
        string releaseNotes,
        Uri releaseUrl,
        DateTimeOffset publishedAt,
        string setupAssetName,
        Uri setupDownloadUrl,
        long setupSizeBytes,
        Uri checksumDownloadUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupAssetName);
        ArgumentNullException.ThrowIfNull(setupDownloadUrl);
        ArgumentNullException.ThrowIfNull(checksumDownloadUrl);
        ArgumentNullException.ThrowIfNull(releaseUrl);

        Version = version;
        ReleaseTitle = releaseTitle ?? string.Empty;
        ReleaseNotes = releaseNotes ?? string.Empty;
        ReleaseUrl = releaseUrl;
        PublishedAt = publishedAt;
        SetupAssetName = setupAssetName;
        SetupDownloadUrl = setupDownloadUrl;
        SetupSizeBytes = setupSizeBytes;
        ChecksumDownloadUrl = checksumDownloadUrl;
    }
}
