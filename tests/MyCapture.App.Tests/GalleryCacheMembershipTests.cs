using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryCacheMembershipTests : IDisposable
{
    private readonly string _workspace = OwnedTestDirectory.Create("MyCapture-gallery-membership-");

    public void Dispose() => OwnedTestDirectory.Delete(_workspace);

    [Theory]
    [InlineData(GalleryFilter.All, "keep")]
    [InlineData(GalleryFilter.Images, "")]
    [InlineData(GalleryFilter.Videos, "")]
    [InlineData(GalleryFilter.Pinned, "")]
    public void FilteredRefresh_RetainsHiddenTiles_ButDropsRemovedRecords(
        GalleryFilter filter, string query)
    {
        // Queue/cache membership must not create this child of the owned fixture.
        string unusedRoot = Path.Combine(_workspace, "unused");
        var queue = new CaptureQueue(AppPaths.CreateForRoot(unusedRoot),
            new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        CaptureRecord[] records = Enumerable.Range(0, 8).Select(i => new CaptureRecord
        {
            CreatedAt = now.AddHours(-i * 12),
            Title = i % 2 == 0 ? "keep " + i : "other " + i,
            MediaKind = i % 2 == 0 ? CaptureMediaKind.Image : CaptureMediaKind.Video,
            IsPinned = i % 3 == 0,
        }).ToArray();
        foreach (CaptureRecord record in records.Reverse()) queue.Add(record);
        var vm = new GalleryViewModel(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            _ => throw new InvalidOperationException("Membership must not request a thumbnail."), 160, () => now);
        Dictionary<Guid, GalleryItemViewModel> cached = records.ToDictionary(r => r.Id, r => vm.FindTile(r.Id)!);

        vm.Filter = filter;
        vm.SearchQuery = query;
        Guid[] visible = vm.Groups.SelectMany(g => g.Items).Select(t => t.Id).ToArray();
        Assert.NotEmpty(visible);
        Assert.True(visible.Length < records.Length);
        Assert.All(records, record => Assert.Same(cached[record.Id], vm.FindTile(record.Id)));

        Guid removedVisible = visible[0];
        Guid removedHidden = records.First(r => !visible.Contains(r.Id)).Id;
        vm.Select(removedVisible);
        Assert.True(queue.Remove(removedVisible));
        Assert.True(queue.Remove(removedHidden));
        vm.Refresh();

        Assert.Equal(visible.Where(id => id != removedVisible),
            vm.Groups.SelectMany(g => g.Items).Select(t => t.Id));
        Assert.Empty(vm.SelectedTiles);
        Assert.Null(vm.Selection.Anchor);
        foreach (Guid removed in new[] { removedVisible, removedHidden })
        {
            Assert.Null(vm.FindTile(removed));
            // Previously bound references must stop loading once their record is gone.
            Assert.Null(cached[removed].Thumbnail);
        }

        vm.SearchQuery = string.Empty;
        vm.Filter = GalleryFilter.All;
        CaptureRecord[] remaining = records.Where(r => r.Id != removedVisible && r.Id != removedHidden).ToArray();
        Assert.Equal(remaining.Select(r => r.Id), vm.Groups.SelectMany(g => g.Items).Select(t => t.Id));
        Assert.All(remaining, record => Assert.Same(cached[record.Id], vm.FindTile(record.Id)));

        // Membership is refreshed again after later queue changes, not retained from an earlier refresh.
        Assert.True(queue.Remove(remaining[^1].Id));
        vm.Refresh();
        Assert.Null(vm.FindTile(remaining[^1].Id));
        Assert.Equal(remaining.Length - 1, vm.VisibleCount);
        Assert.False(Directory.Exists(unusedRoot));
    }
}
