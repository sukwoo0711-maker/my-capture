using System.Diagnostics;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryFirstPageTimingTests
{
    [Fact]
    public void NestedSpansShareStopwatchClockAndRemainDistinct()
    {
        long before = Stopwatch.GetTimestamp();
        var timing = new LibraryFirstPageTiming();
        using (var outer = timing.Begin("outer"))
        {
            using (var inner = timing.Begin("inner")) inner.Complete();
            outer.Complete();
        }
        var snapshot = timing.Snapshot();
        Assert.Equal(Stopwatch.Frequency, snapshot.StopwatchFrequency);
        Assert.Equal(0, snapshot.ActiveScopes);
        Assert.Equal(0, snapshot.Dropped);
        Assert.Equal(new[] { "begin", "begin", "end", "end" }, snapshot.Entries.Select(entry => entry.Kind));
        Assert.Equal(snapshot.Entries[1].ScopeId, snapshot.Entries[2].ScopeId);
        Assert.Equal(snapshot.Entries[0].ScopeId, snapshot.Entries[3].ScopeId);
        Assert.All(snapshot.Entries, entry => Assert.InRange(entry.Ticks, before, snapshot.ObservedThroughTicks));
        Assert.All(snapshot.Entries.Where(entry => entry.Kind == "end"), entry => Assert.True(entry.Completed));
    }

    [Fact]
    public void ExceptionEndsScopeAsIncompleteAndStillPropagates()
    {
        var timing = new LibraryFirstPageTiming();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = timing.Begin("failure");
            throw new InvalidOperationException("Synthetic failure");
        }));
        var snapshot = timing.Snapshot();
        Assert.Equal(0, snapshot.ActiveScopes);
        Assert.False(snapshot.Entries[1].Completed);
    }

    [Fact]
    public void BoundedBufferReportsDropsAndSnapshotDoesNotPretendActiveWorkCompleted()
    {
        var timing = new LibraryFirstPageTiming();
        using var scope = timing.Begin("pending");
        for (int i = 0; i < LibraryFirstPageTiming.Capacity; i++) timing.Queued("synthetic");
        var snapshot = timing.Snapshot();
        Assert.Equal(LibraryFirstPageTiming.Capacity, snapshot.Entries.Length);
        Assert.Equal(1, snapshot.Dropped);
        Assert.Equal(1, snapshot.ActiveScopes);
        scope.Complete();
        scope.Dispose();
        scope.Dispose();
        Assert.Equal(0, timing.Snapshot().ActiveScopes);
        Assert.Equal(1, snapshot.ActiveScopes);
    }

    [Fact]
    public void ExistingGalleryTimingAndColdSubscriptionStateArePreserved()
    {
        var timing = new LibraryFirstPageTiming();
        timing.SubscribedToGallery(3);
        var value = new GalleryWindow.GalleryOpenTiming("metadata-minimum-widths", 12,
            CallNumber: 4, CacheHit: true, MeasureCount: 0, Completed: true);
        timing.ObserveGallery(value);
        var snapshot = timing.Snapshot();
        Assert.Equal(3, snapshot.MetadataChecksAtSubscribe);
        Assert.Equal(value, snapshot.Entries[1].GalleryTiming);
        Assert.Equal("gallery-observer-received", snapshot.Entries[1].Kind);
    }
}
