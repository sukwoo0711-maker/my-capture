namespace MyCapture.App.Updates;

/// <summary>
/// Immutable metadata describing an available update package discovered from the remote repository.
/// </summary>
public sealed class UpdatePackageInfo
{
    public UpdateVersion Version { get; }
    public string ReleaseTag { get; }
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
        Uri checksumDownloadUrl,
        string? releaseTag = null)
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

        if (!string.IsNullOrWhiteSpace(releaseTag))
        {
            ReleaseTag = releaseTag.Trim();
        }
        else
        {
            ReleaseTag = DeriveReleaseTag(version, setupDownloadUrl);
        }
    }

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
        : this(
            version,
            releaseTitle,
            releaseNotes,
            releaseUrl,
            publishedAt,
            setupAssetName,
            setupDownloadUrl,
            setupSizeBytes,
            checksumDownloadUrl,
            null)
    {
    }

    private static string DeriveReleaseTag(UpdateVersion version, Uri setupDownloadUrl)
    {
        string path = setupDownloadUrl.AbsolutePath;
        const string marker = "/releases/download/";
        int idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            string after = path.Substring(idx + marker.Length);
            int slash = after.IndexOf('/');
            if (slash > 0)
            {
                string tagSegment = Uri.UnescapeDataString(after.Substring(0, slash));
                if (UpdateVersion.TryParse(tagSegment, out var parsed) && parsed.Value == version)
                {
                    return tagSegment;
                }
            }
        }

        return $"v{version.ToNormalizedString()}";
    }
}
