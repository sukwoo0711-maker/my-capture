using System.IO;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryTagTests
{
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
}
