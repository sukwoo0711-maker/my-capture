using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal sealed record VideoFrameCaptureRequest(string AssetPath, double RequestedTimeMs, VideoEditDocument? Document);

/// <summary>One library transaction shared by both video surfaces, without a save dialog.</summary>
internal sealed class VideoFrameCaptureService
{
    private readonly CaptureQueue _queue;
    private readonly Func<VideoFrameCaptureRequest, CancellationToken, Task<VideoFrameCapturePixels>> _decode;
    private readonly Func<CaptureRecord, BitmapSource, Task<CaptureRecord>> _write;
    private readonly Func<Guid, bool> _isWriting;
    private readonly HashSet<string> _pendingTitles = new(StringComparer.OrdinalIgnoreCase);

    internal VideoFrameCaptureService(CaptureQueue queue, CapturePersistenceService persistence, Func<Guid, bool>? isWriting = null)
        : this(queue, (record, bitmap) => persistence.PersistPendingOriginalAsync(record, bitmap, bitmap), isWriting: isWriting) { }

    internal VideoFrameCaptureService(CaptureQueue queue,
        Func<CaptureRecord, BitmapSource, Task<CaptureRecord>> writer,
        Func<VideoFrameCaptureRequest, CancellationToken, Task<VideoFrameCapturePixels>>? decoder = null,
        Func<Guid, bool>? isWriting = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _write = writer ?? throw new ArgumentNullException(nameof(writer));
        _isWriting = isWriting ?? (_ => false);
        _decode = decoder ?? ((request, token) => StaThreadTask.RunAsync(
            () => VideoFrameRenderPipeline.CaptureSingleFrame(request.AssetPath, request.RequestedTimeMs, request.Document, token),
            "MyCapture frame image decode"));
    }

    internal async Task<CaptureRecord> SaveAsync(Guid sourceId, long expectedRevision, string assetPath,
        double requestedTimeMs, VideoEditDocument? document = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(requestedTimeMs) || requestedTimeMs < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedTimeMs));
        CaptureRecord source = RequireSource(sourceId, expectedRevision);
        string fullPath = Path.GetFullPath(assetPath);
        string kind = string.Equals(fullPath, Path.GetFullPath(_queue.GetFilePath(source, CaptureFileNames.VideoSource)), StringComparison.OrdinalIgnoreCase)
            ? "source"
            : string.Equals(fullPath, Path.GetFullPath(_queue.GetFilePath(source, CaptureFileNames.VideoRendered)), StringComparison.OrdinalIgnoreCase)
                ? "rendered" : throw new InvalidOperationException("The playing asset does not belong to this video.");
        if (document is not null && kind != "source")
            throw new InvalidOperationException("Editor layers can only be composed over the immutable source.");
        string sourceName = source.Title;
        DateTimeOffset requestedAt = DateTimeOffset.Now;
        VideoEditDocument? snapshot = document?.Clone();
        using IDisposable lease = _queue.AcquireEvictionLease(sourceId);
        // Read sharing holds this exact file generation through decode and publication. A render
        // replacement or deletion must wait/fail rather than silently changing the captured asset.
        using var assetLease = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        RequireSource(sourceId, expectedRevision);
        VideoFrameCapturePixels pixels = await _decode(new(fullPath, requestedTimeMs, snapshot), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RequireSource(sourceId, expectedRevision);
        if (!double.IsFinite(pixels.DecodedTimeMs) || pixels.DecodedTimeMs < 0)
            throw new InvalidDataException("The decoder returned an invalid frame timestamp.");
        string name = FileName(sourceName, pixels.DecodedTimeMs);
        string title;
        lock (_pendingTitles)
        {
            title = name;
            int suffix = 2;
            while (_pendingTitles.Contains(title)
                || _queue.Records.Any(record => string.Equals(record.Title, title, StringComparison.OrdinalIgnoreCase)))
                title = $"{Path.GetFileNameWithoutExtension(name)} ({suffix++}).png";
            _pendingTitles.Add(title);
        }
        try
        {
            string sourceCaption = $"{sourceName} · {VideoTimecode.Format(pixels.DecodedTimeMs)}";
            CaptureRecord result = CapturePersistenceService.CreatePendingRecord(pixels.Bitmap, 1, sourceCaption, string.Empty);
            result.Title = title;
            result.ImportedFileName = title;
            result.SourceVideoId = sourceId;
            result.SourceVideoName = sourceName;
            result.SourceVideoContentRevision = expectedRevision;
            result.SourceAssetKind = kind;
            result.SourceAssetTimeMs = pixels.DecodedTimeMs;
            result.SourceAssetRequestedTimeMs = requestedTimeMs;
            result.SourceFrameRequestedAt = requestedAt;
            return await _write(result, pixels.Bitmap);
        }
        finally { lock (_pendingTitles) _pendingTitles.Remove(title); }
    }

    internal bool CanCapture(Guid id, long revision) => _queue.Find(id) is { IsVideo: true } source
        && source.ContentRevision == revision && !_isWriting(id);

    private CaptureRecord RequireSource(Guid id, long revision)
    {
        CaptureRecord? current = _queue.Find(id);
        if (current is null || !current.IsVideo || current.ContentRevision != revision || _isWriting(id))
            throw new InvalidOperationException("The video generation is no longer available.");
        return current;
    }

    internal static string FileName(string videoName, double milliseconds)
    {
        string stem = videoName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? videoName[..^4] : videoName;
        foreach (char invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(stem)) stem = "video";
        TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        string stamp = string.Create(CultureInfo.InvariantCulture, $"{(long)time.TotalHours:00}-{time.Minutes:00}-{time.Seconds:00}.{time.Milliseconds:000}");
        return $"{stem}_{stamp}.png";
    }
}
