using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryVideoTests
{
    [Fact]
    public void VideoTile_ExposesPlaybackEditGifAndDurationSemantics()
    {
        var record = new CaptureRecord
        {
            MediaKind = CaptureMediaKind.Video,
            Width = 1920,
            Height = 1080,
            DurationMs = 65_400,
            Title = "데모 녹화",
        };
        var tile = new GalleryItemViewModel(record, _ => "missing-thumb.jpg", 320);

        Assert.True(tile.IsVideo);
        Assert.False(tile.IsImage);
        Assert.Equal("영상 편집", tile.ActionLabel);
        Assert.Equal("1:05", tile.DurationCaption);
        Assert.Contains("MP4", tile.ExportToolTip, StringComparison.Ordinal);
        Assert.Contains("동영상", tile.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoDragExport_PrefersEditedRenderAndUsesMp4Extension()
    {
        string root = NewRoot();
        string staging = Path.Combine(root, "drag-stage");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            queue.Load();
            var record = new CaptureRecord
            {
                CreatedAt = new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.FromHours(9)),
                UpdatedAt = new DateTimeOffset(2026, 9, 2, 1, 2, 3, TimeSpan.FromHours(9)),
                MediaKind = CaptureMediaKind.Video,
                Width = 320,
                Height = 180,
                DurationMs = 1000,
            };
            record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.VideoSource), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.VideoRendered), [9, 8, 7, 6]);
            queue.Add(record);

            var export = new GalleryDragExportService(
                queue,
                staging,
                () => record.CreatedAt);
            string output = export.PrepareExport(record);

            Assert.Equal(".mp4", Path.GetExtension(output));
            Assert.Equal(Path.GetFullPath(Path.Combine(directory, CaptureFileNames.VideoRendered)), output);
            // codeql[cs/path-injection] -- isolated GUID test workspace
            Assert.Equal([9, 8, 7, 6], File.ReadAllBytes(output));
            // codeql[cs/path-injection] -- isolated GUID test workspace
            Assert.False(Directory.Exists(staging));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mycapture-gallery-video-" + Guid.NewGuid().ToString("N"));
        // codeql[cs/path-injection] -- isolated GUID test workspace
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            // codeql[cs/path-injection] -- isolated GUID test workspace
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
