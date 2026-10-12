using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Storage;

public enum LegacyMigrationScanStatus { Ready, NoData, Failed }
public enum LegacyMigrationStatus { Completed, Canceled, Failed }
public enum LegacyMigrationStage { Scanning, Copying, Publishing, Recovering }

public sealed record LegacyMigrationScan(LegacyMigrationScanStatus Status, int ImageCount = 0,
    int VideoCount = 0, int FolderCount = 0, long TotalBytes = 0, bool HasSettings = false,
    string? ErrorCode = null);

public sealed record LegacyMigrationProgress(LegacyMigrationStage Stage, int CompletedItems,
    int TotalItems, long CopiedBytes, long TotalBytes);

public sealed record LegacyMigrationResult(LegacyMigrationStatus Status, int ImportedCount = 0,
    int SkippedCount = 0, string? SettingsBackupPath = null, string? ErrorCode = null);

/// <summary>
/// Copies library data without changing current preferences. Call Import only while the
/// supplied queue is quiesced, and recover interrupted operations before loading that queue.
/// </summary>
public interface ILegacyLibraryMigrationService
{
    Task<LegacyMigrationScan> ScanAsync(string sourceDataRoot, AppPaths targetPaths,
        CancellationToken cancellationToken = default);
    Task<LegacyMigrationResult> ImportAsync(string sourceDataRoot, AppPaths targetPaths,
        CaptureQueue quiescedTargetQueue, AppSettings currentSettings,
        IProgress<LegacyMigrationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default);
}
