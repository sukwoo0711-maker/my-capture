using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryTagTests
{
    [Fact]
    public async Task OpenCapture_ClearsExcludingTagAndSelectsUncachedCapture()
    {
        await SettingsThemePreviewTests.RunIsolated(nameof(OpenCapture_ClearsExcludingTagAndSelectsUncachedCapture), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                VerifyOpenCapture(inFolder: false);
                VerifyOpenCapture(inFolder: true);
            }
            finally { application.Shutdown(); }
        }, typeof(GalleryTagTests));
    }

    private static void VerifyOpenCapture(bool inFolder)
    {
        var paths = AppPaths.CreateForRoot(Path.Combine(AppContext.BaseDirectory,
            "tag-test-fixtures", Guid.NewGuid().ToString("N")));
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        Guid? folderId = inFolder ? queue.CreateFolderAsync("Folder").GetAwaiter().GetResult().Id : null;
        queue.Add(Record("work", "existing"));
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var model = new GalleryViewModel(controller, _ => "missing.jpg", 320);
        model.ShowTag("work");
        var target = Record("", "new capture", folderId);
        queue.Add(target);
        model.Refresh();
        Assert.Null(model.FindTile(target.Id));

        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new NoOcr();
        using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var window = new GalleryWindow(model, controller,
            new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
            new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
        };
        try
        {
            window.OpenCapture(target.Id, edit: false);
            Assert.Null(model.SelectedTag);
            Assert.Equal(folderId is { } id ? CaptureFolderScope.ForFolder(id) : CaptureFolderScope.All, model.SelectedScope);
            Assert.Equal(target.Id, model.Selection.SingleSelectedId);
            Assert.Equal(target.Id, model.SingleSelectedTile!.Id);
            Assert.NotNull(model.FindTile(target.Id));
        }
        finally { window.CloseForExit(); }
    }

    [Fact]
    public async Task TagView_CrossesFolders_MatchesWholeTags_AndTogglesLikeAView()
    {
        var queue = Queue();
        var folder = await queue.CreateFolderAsync("Folder");
        var nested = Record("work", "needle", folder.Id);
        var root = Record("WORK", "other");
        var different = Record("network", "needle");
        queue.Add(nested); queue.Add(root); queue.Add(different);
        var model = Model(queue);
        Assert.Equal(2, model.VisibleCount);
        model.NavigateTo(CaptureFolderScope.ForFolder(folder.Id));
        model.ShowTag("work");
        Assert.True(model.SelectedScope.IsAll);
        Assert.False(model.IsAllItemsView);
        Assert.Equal(2, model.VisibleCount);
        Assert.Empty(model.Groups.SelectMany(group => group.Folders));
        Assert.True(model.ListsLocation(nested));
        Assert.True(Assert.Single(model.VisibleTags, tag => tag.Name.Equals("work", StringComparison.OrdinalIgnoreCase)).IsSelected);
        model.SearchQuery = "needle";
        Assert.Equal(nested.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
        model.SearchQuery = "";
        model.ShowTag("WORK");
        Assert.Null(model.SelectedTag);
        Assert.True(model.IsAllItemsView);
        model.ShowTag("work");
        model.ShowView(GalleryFilter.All);
        Assert.Null(model.SelectedTag);
        model.ShowTag("work");
        model.ShowView(GalleryFilter.Images);
        Assert.True(model.IsImagesView);
        Assert.Null(model.SelectedTag);
        model.ShowTag("work");
        model.NavigateTo(CaptureFolderScope.ForFolder(folder.Id));
        Assert.Null(model.SelectedTag);
        Assert.Equal(nested.Id, Assert.Single(model.Groups.SelectMany(group => group.Items)).Id);
    }

    [Fact]
    public async Task TagView_CombinesFieldConditionsAndMatchReasons_CountingOnlyTaggedCandidates()
    {
        var queue = Queue();
        var folder = await queue.CreateFolderAsync("Folder");
        var nested = Record("work, ready", "needle", folder.Id);
        queue.Add(nested);
        queue.Add(Record("WORK, draft", "needle"));
        queue.Add(Record("network, ready", "needle"));
        var model = Model(queue);
        model.ShowTag("work");
        model.SearchQuery = "title:needle -tag:draft";

        GalleryItemViewModel tile = Assert.Single(model.Groups.SelectMany(group => group.Items));
        Assert.Equal(nested.Id, tile.Id);
        Assert.True(tile.HasSearchMatch);
        Assert.Equal(GallerySearchMatchText.Describe(CaptureMatchField.Title), tile.SearchMatchText);
        Assert.Equal(2, model.SearchConditions.Count);
        Assert.Equal(1, model.SearchHiddenCount);
        Assert.Empty(model.Groups.SelectMany(group => group.Folders));

        model.RemoveSearchCondition(1);
        Assert.Equal(2, model.VisibleCount);
        Assert.Equal(0, model.SearchHiddenCount);
        Assert.Equal("work", model.SelectedTag);
        model.SearchQuery = "title:absent";
        Assert.True(model.IsEmpty);
        Assert.Equal(2, model.SearchHiddenCount);
        Assert.True(model.CanResetEmptySearch);
        model.SearchQuery = "";
        Assert.Equal(2, model.VisibleCount);
        Assert.Equal(0, model.SearchHiddenCount);
        Assert.Empty(model.SearchConditions);
        Assert.All(model.Groups.SelectMany(group => group.Items), item => Assert.False(item.HasSearchMatch));
    }

    [Fact]
    public async Task BatchTags_RefreshesActiveTagAndFieldSearchTogether()
    {
        var queue = Queue();
        var record = Record("work, draft", "capture");
        queue.Add(record);
        queue.Add(Record("other, ready", "capture"));
        var model = Model(queue);
        model.ShowTag("work");
        model.SearchQuery = "tag:ready";
        Assert.True(model.IsEmpty);
        Assert.Equal(1, model.SearchHiddenCount);

        await queue.UpdateTagsAsync([record.Id], [new("ready", true), new("draft", false)]);
        model.Refresh();
        GalleryItemViewModel tile = Assert.Single(model.Groups.SelectMany(group => group.Items));
        Assert.Equal(record.Id, tile.Id);
        Assert.Equal(GallerySearchMatchText.Describe(CaptureMatchField.Tags), tile.SearchMatchText);
        Assert.Equal(0, model.SearchHiddenCount);
        Assert.Equal(2, Assert.Single(model.VisibleTags, tag => tag.Name == "ready").Count);

        await queue.UpdateTagsAsync([record.Id], [new("work", false)]);
        model.Refresh();
        Assert.True(model.IsEmpty);
        Assert.Equal(0, model.SearchHiddenCount);
        Assert.Equal(0, Assert.Single(model.VisibleTags, tag => tag.IsSelected).Count);
        model.ShowView(GalleryFilter.All);
        Assert.Equal(2, model.VisibleCount);
        Assert.Equal("tag:ready", model.SearchQuery);
    }

    [Fact]
    public void Tags_LimitInitialRows_KeepCountsIndependentOfSearch_AndRetainEmptyActiveTag()
    {
        var queue = Queue();
        var record = Record(string.Join(", ", Enumerable.Range(0, 12).Select(i => $"tag{i:00}")), "capture");
        queue.Add(record);
        var model = Model(queue);
        Assert.Equal(8, model.VisibleTags.Count);
        Assert.True(model.HasMoreTags);
        model.SearchQuery = "absent";
        Assert.Equal(8, model.VisibleTags.Count);
        Assert.All(model.VisibleTags, tag => Assert.Equal(1, tag.Count));
        model.ShowAllTags();
        Assert.Equal(12, model.VisibleTags.Count);
        Assert.False(model.HasMoreTags);
        model.ShowTag("tag11");
        record.Tags = "";
        model.Refresh();
        GalleryTagItem active = Assert.Single(model.VisibleTags);
        Assert.True(active.IsSelected);
        Assert.Equal(0, active.Count);
        model.Filter = GalleryFilter.All;
        Assert.Null(model.SelectedTag);
        Assert.Empty(model.VisibleTags);
    }

    [Fact]
    public void Dialog_MixedRowsAreUntouchedUntilToggled_NewTagsNormalizeAndValidate()
    {
        StaTestHost.Run(() =>
        {
            var dialog = new GalleryBatchTagsDialog([Record("all, mixed", "a"), Record("ALL", "b")],
                _ => throw new InvalidOperationException("This test must not publish."));
            Assert.Empty(dialog.Changes);
            Assert.True(dialog.Rows["all"].IsChecked);
            Assert.Null(dialog.Rows["mixed"].IsChecked);
            var mixed = (IToggleProvider)new CheckBoxAutomationPeer(dialog.Rows["mixed"]).GetPattern(PatternInterface.Toggle)!;
            mixed.Toggle();
            Assert.True(dialog.Rows["mixed"].IsChecked);
            Assert.Equal(new CaptureTagChange("mixed", true), Assert.Single(dialog.Changes));
            mixed.Toggle();
            Assert.False(dialog.Rows["mixed"].IsChecked);
            Assert.Equal(new CaptureTagChange("mixed", false), Assert.Single(dialog.Changes));
            dialog.NewTagInput.Text = " #new ";
            Assert.True(dialog.AddNewTag());
            Assert.True(dialog.Rows["new"].IsChecked);
            Assert.Contains(new CaptureTagChange("new", true), dialog.Changes);
            dialog.NewTagInput.Text = new string('x', 33);
            Assert.False(dialog.AddNewTag());
            Assert.Equal(3, dialog.Rows.Count);
            dialog.NewTagInput.Text = "MIXED";
            Assert.True(dialog.AddNewTag());
            Assert.True(dialog.Rows["mixed"].IsChecked);
            Assert.Equal(3, dialog.Rows.Count);
        });
    }

    private static CaptureQueue Queue() => new(AppPaths.CreateForRoot(Path.Combine(AppContext.BaseDirectory,
        "tag-test-fixtures", Guid.NewGuid().ToString("N"))), new QueueSettings(), NullLogger<CaptureQueue>.Instance);

    private static CaptureRecord Record(string tags, string title, Guid? folder = null) => new()
    {
        Id = Guid.NewGuid(), RelativeDirectory = Guid.NewGuid().ToString("N"), Tags = tags,
        Title = title, FolderId = folder, IsPinned = true,
    };

    private static GalleryViewModel Model(CaptureQueue queue) => new(
        new GalleryController(queue, NullLogger<GalleryController>.Instance), _ => "missing.jpg", 320);

    private sealed class NoOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Navigation must not run OCR.");
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(System.Windows.Media.Imaging.BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Navigation must not run OCR.");
    }
}
