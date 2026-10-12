using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryPreparedSearchTests
{
    [Fact]
    public void PreparedGalleryQueryKeepsExportFilenameFallbackAndDeterministicOrder()
    {
        string unused = Path.Combine(Path.GetTempPath(), "mc-prepared-search-" + Guid.NewGuid().ToString("N"));
        var queue = new CaptureQueue(AppPaths.CreateForRoot(unused), new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var first = new CaptureRecord { Id = new Guid(1, 0, 0, new byte[8]), CreatedAt = now,
            Title = "Developer", OcrText = "cross field needle" };
        var second = new CaptureRecord { Id = new Guid(2, 0, 0, new byte[8]), CreatedAt = now,
            Title = "", OcrText = "" };
        var third = new CaptureRecord { Id = new Guid(3, 0, 0, new byte[8]), CreatedAt = now.AddHours(-1),
            Title = "Developer", OcrText = "needle" };
        foreach (CaptureRecord record in new[] { third, second, first }) queue.Add(record);
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        Guid[] Search(string query, GallerySortOrder order = GallerySortOrder.Newest) =>
            controller.BuildGroups(query, now, sort: order).SelectMany(group => group.Records).Select(record => record.Id).ToArray();
        Assert.Equal([first.Id, third.Id], Search(" developer\tneedle "));
        Assert.Equal([third.Id, first.Id], Search("developer needle", GallerySortOrder.Oldest));
        // Timestamp filename is outside searchable metadata; its culture-aware fallback remains.
        Assert.Equal([second.Id], Search("mycapture_20261012_120000.PNG"));
        Assert.Empty(Search("developer absent"));
        first.OcrText = "changed";
        Assert.Equal([third.Id], Search("developer needle"));
        Assert.Equal(3, queue.Count);
        Assert.False(Directory.Exists(unused));
    }
}
