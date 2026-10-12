using System.Collections.Specialized;
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

public sealed class GallerySelectAllTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("all")]
    [InlineData("empty")]
    [InlineData("empty-queue")]
    [InlineData("single-image")]
    [InlineData("single-video")]
    [InlineData("videos")]
    [InlineData("hidden-cache")]
    [InlineData("duplicate-visible")]
    [InlineData("stale-visible")]
    [InlineData("shift-anchor")]
    public void SelectAllPreservesSelectionBindingsAndAnchorWithoutRebuilding(string scenario)
    {
        using var fixture = new Fixture(scenario == "empty-queue" ? 0 : 6);
        GalleryViewModel original = fixture.Model(), current = fixture.Model();
        Configure(original);
        Configure(current);
        var originalObserver = new Observer(original);
        var currentObserver = new Observer(current);
        var raised = new List<string?>();
        current.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        GalleryGroupViewModel[] groups = current.Groups.ToArray();
        GalleryRow[] rows = current.Rows.ToArray();

        OriginalSelectAll(original);
        current.SelectAllVisible();

        AssertEquivalent(original, current, fixture);
        Assert.Equal(originalObserver.SelectionNotifications, currentObserver.SelectionNotifications);
        Assert.Equal(0, currentObserver.MetadataNotifications);
        Assert.Equal(0, currentObserver.GroupChanges);
        Assert.Equal(0, currentObserver.RowChanges);
        for (int i = 0; i < groups.Length; i++) Assert.Same(groups[i], current.Groups[i]);
        for (int i = 0; i < rows.Length; i++) Assert.Same(rows[i], current.Rows[i]);
        Assert.Equal(new[] { nameof(GalleryViewModel.SelectedTiles), nameof(GalleryViewModel.HasSelection),
            nameof(GalleryViewModel.SelectionText), nameof(GalleryViewModel.SingleSelectedTile),
            nameof(GalleryViewModel.CanReduceSelected), nameof(GalleryViewModel.CanGifSelected) }, raised);
        Assert.Equal(UiText.Format("LibrarySelection_Count", current.Selection.SelectedIds.Count), current.SelectionText);
        Assert.All(current.SelectedTiles, tile => Assert.True(tile.IsSelected));
        switch (scenario)
        {
            case "all": case "duplicate-visible": case "shift-anchor":
                Assert.Equal(6, current.SelectedTiles.Count); break;
            case "empty": case "empty-queue":
                Assert.Empty(current.SelectedTiles); Assert.Null(current.Selection.Anchor); break;
            case "single-image": case "hidden-cache":
                Assert.True(current.SingleSelectedTile?.IsImage); Assert.True(current.CanReduceSelected);
                Assert.False(current.CanGifSelected); break;
            case "single-video":
                Assert.True(current.SingleSelectedTile?.IsVideo); Assert.True(current.CanGifSelected);
                Assert.False(current.CanReduceSelected); break;
            case "videos":
                Assert.Equal(3, current.SelectedTiles.Count);
                Assert.All(current.SelectedTiles, tile => Assert.True(tile.IsVideo)); break;
            case "stale-visible":
                Assert.Equal(2, current.SelectedTiles.Count); break;
        }

        Guid[] ids = current.Groups.SelectMany(group => group.Items).Select(tile => tile.Id).ToArray();
        if (ids.Length > 1)
        {
            // Ctrl+A replaces a previous shift anchor with the first eligible displayed id.
            original.Select(ids[1], shift: true);
            current.Select(ids[1], shift: true);
            AssertEquivalent(original, current, fixture);
        }
        original.ClearSelection();
        current.ClearSelection();
        AssertEquivalent(original, current, fixture);
        Assert.False(current.HasSelection);

        void Configure(GalleryViewModel vm)
        {
            switch (scenario)
            {
                case "empty": vm.SearchQuery = "no such capture"; break;
                case "single-image": vm.SearchQuery = "capture 2"; break;
                case "single-video": vm.SearchQuery = "capture 3"; break;
                case "videos": vm.ShowView(GalleryFilter.Videos); break;
                case "hidden-cache": vm.SearchQuery = "capture 2"; break;
                case "duplicate-visible":
                case "stale-visible":
                    Guid[] visible = vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id).ToArray();
                    Guid missing = new("00000000-0000-0000-0000-000000000001");
                    vm.Selection.SetVisible(scenario == "duplicate-visible"
                        ? visible.Concat([visible[0], visible[0]])
                        : [missing, visible[2], visible[0], visible[2]]);
                    vm.Selection.Select(scenario == "duplicate-visible" ? visible[^1] : missing);
                    break;
                case "shift-anchor":
                    Guid last = vm.Groups.SelectMany(group => group.Items).Last().Id;
                    vm.Select(last);
                    break;
            }
        }
    }

    [Fact]
    public async Task FolderBrowsingSelectsDirectCapturesWhileSearchIncludesNestedMatches()
    {
        using var fixture = new Fixture(6);
        CaptureFolder folder = await fixture.Queue.CreateFolderAsync("Review", createdAt: Fixture.Now);
        CaptureFolder child = await fixture.Queue.CreateFolderAsync("Nested", folder.Id, createdAt: Fixture.Now);
        CaptureRecord[] records = fixture.Queue.Records.ToArray();
        await fixture.Queue.MoveCapturesAsync([records[0].Id], folder.Id);
        await fixture.Queue.MoveCapturesAsync([records[1].Id], child.Id);
        GalleryViewModel original = fixture.Model(), current = fixture.Model();
        foreach (CaptureFolderScope scope in new[] { CaptureFolderScope.All, CaptureFolderScope.ForFolder(folder.Id) })
        {
            foreach (string query in new[] { string.Empty, "capture" })
            {
                original.NavigateTo(scope); current.NavigateTo(scope);
                original.SearchQuery = query; current.SearchQuery = query;
                Guid[] expected = current.Groups.SelectMany(group => group.Items).Select(tile => tile.Id).ToArray();
                OriginalSelectAll(original); current.SelectAllVisible();
                AssertEquivalent(original, current, fixture);
                Assert.Equal(expected, current.Selection.SelectedIds);
                if (scope.IsAll && query.Length == 0)
                    Assert.DoesNotContain(records[1].Id, current.Selection.SelectedIds);
                if (!scope.IsAll && query.Length == 0)
                    Assert.Equal([records[0].Id], current.Selection.SelectedIds);
                if (!scope.IsAll && query.Length > 0)
                    Assert.Contains(records[1].Id, current.Selection.SelectedIds);
            }
        }
    }

    [Fact]
    public void SelectAllCostOnProductionFixtures()
    {
        const int iterations = 100;
        var results = new List<object>();
        foreach (int count in new[] { 300, 1000 })
        {
            using var fixture = new Fixture(count);
            GalleryViewModel original = fixture.Model();
            GalleryViewModel current = fixture.Model();
            // Observe the first selection separately from repeated, already-selected calls.
            var firstOriginal = new Observer(original);
            var firstCurrent = new Observer(current);
            OriginalSelectAll(original); current.SelectAllVisible();
            AssertEquivalent(original, current, fixture);
            Assert.Equal(count, firstOriginal.SelectionNotifications);
            Assert.Equal(count, firstCurrent.SelectionNotifications);
            Assert.Equal(25L * count, firstOriginal.MetadataNotifications);
            Assert.Equal(0, firstCurrent.MetadataNotifications);
            Assert.Equal(0, firstCurrent.GroupChanges);
            var firstSelection = new { BeforeMetadata = firstOriginal.MetadataNotifications,
                AfterMetadata = firstCurrent.MetadataNotifications,
                BeforeSelection = firstOriginal.SelectionNotifications, AfterSelection = firstCurrent.SelectionNotifications };
            for (int warmup = 0; warmup < 20; warmup++)
            { OriginalSelectAll(original); current.SelectAllVisible(); }
            var observer = new Observer(original);
            var currentObserver = new Observer(current);
            Measurement before = Measure(() => OriginalSelectAll(original), fixture, observer, iterations);
            Measurement after = Measure(current.SelectAllVisible, fixture, currentObserver, iterations);
            AssertEquivalent(original, current, fixture);
            Assert.Equal(iterations, before.ClockCalls);
            Assert.Equal(25L * count * iterations, before.MetadataNotifications);
            Assert.True(before.GroupChanges > 0);
            Assert.Equal(0, after.ClockCalls);
            Assert.Equal(0, after.MetadataNotifications);
            Assert.Equal(0, after.GroupChanges);
            Assert.Equal(before.SelectionNotifications, after.SelectionNotifications);
            Assert.True(after.Bytes < before.Bytes);
            // Timing is reported rather than a flaky pass/fail threshold.
            output.WriteLine($"count={count}, iterations={iterations}: before {before}; after {after}");
            results.Add(new { Count = count, Iterations = iterations, Before = before, After = after,
                FirstSelection = firstSelection });
        }
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SELECT_ALL_REPORT") is { Length: > 0 } report)
            File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertEquivalent(GalleryViewModel original, GalleryViewModel current, Fixture fixture)
    {
        Assert.Equal(original.Selection.SelectedIds, current.Selection.SelectedIds);
        Assert.Equal(original.Selection.Anchor, current.Selection.Anchor);
        Assert.Equal(original.SelectedTiles.Select(tile => tile.Id), current.SelectedTiles.Select(tile => tile.Id));
        Assert.Equal(original.SingleSelectedTile?.Id, current.SingleSelectedTile?.Id);
        Assert.Equal((original.HasSelection, original.SelectionText, original.CanReduceSelected, original.CanGifSelected),
            (current.HasSelection, current.SelectionText, current.CanReduceSelected, current.CanGifSelected));
        Assert.Equal((original.IsEmpty, original.VisibleCount, original.ResultCountText, original.SummaryText,
            original.SelectedScope, original.Filter, original.SearchQuery, original.ColumnCount, original.ShowResultCount),
            (current.IsEmpty, current.VisibleCount, current.ResultCountText, current.SummaryText,
            current.SelectedScope, current.Filter, current.SearchQuery, current.ColumnCount, current.ShowResultCount));
        Assert.Equal(original.Breadcrumb, current.Breadcrumb);
        Assert.Equal(original.Groups.Count, current.Groups.Count);
        for (int i = 0; i < original.Groups.Count; i++)
        {
            Assert.Equal(original.Groups[i].Heading, current.Groups[i].Heading);
            Assert.Equal(original.Groups[i].Items.Select(tile => tile.Id), current.Groups[i].Items.Select(tile => tile.Id));
            Assert.Equal(original.Groups[i].Folders.Select(folder => folder.FolderId), current.Groups[i].Folders.Select(folder => folder.FolderId));
        }
        Assert.Equal(original.Rows.Select(row => row.GetType()), current.Rows.Select(row => row.GetType()));
        foreach (CaptureRecord record in fixture.Queue.Records)
            Assert.Equal(original.FindTile(record.Id)?.IsSelected, current.FindTile(record.Id)?.IsSelected);
    }

    // Exact Ctrl+A view-model path at 1e1a4dd; keep this as the comparison path.
    private static void OriginalSelectAll(GalleryViewModel vm)
    {
        vm.Selection.SelectGroup(vm.Groups.SelectMany(group => group.Items).Select(tile => tile.Id), false);
        vm.Refresh();
    }

    private sealed record Measurement(long Bytes, double Milliseconds, int ClockCalls,
        long MetadataNotifications, long SelectionNotifications, long GroupChanges, long RowChanges);

    private static Measurement Measure(Action action, Fixture fixture, Observer observer, int iterations)
    {
        observer.Reset();
        fixture.ClockCalls = 0;
        var stopwatch = new Stopwatch();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        for (int iteration = 0; iteration < iterations; iteration++) action();
        stopwatch.Stop();
        return new Measurement(GC.GetAllocatedBytesForCurrentThread() - allocated, stopwatch.Elapsed.TotalMilliseconds,
            fixture.ClockCalls, observer.MetadataNotifications, observer.SelectionNotifications,
            observer.GroupChanges, observer.RowChanges);
    }

    private sealed class Observer
    {
        internal long MetadataNotifications, SelectionNotifications, GroupChanges, RowChanges;
        internal Observer(GalleryViewModel vm)
        {
            foreach (GalleryItemViewModel tile in vm.Groups.SelectMany(group => group.Items))
                tile.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(GalleryItemViewModel.IsSelected)) SelectionNotifications++;
                    else MetadataNotifications++;
                };
            ((INotifyCollectionChanged)vm.Groups).CollectionChanged += (_, _) => GroupChanges++;
            ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, _) => RowChanges++;
        }
        internal void Reset() => MetadataNotifications = SelectionNotifications = GroupChanges = RowChanges = 0;
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = OwnedTestDirectory.Create("mycapture-select-all-");
        internal CaptureQueue Queue { get; }
        internal int ClockCalls;
        internal Fixture(int count)
        {
            Queue = new CaptureQueue(AppPaths.CreateForRoot(_root), new QueueSettings { MaxItems = count + 1 },
                NullLogger<CaptureQueue>.Instance);
            for (int index = 0; index < count; index++)
                Queue.Add(new CaptureRecord
                {
                    CreatedAt = Now.AddDays(-(index % 3)).AddSeconds(-index), UpdatedAt = Now,
                    Width = 100, Height = 80, Title = $"capture {index}",
                    MediaKind = index % 2 == 0 ? CaptureMediaKind.Image : CaptureMediaKind.Video,
                });
        }
        internal GalleryViewModel Model() => new(new GalleryController(Queue, NullLogger<GalleryController>.Instance),
            _ => throw new InvalidOperationException("Selection must not load thumbnails."), 20,
            () => { ClockCalls++; return Now; });
        public void Dispose() => OwnedTestDirectory.Delete(_root);
    }
}
