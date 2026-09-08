namespace MyCapture.App.Updates;

/// <summary>
/// Configurable parameters for GitHub update checks, timeouts, and download safety bounds.
/// </summary>
public sealed class UpdateServiceOptions
{
    public const string DefaultOwner = "sukwoo0711-maker";
    public const string DefaultRepo = "my-capture";
    public string RepositoryOwner { get; init; } = DefaultOwner;
    public string RepositoryName { get; init; } = DefaultRepo;

    private TimeSpan _checkTimeout = TimeSpan.FromSeconds(15);
    public TimeSpan CheckTimeout
    {
        get => _checkTimeout;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Check timeout must be positive.");
            }
            _checkTimeout = value;
        }
    }

    private TimeSpan _downloadTimeout = TimeSpan.FromMinutes(5);
    public TimeSpan DownloadTimeout
    {
        get => _downloadTimeout;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Download timeout must be positive.");
            }
            _downloadTimeout = value;
        }
    }

    private long _maxInstallerSizeBytes = 512L * 1024 * 1024; // 512 MiB safety limit
    public long MaxInstallerSizeBytes
    {
        get => _maxInstallerSizeBytes;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Max installer size must be greater than zero.");
            }
            _maxInstallerSizeBytes = value;
        }
    }

    private long _maxChecksumSizeBytes = 1024 * 1024;          // 1 MiB limit for SHA256SUMS.txt
    public long MaxChecksumSizeBytes
    {
        get => _maxChecksumSizeBytes;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Max checksum size must be greater than zero.");
            }
            _maxChecksumSizeBytes = value;
        }
    }

    private int _bufferSizeBytes = 64 * 1024;                  // 64 KiB buffer
    public int BufferSizeBytes
    {
        get => _bufferSizeBytes;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Buffer size must be greater than zero.");
            }
            _bufferSizeBytes = value;
        }
    }

    public void Validate()
    {
        if (BufferSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferSizeBytes), "Buffer size must be greater than zero.");
        }

        if (MaxInstallerSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInstallerSizeBytes), "Max installer size must be greater than zero.");
        }

        if (MaxChecksumSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChecksumSizeBytes), "Max checksum size must be greater than zero.");
        }

        if (CheckTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CheckTimeout), "Check timeout must be positive.");
        }

        if (DownloadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DownloadTimeout), "Download timeout must be positive.");
        }
    }
}
