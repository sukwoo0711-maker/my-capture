using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;

namespace MyCapture.Platform.Capture;

public sealed record ElementGeometry(RectD Bounds, int ControlType);
public enum ElementGeometryStatus { Success, Unavailable, TimedOut, Cancelled }
public sealed record ElementGeometryResult(ElementGeometryStatus Status, IReadOnlyList<ElementGeometry> Chain, bool Partial = false)
{
    public static ElementGeometryResult Empty(ElementGeometryStatus status) => new(status, []);
}
public interface IElementGeometrySource : IDisposable
{
    Task<ElementGeometryResult> ReadAsync(WindowCandidate window, PointD point, CancellationToken cancellation);
}

/// <summary>One lazy MTA per selector session; COM objects belong to one lookup, never to the idle thread.</summary>
public sealed partial class ElementGeometrySource : IElementGeometrySource
{
    public const int CallTimeoutMilliseconds = 250;
    public const int LookupTimeoutMilliseconds = 600;
    private readonly BlockingCollection<Work> _queue = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private bool _disposed;
    private int _busy, _clientCreations, _lookups;
    internal int ClientCreations => Volatile.Read(ref _clientCreations);
    internal int LookupCount => Volatile.Read(ref _lookups);
    internal Task WorkerStopped => _stopped.Task;
    internal bool WorkerStarted => _thread is not null;

