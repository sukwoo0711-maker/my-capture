using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoLibraryServiceTests
{
    [Fact]
    public void Mp4SignatureParser_FillsHeaderAcrossShortStreamReads()
    {
        byte[] header =
        [
            0, 0, 0, 16,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m',
            0, 0, 0, 0,
        ];
        using var stream = new OneByteReadStream(header);

        Assert.True(VideoLibraryService.HasIsoBaseMediaFileSignature(stream));
    }

    [Fact]
    public async Task CompletedCaptureAndEdit_RemainInQueueAndNeverRewriteImmutableSource()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(
                queue,
                paths,
                NullLogger<VideoLibraryService>.Instance);

            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);

            Assert.Same(item.Record, queue.Find(item.Record.Id));
            Assert.True(item.Record.IsVideo);
            Assert.Equal(1000, item.Record.DurationMs, precision: 3);
            string source = library.SourcePath(item.Record);
            byte[] immutableHash = SHA256.HashData(File.ReadAllBytes(source));
            Assert.False(File.Exists(queue.GetFilePath(item.Record, CaptureFileNames.VideoPending)));
            Assert.False(File.Exists(
                queue.GetFilePath(item.Record, CaptureFileNames.VideoPending) + AtomicFile.BackupSuffix));

            using (VideoEditSession edit = library.BeginEdit(item.Record))
            {
                Assert.True(library.IsBusy(item.Record.Id));
                Assert.Throws<InvalidOperationException>(() => library.BeginEdit(item.Record));

                string stage = library.CreateRenderStagingPath(item.Record);
                File.Copy(source, stage);
                VideoEditDocument document = item.EditDocument.Clone();
                document.TextOverlays.Add(new TimedTextOverlay
                {
                    Text = "persistent text",
                    StartMs = 100,
                    EndMs = 800,
                    Placement = VideoTextPlacement.Bottom,
                });

                await library.CommitEditAsync(item.Record, edit, document, stage);

                Assert.Equal(1, item.Record.ContentRevision);
                Assert.True(item.Record.HasAnnotations);
                Assert.True(File.Exists(queue.GetFilePath(item.Record, CaptureFileNames.VideoRendered)));
                Assert.Equal(immutableHash, SHA256.HashData(File.ReadAllBytes(source)));
            }

            Assert.False(library.IsBusy(item.Record.Id));
            var reloadedQueue = NewQueue(paths);
            var reloadedLibrary = new VideoLibraryService(
                reloadedQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord reloaded = Assert.Single(reloadedQueue.Records);
            VideoLibraryItem loaded = reloadedLibrary.Load(reloaded);
            Assert.True(loaded.Record.IsVideo);
            Assert.Single(loaded.EditDocument.TextOverlays);
            Assert.Equal("persistent text", loaded.EditDocument.TextOverlays[0].Text);
            Assert.Equal(immutableHash, SHA256.HashData(File.ReadAllBytes(reloadedLibrary.SourcePath(reloaded))));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompletedCapture_PersistsPlayableTimelineWhenWallClockIncludesSlowStartup()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(
                queue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture(expectedFrameRate: 10);
            RecordingResult encoded = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 1);
            var slowWallClock = new RecordingResult(
                encoded.OutputPath,
                DurationMs: 5_000,
                encoded.Fps,
                encoded.EmittedFrames,
                encoded.Width,
                encoded.Height);

            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, slowWallClock);

            Assert.InRange(item.Record.DurationMs, 99.9, 100.1);
            Assert.Equal(item.Record.DurationMs, item.EditDocument.SourceDurationMs, precision: 3);
            Assert.NotEqual(slowWallClock.DurationMs, item.Record.DurationMs);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void PublishReadyCrashBoundary_RecoversExactMetadataAndPromotesCompletedEncoderFile()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            DateTimeOffset now = DateTimeOffset.Now;
            var exact = new CaptureRecord
            {
                CreatedAt = now,
                UpdatedAt = now,
                MediaKind = CaptureMediaKind.Video,
                Width = 160,
                Height = 90,
                DurationMs = 1000,
                FrameRate = 10,
                FrameCount = 10,
                Title = "recovered video",
            };
            exact.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(exact.Id, exact.CreatedAt);
            string directory = queue.GetDirectory(exact);
            Directory.CreateDirectory(directory);

            var placeholder = new CaptureRecord
            {
                Id = exact.Id,
                CreatedAt = exact.CreatedAt,
                UpdatedAt = exact.CreatedAt,
                MediaKind = CaptureMediaKind.Video,
                Width = 1,
                Height = 1,
                DurationMs = 1,
                FrameRate = 30,
                RelativeDirectory = exact.RelativeDirectory,
            };
            File.WriteAllText(
                Path.Combine(directory, CaptureFileNames.VideoPending),
                JsonSerializer.Serialize(placeholder, JsonDefaults.Readable));
            _ = EncodeClip(
                Path.Combine(directory, CaptureFileNames.VideoWriting),
                exact.Width,
                exact.Height,
                exact.FrameRate,
                (int)exact.FrameCount);
            const string publishMarker = ".video-publish-ready.json";
            File.WriteAllText(
                Path.Combine(directory, publishMarker),
                JsonSerializer.Serialize(exact, JsonDefaults.Readable));

            // Simulate app startup: queue load cannot see a video without source.mp4, then the
            // library's durable publication recovery must promote it and repair the index.
            queue.Load();
            Assert.Empty(queue.Records);
            _ = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);

            CaptureRecord recovered = Assert.Single(queue.Records);
            Assert.Equal(exact.Id, recovered.Id);
            Assert.Equal(160, recovered.Width);
            Assert.Equal(90, recovered.Height);
            Assert.Equal(1000, recovered.DurationMs, precision: 3);
            Assert.Equal(10, recovered.FrameRate);
            Assert.Equal(10, recovered.FrameCount);
            Assert.True(File.Exists(Path.Combine(directory, CaptureFileNames.VideoSource)));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoWriting)));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoPending)));
            Assert.False(File.Exists(
                Path.Combine(directory, CaptureFileNames.VideoPending) + AtomicFile.BackupSuffix));
            Assert.False(File.Exists(Path.Combine(directory, publishMarker)));

            var secondQueue = NewQueue(paths);
            Assert.Single(secondQueue.Records);
            Assert.Equal(exact.Id, secondQueue.Records[0].Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StaleEditorRevision_IsRejectedBeforeRenderedGenerationChanges()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            using VideoEditSession edit = library.BeginEdit(item.Record);
            item.Record.ContentRevision++;
            string stage = library.CreateRenderStagingPath(item.Record);
            File.Copy(library.SourcePath(item.Record), stage);

            Exception error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                library.CommitEditAsync(item.Record, edit, item.EditDocument, stage));

            Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(queue.GetFilePath(item.Record, CaptureFileNames.VideoRendered)));
            Assert.True(File.Exists(stage));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FinalizeCleanupFailure_KeepsCommittedGenerationAndRecoversWithoutRollback()
    {
        string root = NewRoot();
        FileStream? journalLock = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            string directory = queue.GetDirectory(item.Record);
            string journalPath = Path.Combine(directory, ".video-finalize-journal.json");
            string markerPath = Path.Combine(directory, ".video-finalize-commit-ready");

            using (VideoEditSession edit = library.BeginEdit(item.Record))
            {
                string stage = library.CreateRenderStagingPath(item.Record);
                File.Copy(library.SourcePath(item.Record), stage);
                VideoEditDocument document = item.EditDocument.Clone();
                document.TextOverlays.Add(new TimedTextOverlay
                {
                    Text = "committed before cleanup",
                    StartMs = 100,
                    EndMs = 800,
                    Placement = VideoTextPlacement.Top,
                });
                library.BeforeFinalizeCleanupForTest = finalizeDirectory =>
                {
                    journalLock = new FileStream(
                        Path.Combine(finalizeDirectory, ".video-finalize-journal.json"),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                };

                await library.CommitEditAsync(item.Record, edit, document, stage);

                Assert.Equal(1, item.Record.ContentRevision);
                Assert.True(item.Record.HasAnnotations);
                Assert.True(File.Exists(journalPath));
                Assert.True(File.Exists(markerPath));
                Assert.True(library.IsBusy(item.Record.Id));
                Assert.Throws<InvalidOperationException>(() => library.BeginEdit(item.Record));
            }

            journalLock!.Dispose();
            journalLock = null;

            CaptureQueue restartQueue = NewQueue(paths);
            var restarted = new VideoLibraryService(
                restartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord recovered = Assert.Single(restartQueue.Records);
            VideoLibraryItem loaded = restarted.Load(recovered);

            Assert.Equal(1, recovered.ContentRevision);
            Assert.True(recovered.HasAnnotations);
            Assert.Equal("committed before cleanup", Assert.Single(loaded.EditDocument.TextOverlays).Text);
            Assert.True(File.Exists(restarted.CurrentVideoPath(recovered)));
            Assert.False(File.Exists(journalPath));
            Assert.False(File.Exists(markerPath));
            Assert.False(restarted.IsBusy(recovered.Id));
        }
        finally
        {
            journalLock?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CorruptInitialEncoderOutput_IsRejectedBeforeSourcePublication()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            string directory;

            using (VideoCaptureWriteSession capture = library.BeginCapture())
            {
                directory = queue.GetDirectory(capture.Record);
                File.WriteAllBytes(capture.StagingOutputPath, Enumerable.Repeat((byte)0xA5, 2_048).ToArray());
                var result = new RecordingResult(
                    capture.StagingOutputPath,
                    1000,
                    10,
                    10,
                    160,
                    90);

                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    library.CompleteCaptureAsync(capture, result));
            }

            Assert.Empty(queue.Records);
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoSource)));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoWriting)));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoPending)));
            Assert.False(File.Exists(Path.Combine(directory, ".video-publish-ready.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CorruptPublishReadyEncoderOutput_IsNeverPromotedToImmutableSource()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            CaptureRecord record = CreateVideoRecord();
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            string writing = Path.Combine(directory, CaptureFileNames.VideoWriting);
            string marker = Path.Combine(directory, ".video-publish-ready.json");
            File.WriteAllBytes(writing, Enumerable.Repeat((byte)0x5A, 2_048).ToArray());
            File.WriteAllText(
                Path.Combine(directory, CaptureFileNames.VideoPending),
                JsonSerializer.Serialize(record, JsonDefaults.Readable));
            File.WriteAllText(marker, JsonSerializer.Serialize(record, JsonDefaults.Readable));

            queue.Load();
            Assert.Empty(queue.Records);
            _ = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);

            Assert.Empty(queue.Records);
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoSource)));
            Assert.True(File.Exists(writing));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CompletedAbandonedPrivateEncoderOutput_IsRecoveredOnStartup()
    {
        string root = NewRoot();
        VideoCaptureWriteSession? abandoned = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue firstQueue = NewQueue(paths);
            var firstLibrary = new VideoLibraryService(
                firstQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            abandoned = firstLibrary.BeginCapture();
            Guid expectedId = abandoned.Record.Id;
            _ = EncodeClip(abandoned.StagingOutputPath, 160, 90, 30, 30);

            CaptureQueue restartQueue = NewQueue(paths);
            var restarted = new VideoLibraryService(
                restartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord recovered = Assert.Single(restartQueue.Records);

            Assert.Equal(expectedId, recovered.Id);
            Assert.Equal(160, recovered.Width);
            Assert.Equal(90, recovered.Height);
            Assert.Equal(1000, recovered.DurationMs, precision: 2);
            Assert.Equal(30, recovered.FrameRate);
            Assert.Equal(30, recovered.FrameCount);
            Assert.True(File.Exists(restarted.SourcePath(recovered)));
            Assert.False(File.Exists(abandoned.StagingOutputPath));
            Assert.False(File.Exists(Path.Combine(
                restartQueue.GetDirectory(recovered),
                CaptureFileNames.VideoPending)));
            Assert.False(File.Exists(
                Path.Combine(
                    restartQueue.GetDirectory(recovered),
                    CaptureFileNames.VideoPending) + AtomicFile.BackupSuffix));
        }
        finally
        {
            abandoned?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CorruptAbandonedPrivateEncoderOutput_IsCleanedInsteadOfLeaking()
    {
        string root = NewRoot();
        VideoCaptureWriteSession? abandoned = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue firstQueue = NewQueue(paths);
            var firstLibrary = new VideoLibraryService(
                firstQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            abandoned = firstLibrary.BeginCapture();
            string pending = Path.Combine(
                firstQueue.GetDirectory(abandoned.Record),
                CaptureFileNames.VideoPending);
            File.WriteAllBytes(
                abandoned.StagingOutputPath,
                Enumerable.Repeat((byte)0xC3, 512).ToArray());

            CaptureQueue restartQueue = NewQueue(paths);
            _ = new VideoLibraryService(
                restartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);

            Assert.Empty(restartQueue.Records);
            Assert.False(File.Exists(abandoned.StagingOutputPath));
            Assert.False(File.Exists(pending));
        }
        finally
        {
            abandoned?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public void LockedCorruptAbandonedOutput_RetainsSidecarUntilPayloadCanBeDeleted()
    {
        string root = NewRoot();
        VideoCaptureWriteSession? abandoned = null;
        FileStream? payloadLock = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue firstQueue = NewQueue(paths);
            var firstLibrary = new VideoLibraryService(
                firstQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            abandoned = firstLibrary.BeginCapture();
            string pending = Path.Combine(
                firstQueue.GetDirectory(abandoned.Record),
                CaptureFileNames.VideoPending);
            File.WriteAllBytes(
                abandoned.StagingOutputPath,
                Enumerable.Repeat((byte)0xD7, 512).ToArray());
            payloadLock = new FileStream(
                abandoned.StagingOutputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            CaptureQueue lockedRestartQueue = NewQueue(paths);
            _ = new VideoLibraryService(
                lockedRestartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);

            Assert.True(File.Exists(abandoned.StagingOutputPath));
            Assert.True(File.Exists(pending));

            payloadLock.Dispose();
            payloadLock = null;
            CaptureQueue retryQueue = NewQueue(paths);
            _ = new VideoLibraryService(
                retryQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);

            Assert.False(File.Exists(abandoned.StagingOutputPath));
            Assert.False(File.Exists(pending));
        }
        finally
        {
            payloadLock?.Dispose();
            abandoned?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public void DecoderPlatformFailure_PreservesAndBlocksAbandonedRecordingForRetry()
    {
        string root = NewRoot();
        VideoCaptureWriteSession? abandoned = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue firstQueue = NewQueue(paths);
            var firstLibrary = new VideoLibraryService(
                firstQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            abandoned = firstLibrary.BeginCapture();
            Guid expectedId = abandoned.Record.Id;
            string pending = Path.Combine(
                firstQueue.GetDirectory(abandoned.Record),
                CaptureFileNames.VideoPending);
            _ = EncodeClip(abandoned.StagingOutputPath, 160, 90, 10, 1);

            CaptureQueue restartQueue = NewQueue(paths);
            var restarted = new VideoLibraryService(
                restartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance,
                _ => throw new System.Runtime.InteropServices.COMException("decoder unavailable"));

            Assert.Empty(restartQueue.Records);
            Assert.True(File.Exists(abandoned.StagingOutputPath));
            Assert.True(File.Exists(pending));
            Assert.True(restarted.IsBusy(expectedId));
        }
        finally
        {
            abandoned?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RollbackWithNoPreviousRender_RetainsJournalWhenNewTargetCannotBeDeleted()
    {
        string root = NewRoot();
        FileStream? renderLock = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            item.Record.OcrText = "cached before failed edit";
            item.Record.OcrLanguage = "en-US";
            item.Record.OcrContentRevision = item.Record.ContentRevision;
            queue.SaveRecordMetaOrThrow(item.Record);
            queue.Save();

            string directory = queue.GetDirectory(item.Record);
            string rendered = Path.Combine(directory, CaptureFileNames.VideoRendered);
            string journal = Path.Combine(directory, ".video-finalize-journal.json");
            string marker = Path.Combine(directory, ".video-finalize-commit-ready");
            using (VideoEditSession edit = library.BeginEdit(item.Record))
            {
                string stage = library.CreateRenderStagingPath(item.Record);
                File.Copy(library.SourcePath(item.Record), stage);
                VideoEditDocument document = item.EditDocument.Clone();
                document.TextOverlays.Add(new TimedTextOverlay
                {
                    Text = "will roll back",
                    StartMs = 100,
                    EndMs = 800,
                });
                library.AfterFinalizePayloadSwapForTest = finalizeDirectory =>
                {
                    renderLock = new FileStream(
                        Path.Combine(finalizeDirectory, CaptureFileNames.VideoRendered),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                    throw new IOException("injected pre-marker failure");
                };

                await Assert.ThrowsAsync<IOException>(() =>
                    library.CommitEditAsync(item.Record, edit, document, stage));
            }

            Assert.True(File.Exists(rendered));
            Assert.True(File.Exists(journal));
            Assert.False(File.Exists(marker));
            Assert.True(library.IsBusy(item.Record.Id));

            renderLock!.Dispose();
            renderLock = null;
            CaptureQueue restartQueue = NewQueue(paths);
            var restarted = new VideoLibraryService(
                restartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord recovered = Assert.Single(restartQueue.Records);

            Assert.False(File.Exists(rendered));
            Assert.False(File.Exists(journal));
            Assert.False(restarted.IsBusy(recovered.Id));
            Assert.Equal(0, recovered.ContentRevision);
            Assert.Equal("cached before failed edit", recovered.OcrText);
            Assert.Equal("en-US", recovered.OcrLanguage);
            Assert.Equal(0, recovered.OcrContentRevision);
        }
        finally
        {
            renderLock?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CommitMarkerRecovery_RehydratesExactNextMetadataIncludingOcrInvalidation()
    {
        string root = NewRoot();
        FileStream? journalLock = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            item.Record.OcrText = "stale OCR";
            item.Record.OcrLanguage = "ko-KR";
            item.Record.OcrContentRevision = 0;
            queue.SaveRecordMetaOrThrow(item.Record);
            queue.Save();
            long previousBytes = item.Record.TotalBytes;
            DateTimeOffset previousUpdatedAt = item.Record.UpdatedAt;

            string directory = queue.GetDirectory(item.Record);
            string journal = Path.Combine(directory, ".video-finalize-journal.json");
            string marker = Path.Combine(directory, ".video-finalize-commit-ready");
            using (VideoEditSession edit = library.BeginEdit(item.Record))
            {
                string stage = library.CreateRenderStagingPath(item.Record);
                _ = EncodeClip(stage, 160, 90, 10, 10);
                VideoEditDocument document = item.EditDocument.Clone();
                document.TrimInMs = 100;
                document.TrimOutMs = 900;
                document.TextOverlays.Add(new TimedTextOverlay
                {
                    Text = "committed text",
                    StartMs = 150,
                    EndMs = 700,
                });
                library.BeforeFinalizeCleanupForTest = finalizeDirectory =>
                {
                    journalLock = new FileStream(
                        Path.Combine(finalizeDirectory, ".video-finalize-journal.json"),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);
                };

                await library.CommitEditAsync(item.Record, edit, document, stage);
            }

            long committedBytes = item.Record.TotalBytes;
            DateTimeOffset committedUpdatedAt = item.Record.UpdatedAt;
            Assert.True(File.Exists(journal));
            Assert.True(File.Exists(marker));
            Assert.Equal(1, item.Record.ContentRevision);
            Assert.Equal(800, item.Record.DurationMs, precision: 3);
            Assert.Null(item.Record.OcrText);

            // Simulate a crash where the commit marker and payload reached disk but both durable
            // metadata copies still expose the previous generation.
            queue.UpdateByteCount(item.Record.Id, previousBytes);
            item.Record.DurationMs = 1000;
            item.Record.HasAnnotations = false;
            item.Record.ContentRevision = 0;
            item.Record.UpdatedAt = previousUpdatedAt;
            item.Record.OcrText = "stale OCR";
            item.Record.OcrLanguage = "ko-KR";
            item.Record.OcrContentRevision = 0;
            queue.SaveRecordMetaOrThrow(item.Record);
            queue.Save();

            journalLock!.Dispose();
            journalLock = null;
            CaptureQueue restartQueue = NewQueue(paths);
            var restarted = new VideoLibraryService(
                restartQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord recovered = Assert.Single(restartQueue.Records);

            Assert.Equal(1, recovered.ContentRevision);
            Assert.Equal(800, recovered.DurationMs, precision: 3);
            Assert.True(recovered.HasAnnotations);
            Assert.Equal(committedBytes, recovered.TotalBytes);
            Assert.Equal(committedUpdatedAt, recovered.UpdatedAt);
            Assert.Null(recovered.OcrText);
            Assert.Null(recovered.OcrLanguage);
            Assert.Null(recovered.OcrContentRevision);
            Assert.False(File.Exists(journal));
            Assert.False(File.Exists(marker));
            Assert.False(restarted.IsBusy(recovered.Id));
        }
        finally
        {
            journalLock?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StartupCleansPreJournalStagesAndBlocksWhenOneCannotBeRemoved()
    {
        string root = NewRoot();
        FileStream? stageLock = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            string directory = queue.GetDirectory(item.Record);
            string token = Guid.NewGuid().ToString("N");
            string renderStage = Path.Combine(directory, $".render-{token}.mp4");
            string thumbnailStage = Path.Combine(directory, $".thumb-{token}.next.jpg");
            string editsStage = Path.Combine(directory, $".edits-{token}.next.json");
            File.WriteAllBytes(renderStage, [1, 2, 3]);
            File.WriteAllBytes(thumbnailStage, [4, 5, 6]);
            File.WriteAllText(editsStage, "{}");
            stageLock = new FileStream(
                renderStage,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            CaptureQueue lockedQueue = NewQueue(paths);
            var lockedRecovery = new VideoLibraryService(
                lockedQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord lockedRecord = Assert.Single(lockedQueue.Records);

            Assert.True(File.Exists(renderStage));
            Assert.False(File.Exists(thumbnailStage));
            Assert.False(File.Exists(editsStage));
            Assert.True(lockedRecovery.IsBusy(lockedRecord.Id));
            Assert.Throws<InvalidOperationException>(() => lockedRecovery.BeginEdit(lockedRecord));

            stageLock.Dispose();
            stageLock = null;
            CaptureQueue retryQueue = NewQueue(paths);
            var retryRecovery = new VideoLibraryService(
                retryQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord retryRecord = Assert.Single(retryQueue.Records);

            Assert.False(File.Exists(renderStage));
            Assert.False(retryRecovery.IsBusy(retryRecord.Id));
            using VideoEditSession edit = retryRecovery.BeginEdit(retryRecord);
        }
        finally
        {
            stageLock?.Dispose();
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CommitRejectsUnexpectedPreJournalStageResidue()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            string directory = queue.GetDirectory(item.Record);

            using VideoEditSession edit = library.BeginEdit(item.Record);
            string stage = library.CreateRenderStagingPath(item.Record);
            File.Copy(library.SourcePath(item.Record), stage);
            string residue = Path.Combine(
                directory,
                $".thumb-{Guid.NewGuid():N}.next.jpg");
            File.WriteAllBytes(residue, [1, 2, 3]);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                library.CommitEditAsync(item.Record, edit, item.EditDocument, stage));

            Assert.True(File.Exists(stage));
            Assert.True(File.Exists(residue));
            Assert.True(library.IsBusy(item.Record.Id));
            Assert.False(File.Exists(Path.Combine(directory, ".video-finalize-journal.json")));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoRendered)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LockedPendingBackup_KeepsPrimaryDiscoveryKeysUntilRecoveryCanFinish()
    {
        string root = NewRoot();
        FileStream? backupLock = null;
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            using VideoCaptureWriteSession capture = library.BeginCapture();
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            VideoLibraryItem item = await library.CompleteCaptureAsync(capture, result);
            string directory = queue.GetDirectory(item.Record);
            string pending = Path.Combine(directory, CaptureFileNames.VideoPending);
            string pendingBackup = pending + AtomicFile.BackupSuffix;
            string marker = Path.Combine(directory, ".video-publish-ready.json");
            string json = JsonSerializer.Serialize(item.Record, JsonDefaults.Readable);
            File.WriteAllText(pending, json);
            File.WriteAllText(pendingBackup, json);
            File.WriteAllText(marker, json);
            backupLock = new FileStream(
                pendingBackup,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            CaptureQueue lockedQueue = NewQueue(paths);
            var lockedRecovery = new VideoLibraryService(
                lockedQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord lockedRecord = Assert.Single(lockedQueue.Records);

            Assert.True(File.Exists(pendingBackup));
            Assert.True(File.Exists(pending));
            Assert.True(File.Exists(marker));
            Assert.True(lockedRecovery.IsBusy(lockedRecord.Id));

            backupLock.Dispose();
            backupLock = null;
            CaptureQueue retryQueue = NewQueue(paths);
            var retryRecovery = new VideoLibraryService(
                retryQueue,
                paths,
                NullLogger<VideoLibraryService>.Instance);
            CaptureRecord recovered = Assert.Single(retryQueue.Records);

            Assert.False(File.Exists(pendingBackup));
            Assert.False(File.Exists(pending));
            Assert.False(File.Exists(marker));
            Assert.False(retryRecovery.IsBusy(recovered.Id));
        }
        finally
        {
            backupLock?.Dispose();
            DeleteRoot(root);
        }
    }

    private static CaptureRecord CreateVideoRecord()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var record = new CaptureRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            MediaKind = CaptureMediaKind.Video,
            Width = 160,
            Height = 90,
            DurationMs = 1000,
            FrameRate = 10,
            FrameCount = 10,
            Title = "recovery candidate",
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        return record;
    }

    private static CaptureQueue NewQueue(AppPaths paths)
    {
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        return queue;
    }

    private static RecordingResult EncodeClip(
        string path,
        int width,
        int height,
        int fps,
        int frameCount)
    {
        var options = new VideoEncoderOptions(
            path,
            width,
            height,
            fps,
            VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = 0x30;
                pixels[offset + 1] = 0x20;
                pixels[offset + 2] = 0x10;
                pixels[offset + 3] = 0xFF;
            }

            double frameMs = 1000.0 / fps;
            for (int index = 0; index < frameCount; index++)
            {
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, index * frameMs));
            }

            encoder.Complete();
        }

        return new RecordingResult(
            path,
            frameCount * 1000.0 / fps,
            fps,
            frameCount,
            width,
            height);
    }

    private sealed class OneByteReadStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public override int Read(Span<byte> destination) =>
            base.Read(destination[..Math.Min(1, destination.Length)]);
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mycapture-video-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
