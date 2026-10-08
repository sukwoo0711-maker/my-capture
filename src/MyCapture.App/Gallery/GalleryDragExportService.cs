using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>
/// Stages shell-ready PNG/MP4 copies. Batch disk work runs off the owner thread while eviction
/// leases protect the records; unpublished batches are removed as soon as their gesture ends.
/// </summary>
internal sealed class GalleryDragExportService
{
    internal const string PreferredDropEffectFormat = "Preferred DropEffect";
    internal static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(2);

    private readonly CaptureQueue? _queue;
    private CaptureQueue Queue => _queue ?? throw new InvalidOperationException("This exporter has no capture queue.");
    private readonly string _stagingRoot;
    private readonly Func<DateTimeOffset> _clock;

    internal GalleryDragExportService(
        CaptureQueue queue,
        string? stagingRoot = null,
        Func<DateTimeOffset>? clock = null)
        : this(stagingRoot, clock)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    internal GalleryDragExportService(string? stagingRoot = null, Func<DateTimeOffset>? clock = null)
    {
        _stagingRoot = stagingRoot is null
            ? GalleryExportFiles.Child(GalleryExportFiles.Child(DefaultTemporaryRoot(), "MyCapture"), "DragExports")
            : Path.GetFullPath(stagingRoot);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    // Drop targets open these files from another process. The Store build stages them in its
    // real TempState so a virtualized temp write can never hand Explorer a path it cannot see.
    private static string DefaultTemporaryRoot() =>
        MyCapture.Core.Platform.AppPackage.TryGetDataFolders(out _, out _, out string temporary)
            ? temporary
            : Path.GetTempPath();

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
        if (record.FolderId is not null || !string.IsNullOrWhiteSpace(record.Title))
            return new GalleryFolderExportService(Queue, _stagingRoot, _clock).PrepareSingle(record);
        return StageSource(ResolveSource(record), record.IsVideo, _clock(), CancellationToken.None);
    }

    internal string PrepareExport(BitmapSource image, string? sourceUrl = null)
    {
        byte[] png = ImageCodec.EncodePng(image, sourceUrl);
        return Stage(_clock(), false, null, path => GalleryExportFiles.Write(path, png));
    }

    private string ResolveSource(CaptureRecord record)
    {
        string sourcePath;
        if (record.IsVideo)
        {
            string rendered = Queue.GetFilePath(record, CaptureFileNames.VideoRendered);
            sourcePath = File.Exists(rendered)
                ? rendered
                : Queue.GetFilePath(record, CaptureFileNames.VideoSource);
        }
        else
        {
            sourcePath = Queue.GetFilePath(record, CaptureFileNames.Rendered);
        }
        return sourcePath;
    }

    private string StageSource(string sourcePath, bool isVideo, DateTimeOffset timestamp, CancellationToken cancellationToken,
        bool stageVideo = false, string? uniqueSuffix = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The gallery media is unavailable.", sourcePath);
        }

        if (isVideo && !stageVideo)
        {
            GalleryExportFiles.ValidateSource(sourcePath);
            return Path.GetFullPath(sourcePath);
        }

