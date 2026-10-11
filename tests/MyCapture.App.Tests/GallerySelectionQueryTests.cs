using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class GallerySelectionQueryTests(ITestOutputHelper output)
{
    [Fact]
    public void SelectionQueriesFollowVisibleOrderAcrossEveryMutationAndDuplicateInput()
    {
        Guid[] ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var selection = new GallerySelection();
        var random = new Random(731);
        selection.SetVisible(ids);
        for (int step = 0; step < 200; step++)
        {
            switch (step % 5)
            {
                case 0: selection.Select(ids[random.Next(ids.Length)], control: random.Next(2) == 0); break;
                case 1: selection.Select(ids[random.Next(ids.Length)], shift: true, control: random.Next(2) == 0); break;
                case 2: selection.SelectGroup(ids.Where((_, index) => index % 3 == step % 3),
                    control: random.Next(2) == 0, toggle: random.Next(2) == 0); break;
                case 3: selection.SetVisible(ids.Where((_, index) => index % 3 != step % 3).Reverse()); break;
                case 4: selection.Clear(); selection.SetVisible(ids); break;
            }
            Verify(selection);
        }

        selection.SetVisible([ids[0], ids[1], ids[0]]);
        selection.Select(ids[0]);
        Assert.Equal(new[] { ids[0], ids[0] }, selection.SelectedIds);
        Verify(selection);
        selection.SetVisible([ids[1], ids[0]]);
        Verify(selection);
        selection.SetVisible([ids[1]]);
        Verify(selection);
        Assert.Equal(0, selection.Count);
        Assert.Null(selection.Anchor);

        static void Verify(GallerySelection selection)
        {
            IReadOnlyList<Guid> ordered = selection.SelectedIds;
            Assert.Equal(ordered.Count, selection.Count);
            Assert.Equal(ordered.Count == 1 ? ordered[0] : (Guid?)null, selection.SingleSelectedId);
            Assert.Equal(ordered, selection.EnumerateSelectedIds());
        }
    }

    [Fact]
    public void SingleTileProjectionKeepsMissingTilesDuplicatesAndRefreshBehavior()
    {
        string root = OwnedTestDirectory.Create("mycapture-selection-query-");
        try
        {
            GalleryViewModel vm = CreateModel(root, 3);
            Guid[] ids = vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id).ToArray();
            Guid missing = Guid.NewGuid();
            vm.Select(ids[0]);
            Verify(vm);
            Assert.Same(vm.FindTile(ids[0]), vm.SingleSelectedTile);
            vm.Select(ids[1], control: true);
            Verify(vm);
            Assert.Null(vm.SingleSelectedTile);
            vm.Select(ids[1], control: true);
            Verify(vm);
            vm.SearchQuery = "no match";
            Verify(vm);
            Assert.False(vm.HasSelection);
            vm.SearchQuery = string.Empty;
            vm.Select(ids[2]);
            Verify(vm);
            vm.ClearSelection();
            Verify(vm);

            // The public selection object can contain ids absent from the tile cache.
            // The old projection ignored them rather than treating them as extra tiles.
            vm.Selection.SetVisible([ids[0], missing]);
            vm.Selection.SelectGroup([ids[0], missing], control: false);
            Verify(vm);
            Assert.Same(vm.FindTile(ids[0]), vm.SingleSelectedTile);
            vm.Selection.Select(missing);
            Verify(vm);
            Assert.Null(vm.SingleSelectedTile);
            vm.Selection.SetVisible([ids[0], ids[0]]);
            vm.Selection.Select(ids[0]);
            Verify(vm);
            Assert.Null(vm.SingleSelectedTile);
        }
        finally { OwnedTestDirectory.Delete(root); }

        static void Verify(GalleryViewModel vm)
        {
            Assert.Equal(vm.Selection.SelectedIds.Count > 0, vm.HasSelection);
            Assert.Equal(UiText.Format("LibrarySelection_Count", vm.Selection.SelectedIds.Count), vm.SelectionText);
            Assert.Same(OriginalSingleTile(vm), vm.SingleSelectedTile);
            Assert.Equal(OriginalSingleTile(vm)?.IsImage == true, vm.CanReduceSelected);
            Assert.Equal(OriginalSingleTile(vm)?.IsVideo == true, vm.CanGifSelected);
        }
    }

    [Fact]
    public void SelectionBindingQueriesAllocateLessThanOriginalProjectionOnTheSameFixtures()
    {
        const int iterations = 2000;
        var results = new List<object>();
        foreach (int visible in new[] { 50, 1000 })
        {
            string root = OwnedTestDirectory.Create("mycapture-selection-cost-");
            try
            {
                GalleryViewModel vm = CreateModel(root, visible);
                Guid[] ids = vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id).ToArray();
                foreach (int selected in new[] { 0, 1, 20 })
                {
                    vm.Selection.Clear();
                    if (selected > 0) vm.Selection.SelectGroup(ids.Take(selected), control: false);
                    Assert.Equal(OriginalBindings(vm), CurrentBindings(vm));
                    for (int warmup = 0; warmup < 100; warmup++)
                    { OriginalBindings(vm); CurrentBindings(vm); }
                    var before = Measure(() => OriginalBindings(vm), iterations);
                    var after = Measure(() => CurrentBindings(vm), iterations);
                    Assert.Equal(before.Checksum, after.Checksum);
                    Assert.True(after.Bytes < before.Bytes,
                        $"visible={visible}, selected={selected}: original={before.Bytes}, current={after.Bytes}");
                    output.WriteLine($"visible={visible}, selected={selected}, iterations={iterations}: " +
                        $"original {before.Bytes} bytes / {before.Milliseconds:F3} ms; " +
                        $"current {after.Bytes} bytes / {after.Milliseconds:F3} ms");
                    results.Add(new { Visible = visible, Selected = selected, Iterations = iterations, Before = before, After = after });
                }
            }
            finally { OwnedTestDirectory.Delete(root); }
        }
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SELECTION_COST_REPORT") is { Length: > 0 } report)
            File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record Measurement(long Bytes, double Milliseconds, long Checksum);

    private static Measurement Measure(Func<int> read, int iterations)
    {
        var stopwatch = new Stopwatch();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        stopwatch.Start();
        for (int iteration = 0; iteration < iterations; iteration++) checksum += read();
        stopwatch.Stop();
        return new Measurement(GC.GetAllocatedBytesForCurrentThread() - allocated, stopwatch.Elapsed.TotalMilliseconds, checksum);
    }

    // These are the exact getter formulas at c437af1; SelectedTiles itself is unchanged.
    private static GalleryItemViewModel? OriginalSingleTile(GalleryViewModel vm) =>
        vm.SelectedTiles.Count == 1 ? vm.SelectedTiles[0] : null;

    private static int OriginalBindings(GalleryViewModel vm) =>
        (vm.Selection.SelectedIds.Count > 0 ? 1 : 0)
        + UiText.Format("LibrarySelection_Count", vm.Selection.SelectedIds.Count).Length
        + (OriginalSingleTile(vm) is null ? 0 : 1)
        + (OriginalSingleTile(vm)?.IsImage == true ? 1 : 0)
        + (OriginalSingleTile(vm)?.IsVideo == true ? 1 : 0);

    private static int CurrentBindings(GalleryViewModel vm) =>
        (vm.HasSelection ? 1 : 0) + vm.SelectionText.Length
        + (vm.SingleSelectedTile is null ? 0 : 1)
        + (vm.CanReduceSelected ? 1 : 0) + (vm.CanGifSelected ? 1 : 0);

    private static GalleryViewModel CreateModel(string root, int count)
    {
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var queue = new CaptureQueue(AppPaths.CreateForRoot(root), new QueueSettings { MaxItems = count + 1 },
            NullLogger<CaptureQueue>.Instance);
        for (int index = 0; index < count; index++)
            queue.Add(new CaptureRecord { CreatedAt = now.AddSeconds(-index), UpdatedAt = now, Width = 100, Height = 80 });
        return new GalleryViewModel(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            _ => "unused", 20, () => now);
    }
}
