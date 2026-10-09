using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyCapture.App.Editing;

/// <summary>
/// Whole-capture blurred frames for the live editor surface, computed off the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The surface draws a blur layer by clipping a fully blurred copy of the base capture to the
/// layer's pixel rectangle. Moving or resizing a layer is then only a new clip, so dragging a
/// large blur over a 4K capture never recomputes pixels on the UI thread. A frame is computed
/// once per base bitmap and strength, in the background; until it arrives the renderer shows
/// the nearest strength already available, or an opaque cover, never the sharp pixels.
/// </para>
/// <para>
/// Frames are kept only for strengths the latest paint used. A rotation, crop or undo that
/// replaces the base bitmap drops every frame of the old base.
/// </para>
/// </remarks>
internal sealed class AnnotationBlurCache
{
    private readonly Dispatcher _dispatcher;
    private readonly List<Entry> _entries = [];
    private BitmapSource? _source;
    private int _pass;

    internal AnnotationBlurCache(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>Raised on the UI thread when a frame finished, so the surface can repaint.</summary>
    internal event EventHandler? FrameReady;

    /// <summary>Background work still running, for tests that need a settled cache.</summary>
    internal Task Pending => Task.WhenAll(_entries.Select(static entry => entry.Work));

    /// <summary>Starts a paint pass over <paramref name="source"/>.</summary>
    internal void BeginPass(BitmapSource source)
    {
        _dispatcher.VerifyAccess();
        if (!ReferenceEquals(_source, source))
        {
            foreach (Entry entry in _entries)
            {
                entry.Cancel();
            }

            _entries.Clear();
            _source = source;
        }

        _pass++;
    }

    /// <summary>
    /// The base blurred at <paramref name="radius"/>, the closest strength available while it
    /// is being computed, or <see langword="null"/> when nothing is ready yet.
    /// </summary>
    internal BitmapSource? Frame(int radius)
    {
        _dispatcher.VerifyAccess();
        if (_source is null)
        {
            return null;
        }

        Entry? exact = _entries.Find(entry => entry.Radius == radius);
        if (exact is null)
        {
            exact = Start(_source, radius);
            _entries.Add(exact);
        }

        exact.UsedInPass = _pass;
        if (exact.Frame is not null)
        {
            return exact.Frame;
        }

        Entry? nearest = _entries
            .Where(static entry => entry.Frame is not null)
            .OrderBy(entry => Math.Abs(entry.Radius - radius))
            .FirstOrDefault();
        if (nearest is null)
        {
            return null;
        }

        nearest.UsedInPass = _pass;
        return nearest.Frame;
    }

    /// <summary>Drops frames and cancels work for strengths the finished pass did not use.</summary>
    internal void EndPass()
    {
        _dispatcher.VerifyAccess();
        _entries.RemoveAll(entry =>
        {
            if (entry.UsedInPass == _pass)
            {
                return false;
            }

            entry.Cancel();
            return true;
        });
    }

    /// <summary>
    /// Cancels running work and releases every frame, for example when the editor unloads.
    /// The next paint starts afresh.
    /// </summary>
    internal void Clear()
    {
        _dispatcher.VerifyAccess();
        foreach (Entry entry in _entries)
        {
            entry.Cancel();
        }

        _entries.Clear();
        _source = null;
    }

    private Entry Start(BitmapSource source, int radius)
    {
        // A FrozenFrame bitmap is frozen by contract; freeze a copy otherwise so the worker
        // thread can read it.
        BitmapSource readable = source.IsFrozen ? source : (BitmapSource)source.GetAsFrozen();
        var entry = new Entry(radius);
        CancellationToken token = entry.Token;
        entry.Work = Task.Run(() => AnnotationBlurPixels.BlurWhole(readable, radius, token), token)
            .ContinueWith(
                task => _dispatcher.InvokeAsync(() => Complete(entry, task)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return entry;
    }

    private void Complete(Entry entry, Task<BitmapSource> task)
    {
        if (entry.IsCancelled || !_entries.Contains(entry))
        {
            return;
        }

        if (task.IsCompletedSuccessfully)
        {
            entry.Frame = task.Result;
            FrameReady?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (task.Exception is { } failure)
        {
            // Leave the opaque cover in place rather than reveal the pixels; drop the entry so
            // a later paint retries instead of repeating a stale failure forever.
            System.Diagnostics.Trace.TraceWarning("Blur preview failed: {0}", failure.GetBaseException());
            _entries.Remove(entry);
        }
    }

    private sealed class Entry(int radius)
    {
        private readonly CancellationTokenSource _cancellation = new();

        internal int Radius { get; } = radius;

        internal BitmapSource? Frame { get; set; }

        internal int UsedInPass { get; set; }

        internal Task Work { get; set; } = Task.CompletedTask;

        internal CancellationToken Token => _cancellation.Token;

        internal bool IsCancelled => _cancellation.IsCancellationRequested;

        internal void Cancel() => _cancellation.Cancel();
    }
}