        return Stage(timestamp, isVideo, uniqueSuffix,
            path => GalleryExportFiles.Copy(sourcePath, path, cancellationToken));
    }

    private string Stage(DateTimeOffset timestamp, bool isVideo, string? uniqueSuffix, Action<string> write)
    {
        if (uniqueSuffix is null) CleanupExpiredBestEffort(_clock() - DefaultRetention);

        string baseName = BuildBaseFileName(timestamp, isVideo ? CaptureMediaKind.Video : CaptureMediaKind.Image);
        string stem = Path.GetFileNameWithoutExtension(baseName) + uniqueSuffix;
        string extension = Path.GetExtension(baseName);
        baseName = stem + extension;

        for (int sequence = 1; sequence <= 9_999; sequence++)
        {
            string fileName = sequence == 1
                ? baseName
                : $"{stem}-{sequence:00}{extension}";
            string destination = GalleryExportFiles.Child(_stagingRoot, fileName);

            try
            {
                write(destination);
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
        return CreateFileDropData([filePath]);
    }

    internal static DataObject CreateFileDropData(IReadOnlyList<string> filePaths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, filePaths.Select(Path.GetFullPath).ToArray());

        // CFSTR_PREFERREDDROPEFFECT/DROPEFFECT_COPY tells Explorer and the desktop that the queue
        // file must be copied, never moved away from MyCapture's staging area.
        data.SetData(
            PreferredDropEffectFormat,
            new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy), writable: false));
        return data;
    }

    /// <summary>Resolve immutable paths and acquire leases on the owner before disk-only staging.</summary>
    internal async Task<PreparedDrag> PrepareBatchAsync(IReadOnlyList<CaptureRecord> records, CancellationToken cancellationToken)
    {
        if (records.Any(record => record.FolderId is not null || !string.IsNullOrWhiteSpace(record.Title)))
        {
            var exporter = new GalleryFolderExportService(Queue, _stagingRoot, _clock)
                { StagedFileForTest = StagedFileForTest };
            var prepared = await exporter.PrepareAsync(exporter.BuildCapturePlan(records), cancellationToken);
            return new PreparedDrag(prepared.Paths.ToArray(), [], prepared.MarkPublished, prepared.Dispose);
        }

        var leases = new List<IDisposable>();
        var paths = new List<string>();
        try
        {
            var plans = records.Select(record =>
            {
                if (Queue.Find(record.Id) != record) throw new InvalidOperationException("Capture is no longer available.");
                leases.Add(Queue.AcquireEvictionLease(record.Id));
                return (Source: ResolveSource(record), Video: record.IsVideo, Timestamp: _clock());
            }).ToArray();
            string batchId = Guid.NewGuid().ToString("N");
            DateTimeOffset cutoff = _clock() - DefaultRetention;
            await Task.Run(() =>
            {
                CleanupExpiredBestEffort(cutoff);
                for (int index = 0; index < plans.Length; index++)
                {
                    var plan = plans[index];
                    paths.Add(StageSource(plan.Source, plan.Video, plan.Timestamp,
                        cancellationToken, stageVideo: true, uniqueSuffix: $"-{batchId}-{index + 1}"));
                    StagedFileForTest?.Invoke(paths.Count);
                }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new PreparedDrag(paths.ToArray(), leases);
        }
        catch
        {
            await Task.Run(() => { foreach (string path in paths) GalleryExportFiles.DeleteBestEffort(path); });
            foreach (IDisposable lease in leases) lease.Dispose();
            throw;
        }
    }

    internal Action<int>? StagedFileForTest { get; set; }

    internal sealed class PreparedDrag(string[] paths, List<IDisposable> leases,
        Action? onPublish = null, Action? onDispose = null) : IDisposable
    {
        private bool _published;
        internal IReadOnlyList<string> Paths { get; } = paths;
        internal void MarkPublished() { _published = true; onPublish?.Invoke(); }
        public void Dispose()
        {
            if (onDispose is not null) onDispose();
            else if (!_published) foreach (string path in paths) GalleryExportFiles.DeleteBestEffort(path);
            foreach (IDisposable lease in leases) lease.Dispose();
            leases.Clear();
        }
    }

    internal DragDropEffects BeginDrag(DependencyObject dragSource, CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(dragSource);
        using IDisposable evictionLease = Queue.AcquireEvictionLease(record.Id);
        string stagedPath = PrepareExport(record);
        DataObject data = CreateFileDropData(stagedPath);
        return DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy);
    }

    internal void CleanupExpiredBestEffort(DateTimeOffset cutoff) => GalleryExportFiles.Cleanup(_stagingRoot, cutoff);
}
