using MyCapture.Core.Queue;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Storage;

public enum LegacyMigrationScanStatus { Ready, NoData, Failed }
public enum LegacyMigrationStatus { Completed, Canceled, Failed }
public enum LegacyMigrationStage { Scanning, Copying, Publishing, Recovering }

/// <summary>Startup and retry outcome. Only an uncommitted import whose finish failed asks the user.</summary>
public enum LegacyRecoveryStatus { Clean, DecisionRequired }

/// <param name="UnusableCount">Records left in the legacy folder because a file is missing or damaged.</param>
/// <param name="SettingsUnreadable">The legacy settings file exists but neither it nor its backup is usable.</param>
public sealed record LegacyMigrationScan(LegacyMigrationScanStatus Status, int ImageCount = 0,
    int VideoCount = 0, int FolderCount = 0, long TotalBytes = 0, bool HasSettings = false,
    string? ErrorCode = null, int UnusableCount = 0, bool SettingsUnreadable = false);

public sealed record LegacyMigrationProgress(LegacyMigrationStage Stage, int CompletedItems,
    int TotalItems, long CopiedBytes, long TotalBytes);

/// <param name="RequiredBytes">For <c>InsufficientSpace</c>: the free space the import needs.</param>
/// <param name="AvailableBytes">For <c>InsufficientSpace</c>: the free space that was available.</param>
public sealed record LegacyMigrationResult(LegacyMigrationStatus Status, int ImportedCount = 0,
    int SkippedCount = 0, string? SettingsBackupPath = null, string? ErrorCode = null,
    int UnusableCount = 0, long RequiredBytes = 0, long AvailableBytes = 0);

public sealed record LegacyRecoveryResult(LegacyRecoveryStatus Status, string? ErrorCode = null,
    int CompletedCount = 0, int RolledBackCount = 0);

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

    /// <summary>
    /// Finishes committed imports and completes an interrupted publication. Never throws for an
    /// operation it cannot finish; it reports <see cref="LegacyRecoveryStatus.DecisionRequired"/>.
    /// </summary>
    Task<LegacyRecoveryResult> RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back every uncommitted import and finishes committed ones. The library index and the
    /// legacy folder are never changed, so the next launch starts as if the import was not run.
    /// </summary>
    Task<LegacyRecoveryResult> AbandonPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default);
}
