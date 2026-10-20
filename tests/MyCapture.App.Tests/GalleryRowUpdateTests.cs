using System.Collections.Specialized;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A library refresh replaces only the rows that changed: the rows equal what a full rebuild
/// produces, other days keep their row objects (and the list its cards), and no reset is raised.
/// </summary>
public sealed class GalleryRowUpdateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(9));
    private readonly string _workspace = OwnedTestDirectory.Create("MyCapture-gallery-rows-");

    public void Dispose() => OwnedTestDirectory.Delete(_workspace);

    [Fact]
    public void NewCaptureReplacesOnlyItsOwnDaysRows()
    {
        (CaptureQueue queue, GalleryViewModel vm) = Library(today: 10, yesterday: 9, older: 8);
        GalleryRow[] before = [.. vm.Rows];
        GalleryRow[] otherDays = before[Array.FindIndex(before, 1, row => row is GalleryHeaderRow)..];
        Assert.NotEmpty(otherDays);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, e) => changes.Add(e.Action);

        queue.Add(Record(Now.AddMinutes(-1), "new"));
        vm.Refresh();

        AssertRowsMatchFullRebuild(vm);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        int todayRows = vm.Rows.TakeWhile(row => row is not GalleryHeaderRow || ReferenceEquals(row, vm.Rows[0])).Count();
        Assert.InRange(changes.Count, 1, todayRows);
        // Every row of the other days is still the same object.
        Assert.All(otherDays.Zip(vm.Rows.Skip(vm.Rows.Count - otherDays.Length)), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public void RemovingFromAnOlderDayKeepsTheNewerDaysRows()
    {
        (CaptureQueue queue, GalleryViewModel vm) = Library(today: 7, yesterday: 5, older: 4);
        GalleryRow[] before = [.. vm.Rows];
        int newestDay = before.Skip(1).TakeWhile(row => row is not GalleryHeaderRow).Count() + 1;
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, e) => changes.Add(e.Action);

        Assert.True(queue.Remove(queue.Records[^1].Id));
        vm.Refresh();

        AssertRowsMatchFullRebuild(vm);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.All(before.Take(newestDay).Zip(vm.Rows.Take(newestDay)), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public void RefreshWithoutChangesRaisesNothingAndADayEmptiedRemovesItsRows()
    {
        (CaptureQueue queue, GalleryViewModel vm) = Library(today: 3, yesterday: 2, older: 2);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, e) => changes.Add(e.Action);
        vm.Refresh();
        Assert.Empty(changes);

        foreach (CaptureRecord record in queue.Records.Where(record => record.CreatedAt.Date == Now.AddDays(-1).Date).ToArray())
            Assert.True(queue.Remove(record.Id));
        vm.Refresh();
        AssertRowsMatchFullRebuild(vm);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal(2, vm.Rows.OfType<GalleryHeaderRow>().Count());
    }

    [Fact]
    public void ColumnChangeStillRebuildsTheWholeListToTheNewCount()
    {
        (_, GalleryViewModel vm) = Library(today: 9, yesterday: 4, older: 0);
        int before = vm.ColumnCount;
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.Rows).CollectionChanged += (_, e) => changes.Add(e.Action);
        Assert.True(vm.SetColumnCountForWidth(GalleryRowBuilder.MinTileTrackWidth * (before + 2) + 1));
        AssertRowsMatchFullRebuild(vm);
        // A new layout lays the list out anew, as before.
        Assert.Equal(NotifyCollectionChangedAction.Reset, changes[0]);
        Assert.All(vm.Rows.Where(row => row is not GalleryHeaderRow), row => Assert.Equal(vm.ColumnCount, row.Columns));
    }

    private (CaptureQueue Queue, GalleryViewModel ViewModel) Library(int today, int yesterday, int older)
    {
        // Queue membership and grouping only: nothing is written under this root.
        var queue = new CaptureQueue(AppPaths.CreateForRoot(Path.Combine(_workspace, "unused")),
            new QueueSettings { MaxItems = 1000 }, NullLogger<CaptureQueue>.Instance);
        var created = new List<DateTimeOffset>();
        created.AddRange(Enumerable.Range(0, today).Select(i => Now.AddMinutes(-10 - i)));
        created.AddRange(Enumerable.Range(0, yesterday).Select(i => Now.AddDays(-1).AddMinutes(-10 - i)));
        created.AddRange(Enumerable.Range(0, older).Select(i => Now.AddDays(-3).AddMinutes(-10 - i)));
        foreach (DateTimeOffset at in created.OrderBy(at => at)) queue.Add(Record(at, "capture"));
        var vm = new GalleryViewModel(new GalleryController(queue, NullLogger<GalleryController>.Instance),
            _ => throw new InvalidOperationException("Row updates never load a thumbnail."), 160, () => Now);
        vm.SetColumnCountForWidth(GalleryRowBuilder.MinTileTrackWidth * 3 + 1);
        return (queue, vm);
    }

    private static CaptureRecord Record(DateTimeOffset createdAt, string title) => new()
    {
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Title = title,
        MediaKind = CaptureMediaKind.Image,
    };

    private static void AssertRowsMatchFullRebuild(GalleryViewModel vm)
    {
        IReadOnlyList<GalleryRow> rebuilt = GalleryRowBuilder.Build(vm.Groups, vm.ColumnCount, vm.IsCompactLayout);
        Assert.Equal(rebuilt.Count, vm.Rows.Count);
        foreach ((GalleryRow expected, GalleryRow actual) in rebuilt.Zip(vm.Rows))
        {
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Columns, actual.Columns);
            Assert.Equal(expected.IsCompact, actual.IsCompact);
            switch ((expected, actual))
            {
                case (GalleryHeaderRow a, GalleryHeaderRow b): Assert.Equal(a.Heading, b.Heading); break;
                case (GalleryTileRow a, GalleryTileRow b): Assert.Equal(a.Tiles, b.Tiles); break;
                case (GalleryFolderRow a, GalleryFolderRow b): Assert.Equal(a.Folders, b.Folders); break;
            }
        }
    }
}
