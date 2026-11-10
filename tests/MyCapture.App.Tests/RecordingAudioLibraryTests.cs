using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Threading;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioLibraryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task SilentEditIsRejectedBeforePublicationAndKeepsOriginalAndDurableDraft(bool? expected)
    {
        Fixture fixture = Create(expected);
        string originalHash = EditDraftStore.HashFile(fixture.Source);
        VideoEditDocument document = VideoEditDocument.CreateFor(64, 64, 3000);
        document.RemovedRanges = [new(1000, 2000)];
        var drafts = new EditDraftStore(Path.Combine(fixture.Root, "drafts"));
        Guid session = Guid.NewGuid();
        await drafts.WriteAsync(session, 7, "video", JsonSerializer.Serialize(document, JsonDefaults.Readable),
            new() { ["sourcePath"] = fixture.Source, ["sourceSha256"] = originalHash }, new Dictionary<string, byte[]>(), () => true);
        string before = Assert.Single(drafts.ReadAvailable()).Draft.Document;
        using VideoEditSession edit = fixture.Library.BeginEdit(fixture.Record);
        string stage = fixture.Library.CreateRenderStagingPath(fixture.Record);
        RecordingAudioEditingTests.Encode(stage, audioSeconds: 0);
        document.RemovedRanges = []; // same 3s video; only missing audio makes this unacceptable.
        await Assert.ThrowsAsync<RecordingAudioPreservationException>(() =>
            fixture.Library.CommitEditAsync(fixture.Record, edit, document, stage));
        Assert.Equal(0, fixture.Record.ContentRevision);
        Assert.Equal(originalHash, EditDraftStore.HashFile(fixture.Source));
        Assert.Equal(before, Assert.Single(drafts.ReadAvailable()).Draft.Document);
        Assert.False(File.Exists(fixture.Queue.GetFilePath(fixture.Record, CaptureFileNames.VideoRendered)));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, ".video-finalize-journal.json")));
    }

    [Fact]
    public async Task RecoveredDraftReencodesAudioAndKeepsTheImmutableSource()
    {
        Fixture fixture = Create(null);
        var drafts = new EditDraftStore(Path.Combine(fixture.Root, "drafts"));
        var document = VideoEditDocument.CreateFor(64, 64, 3000);
        document.RemovedRanges = [new(1000, 2000)];
        string hash = EditDraftStore.HashFile(fixture.Source);
        await drafts.WriteAsync(Guid.NewGuid(), 1, "video", JsonSerializer.Serialize(document, JsonDefaults.Readable),
            new() { ["sourcePath"] = fixture.Source, ["sourceSha256"] = hash }, new Dictionary<string, byte[]>(), () => true);
        EditDraft recovered = Assert.Single(drafts.ReadAvailable()).Draft;
        VideoEditDocument loaded = JsonSerializer.Deserialize<VideoEditDocument>(recovered.Document, JsonDefaults.Readable)!;
        string output = fixture.Library.CreateRenderStagingPath(fixture.Record);
        await StaThreadTask.RunAsync(() => TrimReencoder.Reencode(fixture.Source, output,
            loaded.TrimInMs, loaded.TrimOutMs, new(fixture.Source, 3000, 10, 30, 64, 64),
            options => new MediaFoundationVideoEncoder(options with { AllowHardwareEncoder = false }, NullLogger.Instance),
            NullLogger.Instance, removedRanges: loaded.RemovedRanges, renderOptions: new() { PreferHardwareDecode = false }));
        new RecordingAudioExpectation(true, 48_000, 2).Validate(output, 2000);
        Assert.Equal(hash, EditDraftStore.HashFile(fixture.Source));
        Assert.Equal(1, Assert.Single(drafts.ReadAvailable()).Draft.Revision);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void CommitJournalChecksBothNewExpectationAndLegacySource(bool legacy, bool hasAudio, bool corruptSource)
    {
        Fixture fixture = Create(null);
        string rendered = fixture.Queue.GetFilePath(fixture.Record, CaptureFileNames.VideoRendered);
        RecordingAudioEditingTests.Encode(rendered, hasAudio ? 3 : 0);
        string thumbnail = fixture.Queue.GetFilePath(fixture.Record, CaptureFileNames.Thumbnail);
        string edits = fixture.Queue.GetFilePath(fixture.Record, CaptureFileNames.VideoEdits);
        File.WriteAllBytes(thumbnail, [1]);
        File.WriteAllText(edits, JsonSerializer.Serialize(VideoEditDocument.CreateFor(64, 64, 3000), JsonDefaults.Readable));
        long bytes = new[] { fixture.Source, rendered, thumbnail, edits }.Sum(path => new FileInfo(path).Length);
        object Snapshot(long revision) => new
        {
            DurationMs = 3000, HasAnnotations = false, TotalBytes = bytes,
            ContentRevision = revision, UpdatedAt = fixture.Record.UpdatedAt,
        };
        var journal = new JsonObject
        {
            ["RecordId"] = fixture.Record.Id.ToString(), ["ExpectedContentRevision"] = 0,
            ["RenderStageFile"] = ".render-" + Guid.NewGuid().ToString("N") + ".mp4",
            ["ThumbnailStageFile"] = ".thumb-" + Guid.NewGuid().ToString("N") + ".next.jpg",
            ["EditsStageFile"] = ".edits-" + Guid.NewGuid().ToString("N") + ".next.json",
            ["Previous"] = JsonSerializer.SerializeToNode(Snapshot(0)),
            ["Next"] = JsonSerializer.SerializeToNode(Snapshot(1)),
        };
        if (!legacy) journal["ExpectedAudio"] = JsonSerializer.SerializeToNode(new RecordingAudioExpectation(true, 48_000, 2));
        string journalPath = Path.Combine(fixture.Directory, ".video-finalize-journal.json");
        File.WriteAllText(journalPath, journal.ToJsonString());
        File.WriteAllText(Path.Combine(fixture.Directory, ".video-finalize-commit-ready"), "ready");
        if (corruptSource)
            File.WriteAllBytes(fixture.Source, new byte[checked((int)new FileInfo(fixture.Source).Length)]);
        string sourceHash = EditDraftStore.HashFile(fixture.Source);
        // Keep recovery artifacts: this seam models an unavailable cleanup service, and the
        // assertions distinguish validation failure (revision 0) from valid recovery (revision 1).
        var recovered = new VideoLibraryService(fixture.Queue, fixture.Paths,
            NullLogger<VideoLibraryService>.Instance,
            recoverableRemovalForTest: _ => throw new IOException("Retain diagnostic recovery artifacts"));
        Assert.Equal(hasAudio && !corruptSource ? 1 : 0, fixture.Record.ContentRevision);
        Assert.True(File.Exists(journalPath));
        Assert.True(recovered.IsBusy(fixture.Record.Id));
        Assert.Equal(sourceHash, EditDraftStore.HashFile(fixture.Source));
    }

    [Fact]
    public async Task CaptureExpectationSurvivesFailedPublicationAndRecoveryRefusesLostAudio()
    {
        string root = OwnedTestDirectory.Create("mc-audio-capture-validation-");
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        using VideoCaptureWriteSession capture = library.BeginCapture(10);
        RecordingResult silent = RecordingAudioEditingTests.Encode(capture.StagingOutputPath, 0);
        await Assert.ThrowsAsync<RecordingAudioPreservationException>(() => library.CompleteCaptureAsync(capture,
            silent with { ExpectedAudio = true }));
        Assert.True(File.Exists(capture.StagingOutputPath));
        string pending = queue.GetFilePath(capture.Record, CaptureFileNames.VideoPending);
        CaptureRecord metadata = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(pending), JsonDefaults.Readable)!;
        Assert.True(metadata.ExpectedAudio);
        var recovered = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        Assert.True(File.Exists(capture.StagingOutputPath));
        Assert.False(File.Exists(library.SourcePath(capture.Record)));
        Assert.Empty(queue.Records);
    }

    private sealed record Fixture(string Root, AppPaths Paths, CaptureQueue Queue, VideoLibraryService Library,
        CaptureRecord Record, string Directory, string Source);

    private static Fixture Create(bool? expected)
    {
        string root = OwnedTestDirectory.Create("mc-audio-library-");
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        var record = new CaptureRecord
        {
            MediaKind = CaptureMediaKind.Video, Width = 64, Height = 64, DurationMs = 3000,
            FrameRate = 10, FrameCount = 30, ExpectedAudio = expected,
        };
        queue.Add(record);
        string directory = queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        string source = library.SourcePath(record);
        RecordingAudioEditingTests.Encode(source);
        return new(root, paths, queue, library, record, directory, source);
    }
}
