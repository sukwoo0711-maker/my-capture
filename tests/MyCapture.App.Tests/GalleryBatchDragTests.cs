using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryBatchDragTests
{
    [Fact]
    public async Task MixedBatch_StagesUniqueNamesWithUnchangedSources_AndHoldsAllLeasesUntilDropEnds()
    {
        string root = Path.Combine(Path.GetTempPath(), "MyCapture-batch-drag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var records = Enumerable.Range(0, 4).Select(i => new CaptureRecord
            { Id = Guid.NewGuid(), MediaKind = i < 2 ? CaptureMediaKind.Image : CaptureMediaKind.Video }).ToArray();
            var originals = new Dictionary<string, byte[]>();
            foreach (CaptureRecord record in records)
            {
                queue.Add(record);
                Directory.CreateDirectory(queue.GetDirectory(record));
                string path = queue.GetFilePath(record, record.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Rendered);
                byte[] payload = new byte[200_000];
                Random.Shared.NextBytes(payload);
                File.WriteAllBytes(path, payload);
                originals.Add(path, payload);
            }
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"), () => DateTimeOffset.UnixEpoch);
            using (GalleryDragExportService.PreparedDrag batch = await service.PrepareBatchAsync(records, CancellationToken.None))
            {
                Assert.Equal(4, batch.Paths.Select(Path.GetFileName).Distinct().Count());
                Assert.All(records, record => Assert.False(queue.Remove(record.Id)));
                for (int i = 0; i < records.Length; i++)
                {
                    string source = queue.GetFilePath(records[i], records[i].IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Rendered);
                    Assert.NotEqual(source, batch.Paths[i]);
                    Assert.Equal(originals[source], File.ReadAllBytes(batch.Paths[i]));
                    Assert.Equal(originals[source], File.ReadAllBytes(source));
                }
            }
            Assert.All(records, record => Assert.True(queue.Remove(record.Id)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledPreparation_ReleasesLeaseAndDoesNotCreateLateExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "MyCapture-batch-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            Directory.CreateDirectory(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var record = new CaptureRecord { Id = Guid.NewGuid() };
            queue.Add(record);
            var service = new GalleryDragExportService(queue, Path.Combine(root, "staging"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareBatchAsync([record], new CancellationToken(true)));
            Assert.False(Directory.Exists(service.StagingRoot));
            Assert.True(queue.Remove(record.Id));
        }
        finally { Directory.Delete(root, true); }
    }
}
