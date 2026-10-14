using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class LibraryExtrasTests
{
    [Fact]
    public async Task SavedSearches_RoundTripRenameDelete_KeepSchemaAndLegacyReaderCompatible()
    {
        var (queue, paths) = Queue();
        CaptureRecord record = Add(queue);
        const string query = "tag:work title:\"release notes\" -type:video";
        CaptureSavedSearch saved = (await queue.SaveSearchAsync("  Release  ", query))!;
        Assert.Equal("Release", saved.Name);
        var reloaded = Reload(paths);
        Assert.Equal(saved, Assert.Single(reloaded.SavedSearches));
        Assert.NotNull(reloaded.Find(record.Id));
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(paths.IndexFile)))
            Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var legacy = JsonSerializer.Deserialize<LegacyIndex>(File.ReadAllText(paths.IndexFile), JsonDefaults.Readable)!;
        Assert.Equal(record.Id, Assert.Single(legacy.Records).Id);
        await reloaded.SaveSearchAsync("Renamed", query, saved.Id);
        Assert.Equal("Renamed", Assert.Single(Reload(paths).SavedSearches).Name);
        await reloaded.DeleteSavedSearchAsync(saved.Id);
        Assert.Empty(Reload(paths).SavedSearches);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SavedSearches_OlderIndexWithoutOptionalDataLoads(int schema)
    {
        var (queue, paths) = Queue();
        CaptureRecord record = Add(queue);
        File.WriteAllText(paths.IndexFile, JsonSerializer.Serialize(new { schemaVersion = schema, records = new[] { record } }, JsonDefaults.Readable));
        var reloaded = Reload(paths);
        Assert.Empty(reloaded.SavedSearches);
        Assert.NotNull(reloaded.Find(record.Id));
    }

    [Fact]
    public async Task SavedSearches_RejectInvalidDuplicateNames_AndRollbackFailedPublication()
    {
        var (queue, paths) = Queue();
        await queue.SaveSearchAsync("Work", "tag:work");
        await Assert.ThrowsAsync<ArgumentException>(() => queue.SaveSearchAsync("work", "other"));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.SaveSearchAsync(" ", "tag:work"));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.SaveSearchAsync("Empty", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.SaveSearchAsync(new string('x', 81), "query"));
        int writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) => { if (++writes == 1) throw new IOException("Injected failure"); };
        await Assert.ThrowsAsync<IOException>(() => queue.SaveSearchAsync("Other", "tag:other"));
        Assert.Equal("Work", Assert.Single(queue.SavedSearches).Name);
        Assert.Equal("Work", Assert.Single(Reload(paths).SavedSearches).Name);
    }

    [Fact]
    public void SavedSearches_MalformedOptionalEntriesDoNotInvalidateTheLibrary()
    {
        var (queue, paths) = Queue();
        CaptureRecord record = Add(queue);
        var valid = new CaptureSavedSearch(Guid.NewGuid(), "Work", "tag:work");
        File.WriteAllText(paths.IndexFile, JsonSerializer.Serialize(new
        {
            schemaVersion = 2, records = new[] { record },
            savedSearches = new object?[] { null, new { id = "invalid", name = "Bad", query = "q" }, valid, valid },
        }, JsonDefaults.Readable));
        var reloaded = Reload(paths);
        Assert.NotNull(reloaded.Find(record.Id));
        Assert.Equal(valid, Assert.Single(reloaded.SavedSearches));
        File.WriteAllText(paths.IndexFile, JsonSerializer.Serialize(new
        { schemaVersion = 2, records = new[] { record }, savedSearches = "invalid shape" }, JsonDefaults.Readable));
        Assert.NotNull(Reload(paths).Find(record.Id));
        Assert.Empty(Reload(paths).SavedSearches);
    }

    [Fact]
    public async Task Undo_FolderMoveRestoresParentWithoutChangingTheFolderName()
    {
        var (queue, paths) = Queue();
        CaptureFolder parent = await queue.CreateFolderAsync("Parent");
        CaptureFolder folder = await queue.CreateFolderAsync("Child");
        await queue.MoveFolderAsync(folder.Id, parent.Id);
        await queue.RenameFolderAsync(folder.Id, "Renamed");
        Assert.Equal(LibraryUndoKind.Move, await queue.LibraryUndo.UndoAsync());
        Assert.Null(folder.ParentId);
        Assert.Equal("Renamed", folder.Name);
        Assert.Null(Reload(paths).FindFolder(folder.Id)!.ParentId);
        Assert.Equal(0, queue.LibraryUndo.Count);
    }

    [Fact]
    public async Task Undo_BatchMoveRestoresExactAssignmentsPinsAndRetention_AsOneStep()
    {
        var (queue, paths) = Queue();
        CaptureRecord first = Add(queue, pinned: false), second = Add(queue);
        CaptureFolder source = await queue.CreateFolderAsync("Source");
        CaptureFolder target = await queue.CreateFolderAsync("Target");
        await queue.MoveCapturesAsync([second.Id], source.Id);
        long sequence = second.FolderSequence;
        int count = queue.LibraryUndo.Count;
        await queue.MoveCapturesAsync([first.Id, second.Id, first.Id], target.Id);
        Assert.Equal(count + 1, queue.LibraryUndo.Count);
        first.OcrText = "later OCR"; first.ContentRevision = 7;
        Assert.Equal(LibraryUndoKind.Move, await queue.LibraryUndo.UndoAsync());
        Assert.Null(first.FolderId);
        Assert.False(first.IsPinned);
        Assert.Null(first.RetentionStartedAt);
        Assert.Equal(source.Id, second.FolderId);
        Assert.Equal(sequence, second.FolderSequence);
        Assert.Equal("later OCR", first.OcrText);
        Assert.Equal(7, first.ContentRevision);
        var reloaded = Reload(paths);
        Assert.Null(reloaded.Find(first.Id)!.FolderId);
        Assert.Equal(source.Id, reloaded.Find(second.Id)!.FolderId);
    }

    [Fact]
    public async Task Undo_LeavingFolderRestoresRetentionSequenceAndManualPin()
    {
        var (queue, _) = Queue();
        CaptureRecord record = Add(queue, pinned: false);
        CaptureFolder folder = await queue.CreateFolderAsync("Filed");
        await queue.MoveCapturesAsync([record.Id], folder.Id);
        long sequence = record.FolderSequence;
        await queue.MoveCapturesAsync([record.Id], null);
        Assert.NotNull(record.RetentionStartedAt);
        await queue.LibraryUndo.UndoAsync();
        Assert.Equal(folder.Id, record.FolderId);
        Assert.Equal(sequence, record.FolderSequence);
        Assert.Null(record.RetentionStartedAt);
        Assert.True(record.IsPinned);
        Assert.False(record.IsManuallyPinned);
    }

    [Fact]
    public async Task Undo_TagsTitleAndPinUseLifo_WithoutRevertingUnrelatedMetadata()
    {
        var (queue, paths) = Queue();
        CaptureRecord first = Add(queue), second = Add(queue);
        first.Tags = "work"; second.Tags = "personal";
        await queue.UpdateTagsAsync([first.Id, second.Id], [new("new", true)]);
        await queue.UpdateOrganizationAsync(first.Id, "Renamed", first.Tags);
        await queue.ToggleLibraryPinAsync(first.Id);
        Assert.Equal(3, queue.LibraryUndo.Count);
        first.OcrText = "independent"; first.Width = 123;
        Assert.Equal(LibraryUndoKind.Pin, await queue.LibraryUndo.UndoAsync());
        Assert.True(first.IsManuallyPinned);
        Assert.Equal(LibraryUndoKind.Title, await queue.LibraryUndo.UndoAsync());
        Assert.Equal("Original", first.Title);
        Assert.Contains("new", first.Tags);
        Assert.Equal(LibraryUndoKind.Tags, await queue.LibraryUndo.UndoAsync());
        Assert.Equal("work", first.Tags); Assert.Equal("personal", second.Tags);
        Assert.Equal("independent", first.OcrText); Assert.Equal(123, first.Width);
        Assert.False(queue.LibraryUndo.CanUndo);
        Assert.Equal("work", Reload(paths).Find(first.Id)!.Tags);
    }

    [Fact]
    public async Task Undo_PinInsideFolderRestoresManualPinAndKeepsFolderProtection()
    {
        var (queue, _) = Queue();
        CaptureRecord record = Add(queue, pinned: false);
        CaptureFolder folder = await queue.CreateFolderAsync("Folder");
        await queue.MoveCapturesAsync([record.Id], folder.Id);
        Assert.True(await queue.ToggleLibraryPinAsync(record.Id));
        await queue.LibraryUndo.UndoAsync();
        Assert.False(record.IsManuallyPinned); Assert.True(record.IsPinned);
        Assert.Equal(folder.Id, record.FolderId);
    }

    [Fact]
    public async Task Undo_NoOpsAndFailedChangesDoNotAddSteps_AndFailedUndoCanRetry()
    {
        var (queue, paths) = Queue();
        CaptureRecord record = Add(queue);
        await queue.UpdateOrganizationAsync(record.Id, record.Title, record.Tags);
        await queue.UpdateTagsAsync([record.Id], []);
        await queue.MoveCapturesAsync([record.Id], null);
        Assert.Equal(0, queue.LibraryUndo.Count);
        int writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) => { if (++writes == 1) throw new IOException("Injected failure"); };
        await Assert.ThrowsAsync<IOException>(() => queue.UpdateOrganizationAsync(record.Id, "Failed", ""));
        Assert.Equal(0, queue.LibraryUndo.Count);
        queue.BeforePublicationWriteForTest = null;
        await queue.UpdateOrganizationAsync(record.Id, "New", "");
        writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) => { if (++writes == 2) throw new IOException("Injected undo failure"); };
        await Assert.ThrowsAsync<IOException>(() => queue.LibraryUndo.UndoAsync());
        Assert.Equal("New", record.Title);
        Assert.Equal("New", Reload(paths).Find(record.Id)!.Title);
        Assert.Equal(1, queue.LibraryUndo.Count);
        queue.BeforePublicationWriteForTest = null;
        Assert.Equal(LibraryUndoKind.Title, await queue.LibraryUndo.UndoAsync());
        Assert.Equal("Original", record.Title);
    }

    [Fact]
    public async Task Undo_ChangedOrMissingRecordsDoNotPartiallyRevertBatch()
    {
        var (queue, _) = Queue();
        CaptureRecord first = Add(queue), second = Add(queue);
        await queue.UpdateTagsAsync([first.Id, second.Id], [new("batch", true)]);
        second.Tags = "external change";
        Assert.Null(await queue.LibraryUndo.UndoAsync());
        Assert.Equal("batch", first.Tags);
        Assert.Equal("external change", second.Tags);
        Assert.Equal(0, queue.LibraryUndo.Count);
    }

    [Fact]
    public async Task Organization_PersistentWriteFailurePreservesBothErrorsAndRollsBackMetadata()
    {
        var (queue, paths) = Queue();
        CaptureRecord record = Add(queue);
        DateTimeOffset updated = record.UpdatedAt;
        var failures = new List<IOException>();
        queue.BeforePublicationWriteForTest = (_, _) =>
        {
            var failure = new IOException($"Injected persistent failure {failures.Count}");
            failures.Add(failure);
            throw failure;
        };
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            queue.UpdateOrganizationAsync(record.Id, "Draft title", "draft tags"));
        Assert.True(failures.Count >= 2);
        Assert.Equal(failures, error.Flatten().InnerExceptions);
        Assert.Equal("Original", record.Title);
        Assert.Equal(string.Empty, record.Tags);
        Assert.Equal(updated, record.UpdatedAt);
        Assert.Equal("Original", Reload(paths).Find(record.Id)!.Title);
        Assert.Equal(0, queue.LibraryUndo.Count);
    }

    [Fact]
    public async Task History_SkipsInvalidEntries_ButRetainsRetryableFailures()
    {
        var history = new LibraryUndoHistory();
        history.Record(LibraryUndoKind.Title, () => Task.FromResult(LibraryUndoResult.Restored));
        bool retry = true;
        history.Record(LibraryUndoKind.Pin, () => Task.FromResult(retry
            ? LibraryUndoResult.RetryableFailure : LibraryUndoResult.Invalidated));
        history.Record(LibraryUndoKind.Move, () => Task.FromResult(LibraryUndoResult.Invalidated));
        Assert.Null(await history.UndoAsync());
        Assert.Equal(2, history.Count);
        Assert.Equal(LibraryUndoKind.Pin, history.NextKind);
        Assert.True(history.CanUndo);
        retry = false;
        Assert.Equal(LibraryUndoKind.Title, await history.UndoAsync());
        Assert.Equal(0, history.Count);
        Assert.Null(history.NextKind);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public async Task FailedMetadataChange_PreservesLaterOcrAndItsTimestamp()
    {
        var (queue, paths) = Queue();
        CaptureRecord record = Add(queue);
        DateTimeOffset later = DateTimeOffset.Now.AddMinutes(1);
        int writes = 0;
        queue.BeforePublicationWriteForTest = (_, _) =>
        {
            if (++writes != 1) return;
            record.OcrText = "new OCR";
            record.UpdatedAt = later;
            throw new IOException("Injected failure after OCR changed metadata.");
        };
        await Assert.ThrowsAsync<IOException>(() => queue.UpdateOrganizationAsync(record.Id, "Failed title", ""));
        Assert.Equal("Original", record.Title);
        Assert.Equal("new OCR", record.OcrText);
        Assert.Equal(later, record.UpdatedAt);
        Assert.Equal(later, Reload(paths).Find(record.Id)!.UpdatedAt);
        Assert.Equal(0, queue.LibraryUndo.Count);
    }

    [Fact]
    public async Task History_DeleteAndMetadataShareOrder_AndConcurrentUndoIsIgnored()
    {
        var history = new LibraryUndoHistory();
        var order = new List<string>();
        history.Record(LibraryUndoKind.Tags, () => { order.Add("tags"); return Task.FromResult(true); });
        var completed = new TaskCompletionSource<bool>();
        history.Record(LibraryUndoKind.Delete, () => completed.Task);
        Task<LibraryUndoKind?> undo = history.UndoAsync();
        Assert.False(history.CanUndo);
        Assert.Null(await history.UndoAsync());
        completed.SetResult(true);
        Assert.Equal(LibraryUndoKind.Delete, await undo);
        Assert.Equal(LibraryUndoKind.Tags, await history.UndoAsync());
        Assert.Equal(["tags"], order);
        for (int i = 0; i < 60; i++) history.Record(LibraryUndoKind.Pin, () => Task.FromResult(true));
        Assert.Equal(50, history.Count);
    }

    [Fact]
    public void Activities_AggregateAllKinds_CancelOnlyOnce_IgnoreLateProgress()
    {
        var tracker = new LibraryActivityTracker();
        int cancelled = 0;
        using var import = tracker.Start(LibraryActivityKind.Import, () => cancelled++, 10);
        using var zip = tracker.Start(LibraryActivityKind.ZipExport);
        using var folder = tracker.Start(LibraryActivityKind.FolderExport);
        using var pdf = tracker.Start(LibraryActivityKind.Pdf);
        using var ocr = tracker.Start(LibraryActivityKind.Ocr);
        Assert.Equal(5, tracker.Running.Count);
        Assert.Null(tracker.Running.Single(item => item.Kind == LibraryActivityKind.Ocr).Total);
        import.Report(3);
        var state = tracker.Running.Single(item => item.Kind == LibraryActivityKind.Import);
        Assert.Equal(3, state.Completed); Assert.Equal(10, state.Total);
        Assert.True(tracker.Cancel(state.Id)); Assert.False(tracker.Cancel(state.Id)); Assert.Equal(1, cancelled);
        Assert.True(tracker.Running.Single(item => item.Id == state.Id).CancellationRequested);
        Assert.False(tracker.Cancel(tracker.Running.Single(item => item.Kind == LibraryActivityKind.Pdf).Id));
        import.Dispose(); import.Report(9); import.Dispose();
        Assert.Equal(4, tracker.Running.Count);
        Assert.DoesNotContain(tracker.Running, item => item.Id == state.Id);
    }

    [Fact]
    public async Task Activities_ConcurrentReportsAndCompletionKeepOnlyRunningJobs()
    {
        var tracker = new LibraryActivityTracker();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            using var scope = tracker.Start(LibraryActivityKind.Ocr);
            for (int i = 0; i < 20; i++) { scope.Report(i, 20); _ = tracker.Running; }
        })));
        Assert.Empty(tracker.Running);
    }

    private sealed class LegacyIndex
    {
        public int SchemaVersion { get; set; }
        public List<CaptureRecord> Records { get; set; } = [];
    }

    private static (CaptureQueue Queue, AppPaths Paths) Queue()
    {
        // Small fixtures stay in ignored build output; never use the real profile or delete files.
        var paths = AppPaths.CreateForRoot(Path.Combine(AppContext.BaseDirectory, "library-extras-fixtures", Guid.NewGuid().ToString("N")));
        return (new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance), paths);
    }

    private static CaptureRecord Add(CaptureQueue queue, bool pinned = true)
    {
        var record = new CaptureRecord { RelativeDirectory = Guid.NewGuid().ToString("N"), Title = "Original",
            Width = 10, Height = 10, IsPinned = pinned };
        queue.Add(record);
        queue.SaveRecordMetaOrThrow(record);
        File.WriteAllText(queue.GetFilePath(record, CaptureFileNames.Original), "fixture");
        queue.Save();
        return record;
    }

    private static CaptureQueue Reload(AppPaths paths)
    {
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        return queue;
    }
}
