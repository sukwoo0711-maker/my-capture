using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureQueueVideoTests
{
    [Fact]
    public void Load_RetainsVideoWithSourceAndDropsVideoWithoutSource()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = CreateQueue(workspace);
        DateTimeOffset now = DateTimeOffset.Now;

        CaptureRecord complete = CreateVideoRecord(now);
        CreateRecordDirectory(workspace, complete);
        File.WriteAllBytes(
            Path.Combine(workspace.Paths.CapturesRoot, complete.RelativeDirectory, CaptureFileNames.VideoSource),
            [0, 1, 2, 3]);
        queue.Add(complete);

        CaptureRecord missingSource = CreateVideoRecord(now.AddSeconds(-1));
        CreateRecordDirectory(workspace, missingSource);
        queue.Add(missingSource);
        queue.Save();

        CaptureQueue reloaded = CreateQueue(workspace);
        reloaded.Load();

        CaptureRecord retained = Assert.Single(reloaded.Records);
        Assert.Equal(complete.Id, retained.Id);
        Assert.Equal(CaptureMediaKind.Video, retained.MediaKind);
        Assert.True(retained.IsVideo);
        Assert.DoesNotContain(reloaded.Records, record => record.Id == missingSource.Id);
    }

    [Fact]
    public void Load_RebuildsCompletedVideoFromPendingSidecar()
    {
        using var workspace = new TempWorkspace();
        CaptureRecord pending = CreateVideoRecord(DateTimeOffset.Now);
        string directory = CreateRecordDirectory(workspace, pending);
        File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.VideoSource), [9, 8, 7]);
        File.WriteAllText(
            Path.Combine(directory, CaptureFileNames.VideoPending),
            JsonSerializer.Serialize(pending, JsonDefaults.Readable));

        CaptureQueue queue = CreateQueue(workspace);
        queue.Load();

        CaptureRecord recovered = Assert.Single(queue.Records);
        Assert.Equal(pending.Id, recovered.Id);
        Assert.Equal(CaptureMediaKind.Video, recovered.MediaKind);
        Assert.Equal(pending.DurationMs, recovered.DurationMs);
        Assert.Equal(pending.RelativeDirectory, recovered.RelativeDirectory);
    }

    [Fact]
    public void SearchHaystack_CombinesMetadataWithMixedImageAndVideoTerms()
    {
        var image = new CaptureRecord
        {
            MediaKind = CaptureMediaKind.Image,
            Title = "release checklist",
            SourceWindowTitle = "Notes",
            OcrText = "approved",
        };
        var video = new CaptureRecord
        {
            MediaKind = CaptureMediaKind.Video,
            Title = "demo",
            SourceWindowTitle = "MyCapture",
        };

        Assert.Contains("release checklist", image.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("approved", image.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("이미지", image.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("screenshot", image.SearchHaystack, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("demo", video.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("동영상", video.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("비디오", video.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("video", video.SearchHaystack, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recording", video.SearchHaystack, StringComparison.OrdinalIgnoreCase);
    }

    private static CaptureQueue CreateQueue(TempWorkspace workspace) =>
        new(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    private static CaptureRecord CreateVideoRecord(DateTimeOffset createdAt)
    {
        var record = new CaptureRecord
        {
            MediaKind = CaptureMediaKind.Video,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Width = 1920,
            Height = 1080,
            DurationMs = 2_500,
            FrameRate = 30,
            FrameCount = 75,
            TotalBytes = 4,
        };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, createdAt);
        return record;
    }

    private static string CreateRecordDirectory(TempWorkspace workspace, CaptureRecord record)
    {
        string directory = Path.Combine(workspace.Paths.CapturesRoot, record.RelativeDirectory);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
