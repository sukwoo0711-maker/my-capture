using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.Core.Tests;

/// <summary>
/// #221: a publication copies its records on the owner thread and the writer serializes the
/// copies. These tests hold what that move rests on: the copy is detached, a queued job writes the
/// state of its own call, the bytes match the string writer, a serializer failure writes no file,
/// and the caller no longer allocates the index text.
/// </summary>
public sealed class CaptureRecordPublicationSnapshotTests(ITestOutputHelper output)
{
    private const string Late = "changed after the call";

    [Fact]
    public void EveryCaptureRecordField_IsSafeToShareWithAShallowCopy()
    {
        // MemberwiseClone is a detached snapshot only while every field is an immutable string or a
        // value without references. A list, array or mutable object would stay shared with the owner.
        var shared = new List<string>();
        int fields = 0;
        for (Type? type = typeof(CaptureRecord); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                fields++;
                if (field.FieldType == typeof(string) || !ContainsReferences(field.FieldType)) continue;
                shared.Add($"{type.Name}.{field.Name}: {field.FieldType}");
            }
        }

        Assert.True(fields > 30, $"Only {fields} fields were found; the check no longer sees the record state.");
        Assert.Empty(shared);
    }

    private static bool ContainsReferences(Type type) =>
        (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(type).Invoke(null, null)!;

    [Fact]
    public void PublicationCopy_KeepsEveryValue_AndNotLaterChanges()
    {
        CaptureRecord record = NewRecord();
        Fill(record);
        string before = JsonSerializer.Serialize(record, JsonDefaults.Readable);

        CaptureRecord copy = record.CloneForPublication();
        Assert.NotSame(record, copy);
        Assert.Equal(before, JsonSerializer.Serialize(copy, JsonDefaults.Readable));
        Assert.Equal(record.FolderDestinationResolved, copy.FolderDestinationResolved);

        record.OcrText = Late; record.Tags = Late; record.Title = Late;
        record.FolderId = Guid.NewGuid(); record.DurationMs = 1; record.IsPinned = !record.IsPinned;
        Assert.Equal(before, JsonSerializer.Serialize(copy, JsonDefaults.Readable));
    }

    [Fact]
    public async Task QueuedJobs_WriteTheStateOfTheirOwnCall()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        CaptureRecord record = AddRecord(queue);
        CaptureRecord other = AddRecord(queue);
        string index = workspace.Paths.IndexFile;
        string meta = queue.GetFilePath(record, CaptureFileNames.Meta);
        string otherMeta = queue.GetFilePath(other, CaptureFileNames.Meta);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int writes = 0;
        var observed = new List<(string Path, string Content)>();
        queue.BeforePublicationWriteForTest = (path, content) =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            lock (observed) observed.Add((path, content));
        };

        // The first job holds the writer, so every following call queues behind it.
        Task held = queue.SaveAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using CaptureWriteReservation reservation = await queue.ReservePublicationAsync();
        var calls = new List<Task> { held };
        try
        {
            record.OcrText = "at save";
            calls.Add(queue.SaveAsync());
            record.OcrText = "at meta";
            calls.Add(queue.SaveRecordMetaOrThrowAsync(record));
            record.OcrText = "at publish";
            calls.Add(queue.PublishRecordAsync(record, reservation));
            other.Title = "at tags";
            calls.Add(queue.UpdateTagsAsync([other.Id], [new CaptureTagChange("tagged", true)]));

            record.OcrText = Late;
            other.Title = Late;
            other.Tags = Late;
            Assert.All(calls, call => Assert.False(call.IsCompleted));
        }
        finally { release.Set(); }

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([index, index, meta, meta, index, otherMeta, index], observed.Select(write => write.Path));
        Assert.DoesNotContain(observed, write => write.Content.Contains(Late, StringComparison.Ordinal));
        Assert.Equal([null, "at save", "at meta", "at publish", "at publish", null, "at publish"],
            observed.Select(write => StringOf(write, record.Id, "ocrText")));
        Assert.Equal([null, null, null, null, null, "at tags", "at tags"],
            observed.Select(write => StringOf(write, other.Id, "title")));
        Assert.Equal("tagged", StringOf(observed[^1], other.Id, "tags"));
    }

    [Fact]
    public async Task EveryEntryPoint_WritesTheBytesOfTheStringWriter()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        CaptureRecord first = AddRecord(queue);
        Fill(first);
        CaptureRecord second = AddRecord(queue);
        CaptureRecord third = AddRecord(queue);
        third.Tags = "회의, 업무";
        third.SourceVideoId = Guid.NewGuid();
        third.SourceAssetTimeMs = 0.1;

        await queue.SaveAsync();
        AssertIndexMatchesStringWriter(queue, workspace);

        using (CaptureWriteReservation reservation = await queue.ReservePublicationAsync())
            await queue.PublishRecordAsync(first, reservation);
        AssertIndexMatchesStringWriter(queue, workspace);
        AssertMetaMatchesStringWriter(queue, first);

        await queue.SaveRecordMetaOrThrowAsync(second);
        AssertMetaMatchesStringWriter(queue, second);

        await queue.UpdateTagsAsync([first.Id, second.Id], [new CaptureTagChange("자료 \"원본\"", true)]);
        AssertIndexMatchesStringWriter(queue, workspace);
        AssertMetaMatchesStringWriter(queue, first);
        AssertMetaMatchesStringWriter(queue, second);

        await queue.SaveSearchAsync("찾기 \"업무\"", "tag:회의 😀 <a&b>");
        AssertIndexMatchesStringWriter(queue, workspace);

        CaptureFolder folder = await queue.CreateFolderAsync("자료 모음");
        Assert.Equal(2, await queue.MoveCapturesAsync([first.Id, third.Id], folder.Id));
        Assert.NotNull(queue.SerializeFolderRecoveryForTest());
        AssertIndexMatchesStringWriter(queue, workspace);
        AssertMetaMatchesStringWriter(queue, first);
        AssertMetaMatchesStringWriter(queue, third);

        Assert.Equal(1, (await queue.RemoveManyAsync([second.Id])).RemovedCount);
        AssertIndexMatchesStringWriter(queue, workspace);

        var restored = NewRecord();
        restored.OcrText = "복원 \u2028 끝";
        Directory.CreateDirectory(Path.GetDirectoryName(queue.GetFilePath(restored, CaptureFileNames.Meta))!);
        Assert.Single(await queue.RestoreRecordsAsync([restored]));
        AssertIndexMatchesStringWriter(queue, workspace);
        AssertMetaMatchesStringWriter(queue, restored);
    }

    [Fact]
    public async Task SerializerFailure_WritesNoFileOfItsJob_AndLaterJobsStillPublish()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        CaptureRecord record = AddRecord(queue);
        CaptureRecord other = AddRecord(queue);
        record.OcrText = "durable";
        using (CaptureWriteReservation reservation = await queue.ReservePublicationAsync())
            await queue.PublishRecordAsync(record, reservation);
        string meta = queue.GetFilePath(record, CaptureFileNames.Meta);
        byte[] indexBefore = File.ReadAllBytes(workspace.Paths.IndexFile);
        byte[] metaBefore = File.ReadAllBytes(meta);
        var written = new List<string>();
        queue.BeforePublicationWriteForTest = (path, _) => { lock (written) written.Add(path); };

        // This record's metadata serializes, but the index cannot: JSON has no NaN number.
        record.OcrText = "not durable";
        other.DurationMs = double.NaN;
        using (CaptureWriteReservation reservation = await queue.ReservePublicationAsync())
            await Assert.ThrowsAnyAsync<ArgumentException>(() => queue.PublishRecordAsync(record, reservation));
        Assert.Empty(written);
        Assert.Equal(indexBefore, File.ReadAllBytes(workspace.Paths.IndexFile));
        Assert.Equal(metaBefore, File.ReadAllBytes(meta));

        // The failed job returned its slot and left the writer usable.
        var slots = new List<CaptureWriteReservation>();
        try
        {
            for (int i = 0; i < 8; i++) slots.Add(await queue.ReservePublicationAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { foreach (CaptureWriteReservation slot in slots) slot.Dispose(); }
        other.DurationMs = 0;
        record.OcrText = "recovered";
        using (CaptureWriteReservation reservation = await queue.ReservePublicationAsync())
            await queue.PublishRecordAsync(record, reservation).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([meta, workspace.Paths.IndexFile], written);
        Assert.Contains("recovered", File.ReadAllText(workspace.Paths.IndexFile));
        Assert.Contains("recovered", File.ReadAllText(meta));
    }

    [Fact]
    public async Task OwnerThread_NoLongerAllocatesTheIndexText()
    {
        // The string writer serialized the whole index on the calling owner thread: at least the
        // index size, and twice that as UTF-16 text. A record copy is a few hundred bytes.
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        string ocr = new('a', 32 * 1024);
        CaptureRecord record = null!;
        for (int i = 0; i < 200; i++)
        {
            record = AddRecord(queue);
            record.OcrText = ocr + i;
        }

        // Warm both paths so the measurement holds no first-call work.
        await queue.SaveAsync();
        using (CaptureWriteReservation reservation = await queue.ReservePublicationAsync())
            await queue.PublishRecordAsync(record, reservation);
        long indexBytes = new FileInfo(workspace.Paths.IndexFile).Length;
        Assert.True(indexBytes > 200L * 32 * 1024, $"The index is only {indexBytes:N0} bytes.");

        long before = GC.GetAllocatedBytesForCurrentThread();
        Task save = queue.SaveAsync();
        long saveAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await save;

        using CaptureWriteReservation next = await queue.ReservePublicationAsync();
        before = GC.GetAllocatedBytesForCurrentThread();
        Task publish = queue.PublishRecordAsync(record, next);
        long publishAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await publish;

        output.WriteLine($"index {indexBytes:N0} B; caller allocation SaveAsync {saveAllocated:N0} B, PublishRecordAsync {publishAllocated:N0} B");
        Assert.True(saveAllocated < indexBytes / 10,
            $"SaveAsync allocated {saveAllocated:N0} bytes on its caller for a {indexBytes:N0}-byte index.");
        Assert.True(publishAllocated < indexBytes / 10,
            $"PublishRecordAsync allocated {publishAllocated:N0} bytes on its caller for a {indexBytes:N0}-byte index.");
    }

    private static void AssertIndexMatchesStringWriter(CaptureQueue queue, TempWorkspace workspace)
    {
        Assert.Equal(Encoding.UTF8.GetBytes(queue.SerializeIndexForTest()), File.ReadAllBytes(workspace.Paths.IndexFile));
        string recovery = Path.Combine(workspace.Paths.DataRoot, "library-folders.json");
        if (queue.SerializeFolderRecoveryForTest() is { } folders)
            Assert.Equal(Encoding.UTF8.GetBytes(folders), File.ReadAllBytes(recovery));
        else
            Assert.False(File.Exists(recovery));
    }

    private static void AssertMetaMatchesStringWriter(CaptureQueue queue, CaptureRecord record) =>
        Assert.Equal(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonDefaults.Readable)),
            File.ReadAllBytes(queue.GetFilePath(record, CaptureFileNames.Meta)));

    private static string? StringOf((string Path, string Content) write, Guid id, string property)
    {
        using JsonDocument document = JsonDocument.Parse(write.Content);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("records", out JsonElement records))
            root = records.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == id);
        else if (root.GetProperty("id").GetGuid() != id)
            return null;
        return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text ? text : null;
    }

    private static void Fill(CaptureRecord record)
    {
        record.OcrText = "따옴표 \" 역슬래시 \\ 줄바꿈 \r\n 탭 \t 이모지 😀 <b>&amp;</b> \u2028 끝";
        record.OcrLanguage = "ko";
        record.OcrQuality = OcrQuality.Accurate;
        record.OcrContentRevision = 3;
        record.Title = "제목 \"인용\"";
        record.SourcePageUrl = "https://example.test/a?b=1&c=<d>";
        record.SourceWindowTitle = "창 'title'";
        record.SourceAppName = "앱";
        record.DurationMs = 1234.5678;
        record.DpiScale = 1.25;
        record.Width = 1920; record.Height = 1080; record.TotalBytes = 123_456;
        record.IsPinned = true; record.HasAnnotations = true;
        record.RetentionStartedAt = new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.FromHours(9));
        record.SourceFrameRequestedAt = new DateTimeOffset(2026, 11, 6, 0, 30, 0, TimeSpan.Zero);
        record.FolderPinBeforeAssignment = false;
        record.InitialRenderShowsSourceWindowTitle = true;
    }

    private static CaptureQueue NewQueue(TempWorkspace workspace) =>
        new(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    private static CaptureRecord NewRecord()
    {
        var record = new CaptureRecord();
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, record.CreatedAt);
        return record;
    }

    private static CaptureRecord AddRecord(CaptureQueue queue)
    {
        CaptureRecord record = NewRecord();
        queue.Add(record);
        return record;
    }
}
