using System.IO;

namespace MyCapture.App.Updates;

/// <summary>
/// Immutable, cryptographically-verified update payload staged in an isolated local directory.
/// Does not execute the installer; exposes verified state so later UI/exit integration can coordinate launch.
/// </summary>
public sealed class VerifiedUpdatePackage
{
    public UpdateVersion Version { get; }
    public string InstallerPath { get; }
    public string ChecksumPath { get; }
    public string ExpectedSha256 { get; }
    public string ActualSha256 { get; }
    public long FileSizeBytes { get; }
    public string StagingDirectory { get; }
    public string ReleaseTitle { get; }
    public string ReleaseNotes { get; }
    public Uri ReleaseUrl { get; }
    public DateTimeOffset PublishedAt { get; }

    internal VerifiedUpdatePackage(
        UpdateVersion version,
        string installerPath,
        string checksumPath,
        string expectedSha256,
        string actualSha256,
        long fileSizeBytes,
        string stagingDirectory,
        string releaseTitle,
        string releaseNotes,
        Uri releaseUrl,
        DateTimeOffset publishedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksumPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(releaseUrl);

        Version = version;
        InstallerPath = Path.GetFullPath(installerPath);
        ChecksumPath = Path.GetFullPath(checksumPath);
        ExpectedSha256 = expectedSha256.ToLowerInvariant();
        ActualSha256 = actualSha256.ToLowerInvariant();
        FileSizeBytes = fileSizeBytes;
        StagingDirectory = Path.GetFullPath(stagingDirectory);
        ReleaseTitle = releaseTitle ?? string.Empty;
        ReleaseNotes = releaseNotes ?? string.Empty;
        ReleaseUrl = releaseUrl;
        PublishedAt = publishedAt;
    }

    internal VerifiedUpdatePackage(
        UpdateVersion version,
        string installerPath,
        string expectedSha256,
        string actualSha256,
        long fileSizeBytes,
        string stagingDirectory,
        string releaseTitle,
        string releaseNotes,
        Uri releaseUrl,
        DateTimeOffset publishedAt)
        : this(
            version,
            installerPath,
            Path.Combine(stagingDirectory, "SHA256SUMS.txt"),
            expectedSha256,
            actualSha256,
            fileSizeBytes,
            stagingDirectory,
            releaseTitle,
            releaseNotes,
            releaseUrl,
            publishedAt)
    {
    }

    /// <summary>
    /// Safely cleans up the staged directory and its downloaded installer and checksum files.
    /// Deletes only the exact generated files owned by this verified session.
    /// Rejects reparse directories and never enumerates or deletes arbitrary caller folders.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(StagingDirectory) || !Directory.Exists(StagingDirectory))
            {
                return;
            }

            var dirInfo = new DirectoryInfo(StagingDirectory);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            string[] exactOwnedFiles = [InstallerPath, ChecksumPath];
            foreach (string filePath in exactOwnedFiles)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    {
                        continue;
                    }

                    string? parentDir = Path.GetDirectoryName(filePath);
                    if (!string.Equals(parentDir, dirInfo.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(filePath);
                    if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    File.Delete(filePath);
                }
                catch
                {
                    // Best-effort cleanup of individual owned files
                }
            }

            // Remove the staging session directory only if empty (non-recursive)
            try
            {
                Directory.Delete(StagingDirectory, recursive: false);
            }
            catch
            {
                // Non-empty or in-use directories are left untouched
            }
        }
        catch
        {
        }
    }
}
