using System.Security.Cryptography;

namespace MyCapture.Core.Queue;

public readonly record struct CaptureImportProgress(int Completed, int Total, string FileName);
public sealed record CaptureImportResult(int Imported, int Duplicates, int Failed, int TooLarge, bool Cancelled);

/// <summary>Copy-only import. Call on the queue owner; all payload I/O runs on workers.</summary>
public sealed class CaptureImportService(CaptureQueue queue,
    Func<CaptureRecord, string, CancellationToken, Task> prepareMedia,
    Func<string, Task>? recycleIncomplete = null)
{
    public const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal Func<CaptureRecord, Task>? BeforeDuplicateReadForTest { get; set; }

    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".mp4";

    public static string OriginalFileName(CaptureRecord record) => record.IsVideo ? CaptureFileNames.VideoSource
        : Path.GetExtension(record.ImportedFileName ?? string.Empty).ToLowerInvariant() is ".jpg" or ".jpeg"
            ? CaptureFileNames.ImportedJpeg : CaptureFileNames.Original;

    public async Task<CaptureImportResult> ImportAsync(IReadOnlyList<string> paths, Guid? folderId = null,
        IProgress<CaptureImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        int imported = 0, duplicates = 0, failed = 0, tooLarge = 0;
        bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            entered = true;
            for (int i = 0; i < paths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = paths[i];
                string? incompleteDirectory = null;
                progress?.Report(new(i, paths.Count, Path.GetFileName(path)));
                try
                {
                    if (!IsSupported(path)) { failed++; continue; }
                    // One open handle denies writes/deletion until hash and copy both finish.
                    await using FileStream source = await Task.Run(() => new FileStream(path, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan),
                        cancellationToken);
                    if (source.Length > MaximumFileBytes) { failed++; tooLarge++; continue; }
                    string hash = await Task.Run(() => HashAsync(source, cancellationToken), cancellationToken);
                    if (await IsDuplicateAsync(hash, source.Length, cancellationToken)) { duplicates++; continue; }
                    var now = DateTimeOffset.Now; // Imported media enters retention at import time, not EXIF/file time.
                    var record = new CaptureRecord
                    {
                        CreatedAt = now, UpdatedAt = now, Title = Path.GetFileNameWithoutExtension(path),
                        ImportedFileName = Path.GetFileName(path), OriginalContentHash = hash, FolderId = folderId,
                        MediaKind = Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                            ? CaptureMediaKind.Video : CaptureMediaKind.Image,
                    };
                    record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, now);
                    string directory = await Task.Run(() => queue.GetDirectory(record), cancellationToken);
                    incompleteDirectory = directory;
                    source.Position = 0;
                    await Task.Run(async () =>
                    {
                        Directory.CreateDirectory(directory);
                        await using var target = new FileStream(Path.Combine(directory, OriginalFileName(record)),
                            FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                        await source.CopyToAsync(target, 128 * 1024, cancellationToken);
                        await target.FlushAsync(cancellationToken);
                        target.Flush(flushToDisk: true);
                    }, cancellationToken);
                    await prepareMedia(record, directory, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    record.TotalBytes = await Task.Run(() => Directory.EnumerateFiles(directory)
                        .Sum(file => new FileInfo(file).Length), cancellationToken);
                    await queue.AddImportedAsync(record, cancellationToken);
                    incompleteDirectory = null;
                    imported++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (ImportRollbackException)
                {
                    // A durable snapshot may still reference the record. Preserve its files
                    // until recovery can resolve publication, even if the recycler is available.
                    incompleteDirectory = null;
                    failed++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                    or InvalidOperationException or NotSupportedException or FormatException or AggregateException
                    or System.Runtime.InteropServices.COMException)
                { failed++; }
                finally
                {
                    // Only the newly allocated managed directory is eligible. The source is
                    // never removed; unavailable recycling preserves the incomplete copy.
                    if (incompleteDirectory is not null && recycleIncomplete is not null)
                        await recycleIncomplete(incompleteDirectory);
                    progress?.Report(new(i + 1, paths.Count, Path.GetFileName(path)));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(imported, duplicates, failed, tooLarge, true); }
        finally { if (entered) _gate.Release(); }
        return new(imported, duplicates, failed, tooLarge, false);
    }

    private async Task<bool> IsDuplicateAsync(string hash, long length, CancellationToken cancellationToken)
    {
        // Snapshot on the owner. One size-matched original at a time, 128 KiB buffers; no startup scan.
        foreach (CaptureRecord record in queue.Records.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.OriginalContentHash is null)
            {
                string? computed = await Task.Run(async () =>
                {
                    if (BeforeDuplicateReadForTest is not null) await BeforeDuplicateReadForTest(record);
                    try
                    {
                        string original = queue.GetFilePath(record, OriginalFileName(record));
                        await using var stream = new FileStream(original, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        return stream.Length == length ? await HashAsync(stream, cancellationToken) : null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
                }, cancellationToken);
                if (computed is not null && queue.Find(record.Id) == record && !queue.IsRemovalPending(record.Id))
                {
                    using CaptureWriteReservation reservation = await queue.ReservePublicationAsync(cancellationToken);
                    if (queue.Find(record.Id) != record || queue.IsRemovalPending(record.Id)) continue;
                    using IDisposable lease = queue.AcquireEvictionLease(record.Id);
                    record.OriginalContentHash = computed;
                    await queue.PublishRecordAsync(record, reservation);
                }
            }
            if (string.Equals(record.OriginalContentHash, hash, StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(queue.Find(record.Id), record) && !queue.IsRemovalPending(record.Id)) return true;
        }
        return false;
    }

    private static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
}
