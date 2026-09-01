using System.IO;
using System.Windows;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>
/// Stages flattened images as normal PNG files for shell drag/drop. Videos are already normal
/// MP4 files, so they are exposed read-only under a queue eviction lease instead of being copied
/// synchronously on the UI thread.
/// </summary>
internal sealed class GalleryDragExportService
{
    internal const string PreferredDropEffectFormat = "Preferred DropEffect";
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(2);

    private readonly CaptureQueue _queue;
    private readonly string _stagingRoot;
    private readonly Func<DateTimeOffset> _clock;

    internal GalleryDragExportService(
        CaptureQueue queue,
        string? stagingRoot = null,
        Func<DateTimeOffset>? clock = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _stagingRoot = stagingRoot ?? Path.Combine(Path.GetTempPath(), "MyCapture", "DragExports");
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    internal string StagingRoot => _stagingRoot;

    internal static string BuildBaseFileName(DateTimeOffset timestamp) =>
        $"MyCapture_{timestamp:yyyyMMdd_HHmmss}.png";

    internal static string BuildBaseFileName(DateTimeOffset timestamp, CaptureMediaKind mediaKind) =>
        $"MyCapture_{timestamp:yyyyMMdd_HHmmss}" +
        (mediaKind == CaptureMediaKind.Video ? ".mp4" : ".png");

    /// <summary>
    /// Returns a shell-ready media path. Images are copied to a unique temporary PNG; videos use
    /// their immutable/current MP4 directly so a large recording cannot stall drag initiation.
    /// Same-second image exports are suffixed <c>-02</c>, <c>-03</c>, and so on.
    /// </summary>
    internal string PrepareExport(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string sourcePath;
        if (record.IsVideo)
        {
            string rendered = _queue.GetFilePath(record, CaptureFileNames.VideoRendered);
            sourcePath = File.Exists(rendered)
                ? rendered
                : _queue.GetFilePath(record, CaptureFileNames.VideoSource);
        }
        else
        {
            sourcePath = _queue.GetFilePath(record, CaptureFileNames.Rendered);
        }
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The gallery media is unavailable.", sourcePath);
        }

        if (record.IsVideo)
        {
            return Path.GetFullPath(sourcePath);
        }

        Directory.CreateDirectory(_stagingRoot);
        CleanupExpiredBestEffort(_clock() - DefaultRetention);

        string baseName = BuildBaseFileName(_clock(), record.MediaKind);
        string stem = Path.GetFileNameWithoutExtension(baseName);
        string extension = Path.GetExtension(baseName);

        for (int sequence = 1; sequence <= 9_999; sequence++)
        {
            string fileName = sequence == 1
                ? baseName
                : $"{stem}-{sequence:00}{extension}";
            string destination = Path.Combine(_stagingRoot, fileName);

            try
            {
                CopyWithoutOverwrite(sourcePath, destination);
                return destination;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Another drag already owns this timestamp/suffix. Continue with the next one.
            }
        }

        throw new IOException("Could not allocate a unique drag-export filename.");
    }

    internal static DataObject CreateFileDropData(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath(filePath) });

        // CFSTR_PREFERREDDROPEFFECT/DROPEFFECT_COPY tells Explorer and the desktop that the queue
        // file must be copied, never moved away from MyCapture's staging area.
        data.SetData(
            PreferredDropEffectFormat,
            new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy), writable: false));
        return data;
    }

    internal DragDropEffects BeginDrag(DependencyObject dragSource, CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(dragSource);
        using IDisposable evictionLease = _queue.AcquireEvictionLease(record.Id);
        string stagedPath = PrepareExport(record);
        DataObject data = CreateFileDropData(stagedPath);
        return DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy);
    }

    internal void CleanupExpiredBestEffort(DateTimeOffset cutoff)
    {
        try
        {
            if (!Directory.Exists(_stagingRoot))
            {
                return;
            }

            // The root is the application's private drag-export directory (or an internal test
            // seam); the fixed allow-list pattern below cannot select an arbitrary user path.
            // codeql[cs/path-injection]
            foreach (string file in Directory.EnumerateFiles(_stagingRoot, "MyCapture_*.*"))
            {
                try
                {
                    string extension = Path.GetExtension(file);
                    if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // A shell drop may still have the file open; retain it for the next cleanup.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort temporary-file housekeeping must never block gallery use.
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CopyWithoutOverwrite(string sourcePath, string destination)
    {
        bool destinationCreated = false;
        try
        {
            using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using FileStream target = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            destinationCreated = true;
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        catch
        {
            if (destinationCreated)
            {
                try
                {
                    File.Delete(destination);
                }
                catch
                {
                    // Preserve the original exception; cleanup is strictly secondary.
                }
            }

            throw;
        }
    }
}
