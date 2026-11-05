using System.Diagnostics;
using MyCapture.App.Gallery;

namespace MyCapture.App.Diagnostics;

/// <summary>Opt-in first-page attribution. Bounded RAM only until the existing observation ends.</summary>
internal sealed class LibraryFirstPageTiming
{
    internal const string Variable = "MYCAPTURE_LIBRARY_FIRST_PAGE_TIMING";
    internal const int Capacity = 128;
    private readonly object _gate = new();
    private readonly Entry[] _entries = new Entry[Capacity];
    private readonly long _created = Stopwatch.GetTimestamp();
    private int _count, _dropped, _nextId, _active;
    private int? _metadataChecksAtSubscribe;

    internal readonly record struct Entry(string Kind, string Stage, int ScopeId, long Ticks,
        int ManagedThreadId, bool? Completed, GalleryWindow.GalleryOpenTiming? GalleryTiming);
    internal sealed record Observation(string Schema, long StopwatchFrequency, long CreatedTicks,
        long ObservedThroughTicks, int Capacity, int Dropped, int ActiveScopes,
        int? MetadataChecksAtSubscribe, Entry[] Entries);

    internal Scope Begin(string stage)
    {
        lock (_gate)
        {
            int id = ++_nextId;
            _active++;
            Add("begin", stage, id, null, null);
            return new Scope(this, stage, id);
        }
    }

    internal void Queued(string stage)
    {
        lock (_gate) Add("queued", stage, 0, null, null);
    }

    internal void SubscribedToGallery(int checksAlreadyMade)
    {
        lock (_gate)
        {
            _metadataChecksAtSubscribe = checksAlreadyMade;
            Add("subscribed", "gallery-open-timing", 0, null, null);
        }
    }

    internal void ObserveGallery(GalleryWindow.GalleryOpenTiming timing)
    {
        // This is callback-receipt time; the existing observer carries its own elapsed durations.
        lock (_gate) Add("gallery-observer-received", timing.Stage, 0, timing.Completed, timing);
    }

    internal Observation Snapshot()
    {
        lock (_gate)
            return new("library-first-page-timing-v1", Stopwatch.Frequency, _created,
                Stopwatch.GetTimestamp(), Capacity, _dropped, _active, _metadataChecksAtSubscribe,
                _entries.AsSpan(0, _count).ToArray());
    }

    private void End(string stage, int id, bool completed)
    {
        lock (_gate)
        {
            _active--;
            Add("end", stage, id, completed, null);
        }
    }

    private void Add(string kind, string stage, int id, bool? completed, GalleryWindow.GalleryOpenTiming? timing)
    {
        if (_count == Capacity) { _dropped++; return; }
        _entries[_count++] = new(kind, stage, id, Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId, completed, timing);
    }

    internal sealed class Scope(LibraryFirstPageTiming owner, string stage, int id) : IDisposable
    {
        private bool _completed;
        private int _disposed;
        internal void Complete() => _completed = true;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End(stage, id, _completed);
        }
    }
}
