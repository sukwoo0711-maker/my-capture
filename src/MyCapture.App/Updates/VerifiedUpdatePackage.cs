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
    public string ExpectedSha256 { get; }
    public string ActualSha256 { get; }
    public long FileSizeBytes { get; }
    public string StagingDirectory { get; }
    public string ReleaseTitle { get; }
    public string ReleaseNotes { get; }
    public Uri ReleaseUrl { get; }
    public DateTimeOffset PublishedAt { get; }

    public VerifiedUpdatePackage(
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(releaseUrl);

        Version = version;
        InstallerPath = Path.GetFullPath(installerPath);
        ExpectedSha256 = expectedSha256.ToLowerInvariant();
        ActualSha256 = actualSha256.ToLowerInvariant();
        FileSizeBytes = fileSizeBytes;
        StagingDirectory = Path.GetFullPath(stagingDirectory);
        ReleaseTitle = releaseTitle ?? string.Empty;
        ReleaseNotes = releaseNotes ?? string.Empty;
        ReleaseUrl = releaseUrl;
        PublishedAt = publishedAt;
    }

    /// <summary>
    /// Safely cleans up the staged directory and its downloaded installer and checksum files.
    /// Never deletes files outside this specific staging session directory.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (!Directory.Exists(StagingDirectory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(StagingDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                }
            }

            Directory.Delete(StagingDirectory, recursive: false);
        }
        catch (Exception)
        {
        }
    }
}
