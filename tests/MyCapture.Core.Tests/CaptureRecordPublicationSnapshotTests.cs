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
    public void HasSameValuesAs_SeesAChangeInEveryField()
    {
        // A publication reuses the last copy of a record while this comparison holds, so a field it
        // skipped would publish a stale value. Each field is changed alone, including the changes an
        // equality operator misses: another offset at the same instant, negative zero, null.
        CaptureRecord record = NewRecord();
        Fill(record);
        Assert.True(record.HasSameValuesAs(record.CloneForPublication()));
        var missed = new List<string>();
        int changes = 0, fields = 0;
        foreach (FieldInfo field in typeof(CaptureRecord).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            fields++;
            foreach (object? value in OtherValues(field.FieldType, field.GetValue(record)))
            {
                CaptureRecord copy = record.CloneForPublication();
                field.SetValue(copy, value);
                changes++;
                if (record.HasSameValuesAs(copy)) missed.Add($"{field.Name} = {value ?? "null"}");
            }
        }

        Assert.True(fields > 40, $"Only {fields} fields were found; the check no longer sees the record state.");
        Assert.True(changes > fields, $"Only {changes} changes were tried.");
        Assert.Empty(missed);
    }

    private static IEnumerable<object?> OtherValues(Type type, object? current)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            if (current is null)
            {
                yield return Activator.CreateInstance(inner);
                yield break;
            }
            yield return null;
            foreach (object? value in OtherValues(inner, current)) yield return value;
            yield break;
        }
        switch (current)
        {
            case string text:
                yield return text + "x";
                yield return null;
                break;
            case null when type == typeof(string):
                yield return "x";
                break;
            case Guid:
                yield return Guid.NewGuid();
                break;
            case DateTimeOffset time:
                yield return time.ToOffset(time.Offset + TimeSpan.FromHours(1));
                yield return time.AddTicks(1);
                break;
            case double number:
                yield return number == 0 && !double.IsNegative(number) ? -0.0 : 0.0;
                yield return number + 1;
                break;
            case int number:
                yield return number + 1;
                break;
            case long number:
                yield return number + 1;
                break;
            case bool flag:
                yield return !flag;
                break;
            case Enum value:
                yield return Enum.GetValues(type).Cast<object>().First(other => !other.Equals(value));
                break;
            default:
                throw new InvalidOperationException($"No other value for {type}; add one so its comparison is checked.");
        }
    }

    [Fact]
    public async Task Publication_CopiesOnlyTheRecordsThatChanged_AndKeepsNoCopyOfRemovedOnes()
    {
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 5_000, MaxBytes = 1024L * 1024 * 1024 };
        var queue = new CaptureQueue(workspace.Paths, limits, NullLogger<CaptureQueue>.Instance);
        var records = new List<CaptureRecord>();
        for (int i = 0; i < 1_000; i++)
        {
            CaptureRecord added = AddRecord(queue);
            added.Width = 2; added.Height = 2;
            Directory.CreateDirectory(queue.GetDirectory(added));
            File.WriteAllBytes(queue.GetFilePath(added, CaptureFileNames.Original), [0]);
            records.Add(added);
        }
        await queue.SaveAsync();
        Assert.Equal(1_000, queue.PublicationCopyCountForTest);

        // One changed record costs one copy. A copy is about 440 bytes, the list of 1,000 references
        // 8 KB; copying every record again was 435 KB.
        // The first publication also pays one-time costs on this thread (first calls, type loading), so each
        // window publishes one more single-record change; copying every record would exceed the limit in all
        // of them (see ThreadAllocation).
        CaptureRecord target = records[^1];
        var windows = new List<long>();
        for (int window = 0; window < ThreadAllocation.Windows; window++)
        {
            target.Title = "changed " + window;
            using CaptureWriteReservation reservation = await queue.ReservePublicationAsync();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Task publication = queue.PublishRecordAsync(target, reservation);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            await publication;
            windows.Add(allocated);
            if (allocated < 16 * 1024) break;
        }
        output.WriteLine($"caller allocation after one change: {ThreadAllocation.Describe([.. windows])}");
        Assert.True(ThreadAllocation.AnyWithin([.. windows], 16 * 1024 - 1),
            $"PublishRecordAsync allocated {ThreadAllocation.Describe([.. windows])} on its caller.");
        AssertIndexMatchesStringWriter(queue, workspace);
        AssertMetaMatchesStringWriter(queue, target);

        Assert.Equal(3, (await queue.RemoveManyAsync(records.Take(3).Select(record => record.Id))).RemovedCount);
        Assert.Equal(997, queue.PublicationCopyCountForTest);
        AssertIndexMatchesStringWriter(queue, workspace);

        queue.Load();
        Assert.Equal(997, queue.Count);
        Assert.Equal(0, queue.PublicationCopyCountForTest);
        await queue.SaveAsync();
        Assert.Equal(997, queue.PublicationCopyCountForTest);
        AssertIndexMatchesStringWriter(queue, workspace);
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
    public async Task RecoverableRemoval_AndItsCorrection_PublishDetachedIndexes_WithTheStringWriterBytes()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        CaptureRecord kept = AddRecord(queue);
        Fill(kept);
        CaptureRecord removed = AddRecord(queue);
        CaptureRecord restored = AddRecord(queue);
        await queue.SaveAsync();
        var rolledBack = new List<Guid>();
        queue.PrepareRemoval = (record, _) => new IndexOnlyRemoval(() => { lock (rolledBack) rolledBack.Add(record.Id); });
        int serialized = 0;
        queue.IndexSerializedForTest = (_, _) => Interlocked.Increment(ref serialized);

        // A recycled removal publishes a detached index, as every other entry point does.
        Assert.Equal(1, (await queue.RemoveManyPermanentlyAsync([removed.Id])).RemovedCount);
        Assert.Equal(1, Volatile.Read(ref serialized));
        AssertIndexMatchesStringWriter(queue, workspace);

        // A failed publication rolls the move back and publishes a corrective index the same way.
        int writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) =>
        {
            if (Interlocked.Increment(ref writes) == 1) throw new IOException("The disk is full.");
        };
        await Assert.ThrowsAsync<IOException>(() => queue.RemoveManyPermanentlyAsync([restored.Id]));
        Assert.Equal([restored.Id], rolledBack);
        Assert.NotNull(queue.Find(restored.Id));
        Assert.Equal(3, Volatile.Read(ref serialized));
        AssertIndexMatchesStringWriter(queue, workspace);
    }

    [Fact]
    public async Task IndexBytes_StayThoseOfTheStringWriter_WhenTheIndexOutgrowsItsBuffer()
    {
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        CaptureRecord oldest = AddRecord(queue);
        AddRecord(queue);
        await queue.SaveAsync();
        AssertIndexMatchesStringWriter(queue, workspace);

        // The oldest record ends the index. Its text, over 64 K characters, escapes to several times
        // the buffer the last small index sized, so the writer must grow it in place.
        oldest.OcrText = string.Concat(Enumerable.Repeat("가나다 \"인용\" \\ \u0001\u001f\u007f <b>&amp;</b> 😀 \u2028\r\n\t끝", 3_000));
        Assert.True(oldest.OcrText.Length > 64 * 1024);
        await queue.SaveAsync();
        AssertIndexMatchesStringWriter(queue, workspace);

        oldest.OcrText = "짧음";
        await queue.SaveAsync();
        AssertIndexMatchesStringWriter(queue, workspace);
    }

    private sealed class IndexOnlyRemoval(Action rolledBack) : ICaptureRemovalTransaction
    {
        public bool Rollback()
        {
            rolledBack();
            return true;
        }
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

    [Theory]
    [InlineData(300, 0)]
    [InlineData(300, 4 * 1024)]
    [InlineData(1000, 0)]
    [InlineData(100, 64 * 1024)]
    public async Task Writer_SerializesTheIndexIntoOneBufferOfAboutItsLength(int records, int ocrChars)
    {
        // Each job runs on a new thread whose pools start empty: SerializeToUtf8Bytes grew a pooled
        // buffer from 16 KB to the index size and then copied it out, two to three index lengths a
        // job. A buffer sized from the previous index allocates about one, and so does the first job
        // after a load.
        using var workspace = new TempWorkspace();
        var limits = new QueueSettings { MaxItems = 5_000, MaxBytes = 1024L * 1024 * 1024 };
        var queue = new CaptureQueue(workspace.Paths, limits, NullLogger<CaptureQueue>.Instance);
        string ocr = new('a', ocrChars);
        CaptureRecord record = null!;
        for (int i = 0; i < records; i++)
        {
            record = AddRecord(queue);
            if (ocrChars > 0) record.OcrText = ocr + i;
            // Load accepts the index only with valid sizes and keeps records whose original exists.
            record.Width = 2; record.Height = 2;
            Directory.CreateDirectory(queue.GetDirectory(record));
            File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Original), [0]);
        }

        var observed = new List<(string Job, int Thread, long Allocated, int Length)>();
        string job = "";
        void Observe(long allocated, int length)
        {
            lock (observed) observed.Add((job, Environment.CurrentManagedThreadId, allocated, length));
        }
        queue.IndexSerializedForTest = Observe;
        for (int i = 0; i < 4; i++)
        {
            job = $"publish {i}";
            record.Title = job;
            using CaptureWriteReservation reservation = await queue.ReservePublicationAsync();
            long callerBefore = GC.GetAllocatedBytesForCurrentThread();
            Task publication = queue.PublishRecordAsync(record, reservation);
            output.WriteLine($"{job}: the caller allocated {GC.GetAllocatedBytesForCurrentThread() - callerBefore:N0} B");
            await publication;
        }
        job = "save";
        await queue.SaveAsync();

        var loaded = new CaptureQueue(workspace.Paths, limits, NullLogger<CaptureQueue>.Instance);
        loaded.Load();
        int loadedCount = loaded.Count;
        loaded.IndexSerializedForTest = Observe;
        job = "first save after load";
        await loaded.SaveAsync();

        foreach (var (name, thread, allocated, length) in observed)
            output.WriteLine($"{name} (thread {thread}): {allocated:N0} B allocated for a {length:N0}-byte index, {(double)allocated / length:F2}x");
        Assert.Equal(records, loadedCount);
        Assert.Equal(6, observed.Count);
        // Only the first job of a new, empty-handed queue has no earlier length to size from.
        foreach (var (name, _, allocated, length) in observed.Skip(1))
            Assert.True(allocated <= length + length / 8 + 16 * 1024,
                $"{name}: the writer allocated {allocated:N0} bytes to serialize a {length:N0}-byte index.");
    }

    [Fact]
    public async Task Writer_StartsSmallMetadataInASmallBuffer_AndKeepsTheStringWriterBytes()
    {
        // The writer's new thread starts with an empty array pool, so SerializeToUtf8Bytes allocated
        // its 16 KB first buffer every job, for about 1 KB of metadata.
        using var workspace = new TempWorkspace();
        CaptureQueue queue = NewQueue(workspace);
        CaptureRecord record = AddRecord(queue);
        Fill(record);
        string?[] texts =
        [
            null,
            // 100 characters, every one escaped: the most OCR text the small first buffer takes.
            string.Concat(Enumerable.Repeat("가\"\r\n", 25)),
            string.Concat(Enumerable.Repeat("가나다 \"인용\" \\ \r\n", 2_000)),
        ];
        var observed = new List<(long Allocated, int Length)>();
        queue.MetaSerializedForTest = (allocated, length) => { lock (observed) observed.Add((allocated, length)); };
        // The first round builds the serializer's metadata for CaptureRecord; the second is measured.
        for (int round = 0; round < 2; round++)
        {
            foreach (string? text in texts)
            {
                record.OcrText = text;
                using (CaptureWriteReservation reservation = await queue.ReservePublicationAsync())
                    await queue.PublishRecordAsync(record, reservation);
                AssertMetaMatchesStringWriter(queue, record);
            }
        }

        foreach (var (allocated, length) in observed)
            output.WriteLine($"metadata of {length:N0} bytes: {allocated:N0} bytes allocated");
        Assert.Equal(2 * texts.Length, observed.Count);
        foreach (var (allocated, length) in observed.Skip(texts.Length).Take(2))
            Assert.True(allocated < 16 * 1024, $"Metadata of {length:N0} bytes allocated {allocated:N0} bytes on the writer.");
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
