using System.IO;
using System.Windows;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>
/// Stages flattened captures as normal files for shell drag/drop without exposing or moving the
/// queue's internal <c>rendered.png</c> file.
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

    /// <summary>
    /// Copies the flattened capture to a unique temporary path. Same-second exports are suffixed
    /// <c>-02</c>, <c>-03</c>, and so on without overwriting another drag.
    /// </summary>
    internal string PrepareExport(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string sourcePath = _queue.GetFilePath(record, CaptureFileNames.Rendered);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The rendered capture is unavailable.", sourcePath);
        }

        Directory.CreateDirectory(_stagingRoot);
        CleanupExpiredBestEffort(_clock() - DefaultRetention);

        string baseName = BuildBaseFileName(_clock());
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

            foreach (string file in Directory.EnumerateFiles(_stagingRoot, "MyCapture_*.png"))
            {
                try
                {
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
