using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
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
    public void Load_RejectsOversizedFileAndDecodedAssetSetWithoutChangingSourceOrMetadata()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            CaptureQueue queue = NewQueue(paths);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            CaptureRecord record = CreateVideoRecord();
            Directory.CreateDirectory(queue.GetDirectory(record));
            string source = library.SourcePath(record);
            File.WriteAllBytes(source, [1, 2, 3]); // Load validates edit metadata without decoding video.
            string edits = queue.GetFilePath(record, CaptureFileNames.VideoEdits);
            using (var file = File.Create(edits)) { file.SetLength(VideoLayerResourceBudget.MaximumDocumentFileBytes + 1); }
            Assert.Throws<VideoLayerLimitException>(() => library.Load(record));
            Assert.Equal(VideoLayerResourceBudget.MaximumDocumentFileBytes + 1, new FileInfo(edits).Length);
            var document = VideoEditDocument.CreateFor(160, 90, 1000);
            document.FrameEditLayers = Enumerable.Range(0, 5).Select(_ => VideoLayerResourceBudgetTests.HeaderLayer()).ToList();
            string json = JsonSerializer.Serialize(document);
            File.WriteAllText(edits, json);
            Assert.Throws<VideoLayerLimitException>(() => library.Load(record));
            Assert.Equal(json, File.ReadAllText(edits));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        }
        finally { DeleteRoot(root); }
    }

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

    [Theory]
    [InlineData("com")]
    [InlineData("timeout")]
    [InlineData("io")]
    public async Task TransientLiveValidationFailure_KeepsFinishedRecordingUntilStartupPublishesIt(string fault)
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var bin = new FakeRecycleBin(root);
            CaptureQueue queue = NewQueue(paths);
            VideoLibraryService library = NewLibrary(queue, paths, bin);
            Exception injected = fault switch
            {
                // MF_E_TOPO_CODEC_NOT_FOUND: the decoder could not be created this time.
                "com" => new COMException("The video decoder is unavailable.", unchecked((int)0xC00D5212)),
                "timeout" => new TimeoutException("The video decoder did not answer in time."),
                // ERROR_SHARING_VIOLATION: an indexer or a virus scan holds the file for a moment.
                _ => new IOException("The recording is in use.", unchecked((int)0x80070020)),
            };
            library.CaptureCompletionProbeForTest = (_, _) => throw injected;
            VideoCaptureWriteSession capture = library.BeginCapture(expectedFrameRate: 10);
            Guid id = capture.Record.Id;
            string pending = Path.Combine(queue.GetDirectory(capture.Record), CaptureFileNames.VideoPending);
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            byte[] encoded = SHA256.HashData(File.ReadAllBytes(capture.StagingOutputPath));

            Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => library.CompleteCaptureAsync(capture, result));
            capture.Dispose();

            // The failure keeps its own type instead of reading as a damaged recording, and
            // disposal leaves the finished MP4 and its discovery sidecar exactly where they were.
            Assert.Same(injected, thrown);
            Assert.True(capture.IsRetainedForRecovery);
            Assert.Equal(encoded, SHA256.HashData(File.ReadAllBytes(capture.StagingOutputPath)));
            Assert.True(File.Exists(pending));
            Assert.Empty(bin.Moved);
            Assert.Empty(queue.Records);
            Assert.True(library.IsBusy(id));

            AssertRestartPublishes(paths, bin, id, encoded);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PublishReadyWriteFailure_KeepsValidatedRecordingUntilStartupPublishesIt()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var bin = new FakeRecycleBin(root);
            CaptureQueue queue = NewQueue(paths);
            VideoLibraryService library = NewLibrary(queue, paths, bin);
            VideoCaptureWriteSession capture = library.BeginCapture(expectedFrameRate: 10);
            Guid id = capture.Record.Id;
            string directory = queue.GetDirectory(capture.Record);
            string pending = Path.Combine(directory, CaptureFileNames.VideoPending);
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            byte[] encoded = SHA256.HashData(File.ReadAllBytes(capture.StagingOutputPath));

            Exception thrown;
            // The real decoder accepts the clip; Windows then refuses to create the publish-ready
            // sidecar beside it, as a changed folder permission would.
            using (new DeniedFileCreation(directory))
            {
                thrown = await Assert.ThrowsAnyAsync<Exception>(
                    () => library.CompleteCaptureAsync(capture, result));
                capture.Dispose();
            }

            Assert.True(thrown is UnauthorizedAccessException or IOException, thrown.ToString());
            Assert.True(capture.IsRetainedForRecovery);
            Assert.False(File.Exists(Path.Combine(directory, PublishReadyMarker)));
            Assert.Equal(encoded, SHA256.HashData(File.ReadAllBytes(capture.StagingOutputPath)));
            Assert.True(File.Exists(pending));
            Assert.Empty(bin.Moved);
            Assert.True(library.IsBusy(id));

            AssertRestartPublishes(paths, bin, id, encoded);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FailureAfterPublishReady_KeepsRecordingUntilStartupPublishesIt()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var bin = new FakeRecycleBin(root);
            CaptureQueue queue = NewQueue(paths);
            VideoLibraryService library = NewLibrary(queue, paths, bin);
            VideoCaptureWriteSession capture = library.BeginCapture(expectedFrameRate: 10);
            Guid id = capture.Record.Id;
            string directory = queue.GetDirectory(capture.Record);
            string pending = Path.Combine(directory, CaptureFileNames.VideoPending);
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
            byte[] encoded = SHA256.HashData(File.ReadAllBytes(capture.StagingOutputPath));

            // A reader without delete sharing (an indexer or a virus scan) blocks the rename that
            // publishes source.mp4 after the durable publish-ready sidecar already exists.
            using (new FileStream(capture.StagingOutputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                _ = await Assert.ThrowsAnyAsync<IOException>(
                    () => library.CompleteCaptureAsync(capture, result));
                capture.Dispose();
            }

            Assert.True(capture.IsRetainedForRecovery);
            Assert.True(File.Exists(Path.Combine(directory, PublishReadyMarker)));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoSource)));
            Assert.Equal(encoded, SHA256.HashData(File.ReadAllBytes(capture.StagingOutputPath)));
            Assert.True(File.Exists(pending));
            Assert.Empty(bin.Moved);
            Assert.True(library.IsBusy(id));

            AssertRestartPublishes(paths, bin, id, encoded);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("not-mp4")]
    [InlineData("decoder-rejected")]
    public async Task ConclusivelyDamagedEncoderOutput_IsStillRemovedLikeStartupRecovery(string damage)
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var bin = new FakeRecycleBin(root);
            CaptureQueue queue = NewQueue(paths);
            VideoLibraryService library = NewLibrary(queue, paths, bin);
            VideoCaptureWriteSession capture = library.BeginCapture(expectedFrameRate: 10);
            string directory = queue.GetDirectory(capture.Record);
            var result = new RecordingResult(capture.StagingOutputPath, 1000, 10, 10, 160, 90);
            switch (damage)
            {
                case "empty":
                    File.WriteAllBytes(capture.StagingOutputPath, []);
                    break;
                case "not-mp4":
                    File.WriteAllBytes(
                        capture.StagingOutputPath,
                        Enumerable.Repeat((byte)0xA5, 2_048).ToArray());
                    break;
                default:
                    result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 10);
                    // The decoder's own verdict on the media, as for a truncated tail.
                    library.CaptureCompletionProbeForTest = (_, _) =>
                        throw new InvalidDataException("The decoded video ends before its container duration.");
                    break;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => library.CompleteCaptureAsync(capture, result));
            capture.Dispose();

            Assert.False(capture.IsRetainedForRecovery);
            Assert.Empty(queue.Records);
            Assert.False(File.Exists(capture.StagingOutputPath));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoPending)));
            Assert.False(Directory.Exists(directory));
            Assert.Contains(capture.StagingOutputPath, bin.Moved);
            Assert.False(library.IsBusy(capture.Record.Id));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CancelledRecording_IsStillRemovedAndCannotBeCompletedAfterwards()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var bin = new FakeRecycleBin(root);
            CaptureQueue queue = NewQueue(paths);
            VideoLibraryService library = NewLibrary(queue, paths, bin);
            VideoCaptureWriteSession capture = library.BeginCapture(expectedFrameRate: 10);
            string directory = queue.GetDirectory(capture.Record);
            // Even a playable file is discarded when the user cancels before completion.
            RecordingResult result = EncodeClip(capture.StagingOutputPath, 160, 90, 10, 3);

            capture.Dispose();

            Assert.False(capture.IsRetainedForRecovery);
            Assert.False(File.Exists(capture.StagingOutputPath));
            Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoPending)));
            Assert.Contains(capture.StagingOutputPath, bin.Moved);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => library.CompleteCaptureAsync(capture, result));
            Assert.Empty(queue.Records);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RetainedCompletionFailure_IsExplainedAsKeptForRecoveryOnTheNextStart()
    {
        string root = NewRoot();
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var bin = new FakeRecycleBin(root);
            CaptureQueue queue = NewQueue(paths);
            VideoLibraryService library = NewLibrary(queue, paths, bin);
            library.CaptureCompletionProbeForTest = (_, _) =>
                throw new COMException("The video decoder is unavailable.", unchecked((int)0xC00D5212));
            VideoCaptureWriteSession retained = library.BeginCapture(expectedFrameRate: 10);
            RecordingResult result = EncodeClip(retained.StagingOutputPath, 160, 90, 10, 3);
            Exception thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => library.CompleteCaptureAsync(retained, result));
            retained.Dispose();

            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using IDisposable scope = UiText.UseLanguage(language);
                Exception shown = RegionRecordingCoordinator.DescribeCompletionFailure(retained, thrown);

                Assert.Equal(UiText.Get("Text_C5AC44062ACA").TrimEnd(), ThemedDialog.RecoveryMessage(shown));
                Assert.Same(thrown, shown.InnerException);
            }

            // A clip proven unusable is already gone, so the dialog explains the original failure.
            VideoCaptureWriteSession rejected = library.BeginCapture(expectedFrameRate: 10);
            File.WriteAllBytes(rejected.StagingOutputPath, []);
            Exception damaged = await Assert.ThrowsAsync<InvalidDataException>(
                () => library.CompleteCaptureAsync(
                    rejected,
                    new RecordingResult(rejected.StagingOutputPath, 1000, 10, 10, 160, 90)));
            rejected.Dispose();

            Assert.Same(damaged, RegionRecordingCoordinator.DescribeCompletionFailure(rejected, damaged));
        }
        finally
        {
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

    private const string PublishReadyMarker = ".video-publish-ready.json";

    private static CaptureQueue NewQueue(AppPaths paths)
    {
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        return queue;
    }

    private static VideoLibraryService NewLibrary(CaptureQueue queue, AppPaths paths, FakeRecycleBin bin) =>
        new(queue, paths, NullLogger<VideoLibraryService>.Instance, recoverableRemovalForTest: bin.Remove);

    /// <summary>A restart with the normal decoder publishes the kept file as the same recording.</summary>
    private static void AssertRestartPublishes(AppPaths paths, FakeRecycleBin bin, Guid id, byte[] encoded)
    {
        CaptureQueue restartQueue = NewQueue(paths);
        VideoLibraryService restarted = NewLibrary(restartQueue, paths, bin);

        CaptureRecord recovered = Assert.Single(restartQueue.Records);
        Assert.Equal(id, recovered.Id);
        Assert.Equal(160, recovered.Width);
        Assert.Equal(90, recovered.Height);
        Assert.Equal(encoded, SHA256.HashData(File.ReadAllBytes(restarted.SourcePath(recovered))));
        Assert.False(restarted.IsBusy(recovered.Id));
        string directory = restartQueue.GetDirectory(recovered);
        Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoWriting)));
        Assert.False(File.Exists(Path.Combine(directory, CaptureFileNames.VideoPending)));
        Assert.False(File.Exists(Path.Combine(directory, PublishReadyMarker)));
        // Startup removes only its own sidecars, never the recording itself.
        Assert.DoesNotContain(bin.Moved, path => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(id, Assert.Single(NewQueue(paths).Records).Id);
    }

    /// <summary>
    /// Stands in for the Recycle Bin: every removal moves into the fixture and is recorded, so a
    /// test can count the moves and nothing reaches the user's Recycle Bin.
    /// </summary>
    private sealed class FakeRecycleBin
    {
        private readonly string _folder;

        internal FakeRecycleBin(string root)
        {
            _folder = Path.Combine(root, "fake-recycle-bin");
            Directory.CreateDirectory(_folder);
        }

        internal List<string> Moved { get; } = [];

        internal void Remove(string path)
        {
            string target = Path.Combine(_folder, $"{Moved.Count:D3}-{Path.GetFileName(path)}");
            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target);
            }

            Moved.Add(path);
        }
    }

    /// <summary>
    /// Denies the current user creating files in one directory until disposed, the way
    /// ImageExportDestinationTests denies a remembered save folder.
    /// </summary>
    private sealed class DeniedFileCreation : IDisposable
    {
        private readonly DirectoryInfo _directory;
        private readonly string _originalAccess;

        internal DeniedFileCreation(string directory)
        {
            _directory = new DirectoryInfo(directory);
            // Windows may add the auto-inherited DACL flag on its first ACL write. Take the
            // restoration baseline after that normalization, without changing any rules.
            var normalized = new DirectorySecurity();
            normalized.SetSecurityDescriptorSddlForm(
                _directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                AccessControlSections.Access);
            _directory.SetAccessControl(normalized);
            _originalAccess = _directory.GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            DirectorySecurity denied = _directory.GetAccessControl();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User
                ?? throw new InvalidOperationException("The test identity is unavailable.");
            denied.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.CreateFiles, AccessControlType.Deny));
            _directory.SetAccessControl(denied);
        }

        public void Dispose()
        {
            _directory.Refresh();
            if (!_directory.Exists)
            {
                return;
            }

            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(_originalAccess, AccessControlSections.Access);
            _directory.SetAccessControl(restored);
        }
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
        string root = TestRecycleBin.CreateTempSubdirectory("mycapture-video-library-").FullName;
        // codeql[cs/path-injection] -- isolated GUID test workspace
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            // codeql[cs/path-injection] -- isolated GUID test workspace
            TestRecycleBin.DeleteDirectory(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
