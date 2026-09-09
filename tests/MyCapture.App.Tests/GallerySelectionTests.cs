using MyCapture.App.Gallery;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GallerySelectionTests
{
    [Fact]
    public void ShiftRange_IsInclusiveAcrossRowsAndDays_AndRetainsAnchor()
    {
        Guid[] ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new GallerySelection();
        selection.SetVisible(ids);
        selection.Select(ids[2]);
        selection.Select(ids[9], shift: true);
        Assert.Equal(ids.Skip(2).Take(8), selection.SelectedIds);
        selection.Select(ids[0], shift: true);
        Assert.Equal(ids.Take(3), selection.SelectedIds);
        Assert.Equal(ids[2], selection.Anchor);
        selection.Select(ids[10], control: true);
        selection.Select(ids[6], control: true, shift: true);
        Assert.Equal(ids.Take(3).Concat(ids.Skip(6).Take(5)), selection.SelectedIds);
    }

    [Fact]
    public void FilteringAndDateSelection_UseOnlyVisibleOrder_AndControlTogglesWholeDay()
    {
        Guid[] ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new GallerySelection();
        selection.SetVisible(ids);
        selection.Select(ids[0]);
        selection.SetVisible([ids[2], ids[4], ids[6]]);
        Assert.Null(selection.Anchor);
        Assert.Empty(selection.SelectedIds);
        selection.Select(ids[2]);
        selection.Select(ids[6], shift: true);
        Assert.Equal(new[] { ids[2], ids[4], ids[6] }, selection.SelectedIds);
        selection.SelectGroup([ids[4], ids[5], ids[6]], control: true);
        Assert.Equal(new[] { ids[2] }, selection.SelectedIds);
        selection.SelectGroup([ids[4], ids[6]], control: true);
        Assert.Equal(new[] { ids[2], ids[4], ids[6] }, selection.SelectedIds);
        selection.SelectGroup([ids[4]], control: false);
        Assert.Equal(new[] { ids[4] }, selection.SelectedIds);
    }

    [Fact]
    public void PreparedGesture_ReleaseLossEscapeOrNewPress_CannotStartLateDrag()
    {
        var gesture = new GalleryDragGesture();
        int first = gesture.Arm();
        Assert.True(gesture.CanStart(first, true, true));
        Assert.False(gesture.CanStart(first, false, true));
        Assert.False(gesture.CanStart(first, true, false));
        gesture.Cancel();
        Assert.False(gesture.CanStart(first, true, true));
        int second = gesture.Arm();
        Assert.False(gesture.CanStart(first, true, true));
        Assert.True(gesture.CanStart(second, true, true));
    }

    [Fact]
    public void ArrowNavigation_UsesAdjacentVisualRowsAndClampsColumnAcrossPartialDateGroups()
    {
        GalleryItemViewModel[] tiles = Enumerable.Range(0, 9).Select(_ => new GalleryItemViewModel(
            new MyCapture.Core.Queue.CaptureRecord { Id = Guid.NewGuid() }, _ => "unused", 20)).ToArray();
        GalleryRow[] rows = [new GalleryHeaderRow("Today"), new GalleryTileRow(tiles[..3]),
            new GalleryTileRow([tiles[3]]), new GalleryHeaderRow("Yesterday"),
            new GalleryTileRow(tiles[4..7]), new GalleryTileRow(tiles[7..9])];
        Assert.Same(tiles[3], GallerySelectionNavigation.FindNeighbor(rows, tiles[2], true, 1));
        Assert.Same(tiles[4], GallerySelectionNavigation.FindNeighbor(rows, tiles[3], true, 1));
        Assert.Same(tiles[3], GallerySelectionNavigation.FindNeighbor(rows, tiles[6], true, -1));
        Assert.Same(tiles[8], GallerySelectionNavigation.FindNeighbor(rows, tiles[6], true, 1));
        Assert.Same(tiles[5], GallerySelectionNavigation.FindNeighbor(rows, tiles[8], true, -1));
        Assert.Same(tiles[4], GallerySelectionNavigation.FindNeighbor(rows, tiles[3], false, 1));
        Assert.Same(tiles[3], GallerySelectionNavigation.FindNeighbor(rows, tiles[4], false, -1));
        var selection = new GallerySelection(); selection.SetVisible(tiles.Select(tile => tile.Id));
        selection.Select(tiles[2].Id);
        selection.Select(GallerySelectionNavigation.FindNeighbor(rows, tiles[2], true, 1)!.Id, shift: true);
        Assert.Equal(tiles[2..4].Select(tile => tile.Id), selection.SelectedIds);
    }
}
