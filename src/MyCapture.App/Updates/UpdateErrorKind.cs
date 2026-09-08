namespace MyCapture.App.Updates;

/// <summary>
/// Machine-readable, language-neutral category of update failure or status.
/// UI and logging layers can project this to localized strings.
/// </summary>
public enum UpdateErrorKind
{
    None = 0,
    AlreadyUpToDate = 1,
    CheckFailed = 2,
    RateLimited = 3,
    InvalidReleaseData = 4,
    AssetNotFound = 5,
    InvalidUrl = 6,
    DownloadFailed = 7,
    PayloadTooLarge = 8,
    ChecksumParseFailed = 9,
    HashMismatch = 10,
    Cancelled = 11,
    StagingError = 12,
}
