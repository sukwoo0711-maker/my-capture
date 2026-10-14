using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureTagTests
{
    [Fact]
    public void Selection_ComputesAllSomeNone_AndDoesNotDoubleCountVariants()
    {
        string[] tags = ["Work, work, common", "common, personal", "COMMON"];
        Assert.True(CaptureOrganization.TagState(tags, "common"));
        Assert.Null(CaptureOrganization.TagState(tags, "work"));
        Assert.False(CaptureOrganization.TagState(tags, "missing"));
        Assert.False(CaptureOrganization.TagState([], "common"));
        var states = CaptureOrganization.SelectionTags(tags).ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, states.Count);
        Assert.True(states["common"].IsChecked);
        Assert.Null(states["personal"].IsChecked);
    }

    [Fact]
    public void Counts_SortByCaptureCountThenName_WithStableCaseAndSeparators()
    {
        var counts = CaptureOrganization.CountTags(["#zoo; alpha; ZOO", "beta，Alpha", "beta\nzoo", null]);
        Assert.Equal([new CaptureTagCount("Alpha", 2), new CaptureTagCount("beta", 2), new CaptureTagCount("zoo", 2)], counts);
        Assert.Equal(counts, CaptureOrganization.CountTags([null, "beta\nzoo", "beta，Alpha", "#zoo; alpha; ZOO"]));
        Assert.Equal([new CaptureTagCount("zoo", 2), new CaptureTagCount("alpha", 1)],
            CaptureOrganization.CountTags(["alpha, zoo", "zoo"]));
    }

    [Fact]
    public void Apply_NormalizesAndRemovesBeforeAdding_WithoutReplacingUnchangedTags()
    {
        string full = string.Join(", ", Enumerable.Range(0, 12).Select(i => $"tag{i}"));
        var result = CaptureOrganization.ApplyTagChanges(full, [new(" #new ", true), new("TAG0", false)]);
        Assert.False(result.LimitReached);
        Assert.Equal(12, CaptureOrganization.SplitTags(result.Tags).Count);
        Assert.DoesNotContain("tag0", CaptureOrganization.SplitTags(result.Tags));
        Assert.Contains("new", CaptureOrganization.SplitTags(result.Tags));
        var limited = CaptureOrganization.ApplyTagChanges(full, [new("tag0", true), new("extra", true)]);
        Assert.True(limited.LimitReached);
        Assert.Equal(full, limited.Tags);
        Assert.Equal("Keep, New", CaptureOrganization.ApplyTagChanges("#Keep; old", [new("OLD", false), new(" New ", true)]).Tags);
    }

    [Fact]
    public void Intents_AllowLargeUnion_LastIntentWins_AndEnforceTagLength()
    {
        var changes = Enumerable.Range(0, 20).Select(i => new CaptureTagChange($"tag{i}", true));
        var result = CaptureOrganization.ApplyTagChanges("", changes);
        Assert.True(result.LimitReached);
        Assert.Equal(12, CaptureOrganization.SplitTags(result.Tags).Count);
        Assert.Equal("", CaptureOrganization.ApplyTagChanges("work", [new("Work", true), new("WORK", false)]).Tags);
        Assert.Equal(new string('a', 32), CaptureOrganization.ApplyTagChanges("", [new(new string('a', 32), true)]).Tags);
        foreach (string invalid in new[] { new string('a', 33), "a\tb", "a,b", "# " })
            Assert.Throws<ArgumentException>(() => CaptureOrganization.ApplyTagChanges("keep", [new(invalid, true)]));
    }

    [Fact]
    public async Task Batch_WritesOneIndexAndChangedMetadata_ReportsLimitsAndMissingIds()
    {
        var (queue, paths) = Queue();
        var full = Record(string.Join(", ", Enumerable.Range(0, 12).Select(i => $"tag{i}")));
        var partial = Record("keep");
        queue.Add(full); queue.Add(partial);
        var writes = new List<string>();
        queue.BeforePublicationWriteForTest = (path, _) => writes.Add(path);
        var result = await queue.UpdateTagsAsync([full.Id, partial.Id, partial.Id, Guid.NewGuid()], [new("#new", true)]);
        Assert.Equal(new CaptureBatchTagResult(1, 1, 1), result);
        Assert.Equal(1, writes.Count(path => path == paths.IndexFile));
        Assert.Equal(2, writes.Count);
        Assert.Equal("keep, new", partial.Tags);
        using var index = JsonDocument.Parse(File.ReadAllText(paths.IndexFile));
        Assert.Contains("keep, new", index.RootElement.GetRawText());
        var meta = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(queue.GetFilePath(partial, CaptureFileNames.Meta)), JsonDefaults.Readable);
        Assert.Equal(partial.Tags, meta!.Tags);
        writes.Clear();
        Assert.Equal(0, (await queue.UpdateTagsAsync([partial.Id], [new("NEW", true)])).UpdatedCount);
        Assert.Empty(writes);
        await queue.UpdateTagsAsync([partial.Id], []);
        Assert.Empty(writes);
    }

    [Fact]
    public async Task FailedBatch_RollsBackTagsAndCorrectsMetadataAndIndex()
    {
        var (queue, paths) = Queue();
        var first = Record("first");
        var second = Record("second");
        queue.Add(first); queue.Add(second);
        int calls = 0;
        queue.BeforePublicationWriteForTest = (_, _) =>
        {
            if (++calls == 2) throw new IOException("Synthetic failure before write.");
        };
        await Assert.ThrowsAsync<IOException>(() => queue.UpdateTagsAsync([first.Id, second.Id], [new("added", true)]));
        Assert.Equal("first", first.Tags);
        Assert.Equal("second", second.Tags);
        Assert.DoesNotContain("added", File.ReadAllText(paths.IndexFile));
        Assert.DoesNotContain("added", File.ReadAllText(queue.GetFilePath(first, CaptureFileNames.Meta)));
    }

    [Fact]
    public async Task InvalidBatch_DoesNotMutateOrWrite_AndCancellationReleasesAdmission()
    {
        var (queue, paths) = Queue();
        var record = Record("keep");
        queue.Add(record);
        await Assert.ThrowsAsync<ArgumentException>(() => queue.UpdateTagsAsync([record.Id], [new(new string('a', 33), true)]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.UpdateTagsAsync([record.Id], [new("new", true)], cancellation.Token));
        Assert.Equal("keep", record.Tags);
        Assert.False(File.Exists(paths.IndexFile));
        Assert.Equal(1, (await queue.UpdateTagsAsync([record.Id], [new("new", true)])).UpdatedCount);
    }

    private static CaptureRecord Record(string tags) => new()
    {
        Id = Guid.NewGuid(), RelativeDirectory = Guid.NewGuid().ToString("N"), Tags = tags, IsPinned = true,
    };

    private static (CaptureQueue Queue, AppPaths Paths) Queue()
    {
        // Retain these small fixtures in ignored build output; no profile or permanent cleanup is involved.
        string root = Path.Combine(AppContext.BaseDirectory, "tag-test-fixtures", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.CreateForRoot(root);
        return (new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance), paths);
    }
}
