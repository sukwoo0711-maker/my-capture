using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Threading;
using MyCapture.Core.Diagnostics;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

internal sealed record VideoLibraryItem(
    CaptureRecord Record,
    RecordingResult Recording,
    VideoEditDocument EditDocument);

/// <summary>
/// Makes recordings first-class queue records. The source MP4 is immutable; trim/text edits
/// replace only rendered.mp4, video-edits.json and the thumbnail under a recoverable journal.
/// </summary>
internal sealed class VideoLibraryService
{
    private const string FinalizeJournal = ".video-finalize-journal.json";
    private const string FinalizeMarker = ".video-finalize-commit-ready";
    private const string RenderBackup = ".rendered-video-backup.mp4";
    private const string ThumbnailBackup = ".video-thumbnail-backup.jpg";
    private const string EditsBackup = ".video-edits-backup.json";
    private const string PublishReady = ".video-publish-ready.json";
    private const int ThumbnailLongEdge = 640;

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Readable;

    private readonly CaptureQueue _queue;
    private readonly AppPaths _paths;
    private readonly ILogger<VideoLibraryService> _log;
    private readonly Func<string, VideoMediaInfo>? _abandonedCaptureProbeForTest;
    private readonly HashSet<Guid> _busy = [];
    private readonly HashSet<Guid> _openEdits = [];
    private readonly HashSet<Guid> _blocked = [];
    private readonly Dictionary<Guid, IDisposable> _blockedLeases = [];

    internal VideoLibraryService(
        CaptureQueue queue,
        AppPaths paths,
        ILogger<VideoLibraryService> log,
        Func<string, VideoMediaInfo>? abandonedCaptureProbeForTest = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _abandonedCaptureProbeForTest = abandonedCaptureProbeForTest;

        RecoverAbandonedCaptureWrites();
        RecoverInterruptedCapturePublications();
        RecoverInterruptedFinalizes();
        RepairCompletedPendingCaptures();
    }

    internal event EventHandler<CaptureRecord>? VideoAdded;

    internal event EventHandler<CaptureRecord>? VideoUpdated;

    internal Action<string>? BeforeFinalizeCleanupForTest { get; set; }

    internal Action<string>? AfterFinalizePayloadSwapForTest { get; set; }

    internal bool IsBusy(Guid recordId) =>
        _busy.Contains(recordId) || _openEdits.Contains(recordId) || _blocked.Contains(recordId);

    internal VideoEditSession BeginEdit(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsVideo)
        {
            throw new ArgumentException("The record is not a video.", nameof(record));
        }

        if (_blocked.Contains(record.Id) || _busy.Contains(record.Id) || !_openEdits.Add(record.Id))
        {
            throw new InvalidOperationException("This video is already being edited or requires recovery.");
        }