    public Task<ElementGeometryResult> ReadAsync(WindowCandidate window, PointD point, CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (_disposed || cancellation.IsCancellationRequested)
                return Task.FromResult(ElementGeometryResult.Empty(ElementGeometryStatus.Cancelled));
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return Task.FromResult(ElementGeometryResult.Empty(ElementGeometryStatus.Unavailable));
            var work = new Work(window, point, cancellation);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "MyCapture element geometry" };
                _thread.SetApartmentState(ApartmentState.MTA);
                try { _thread.Start(); }
                catch
                {
                    _thread = null;
                    Interlocked.Exchange(ref _busy, 0);
                    throw;
                }
            }
            _queue.Add(work);
            return work.Completion.Task;
        }
    }

    private void Run()
    {
        try
        {
            foreach (Work work in _queue.GetConsumingEnumerable())
            {
                ElementGeometryResult result;
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, work.Cancellation);
                try { Interlocked.Increment(ref _lookups); result = ReadNative(work.Window, work.Point, cancel.Token); }
                catch (OperationCanceledException) { result = ElementGeometryResult.Empty(ElementGeometryStatus.Cancelled); }
                catch (TimeoutException) { result = ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut); }
                catch (COMException error) when (error.HResult is unchecked((int)0x80131505)
                    or unchecked((int)0x800705B4) or unchecked((int)0x8001011F))
                { result = ElementGeometryResult.Empty(ElementGeometryStatus.TimedOut); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { result = ElementGeometryResult.Empty(ElementGeometryStatus.Unavailable); }
                Interlocked.Exchange(ref _busy, 0);
                work.Completion.TrySetResult(result);
            }
        }
        finally { _queue.Dispose(); _stop.Dispose(); _stopped.TrySetResult(); }
    }

    private ElementGeometryResult ReadNative(WindowCandidate window, PointD point, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        GetWindowThreadProcessId(window.Handle, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId || (window.ProcessId != 0 && pid != window.ProcessId)
            || !IsWindowVisible(window.Handle) || IsHungAppWindow(window.Handle))
            return ElementGeometryResult.Empty(ElementGeometryStatus.Unavailable);
        var clock = Stopwatch.StartNew();
        nint dpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
        var owned = new List<object>();
        var unique = new HashSet<object>(ReferenceEqualityComparer.Instance);
        T Own<T>(T value) where T : class { if (unique.Add(value)) owned.Add(value); return value; }
        try
        {
            var automation = Own((IUiAutomation2)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("E22AD333-B25F-460C-83D0-0581107395C9"), true)!)!);
            Interlocked.Increment(ref _clientCreations);
            automation.SetAutoSetFocus(false);
            automation.SetConnectionTimeout(CallTimeoutMilliseconds);
            automation.SetTransactionTimeout(CallTimeoutMilliseconds);
            var walker = Own(automation.GetRawViewWalker());
            var cache = Own(automation.CreateCacheRequest());
            cache.SetTreeScope(1);
            cache.SetTreeFilter(Own(automation.GetRawViewCondition()));
            cache.SetAutomationElementMode(1);
            cache.AddProperty(30001); // BoundingRectangle
            cache.AddProperty(30003); // ControlType: no names, values, patterns or events.
            T Call<T>(Func<T> call)
            {
                cancellation.ThrowIfCancellationRequested();
                int remaining = LookupTimeoutMilliseconds - (int)Math.Ceiling(clock.Elapsed.TotalMilliseconds);
                // UIA rejects a connection timeout below 50 ms. Do not start a call that
                // cannot fit its minimum timeout inside the remaining 600 ms lookup budget.
                if (remaining < 50) throw new TimeoutException();
                uint budget = (uint)Math.Min(CallTimeoutMilliseconds, remaining);
                automation.SetConnectionTimeout(budget);
                automation.SetTransactionTimeout(budget);
                T value = call();
                cancellation.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= LookupTimeoutMilliseconds) throw new TimeoutException();
                return value;
            }
            IUiElement root = Own(Call(() => automation.ElementFromHandleBuildCache(window.Handle, cache)));
            ElementGeometryResult result = Walk(root, point, element =>
            {
                object bounds = element.GetCachedPropertyValue(30001);
                object type = element.GetCachedPropertyValue(30003);
                RectD rect = bounds is double[] { Length: 4 } r ? new(r[0], r[1], r[2], r[3]) : RectD.Empty;
                return new ElementGeometry(rect, type is int controlType ? controlType : 0);
            }, element => { var child = Call(() => walker.GetFirstChildElementBuildCache(element, cache)); return child is null ? null : Own(child); },
                element => { var sibling = Call(() => walker.GetNextSiblingElementBuildCache(element, cache)); return sibling is null ? null : Own(sibling); },
                () => clock.Elapsed, cancellation);
            GetWindowThreadProcessId(window.Handle, out uint afterPid);
            return afterPid == pid ? result
                : ElementGeometryResult.Empty(ElementGeometryStatus.Unavailable);
        }
        finally
        {
            for (int i = owned.Count - 1; i >= 0; i--) Marshal.FinalReleaseComObject(owned[i]);
            if (dpi != IntPtr.Zero) _ = SetThreadDpiAwarenessContext(dpi);
        }
    }

    // Shared traversal is independently testable without COM, wall-clock sleeps or a live desktop.
    internal static ElementGeometryResult Walk<T>(T root, PointD point, Func<T, ElementGeometry> geometry,
        Func<T, T?> firstChild, Func<T, T?> nextSibling, Func<TimeSpan> elapsed, CancellationToken cancellation) where T : class
    {
        List<ElementGeometry> best = [], path = [];
        int nodes = 0;
        bool partial = false, exhausted = false, timedOut = false;
        bool Timeout(Exception error) => error is TimeoutException || error is COMException com
            && com.HResult is unchecked((int)0x80131505) or unchecked((int)0x800705B4) or unchecked((int)0x8001011F);
        void Visit(T element, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (nodes >= 2048 || depth > 64 || elapsed().TotalMilliseconds >= LookupTimeoutMilliseconds)
            { partial = exhausted = true; return; }
            nodes++;
            int count = path.Count;
            try
            {
                ElementGeometry entry = geometry(element);
                bool valid = ElementGeometryBounds.Valid(entry.Bounds);
                if (depth > 0 && valid && !ElementGeometryBounds.Contains(entry.Bounds, point)) return;
                if (valid) path.Add(entry);
                if (path.Count > best.Count) best = [.. path];
                T? child = firstChild(element);
                while (child is not null && !exhausted)
                {
                    Visit(child, depth + 1);
                    if (!exhausted) child = nextSibling(child);
                }
            }
            catch (Exception error) when (Timeout(error)) { partial = exhausted = timedOut = true; }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            { partial = true; } // An unavailable subtree does not discard its siblings or the known path.
            finally { path.RemoveRange(count, path.Count - count); }
        }
        Visit(root, 0);
        return best.Count == 0
            ? ElementGeometryResult.Empty(timedOut ? ElementGeometryStatus.TimedOut : ElementGeometryStatus.Unavailable)
            : new(ElementGeometryStatus.Success, best, partial);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _queue.CompleteAdding();
            if (_thread is null) { _queue.Dispose(); _stop.Dispose(); _stopped.TrySetResult(); }
        }
    }
    private sealed record Work(WindowCandidate Window, PointD Point, CancellationToken Cancellation)
    {
        internal TaskCompletionSource<ElementGeometryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}

public static class ElementGeometryBounds
{
    public static bool Valid(RectD r) => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width)
        && double.IsFinite(r.Height) && r.Width > 0 && r.Height > 0;
    public static bool Contains(RectD r, PointD p) => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    public static RectD Intersect(RectD a, RectD b) => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top),
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)),
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top)));
}
