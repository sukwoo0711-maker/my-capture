namespace MyCapture.App.Updates;

/// <summary>
/// Lifecycle phase of an update check, download, or verification operation.
/// </summary>
public enum UpdatePhase
{
    None = 0,
    Checking = 1,
    DownloadingChecksums = 2,
    DownloadingInstaller = 3,
    VerifyingIntegrity = 4,
    Ready = 5,
    Cancelled = 6,
    Failed = 7,
}

/// <summary>
/// Language-neutral progress report data for update operations.
/// </summary>
public readonly record struct UpdateProgress(
    UpdatePhase Phase,
    long BytesReceived,
    long? TotalBytes,
    double? Percent)
{
    public static UpdateProgress Checking() =>
        new(UpdatePhase.Checking, 0, null, null);

    public static UpdateProgress DownloadingChecksums() =>
        new(UpdatePhase.DownloadingChecksums, 0, null, null);

    public static UpdateProgress DownloadingInstaller(long bytesReceived, long? totalBytes)
    {
        double? percent = totalBytes > 0
            ? Math.Clamp((double)bytesReceived / totalBytes.Value * 100.0, 0.0, 100.0)
            : null;
        return new(UpdatePhase.DownloadingInstaller, bytesReceived, totalBytes, percent);
    }

    public static UpdateProgress VerifyingIntegrity(long totalBytes) =>
        new(UpdatePhase.VerifyingIntegrity, totalBytes, totalBytes, 100.0);

    public static UpdateProgress Ready(long totalBytes) =>
        new(UpdatePhase.Ready, totalBytes, totalBytes, 100.0);

    public static UpdateProgress Cancelled(long bytesReceived) =>
        new(UpdatePhase.Cancelled, bytesReceived, null, null);

    public static UpdateProgress Failed(long bytesReceived) =>
        new(UpdatePhase.Failed, bytesReceived, null, null);
}
