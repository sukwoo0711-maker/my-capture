using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A delete command is one batch that Undo restores together; the restore menu knows the last
/// batch and finds each capture's thumbnail in its Recycle Bin item; the first automatic deletion
/// is reported once per installation. Only an app-owned fake trash is used, never the Recycle Bin.
/// </summary>
public sealed class LibraryUndoAndRetentionNoticeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MoveDeleteUndoDeleteUndoMove_PreservesRecoveredRetention(bool sourceFolder, bool pinned)
    {
        var fixture = new UndoFixture(1);
        CaptureRecord record = fixture.Records[0];
        record.IsPinned = pinned;
        CaptureFolder? source = sourceFolder ? await fixture.Queue.CreateFolderAsync("Source") : null;
        if (source is not null) await fixture.Queue.MoveCapturesAsync([record.Id], source.Id);
        long sequence = record.FolderSequence;
        CaptureFolder target = await fixture.Queue.CreateFolderAsync("Target");
        await fixture.Queue.MoveCapturesAsync([record.Id], target.Id);
        Guid batch = Guid.NewGuid();
        using (fixture.Service.BeginBatch(batch))
            Assert.Equal(1, (await fixture.Queue.RemoveManyAsync([record.Id])).RemovedCount);
        Assert.Null(fixture.Queue.Find(record.Id));
        fixture.Queue.LibraryUndo.Record(LibraryUndoKind.Delete, async () =>
        {
            Assert.Equal(new LibraryBatchRestoreResult(1, 0), await fixture.Service.RestoreBatchAsync(batch));
            return true;
        });

        Assert.Equal(LibraryUndoKind.Delete, await fixture.Queue.LibraryUndo.UndoAsync());
        CaptureRecord restored = fixture.Queue.Find(record.Id)!;
        Assert.Equal(target.Id, restored.FolderId);
        DateTimeOffset? retention = restored.RetentionStartedAt;
        Assert.NotNull(retention);
        Assert.Equal(LibraryUndoKind.Move, await fixture.Queue.LibraryUndo.UndoAsync());
        Assert.Equal(source?.Id, restored.FolderId);
        Assert.Equal(sequence, restored.FolderSequence);
        Assert.Equal(pinned, restored.IsManuallyPinned);
        Assert.Equal(sourceFolder || pinned, restored.IsPinned);
        Assert.Equal(retention, restored.RetentionStartedAt);
        var reloaded = new CaptureQueue(fixture.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        Assert.Equal(source?.Id, reloaded.Find(record.Id)!.FolderId);
        Assert.Equal(retention, reloaded.Find(record.Id)!.RetentionStartedAt);
    }

    [Fact]
    public async Task Undo_SkipsTitleOfExpiredCapture_AndRestoresOlderTitle()
    {
        var fixture = new UndoFixture(1);
        CaptureRecord first = fixture.Records[0];
        CaptureRecord expired = fixture.Add(DateTimeOffset.Now.AddDays(-10));
        await fixture.Queue.UpdateOrganizationAsync(first.Id, "First renamed", "");
        await fixture.Queue.UpdateOrganizationAsync(expired.Id, "Expired renamed", "");
        fixture.Service.ConnectAutomaticMaintenance();
        fixture.Queue.ExpireHistory(DateTimeOffset.Now);
        await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Queue.Find(expired.Id));
        Assert.Equal(LibraryUndoKind.Title, await fixture.Queue.LibraryUndo.UndoAsync());
        Assert.Equal(string.Empty, first.Title);
        Assert.Equal(0, fixture.Queue.LibraryUndo.Count);
    }

    [Fact]
    public async Task DeleteBatch_IsTaggedAndRestoredTogether_WithOneIndexWrite()
    {
        var fixture = new UndoFixture(6);
        Guid[] first = fixture.Records.Take(4).Select(record => record.Id).ToArray();
        Guid[] second = fixture.Records.Skip(4).Select(record => record.Id).ToArray();
        Guid firstBatch = Guid.NewGuid(), secondBatch = Guid.NewGuid();
        using (fixture.Service.BeginBatch(firstBatch)) Assert.Equal(4, (await fixture.Queue.RemoveManyAsync(first)).RemovedCount);
        await Task.Delay(20); // DeletedAt orders the batches.
        using (fixture.Service.BeginBatch(secondBatch)) Assert.Equal(2, (await fixture.Queue.RemoveManyAsync(second)).RemovedCount);
        Assert.Empty(fixture.Queue.Records);

        DeletedLibraryOverview overview = await fixture.Service.RecentDeletedOverviewAsync();
        Assert.Equal(secondBatch, overview.LastBatch);
        Assert.Equal(2, overview.LastBatchCount);
        Assert.Equal(6, overview.Recent.Count);
        Assert.All(overview.Recent, entry =>
        {
            Assert.NotNull(entry.ThumbnailPath);
            Assert.StartsWith(fixture.Trash.Root, entry.ThumbnailPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(entry.ThumbnailPath));
        });

        int indexWrites = 0;
        fixture.Queue.BeforePublicationWriteForTest = (path, _) => { if (path == fixture.Paths.IndexFile) indexWrites++; };
        LibraryBatchRestoreResult restored = await fixture.Service.RestoreBatchAsync(firstBatch);
        Assert.Equal(new LibraryBatchRestoreResult(4, 0), restored);
        Assert.Equal(1, indexWrites);
        Assert.Equal(first.Order(), fixture.Queue.Records.Select(record => record.Id).Order());
        Assert.All(first, id => Assert.True(File.Exists(Path.Combine(fixture.Queue.GetDirectory(fixture.Queue.Find(id)!), CaptureFileNames.Original))));

        // The other batch is untouched and still the restore menu's last batch.
        overview = await fixture.Service.RecentDeletedOverviewAsync();
        Assert.Equal(secondBatch, overview.LastBatch);
        Assert.Equal(2, overview.Recent.Count);
        Assert.Equal(new LibraryBatchRestoreResult(0, 0), await fixture.Service.RestoreBatchAsync(firstBatch));
    }

    [Fact]
    public async Task BatchRestore_CountsAnItemWhoseRecycleBinEntryIsGone_AndRestoresTheRest()
    {
        var fixture = new UndoFixture(3);
        Guid batch = Guid.NewGuid();
        using (fixture.Service.BeginBatch(batch)) await fixture.Queue.RemoveManyAsync(fixture.Records.Select(record => record.Id));
        DeletedLibraryOverview overview = await fixture.Service.RecentDeletedOverviewAsync();
        string emptied = Path.GetDirectoryName(overview.Recent[0].ThumbnailPath!)!;
        TestRecycleBin.DeleteDirectory(emptied, recursive: true);
        Assert.Equal(new LibraryBatchRestoreResult(2, 1), await fixture.Service.RestoreBatchAsync(batch));
        Assert.Equal(2, fixture.Queue.Count);
    }

    [Fact]
    public void JournalWrittenBeforeBatches_IsItsOwnBatch()
    {
        var record = new CaptureRecord { Id = Guid.NewGuid() };
        Guid token = Guid.NewGuid();
        var legacy = JsonSerializer.Deserialize<DeletedLibraryCapture>(JsonSerializer.Serialize(new
        {
            Token = token, Record = record, OriginalPath = "C:\\x", Identity = "i", Reason = "manual-batch", DeletedAt = DateTimeOffset.UtcNow,
        }))!;
        Assert.Null(legacy.Batch);
        Assert.Equal(token, legacy.BatchKey);
    }

    [Fact]
    public async Task FirstRetentionDeletion_IsReportedOnceAndRemembered()
    {
        var fixture = new UndoFixture(0);
        CaptureRecord old = fixture.Add(DateTimeOffset.Now.AddDays(-10));
        fixture.Add(DateTimeOffset.Now.AddDays(-9));
        fixture.Add(DateTimeOffset.Now);
        fixture.Service.ConnectAutomaticMaintenance();
        Assert.Null(fixture.Service.PendingRetentionNotice);

        fixture.Queue.ExpireHistory(DateTimeOffset.Now);
        await fixture.Service.MaintenanceCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Queue.Find(old.Id));
        Assert.Equal(2, fixture.Service.PendingRetentionNotice);

        // Survives a restart until shown; shown once, never again.
        var restarted = new RecoverableLibraryService(fixture.Queue, fixture.Paths, fixture.Trash, NullLogger.Instance);
        Assert.Equal(2, restarted.TakeRetentionNotice());
        Assert.Null(restarted.TakeRetentionNotice());
        var again = new RecoverableLibraryService(fixture.Queue, fixture.Paths, fixture.Trash, NullLogger.Instance);
        again.NoteRetentionDeletion(5);
        Assert.Null(again.PendingRetentionNotice);
        Assert.Null(again.TakeRetentionNotice());
    }

    [Fact]
    public void UserDeletes_DoNotCountAsRetention()
    {
        var fixture = new UndoFixture(2);
        fixture.Service.NoteRetentionDeletion(0);
        Assert.True(fixture.Queue.Remove(fixture.Records[0].Id));
        Assert.Null(fixture.Service.PendingRetentionNotice);
    }

    [Theory]
    [InlineData("ko-KR", 0.5, "방금")]
    [InlineData("ko-KR", 3, "3분 전")]
    [InlineData("ko-KR", 125, "2시간 전")]
    [InlineData("en-US", 3, "3 min ago")]
    [InlineData("en-US", 125, "2 hr ago")]
    public void RestoreEntries_SayWhenTheyWereDeleted(string language, double minutesAgo, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        var now = new DateTimeOffset(2026, 10, 13, 14, 2, 0, TimeSpan.FromHours(9));
        Assert.Equal(expected, GalleryWindow.RelativeDeletionTime(now.AddMinutes(-minutesAgo), now));
    }

    [Fact]
    public void RestoreEntries_UseTheTitleOrTheKind_AndCalendarDaysAfterADay()
    {
        using var scope = UiText.UseLanguage("ko-KR");
        DateTimeOffset now = DateTimeOffset.Now.Date.AddHours(14);
        var titled = new DeletedLibraryCapture(Guid.NewGuid(), new CaptureRecord { Title = " 회의 메모 " }, "C:\\x", "i", "manual-batch", now.AddMinutes(-3));
        var untitled = titled with { Record = new CaptureRecord() };
        var video = titled with { Record = new CaptureRecord { MediaKind = CaptureMediaKind.Video } };
        Assert.Equal("회의 메모 · 3분 전 삭제", GalleryWindow.RestoreEntryLabel(titled, now));
        Assert.Equal("이미지 · 3분 전 삭제", GalleryWindow.RestoreEntryLabel(untitled, now));
        Assert.Equal("동영상 · 3분 전 삭제", GalleryWindow.RestoreEntryLabel(video, now));
        Assert.Equal("어제", GalleryWindow.RelativeDeletionTime(now.AddDays(-1).AddHours(-2), now));
        Assert.Equal("4일 전", GalleryWindow.RelativeDeletionTime(now.AddDays(-4), now));
        Assert.Equal(now.AddDays(-9).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            GalleryWindow.RelativeDeletionTime(now.AddDays(-9), now));
        using var english = UiText.UseLanguage("en-US");
        Assert.Equal("Image · deleted 3 min ago", GalleryWindow.RestoreEntryLabel(untitled, now));
    }

    [Fact]
    public void DeleteStatus_AddsKeptOnlyWhenSomethingStayed()
    {
        using var scope = UiText.UseLanguage("ko-KR");
        Assert.Equal("3개를 휴지통으로 옮김", GalleryWindow.DeleteStatus(3, new CaptureBatchRemovalResult(3, 0), null));
        Assert.Equal("3개를 휴지통으로 옮김 · 1개 유지", GalleryWindow.DeleteStatus(4, new CaptureBatchRemovalResult(3, 0), null));
        using var english = UiText.UseLanguage("en-US");
        Assert.Equal("Moved 2 items to the Recycle Bin", GalleryWindow.DeleteStatus(2, new CaptureBatchRemovalResult(2, 0), null));
        Assert.Equal("Moved 1 item to the Recycle Bin", GalleryWindow.DeleteStatus(1, new CaptureBatchRemovalResult(1, 0), null));
    }

    [Fact]
    public void RetentionChoices_OfferThePresets_AndKeepACustomHourCount()
    {
        using var scope = UiText.UseLanguage("ko-KR");
        string never = CaptureRetention.NeverHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<RetentionChoice> presets = SettingsUxText.RetentionChoices("168");
        Assert.Equal(["1일", "7일", "30일", "90일", "1년", "삭제 안 함"], presets.Select(choice => choice.Label));
        Assert.Equal(["24", "168", "720", "2160", "8760", never], presets.Select(choice => choice.Hours));

        // A custom count saved earlier stays offered, first, while chosen or saved.
        IReadOnlyList<RetentionChoice> custom = SettingsUxText.RetentionChoices("36", "36");
        Assert.Equal(new RetentionChoice("36", "사용자 지정 (36시간)"), custom[0]);
        Assert.Equal(7, custom.Count);
        Assert.Equal(7, SettingsUxText.RetentionChoices("168", "36").Count);
        Assert.Equal(6, SettingsUxText.RetentionChoices("168", "720").Count);
        Assert.Equal(UiText.Get("Retention_SummaryNever"), SettingsUxText.RetentionSummary(never));
        using var english = UiText.UseLanguage("en-US");
        Assert.Equal("Custom (36 hours)", SettingsUxText.RetentionChoices("36")[0].Label);
    }

    [Fact]
    public void FontChoices_KeepTheSavedNameExactly()
    {
        FontChoice[] installed = [new("Arial", "Arial"), new("Malgun Gothic", "맑은 고딕")];
        Assert.Same(installed, SettingsWindow.FontChoicesFor("Malgun Gothic", installed));
        IReadOnlyList<FontChoice> missing = SettingsWindow.FontChoicesFor("Retired Font", installed);
        Assert.Equal(new FontChoice("Retired Font", "Retired Font"), missing[0]);
        Assert.Equal(3, missing.Count);
        IReadOnlyList<FontChoice> casing = SettingsWindow.FontChoicesFor("malgun gothic", installed);
        Assert.Equal(new FontChoice("malgun gothic", "맑은 고딕"), casing[1]);
        Assert.Equal(2, casing.Count);
    }

    private sealed class UndoFixture
    {
        public AppPaths Paths { get; } = AppPaths.CreateForRoot(TestRecycleBin.CreateTempSubdirectory("mc-library-undo-").FullName);
        public CaptureQueue Queue { get; }
        public FolderTrash Trash { get; }
        public RecoverableLibraryService Service { get; }
        public List<CaptureRecord> Records { get; } = [];

        public UndoFixture(int count)
        {
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            for (int index = 0; index < count; index++) Records.Add(Add(DateTimeOffset.Now.AddMinutes(-index)));
            Trash = new FolderTrash(Path.Combine(Paths.DataRoot, "fake-trash"));
            Service = new RecoverableLibraryService(Queue, Paths, Trash, NullLogger.Instance);
            Queue.PrepareRemoval = Service.PrepareRemoval;
        }

        public CaptureRecord Add(DateTimeOffset created)
        {
            var record = new CaptureRecord { Id = Guid.NewGuid(), TotalBytes = 25, CreatedAt = created, UpdatedAt = created };
            Queue.Add(record);
            string path = Queue.GetDirectory(record);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, CaptureFileNames.Original), "pixels");
            File.WriteAllText(Path.Combine(path, CaptureFileNames.Thumbnail), "thumbnail");
            Queue.SaveRecordMetaOrThrow(record);
            Queue.Save();
            return record;
        }
    }

    /// <summary>Moves directories into an app-owned folder and back; never the Windows Recycle Bin.</summary>
    private sealed class FolderTrash(string root) : IRecoverableTrash
    {
        private readonly WindowsRecycleBin _identity = new();
        private readonly Dictionary<string, RecycledPathReceipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
        public string Root => root;
        public string GetIdentity(string path) => _identity.GetIdentity(path);
        public RecycledPathReceipt RecycleDirectory(string path)
        {
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string identity = GetIdentity(path);
            Directory.Move(path, target);
            var receipt = new RecycledPathReceipt(path, target, identity, "fixture-shell-id", true);
            lock (_receipts) _receipts[path] = receipt;
            return receipt;
        }
        public void RestoreDirectory(RecycledPathReceipt receipt)
        {
            if (Directory.Exists(receipt.OriginalPath) || !Directory.Exists(receipt.RecycledPath))
                throw new IOException("restore unavailable or collision");
            Directory.Move(receipt.RecycledPath, receipt.OriginalPath);
        }
        public RecycledPathReceipt? FindRecycledDirectory(string originalPath, string identity)
        {
            lock (_receipts)
                return _receipts.TryGetValue(originalPath, out RecycledPathReceipt? receipt) && receipt.Identity == identity
                    && Directory.Exists(receipt.RecycledPath) ? receipt : null;
        }
    }
}