        try
        {
            return new VideoEditSession(
                this,
                record.Id,
                record.ContentRevision,
                _queue.AcquireEvictionLease(record.Id));
        }
        catch
        {
            _openEdits.Remove(record.Id);
            throw;
        }
    }

    internal VideoCaptureWriteSession BeginCapture(int expectedFrameRate = 30)
    {
        if (expectedFrameRate is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedFrameRate),
                "The expected recording frame rate must be between 1 and 120.");
        }

        DateTimeOffset now = DateTimeOffset.Now;
        var record = new CaptureRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            MediaKind = CaptureMediaKind.Video,
            Width = 1,
            Height = 1,
            DurationMs = 1,
            FrameRate = expectedFrameRate,
            DpiScale = 1,
            SourceWindowTitle = "화면 녹화",
            Title = "화면 녹화",
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);

        string directory = _queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(
            Path.Combine(directory, CaptureFileNames.VideoPending),
            JsonSerializer.Serialize(record, JsonOptions));

        return new VideoCaptureWriteSession(
            this,
            record,
            Path.Combine(directory, CaptureFileNames.VideoWriting));
    }

    internal async Task<VideoLibraryItem> CompleteCaptureAsync(
        VideoCaptureWriteSession session,
        RecordingResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(result);
        session.BeginCompletion(this);

        CaptureRecord record = session.Record;
        if (!_busy.Add(record.Id))
        {
            throw new InvalidOperationException("This recording is already being finalized.");
        }

        try
        {
            string directory = _queue.GetDirectory(record);
            string expectedStage = Path.GetFullPath(session.StagingOutputPath);
            if (!string.Equals(Path.GetFullPath(result.OutputPath), expectedStage, StringComparison.Ordinal)
                || !File.Exists(expectedStage))
            {
                throw new InvalidDataException("The encoder did not complete the expected private recording file.");
            }

            string sourcePath = Path.Combine(directory, CaptureFileNames.VideoSource);
            if (File.Exists(sourcePath))
            {
                throw new IOException("A source recording already exists for this queue record.");
            }

            record.Width = Math.Max(1, result.Width);
            record.Height = Math.Max(1, result.Height);
            record.DurationMs = Math.Max(1, result.DurationMs);
            record.FrameRate = Math.Max(1, result.Fps);
            record.FrameCount = Math.Max(1, result.EmittedFrames);
            record.UpdatedAt = DateTimeOffset.Now;

            // A path and plausible metadata do not prove that the encoder completed a playable
            // MP4. Decode the first and final presentation samples before creating the durable
            // publish-ready boundary.
            VideoMediaInfo playable = await ValidateCompletedRenderAsync(
                expectedStage,
                record,
                record.DurationMs,
                cancellationToken,
                allowShorterThanExpected: true);
            // RegionRecorder's wall clock includes Media Foundation startup and a capture/encode
            // call already in flight when Stop is requested. The decoded container timeline is
            // the duration users can actually play, trim and export, so persist that canonical
            // value instead of rejecting a valid short clip on a slow machine.
            record.DurationMs = playable.DurationMs;

            // Persist the completed encoder result before publishing source.mp4. The publish-ready
            // sidecar is the durable phase boundary: startup can finish the same-directory rename
            // with exact dimensions/timing even if the process stops between these two operations.
            AtomicFile.WriteAllText(
                Path.Combine(directory, PublishReady),
                JsonSerializer.Serialize(record, JsonOptions));

            // Same-directory rename is the publication boundary: a partial encoder file is never
            // named source.mp4, and the immutable source is never changed by later edits.
            File.Move(expectedStage, sourcePath);

            AtomicFile.WriteAllText(
                Path.Combine(directory, CaptureFileNames.VideoPending),
                JsonSerializer.Serialize(record, JsonOptions));

            VideoEditDocument document = VideoEditDocument.CreateFor(
                record.Width,
                record.Height,
                record.DurationMs);
            string editsJson = JsonSerializer.Serialize(document, JsonOptions);
            AtomicFile.WriteAllText(Path.Combine(directory, CaptureFileNames.VideoEdits), editsJson);

            BitmapSource thumbnail = await CreateThumbnailAsync(
                sourcePath,
                record.Width,
                record.Height,
                Math.Min(100, Math.Max(0, record.DurationMs / 2)),
                cancellationToken,
                allowFallback: false);
            _ = ImageCodec.SaveJpeg(
                thumbnail,
                Path.Combine(directory, CaptureFileNames.Thumbnail),
                ImageCodec.ThumbnailJpegQuality);

            record.TotalBytes = MeasureMediaBytes(directory);
            _queue.SaveRecordMetaOrThrow(record);
            _queue.Add(record);
            _queue.Save();
            if (!CleanupPublishedCaptureSidecars(directory))
            {
                _log.LogWarning(
                    "Video publication cleanup remains incomplete for {Id}; the record is " +
                    "blocked until startup recovery retries it",
                    record.Id);
                BlockRecord(record);
            }

            session.MarkCompleted(this);
            RaiseLibraryEventBestEffort(VideoAdded, record, "added");
            _log.LogInformation(
                "Persisted video {Id} ({Width}x{Height}, {Duration:0}ms, {Bytes} bytes)",
                record.Id,
                record.Width,
                record.Height,
                record.DurationMs,
                record.TotalBytes);
            return new VideoLibraryItem(record, ToRecordingResult(record, document), document);
        }
        finally
        {
            _busy.Remove(record.Id);
        }
    }

    internal VideoLibraryItem Load(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsVideo)
        {
            throw new ArgumentException("The record is not a video.", nameof(record));
        }

        string source = SourcePath(record);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The original recording is unavailable.", source);
        }

        VideoEditDocument document = LoadDocument(record);
        return new VideoLibraryItem(record, ToRecordingResult(record, document), document);
    }

    internal string CreateRenderStagingPath(CaptureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Path.Combine(
            _queue.GetDirectory(record),
            $".render-{Guid.NewGuid():N}.mp4");
    }

    internal string SourcePath(CaptureRecord record) =>
        _queue.GetFilePath(record, CaptureFileNames.VideoSource);

    internal string CurrentVideoPath(CaptureRecord record)
    {
        string rendered = _queue.GetFilePath(record, CaptureFileNames.VideoRendered);
        return File.Exists(rendered) ? rendered : SourcePath(record);
    }

    internal async Task CommitEditAsync(
        CaptureRecord record,
        VideoEditSession editSession,
        VideoEditDocument editDocument,
        string completedStagedVideo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(editSession);
        ArgumentNullException.ThrowIfNull(editDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(completedStagedVideo);
        if (!record.IsVideo)
        {
            throw new ArgumentException("The record is not a video.", nameof(record));
        }

        editSession.VerifyOwner(this, record);
        if (_blocked.Contains(record.Id)
            || !ReferenceEquals(_queue.Find(record.Id), record)
            || record.ContentRevision != editSession.ExpectedContentRevision)
        {
            throw new CaptureGenerationConflictException(
                $"Video {record.Id} changed from revision {editSession.ExpectedContentRevision} " +
                $"to {record.ContentRevision} while it was being edited.");
        }

        string directory = _queue.GetDirectory(record);
        string stagePath = ValidateRenderStagePath(directory, completedStagedVideo);
        if (!File.Exists(stagePath))
        {
            throw new FileNotFoundException("The completed video render is unavailable.", stagePath);
        }

        if (!_busy.Add(record.Id))
        {
            throw new InvalidOperationException("This video is already being updated.");
        }

        using IDisposable evictionLease = _queue.AcquireEvictionLease(record.Id);
        try
        {
            EnsureNoUnresolvedFinalizeArtifacts(record, directory, Path.GetFileName(stagePath));
            VideoEditDocument normalized = editDocument.NormalizeFor(
                record.Width,
                record.Height,
                editDocument.SourceDurationMs);
            cancellationToken.ThrowIfCancellationRequested();

            _ = await ValidateCompletedRenderAsync(
                stagePath,
                record,
                normalized.TrimOutMs - normalized.TrimInMs,
                cancellationToken);
            BitmapSource thumbnail = await CreateThumbnailAsync(
                stagePath,
                record.Width,
                record.Height,
                sourceTimeMs: 0,
                cancellationToken,
                allowFallback: false);

            string token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            string thumbnailStage = Path.Combine(directory, $".thumb-{token}.next.jpg");
            string editsStage = Path.Combine(directory, $".edits-{token}.next.json");
            _ = ImageCodec.SaveJpeg(thumbnail, thumbnailStage, ImageCodec.ThumbnailJpegQuality);
            AtomicFile.WriteAllText(editsStage, JsonSerializer.Serialize(normalized, JsonOptions));

            var previous = VideoRecordSnapshot.From(record);
            var next = VideoRecordSnapshot.CreateCommitted(
                record,
                Math.Max(1, normalized.TrimOutMs - normalized.TrimInMs),
                normalized.HasEdits,
                MeasureProspectiveMediaBytes(directory, stagePath, thumbnailStage, editsStage),
                DateTimeOffset.Now);
            var journal = new VideoFinalizeJournal
            {
                RecordId = record.Id,
                ExpectedContentRevision = record.ContentRevision,
                RenderStageFile = Path.GetFileName(stagePath),
                ThumbnailStageFile = Path.GetFileName(thumbnailStage),
                EditsStageFile = Path.GetFileName(editsStage),
                HadRendered = File.Exists(Path.Combine(directory, CaptureFileNames.VideoRendered)),
                HadThumbnail = File.Exists(Path.Combine(directory, CaptureFileNames.Thumbnail)),
                HadEdits = File.Exists(Path.Combine(directory, CaptureFileNames.VideoEdits)),
                Previous = previous,
                Next = next,
            };
            AtomicFile.WriteAllText(
                Path.Combine(directory, FinalizeJournal),
                JsonSerializer.Serialize(journal, JsonOptions));

            try
            {
                SwapStage(
                    stagePath,
                    Path.Combine(directory, CaptureFileNames.VideoRendered),
                    Path.Combine(directory, RenderBackup));
                SwapStage(
                    thumbnailStage,
                    Path.Combine(directory, CaptureFileNames.Thumbnail),
                    Path.Combine(directory, ThumbnailBackup));
                SwapStage(
                    editsStage,
                    Path.Combine(directory, CaptureFileNames.VideoEdits),
                    Path.Combine(directory, EditsBackup));

                AfterFinalizePayloadSwapForTest?.Invoke(directory);
                ApplySnapshotToQueue(record, next);
                _queue.SaveRecordMetaOrThrow(record);
                _queue.Save();

                AtomicFile.WriteAllText(Path.Combine(directory, FinalizeMarker), "ready");
                BeforeFinalizeCleanupForTest?.Invoke(directory);
                if (!CleanupFinalizeFiles(directory, journal))
                {
                    _log.LogWarning(
                        "Video finalize cleanup remains incomplete for {Id}; the committed " +
                        "generation is blocked until startup recovery retries it",
                        record.Id);
                    BlockRecord(record);
                }
                editSession.Advance(this, record.ContentRevision);
            }
            catch
            {
                if (PathExists(Path.Combine(directory, FinalizeMarker)))
                {
                    // Once the marker exists, the new generation is the durable commit decision.
                    // Retain the journal and block rather than turning a post-commit cleanup
                    // failure into a rollback of files whose metadata may already be persisted.
                    BlockRecord(record);
                }
                else
                {
                    _ = RollBackFinalize(directory, journal, record);
                }
                throw;
            }

            RaiseLibraryEventBestEffort(VideoUpdated, record, "updated");
        }
        finally
        {
            _busy.Remove(record.Id);
        }
    }

    internal void AbortCapture(VideoCaptureWriteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string directory = _queue.GetDirectory(session.Record);
        string source = Path.Combine(directory, CaptureFileNames.VideoSource);
        if (File.Exists(source) || File.Exists(Path.Combine(directory, PublishReady)))
        {
            // A published source or durable publish-ready phase means completed user data exists.
            // Leave the encoder file and recovery sidecars for startup rather than deleting it.
            return;
        }

        // Keep the discovery sidecar if a sharing/ACL failure prevents payload deletion. Startup
        // can then retry instead of leaking an undiscoverable private recording indefinitely.
        if (DeleteVerified(session.StagingOutputPath)
            && CleanupPendingCaptureSidecars(
                Path.Combine(directory, CaptureFileNames.VideoPending)))
        {
            TryDeleteEmptyDirectory(directory);
        }
    }

    private VideoEditDocument LoadDocument(CaptureRecord record)
    {
        string path = _queue.GetFilePath(record, CaptureFileNames.VideoEdits);
        VideoEditDocument? document = null;
        try
        {
            if (File.Exists(path))
            {
                document = JsonSerializer.Deserialize<VideoEditDocument>(File.ReadAllText(path), JsonOptions);
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Video edit document is unreadable for {Id}", record.Id);
        }

        double sourceDuration = document?.SourceDurationMs > 0
            ? document.SourceDurationMs
            : Math.Max(1, record.DurationMs);
        return (document ?? VideoEditDocument.CreateFor(record.Width, record.Height, sourceDuration))
            .NormalizeFor(record.Width, record.Height, sourceDuration);
    }

    private RecordingResult ToRecordingResult(CaptureRecord record, VideoEditDocument document) => new(
        SourcePath(record),
        document.SourceDurationMs,
        Math.Max(1, record.FrameRate),
        Math.Max(1, record.FrameCount),
        record.Width,
        record.Height);

    private async Task<BitmapSource> CreateThumbnailAsync(
        string sourcePath,
        int sourceWidth,
        int sourceHeight,
        double sourceTimeMs,
        CancellationToken cancellationToken,
        bool allowFallback = true)
    {
        (int width, int height) = FitWithin(sourceWidth, sourceHeight, ThumbnailLongEdge);
        try
        {
            return await StaThreadTask.RunAsync(
                () => VideoFrameRenderPipeline.RenderSingleFrame(
                    sourcePath,
                    sourceTimeMs,
                    width,
                    height,
                    cancellationToken: cancellationToken),
                "MyCapture video thumbnail");
        }
        catch (Exception ex) when (allowFallback && ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not decode a video thumbnail from {Path}", LogText.SingleLine(sourcePath));
            return CreateFallbackThumbnail(width, height);
        }
    }

    private static async Task<VideoMediaInfo> ValidateCompletedRenderAsync(
        string stagePath,
        CaptureRecord record,
        double expectedDurationMs,
        CancellationToken cancellationToken,
        bool allowShorterThanExpected = false)
    {
        if (new FileInfo(stagePath).Length == 0)
        {
            throw new InvalidDataException("The completed video render is empty.");
        }

        if (!HasIsoBaseMediaFileSignature(stagePath))
        {
            throw new InvalidDataException("The completed video render has no MP4 file signature.");
        }

        VideoMediaInfo info;
        try
        {
            info = await StaThreadTask.RunAsync(
                () => VideoFrameRenderPipeline.Probe(stagePath, cancellationToken),
                "MyCapture video validation");
        }
        catch (Exception ex) when (ex is IOException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or ArgumentException
                                   or TimeoutException
                                   or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException(
                "The completed video render could not be decoded from beginning to end.",
                ex);
        }
        if (info.Width != record.Width || info.Height != record.Height)
        {
            throw new InvalidDataException(
                $"The rendered video size {info.Width}x{info.Height} does not match " +
                $"the source {record.Width}x{record.Height}.");
        }

        if (!double.IsFinite(info.DurationMs) || info.DurationMs <= 0)
        {
            throw new InvalidDataException("The rendered video has no playable duration.");
        }

        double durationTolerance = Math.Max(250, 2000.0 / Math.Max(1, record.FrameRate));
        bool durationMismatch = allowShorterThanExpected
            ? info.DurationMs - expectedDurationMs > durationTolerance
            : Math.Abs(info.DurationMs - expectedDurationMs) > durationTolerance;
        if (!double.IsFinite(expectedDurationMs)
            || expectedDurationMs <= 0
            || durationMismatch)
        {
            throw new InvalidDataException(
                $"The rendered video duration {info.DurationMs:0}ms does not match " +
                $"the requested {expectedDurationMs:0}ms interval.");
        }

        return info;
    }

    /// <summary>
    /// Repairs the crash window between encoder completion and the publish-ready sidecar. A
    /// decodable private MP4 is promoted through the normal durable publication path. Only an
    /// artifact that is conclusively empty/truncated or rejected as invalid media is discarded;
    /// decoder/platform failures retain both payload and discovery sidecar for a later startup.
    /// </summary>
    private void RecoverAbandonedCaptureWrites()
    {
        if (!Directory.Exists(_paths.CapturesRoot))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerable<string> pendingFiles;
        try
        {
            pendingFiles = Directory.EnumerateFiles(
                    _paths.CapturesRoot,
                    CaptureFileNames.VideoPending,
                    options)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not enumerate abandoned recording sidecars");
            return;
        }

        foreach (string pending in pendingFiles)
        {
            string? directory = Path.GetDirectoryName(pending);
            if (directory is null)
            {
                continue;
            }

            string source = Path.Combine(directory, CaptureFileNames.VideoSource);
            string writing = Path.Combine(directory, CaptureFileNames.VideoWriting);
            string publishReady = Path.Combine(directory, PublishReady);
            if (File.Exists(source) || File.Exists(publishReady))
            {
                continue;
            }

            if (!PathExists(writing))
            {
                // No user media exists to recover. This is the crash window immediately after
                // BeginCapture and is safe to clean because only MyCapture's private sidecar is
                // addressed.
                if (CleanupPendingCaptureSidecars(pending))
                {
                    TryDeleteEmptyDirectory(directory);
                }
                continue;
            }

            CaptureRecord? recovered = null;
            try
            {
                if (new FileInfo(writing).Length == 0 || !HasIsoBaseMediaFileSignature(writing))
                {
                    _log.LogWarning(
                        "Discarding empty or non-MP4 abandoned encoder output {Path}",
                        LogText.SingleLine(writing));
                    if (CleanupCorruptAbandonedCapture(writing, pending))
                    {
                        TryDeleteEmptyDirectory(directory);
                    }
                    continue;
                }

                string? serialized = AtomicFile.ReadAllTextWithRecovery(
                    pending,
                    IsValidPendingCaptureJson);
                recovered = serialized is null
                    ? null
                    : JsonSerializer.Deserialize<CaptureRecord>(serialized, JsonOptions);
                if (recovered is null || recovered.Id == Guid.Empty || !recovered.IsVideo)
                {
                    _log.LogWarning(
                        "Could not validate abandoned recording sidecar; preserving encoder " +
                        "output for a later recovery: {Path}",
                        LogText.SingleLine(writing));
                    continue;
                }

                string relative = Path.GetRelativePath(_paths.CapturesRoot, directory);
                if (Path.IsPathRooted(relative)
                    || relative.Equals("..", StringComparison.Ordinal)
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    _log.LogWarning(
                        "Rejected abandoned recording outside the capture root; preserving {Path}",
                        LogText.SingleLine(writing));
                    BlockRecordId(recovered.Id);
                    continue;
                }

                VideoMediaInfo mediaInfo = _abandonedCaptureProbeForTest is null
                    ? StaThreadTask.RunAsync(
                            () => VideoFrameRenderPipeline.Probe(writing, CancellationToken.None),
                            "MyCapture abandoned video recovery")
                        .GetAwaiter()
                        .GetResult()
                    : _abandonedCaptureProbeForTest(writing);
                int framesPerSecond = recovered.FrameRate is >= 1 and <= 120
                    ? recovered.FrameRate
                    : 30;
                double estimatedFrames = mediaInfo.DurationMs * framesPerSecond / 1000.0;
                if (!double.IsFinite(estimatedFrames) || estimatedFrames > long.MaxValue)
                {
                    throw new InvalidDataException("The abandoned recording duration is invalid.");
                }

                recovered.RelativeDirectory = relative;
                recovered.Width = mediaInfo.Width;
                recovered.Height = mediaInfo.Height;
                recovered.DurationMs = mediaInfo.DurationMs;
                recovered.FrameRate = framesPerSecond;
                recovered.FrameCount = Math.Max(1, (long)Math.Ceiling(estimatedFrames));
                recovered.UpdatedAt = DateTimeOffset.Now;
                AtomicFile.WriteAllText(
                    publishReady,
                    JsonSerializer.Serialize(recovered, JsonOptions));
                _log.LogInformation(
                    "Recovered completed private encoder output for video {Id}",
                    recovered.Id);
            }
            catch (InvalidDataException ex)
            {
                _log.LogWarning(
                    ex,
                    "Discarding conclusively invalid abandoned encoder output {Path}",
                    LogText.SingleLine(writing));
                // The sidecar is the only discovery key for this private payload. Never remove
                // it until the large encoder file is confirmed gone; otherwise a sharing/ACL
                // failure would turn the MP4 into a permanent, undiscoverable disk leak.
                if (!CleanupCorruptAbandonedCapture(writing, pending))
                {
                    _log.LogWarning(
                        "Could not remove invalid abandoned encoder output; retaining its " +
                        "recovery sidecar for a later startup: {Path}",
                        LogText.SingleLine(writing));
                    if (recovered is not null)
                    {
                        BlockRecordId(recovered.Id);
                    }
                }
                else
                {
                    TryDeleteEmptyDirectory(directory);
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidOperationException
                                       or NotSupportedException
                                       or ArgumentException
                                       or TimeoutException
                                       or System.Runtime.InteropServices.COMException)
            {
                // A decoder, codec, platform, lock, or transient storage failure is not proof of
                // corrupt media. Keep the private files so a later startup can retry instead of
                // deleting user data.
                _log.LogWarning(
                    ex,
                    "Could not inspect abandoned recording; preserving it for retry: {Path}",
                    LogText.SingleLine(writing));
                if (recovered is not null)
                {
                    BlockRecordId(recovered.Id);
                }
            }
        }
    }

    private void RecoverInterruptedCapturePublications()
    {
        if (!Directory.Exists(_paths.CapturesRoot))
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerable<string> markers;
        try
        {
            markers = Directory.EnumerateFiles(_paths.CapturesRoot, PublishReady, options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not enumerate interrupted video publications");
            return;
        }

        foreach (string marker in markers)
        {
            try
            {
                string? directory = Path.GetDirectoryName(marker);
                CaptureRecord? recovered = JsonSerializer.Deserialize<CaptureRecord>(
                    File.ReadAllText(marker),
                    JsonOptions);
                if (directory is null || recovered is null || !recovered.IsVideo)
                {
                    throw new InvalidDataException("The video publication sidecar is invalid.");
                }

                string relative = Path.GetRelativePath(_paths.CapturesRoot, directory);
                if (Path.IsPathRooted(relative)
                    || relative.Equals("..", StringComparison.Ordinal)
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The recovered video directory is outside the capture root.");
                }

                recovered.RelativeDirectory = relative;
                string source = Path.Combine(directory, CaptureFileNames.VideoSource);
                string writing = Path.Combine(directory, CaptureFileNames.VideoWriting);
                string publicationCandidate = File.Exists(source) ? source : writing;
                if (!File.Exists(publicationCandidate))
                {
                    throw new FileNotFoundException(
                        "The completed encoder file is unavailable for recovery.",
                        publicationCandidate);
                }

                VideoMediaInfo playable = ValidateCompletedRenderAsync(
                        publicationCandidate,
                        recovered,
                        recovered.DurationMs,
                        CancellationToken.None,
                        allowShorterThanExpected: true)
                    .GetAwaiter()
                    .GetResult();
                recovered.DurationMs = playable.DurationMs;
                if (!File.Exists(source))
                {
                    File.Move(writing, source);
                }

                AtomicFile.WriteAllText(
                    Path.Combine(directory, CaptureFileNames.VideoPending),
                    JsonSerializer.Serialize(recovered, JsonOptions));

                CaptureRecord? existing = _queue.Find(recovered.Id);
                if (existing is null)
                {
                    _queue.Add(recovered);
                }
                else
                {
                    ApplyRecoveredCaptureMetadata(existing, recovered);
                }

                _log.LogInformation("Recovered source publication for video {Id}", recovered.Id);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException)
            {
                _log.LogWarning(ex, "Could not recover video publication marker {Path}", marker);
            }
        }
    }

    private void RepairCompletedPendingCaptures()
    {
        bool changed = false;
        var completedDirectories = new List<string>();
        foreach (CaptureRecord record in _queue.Records.Where(item => item.IsVideo).ToList())
        {
            string directory = _queue.GetDirectory(record);
            string pending = Path.Combine(directory, CaptureFileNames.VideoPending);
            string pendingBackup = pending + AtomicFile.BackupSuffix;
            string source = Path.Combine(directory, CaptureFileNames.VideoSource);
            string publishReady = Path.Combine(directory, PublishReady);
            string publishReadyBackup = publishReady + AtomicFile.BackupSuffix;
            bool hasDiscoverySidecar = PathExists(pending)
                                       || PathExists(pendingBackup)
                                       || PathExists(publishReady)
                                       || PathExists(publishReadyBackup);
            if (!hasDiscoverySidecar || !File.Exists(source))
            {
                continue;
            }

            try
            {
                string edits = Path.Combine(directory, CaptureFileNames.VideoEdits);
                if (!File.Exists(edits))
                {
                    AtomicFile.WriteAllText(
                        edits,
                        JsonSerializer.Serialize(
                            VideoEditDocument.CreateFor(record.Width, record.Height, record.DurationMs),
                            JsonOptions));
                }

                string thumbnail = Path.Combine(directory, CaptureFileNames.Thumbnail);
                if (!File.Exists(thumbnail))
                {
                    (int width, int height) = FitWithin(record.Width, record.Height, ThumbnailLongEdge);
                    _ = ImageCodec.SaveJpeg(
                        CreateFallbackThumbnail(width, height),
                        thumbnail,
                        ImageCodec.ThumbnailJpegQuality);
                }

                _queue.UpdateByteCount(record.Id, MeasureMediaBytes(directory));
                _queue.SaveRecordMetaOrThrow(record);
                completedDirectories.Add(directory);
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not repair pending video {Id}", record.Id);
            }
        }

        if (changed)
        {
            // The durable index must name every recovered video before its discovery sidecars are
            // removed. A crash before Save leaves the markers in place and startup retries safely.
            _queue.Save();
            foreach (string directory in completedDirectories)
            {
                CaptureRecord? record = _queue.Records.FirstOrDefault(
                    candidate => string.Equals(
                        _queue.GetDirectory(candidate),
                        directory,
                        StringComparison.Ordinal));
                if (!CleanupPublishedCaptureSidecars(directory) && record is not null)
                {
                    _log.LogWarning(
                        "Recovered video publication cleanup remains incomplete for {Id}",
                        record.Id);
                    BlockRecord(record);
                }
            }
        }
    }

    private static void ApplyRecoveredCaptureMetadata(CaptureRecord target, CaptureRecord recovered)
    {
        target.UpdatedAt = recovered.UpdatedAt;
        target.MediaKind = CaptureMediaKind.Video;
        target.Width = recovered.Width;
        target.Height = recovered.Height;
        target.DurationMs = recovered.DurationMs;
        target.FrameRate = recovered.FrameRate;
        target.FrameCount = recovered.FrameCount;
        target.DpiScale = recovered.DpiScale;
        target.SourceMonitor = recovered.SourceMonitor;
        target.SourceWindowTitle = recovered.SourceWindowTitle;
        target.Title = recovered.Title;
        target.RelativeDirectory = recovered.RelativeDirectory;
    }

    private void RecoverInterruptedFinalizes()
    {
        foreach (CaptureRecord record in _queue.Records.Where(item => item.IsVideo).ToList())
        {
            string directory = _queue.GetDirectory(record);
            string journalPath = Path.Combine(directory, FinalizeJournal);
            string journalBackupPath = journalPath + AtomicFile.BackupSuffix;
            string markerPath = Path.Combine(directory, FinalizeMarker);
            string markerBackupPath = markerPath + AtomicFile.BackupSuffix;
            bool hasJournal = PathExists(journalPath) || PathExists(journalBackupPath);
            if (!hasJournal)
            {
                if (HasAmbiguousFinalizeBackups(directory))
                {
                    _log.LogWarning(
                        "Video finalize backups have no recovery journal for {Id}",
                        record.Id);
                    BlockRecord(record);
                    continue;
                }

                // Cleanup deliberately deletes the journal before the commit marker. If a process
                // stops in that safe gap, the remaining marker is only an orphaned success token.
                if (!DeleteVerified(markerBackupPath) || !DeleteVerified(markerPath))
                {
                    _log.LogWarning("Could not remove orphan video finalize marker for {Id}", record.Id);
                    BlockRecord(record);
                    continue;
                }

                if (!CleanupOrphanFinalizeStages(directory))
                {
                    _log.LogWarning("Could not remove orphan video render stages for {Id}", record.Id);
                    BlockRecord(record);
                }
                continue;
            }

            try
            {
                string? serialized = AtomicFile.ReadAllTextWithRecovery(
                    journalPath,
                    IsValidFinalizeJournalJson);
                VideoFinalizeJournal? journal = serialized is null
                    ? null
                    : JsonSerializer.Deserialize<VideoFinalizeJournal>(serialized, JsonOptions);
                if (journal is null
                    || journal.RecordId != record.Id
                    || !journal.IsValidFor(record))
                {
                    _log.LogWarning("Rejected invalid video finalize journal for {Id}", record.Id);
                    BlockRecord(record);
                    continue;
                }

                bool recovered;
                if (PathExists(markerPath) || PathExists(markerBackupPath))
                {
                    recovered = RecoverCommittedFinalize(directory, journal, record);
                }
                else
                {
                    recovered = RollBackFinalize(directory, journal, record);
                }

                if (!recovered)
                {
                    continue;
                }

                if (!CleanupOrphanFinalizeStages(directory))
                {
                    _log.LogWarning("Could not remove orphan video render stages for {Id}", record.Id);
                    BlockRecord(record);
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidDataException)
            {
                _log.LogWarning(ex, "Could not recover video finalize for {Id}", record.Id);
                BlockRecord(record);
            }
        }
    }

    private void BlockRecord(CaptureRecord record)
    {
        BlockRecordId(record.Id);
    }

    private void BlockRecordId(Guid recordId)
    {
        if (_blocked.Add(recordId) && _queue.Find(recordId) is not null)
        {
            _blockedLeases[recordId] = _queue.AcquireEvictionLease(recordId);
        }
    }

    private void RaiseLibraryEventBestEffort(
        EventHandler<CaptureRecord>? handler,
        CaptureRecord record,
        string operation)
    {
        try
        {
            handler?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            // UI refresh subscribers run after the durable queue boundary. Their failure must not
            // make a successfully saved video appear to have failed or trigger capture rollback.
            _log.LogWarning(ex, "Video {Operation} notification failed for {Id}", operation, record.Id);
        }
    }

    internal void EndEdit(Guid recordId) => _openEdits.Remove(recordId);

    private bool RecoverCommittedFinalize(
        string directory,
        VideoFinalizeJournal journal,
        CaptureRecord record)
    {
        VideoRecordSnapshot next = journal.Next
            ?? throw new InvalidDataException("The video finalize journal has no committed record state.");
        string rendered = Path.Combine(directory, CaptureFileNames.VideoRendered);
        string thumbnail = Path.Combine(directory, CaptureFileNames.Thumbnail);
        string edits = Path.Combine(directory, CaptureFileNames.VideoEdits);
        if (!File.Exists(rendered) || !File.Exists(thumbnail) || !File.Exists(edits))
        {
            _log.LogWarning("Committed video payload is incomplete for {Id}", record.Id);
            BlockRecord(record);
            return false;
        }

        long actualBytes = MeasureMediaBytes(directory);
        if (actualBytes != next.TotalBytes)
        {
            _log.LogWarning(
                "Committed video payload size changed for {Id}: expected {Expected}, actual {Actual}",
                record.Id,
                next.TotalBytes,
                actualBytes);
            BlockRecord(record);
            return false;
        }

        ApplySnapshotToQueue(record, next);
        _queue.SaveRecordMetaOrThrow(record);
        _queue.Save();
        if (!CleanupFinalizeFiles(directory, journal))
        {
            _log.LogWarning("Video finalize cleanup is still incomplete for {Id}", record.Id);
            BlockRecord(record);
            return false;
        }

        return true;
    }

    private bool RollBackFinalize(string directory, VideoFinalizeJournal journal, CaptureRecord record)
    {
        bool renderedRestored = RestoreTarget(
            Path.Combine(directory, CaptureFileNames.VideoRendered),
            Path.Combine(directory, RenderBackup),
            journal.HadRendered);
        bool thumbnailRestored = RestoreTarget(
            Path.Combine(directory, CaptureFileNames.Thumbnail),
            Path.Combine(directory, ThumbnailBackup),
            journal.HadThumbnail);
        bool editsRestored = RestoreTarget(
            Path.Combine(directory, CaptureFileNames.VideoEdits),
            Path.Combine(directory, EditsBackup),
            journal.HadEdits);

        if (!renderedRestored || !thumbnailRestored || !editsRestored)
        {
            // The journal is the recovery evidence. In particular, when HadRendered is false,
            // a locked new target must not be left behind while metadata is rolled back and the
            // journal erased. Retain everything and retry after the lock/storage fault clears.
            _log.LogWarning("Video rollback payload restoration is incomplete for {Id}", record.Id);
            BlockRecord(record);
            return false;
        }

        VideoRecordSnapshot previous = journal.Previous
            ?? throw new InvalidDataException("The video finalize journal has no prior record state.");
        ApplySnapshotToQueue(record, previous);
        _queue.SaveRecordMetaOrThrow(record);
        _queue.Save();
        if (!CleanupFinalizeFiles(directory, journal))
        {
            _log.LogWarning("Video rollback cleanup is still incomplete for {Id}", record.Id);
            BlockRecord(record);
            return false;
        }

        return true;
    }

    private static bool CleanupFinalizeFiles(string directory, VideoFinalizeJournal journal)
    {
        // Marker is the commit decision while the journal exists. Remove the journal before the
        // marker. If any stage/backup cannot be removed, retain both decision files so startup
        // can only retry the committed cleanup and can never misclassify it as a rollback.
        bool payloadsRemoved = true;
        foreach (string file in new[]
                 {
                     journal.RenderStageFile!,
                     journal.ThumbnailStageFile!,
                     journal.EditsStageFile!,
                     RenderBackup,
                     ThumbnailBackup,
                     EditsBackup,
                 })
        {
            payloadsRemoved &= DeleteVerified(Path.Combine(directory, file));
        }

        if (!payloadsRemoved)
        {
            return false;
        }

        string journalPath = Path.Combine(directory, FinalizeJournal);
        string markerPath = Path.Combine(directory, FinalizeMarker);
        // Backups cannot be allowed to outlive their primary recovery files: startup could
        // otherwise rediscover stale transaction state after a successful cleanup.
        if (!DeleteVerified(journalPath + AtomicFile.BackupSuffix)
            || !DeleteVerified(markerPath + AtomicFile.BackupSuffix)
            || !DeleteVerified(journalPath))
        {
            return false;
        }

        return DeleteVerified(markerPath);
    }

    private void EnsureNoUnresolvedFinalizeArtifacts(
        CaptureRecord record,
        string directory,
        string allowedRenderStageFile)
    {
        string[] artifacts =
        [
            FinalizeJournal,
            FinalizeJournal + AtomicFile.BackupSuffix,
            FinalizeMarker,
            FinalizeMarker + AtomicFile.BackupSuffix,
            RenderBackup,
            ThumbnailBackup,
            EditsBackup,
        ];
        bool hasFixedArtifact = artifacts.Any(name => PathExists(Path.Combine(directory, name)));
        bool hasUnexpectedStage;
        try
        {
            hasUnexpectedStage = EnumerateFinalizeStageResidues(directory)
                .Any(path => !string.Equals(
                    Path.GetFileName(path),
                    allowedRenderStageFile,
                    StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BlockRecord(record);
            throw new InvalidOperationException(
                "Video recovery files could not be inspected before committing the edit.",
                ex);
        }

        if (!hasFixedArtifact && !hasUnexpectedStage)
        {
            return;
        }

        BlockRecord(record);
        throw new InvalidOperationException(
            "This video has unresolved recovery files and cannot be edited until recovery succeeds.");
    }

    private static void SwapStage(string stage, string target, string backup)
    {
        if (!DeleteVerified(backup))
        {
            throw new IOException("A previous video finalize backup could not be removed.");
        }
        if (!File.Exists(target))
        {
            File.Move(stage, target);
            return;
        }

        try
        {
            File.Replace(stage, target, backup, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(target, backup);
            File.Move(stage, target);
        }
    }

    private static bool RestoreTarget(string target, string backup, bool hadTarget)
    {
        if (!hadTarget)
        {
            if (!DeleteVerified(target))
            {
                return false;
            }

            return DeleteVerified(backup);
        }

        if (!File.Exists(backup))
        {
            // A journal can exist before this target was swapped. In that state the old target
            // is still valid and must be left untouched.
            return !PathExists(backup) && File.Exists(target);
        }

        if (!DeleteVerified(target))
        {
            return false;
        }

        try
        {
            File.Move(backup, target);
            return File.Exists(target) && !PathExists(backup);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long MeasureMediaBytes(string directory)
    {
        long total = 0;
        foreach (string name in new[]
                 {
                     CaptureFileNames.VideoSource,
                     CaptureFileNames.VideoRendered,
                     CaptureFileNames.VideoEdits,
                     CaptureFileNames.Thumbnail,
                 })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                total = checked(total + new FileInfo(path).Length);
            }
        }

        return total;
    }

    private static long MeasureProspectiveMediaBytes(
        string directory,
        string renderedStage,
        string thumbnailStage,
        string editsStage)
    {
        long total = new FileInfo(Path.Combine(directory, CaptureFileNames.VideoSource)).Length;
        total = checked(total + new FileInfo(renderedStage).Length);
        total = checked(total + new FileInfo(thumbnailStage).Length);
        total = checked(total + new FileInfo(editsStage).Length);
        return total;
    }

    private void ApplySnapshotToQueue(CaptureRecord record, VideoRecordSnapshot snapshot)
    {
        // UpdateByteCount must observe the old byte value to adjust the queue aggregate. It also
        // stamps UpdatedAt, so apply the durable snapshot afterwards to restore the exact commit
        // or rollback timestamp and all generation-coupled metadata.
        _queue.UpdateByteCount(record.Id, snapshot.TotalBytes);
        snapshot.Apply(record);
    }

    private static bool CleanupPublishedCaptureSidecars(string directory)
    {
        string pending = Path.Combine(directory, CaptureFileNames.VideoPending);
        string marker = Path.Combine(directory, PublishReady);
        // Remove redundant backups first and keep the primary publish marker until last. Any
        // intermediate failure therefore leaves at least one canonical discovery key behind.
        if (!DeleteVerified(pending + AtomicFile.BackupSuffix)
            || !DeleteVerified(marker + AtomicFile.BackupSuffix)
            || !DeleteVerified(pending))
        {
            return false;
        }

        return DeleteVerified(marker);
    }

    private static bool CleanupPendingCaptureSidecars(string pending)
    {
        // The primary sidecar is the discovery key. Never remove it before its stale backup.
        return DeleteVerified(pending + AtomicFile.BackupSuffix)
               && DeleteVerified(pending);
    }

    private static bool CleanupCorruptAbandonedCapture(string writing, string pending)
    {
        // Do not erase the only discovery sidecar until the payload is confirmed gone.
        return DeleteVerified(writing) && CleanupPendingCaptureSidecars(pending);
    }

    private static bool IsValidPendingCaptureJson(string json)
    {
        try
        {
            CaptureRecord? record = JsonSerializer.Deserialize<CaptureRecord>(json, JsonOptions);
            return record is not null && record.Id != Guid.Empty && record.IsVideo;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidFinalizeJournalJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<VideoFinalizeJournal>(json, JsonOptions)
                ?.IsStructurallyValid() == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool CleanupOrphanFinalizeStages(string directory)
    {
        bool removed = true;
        foreach (string path in EnumerateFinalizeStageResidues(directory))
        {
            removed &= DeleteVerified(path);
        }

        return removed;
    }

    private static IEnumerable<string> EnumerateFinalizeStageResidues(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(directory, ".*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return IsSafeRenderStageName(name)
                       || IsSafeAuxiliaryStageName(name, ".thumb-", ".next.jpg")
                       || IsSafeAuxiliaryStageName(name, ".edits-", ".next.json");
            })
            .ToList();
    }

    private static bool HasAmbiguousFinalizeBackups(string directory) =>
        new[] { RenderBackup, ThumbnailBackup, EditsBackup }
            .Any(name => PathExists(Path.Combine(directory, name)));

    private static string ValidateRenderStagePath(string directory, string path)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string full = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(full);
        string name = Path.GetFileName(full);
        if (!string.Equals(parent, fullDirectory, StringComparison.Ordinal)
            || !IsSafeRenderStageName(name))
        {
            throw new InvalidDataException("The render stage must be a private file in the record directory.");
        }

        return full;
    }

    private static bool IsSafeRenderStageName(string? name) =>
        name is not null
        && name.StartsWith(".render-", StringComparison.Ordinal)
        && name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(name[8..^4], "N", out _);

    private static bool IsSafeAuxiliaryStageName(string? name, string prefix, string suffix) =>
        name is not null
        && name.StartsWith(prefix, StringComparison.Ordinal)
        && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        && Guid.TryParseExact(name[prefix.Length..^suffix.Length], "N", out _);

    private static (int Width, int Height) FitWithin(int width, int height, int longEdge)
    {
        int safeWidth = Math.Max(1, width);
        int safeHeight = Math.Max(1, height);
        int sourceLongEdge = Math.Max(safeWidth, safeHeight);
        if (sourceLongEdge <= longEdge)
        {
            return (safeWidth, safeHeight);
        }

        double scale = (double)longEdge / sourceLongEdge;
        return (
            Math.Max(1, (int)Math.Round(safeWidth * scale)),
            Math.Max(1, (int)Math.Round(safeHeight * scale)));
    }

    private static BitmapSource CreateFallbackThumbnail(int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x21, 0x23, 0x28)), null, new Rect(0, 0, width, height));
            double size = Math.Clamp(Math.Min(width, height) * 0.24, 24, 96);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(-size * 0.34, -size * 0.5), isFilled: true, isClosed: true);
                context.LineTo(new Point(size * 0.52, 0), isStroked: true, isSmoothJoin: true);
                context.LineTo(new Point(-size * 0.34, size * 0.5), isStroked: true, isSmoothJoin: true);
            }

            geometry.Freeze();
            dc.PushTransform(new TranslateTransform(width / 2.0, height / 2.0));
            dc.DrawGeometry(Brushes.White, null, geometry);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool HasIsoBaseMediaFileSignature(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return HasIsoBaseMediaFileSignature(stream);
    }

    internal static bool HasIsoBaseMediaFileSignature(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[16];
        int read = 0;
        while (read < header.Length)
        {
            int count = stream.Read(header[read..]);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read < 12
            || header[4] != (byte)'f'
            || header[5] != (byte)'t'
            || header[6] != (byte)'y'
            || header[7] != (byte)'p')
        {
            return false;
        }

        uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        return boxSize == 1
            ? read >= 16 && stream.Length >= 16
            : boxSize >= 8 && boxSize <= stream.Length;
    }

    private static bool DeleteVerified(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return false;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !PathExists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class VideoFinalizeJournal
    {
        public Guid RecordId { get; set; }

        public long ExpectedContentRevision { get; set; }

        public string? RenderStageFile { get; set; } = string.Empty;

        public string? ThumbnailStageFile { get; set; } = string.Empty;

        public string? EditsStageFile { get; set; } = string.Empty;

        public bool HadRendered { get; set; }

        public bool HadThumbnail { get; set; }

        public bool HadEdits { get; set; }

        public VideoRecordSnapshot? Previous { get; set; } = new();

        public VideoRecordSnapshot? Next { get; set; } = new();

        public bool IsStructurallyValid() =>
            RecordId != Guid.Empty
            && Previous is not null
            && Next is not null
            && ExpectedContentRevision is >= 0 and < long.MaxValue
            && Previous.IsValid(ExpectedContentRevision)
            && Next.IsValid(ExpectedContentRevision + 1)
            && Next.OcrText is null
            && Next.OcrLanguage is null
            && Next.OcrContentRevision is null
            && IsSafeRenderStageName(RenderStageFile)
            && IsSafeAuxiliaryStageName(ThumbnailStageFile, ".thumb-", ".next.jpg")
            && IsSafeAuxiliaryStageName(EditsStageFile, ".edits-", ".next.json");

        public bool IsValidFor(CaptureRecord current) =>
            IsStructurallyValid()
            && current.ContentRevision is >= 0
            && current.ContentRevision >= ExpectedContentRevision
            && current.ContentRevision <= ExpectedContentRevision + 1;
    }

    private sealed class VideoRecordSnapshot
    {
        public double DurationMs { get; set; }

        public bool HasAnnotations { get; set; }

        public long TotalBytes { get; set; }

        public long ContentRevision { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public string? OcrText { get; set; }

        public string? OcrLanguage { get; set; }

        public long? OcrContentRevision { get; set; }

        internal static VideoRecordSnapshot From(CaptureRecord record) => new()
        {
            DurationMs = record.DurationMs,
            HasAnnotations = record.HasAnnotations,
            TotalBytes = record.TotalBytes,
            ContentRevision = record.ContentRevision,
            UpdatedAt = record.UpdatedAt,
            OcrText = record.OcrText,
            OcrLanguage = record.OcrLanguage,
            OcrContentRevision = record.OcrContentRevision,
        };

        internal static VideoRecordSnapshot CreateCommitted(
            CaptureRecord record,
            double durationMs,
            bool hasAnnotations,
            long totalBytes,
            DateTimeOffset updatedAt) => new()
            {
                DurationMs = durationMs,
                HasAnnotations = hasAnnotations,
                TotalBytes = totalBytes,
                ContentRevision = checked(record.ContentRevision + 1),
                UpdatedAt = updatedAt,
                OcrText = null,
                OcrLanguage = null,
                OcrContentRevision = null,
            };

        internal bool IsValid(long expectedRevision) =>
            ContentRevision == expectedRevision
            && double.IsFinite(DurationMs)
            && DurationMs > 0
            && TotalBytes >= 0
            && UpdatedAt != default
            && (OcrContentRevision is null
                || (OcrContentRevision >= 0 && OcrContentRevision <= ContentRevision));

        internal void Apply(CaptureRecord record)
        {
            record.DurationMs = DurationMs;
            record.HasAnnotations = HasAnnotations;
            record.TotalBytes = TotalBytes;
            record.ContentRevision = ContentRevision;
            record.UpdatedAt = UpdatedAt;
            record.OcrText = OcrText;
            record.OcrLanguage = OcrLanguage;
            record.OcrContentRevision = OcrContentRevision;
        }
    }
}

/// <summary>
/// Holds the source record against eviction and owns the revision observed when a video editor
/// opened. A stale editor can never silently replace a newer rendered generation.
/// </summary>
internal sealed class VideoEditSession : IDisposable
{
    private VideoLibraryService? _owner;
    private IDisposable? _evictionLease;

    internal VideoEditSession(
        VideoLibraryService owner,
        Guid recordId,
        long expectedContentRevision,
        IDisposable evictionLease)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        RecordId = recordId;
        ExpectedContentRevision = expectedContentRevision;
        _evictionLease = evictionLease ?? throw new ArgumentNullException(nameof(evictionLease));
    }

    internal Guid RecordId { get; }

    internal long ExpectedContentRevision { get; private set; }

    internal void VerifyOwner(VideoLibraryService owner, CaptureRecord record)
    {
        if (!ReferenceEquals(_owner, owner) || record.Id != RecordId)
        {
            throw new InvalidOperationException("The video edit session does not own this record.");
        }
    }

    internal void Advance(VideoLibraryService owner, long revision)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException("The video edit session is no longer active.");
        }

        ExpectedContentRevision = revision;
    }

    public void Dispose()
    {
        VideoLibraryService? owner = Interlocked.Exchange(ref _owner, null);
        IDisposable? lease = Interlocked.Exchange(ref _evictionLease, null);
        if (owner is not null)
        {
            // Remove the editor ownership before releasing its lease. Releasing the lease may
            // enforce queue limits and raise eviction callbacks synchronously.
            owner.EndEdit(RecordId);
        }

        lease?.Dispose();
    }
}

/// <summary>Owns one pending recording path and aborts incomplete encoder output on disposal.</summary>
internal sealed class VideoCaptureWriteSession : IDisposable
{
    private VideoLibraryService? _owner;
    private int _state;

    internal VideoCaptureWriteSession(
        VideoLibraryService owner,
        CaptureRecord record,
        string stagingOutputPath)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Record = record ?? throw new ArgumentNullException(nameof(record));
        StagingOutputPath = stagingOutputPath ?? throw new ArgumentNullException(nameof(stagingOutputPath));
    }

    internal CaptureRecord Record { get; }

    internal string StagingOutputPath { get; }

    internal void BeginCompletion(VideoLibraryService owner)
    {
        if (!ReferenceEquals(_owner, owner) || Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("The video capture session is no longer pending.");
        }
    }

    internal void MarkCompleted(VideoLibraryService owner)
    {
        if (!ReferenceEquals(_owner, owner) || Interlocked.CompareExchange(ref _state, 2, 1) != 1)
        {
            throw new InvalidOperationException("The video capture session cannot be completed.");
        }
    }

    public void Dispose()
    {
        VideoLibraryService? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null && Volatile.Read(ref _state) != 2)
        {
            owner.AbortCapture(this);
        }
    }
}
