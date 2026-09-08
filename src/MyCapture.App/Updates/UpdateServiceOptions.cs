namespace MyCapture.App.Updates;

/// <summary>
/// Configurable parameters for GitHub update checks, timeouts, and download safety bounds.
/// </summary>
public sealed class UpdateServiceOptions
{
    public const string DefaultOwner = "sukwoo0711-maker";
    public const string DefaultRepo = "my-capture";
    public const string DefaultApiBaseUrl = "https://api.github.com";

    public string RepositoryOwner { get; init; } = DefaultOwner;
    public string RepositoryName { get; init; } = DefaultRepo;
    public string ApiBaseUrl { get; init; } = DefaultApiBaseUrl;

    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public long MaxInstallerSizeBytes { get; init; } = 512L * 1024 * 1024; // 512 MiB safety limit
    public long MaxChecksumSizeBytes { get; init; } = 1024 * 1024;          // 1 MiB limit for SHA256SUMS.txt

    public int BufferSizeBytes { get; init; } = 64 * 1024;                  // 64 KiB buffer
}
