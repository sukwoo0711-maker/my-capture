using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureRetentionTests
{
    [Fact]
    public void RetentionUsesCreationFifoAndPreservesPinsLeasesAndRecentRecords()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CaptureRecord oldest = Add(queue, now.AddDays(-10));
        oldest.UpdatedAt = now; // Re-editing does not extend the seven-day creation policy.
        CaptureRecord boundary = Add(queue, now.AddDays(-7));
        CaptureRecord recent = Add(queue, now.AddDays(-7).AddTicks(1));
        CaptureRecord pinned = Add(queue, now.AddDays(-9));
        pinned.IsPinned = true;
        CaptureRecord editing = Add(queue, now.AddDays(-8));
        CaptureRecord video = Add(queue, now.AddDays(-20));
        video.MediaKind = CaptureMediaKind.Video;
        var evictions = new List<Guid>();
        queue.Evicted += (_, e) =>
        {
            Assert.Equal("retention-age", e.Reason);
            evictions.Add(e.Record.Id);
        };
        using (queue.AcquireEvictionLease(editing.Id))
        {
            Assert.Equal(2, queue.ExpireHistory(now));
            Assert.Equal(new[] { oldest.Id, boundary.Id }, evictions);
            Assert.NotNull(queue.Find(recent.Id));
            Assert.NotNull(queue.Find(pinned.Id));
            Assert.NotNull(queue.Find(editing.Id));
        }
        Assert.Equal(1, queue.ExpireHistory(now));
        Assert.Null(queue.Find(editing.Id));
        Assert.NotNull(queue.Find(video.Id));
        Assert.Equal(3, queue.Count);
    }

    [Fact]
    public void RetentionDefersDuringRecoveryAndNeverTouchesQuickSaveExports()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        Directory.CreateDirectory(workspace.Paths.QuickSaveRoot);
        string exported = Path.Combine(workspace.Paths.QuickSaveRoot, "keep.png");
        File.WriteAllBytes(exported, [1, 2, 3]);
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CaptureRecord expired = Add(queue, now.AddDays(-8));
        string managed = queue.GetDirectory(expired);
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(queue.GetFilePath(expired, CaptureFileNames.Original), [4, 5, 6]);
        queue.Evicted += (_, e) =>
        {
            // Same checked directory resolution used by the shell's deletion handler.
            string directory = queue.GetDirectory(e.Record);
            Assert.StartsWith(workspace.Paths.CapturesRoot + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
            Assert.Equal(managed, directory);
            Directory.Delete(directory, recursive: true);
        };
        using (queue.SuspendEviction())
        {
            Assert.Equal(0, queue.ExpireHistory(now));
        }
        Assert.Equal(1, queue.ExpireHistory(now));
        Assert.True(File.Exists(exported));
        Assert.False(Directory.Exists(managed));
        Assert.True(File.Exists(workspace.Paths.IndexFile));
    }

    private static CaptureRecord Add(CaptureQueue queue, DateTimeOffset created)
    {
        var record = new CaptureRecord { Id = Guid.NewGuid(), CreatedAt = created, UpdatedAt = created, TotalBytes = 10 };
        queue.Add(record);
        return record;
    }
}
