using System.Diagnostics;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Capture;

/// <summary>Dispatcher-owned element depth. Only explicit step-in creates a source or starts a lookup.</summary>
internal sealed class ElementSelectionController : IDisposable
{
    private readonly Func<IElementGeometrySource> _createSource;
    private readonly Action<Action> _post;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Action _changed;
    private readonly Func<TimeSpan> _now;
    private IElementGeometrySource? _source;
    private WindowCandidate? _window;
    private RectD _root, _frame;
    private PointD _point, _chainPoint;
    private TimeSpan _lastMove;
    private double _scale = 1;
    private IReadOnlyList<RectD> _chain = [];
    private readonly Dictionary<IntPtr, int> _timeouts = [];
    private readonly List<int> _steps = [];
    private CancellationTokenSource? _cancellation;
    private IDisposable? _debounce;
    private long _generation;
    private bool _closed, _running, _wanted, _active, _noElements, _partial;
    private int _wheel, _targetDepth;
    internal bool Finest { get; private set; }
    internal bool CanStep => _window is { ProcessId: > 0 } window && window.ProcessId != Environment.ProcessId
        && _timeouts.GetValueOrDefault(window.Handle) < 2 && !_closed;
    internal bool ShowHint => CanStep && !_noElements;
    internal int Depth { get; private set; }
    internal bool Pending => _running || _debounce is not null;
    internal RectD Highlight { get; private set; }
    internal bool HasElement => Highlight != _root && Depth > 0;

    internal ElementSelectionController(Action changed, Dispatcher dispatcher, Func<IElementGeometrySource>? create = null)
        : this(changed, create ?? (() => new ElementGeometrySource()),
            action => { if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(action); },
            (delay, action) => new Deferred(dispatcher, delay, action)) { }

    internal ElementSelectionController(Action changed, Func<IElementGeometrySource> create, Action<Action> post,
        Func<TimeSpan, Action, IDisposable> schedule, Func<TimeSpan>? now = null)
    { _changed = changed; _createSource = create; _post = post; _schedule = schedule;
        _now = now ?? (() => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency)); }

    internal void Track(WindowCandidate? window, RectD root, RectD frame, PointD point, double scale = 1)
    {
        if (_closed) return;
        bool different = window?.Handle != _window?.Handle || window?.ProcessId != _window?.ProcessId || root != _root;
        if (different)
        {
            Reset(); _window = window; _root = root; _frame = frame; _point = point; _scale = scale;
            _lastMove = _now(); Highlight = root;
            return;
        }
        if (point == _point) return;
        _point = point;
        _lastMove = _now();
        // A provider call survives same-window movement. Its containing prefix can still be useful.
        bool needsLookup = _active && (Finest && _point != _chainPoint || !ElementGeometryBounds.Contains(Highlight, point));
        if (needsLookup && !_running) QueueLookup();
        else if (!needsLookup && _debounce is not null)
        { _debounce.Dispose(); _debounce = null; _wanted = false; }
    }

    internal bool Wheel(int delta)
    {
        if (!CanStep) return false;
        _wheel = (int)Math.Clamp((long)_wheel + delta, -120L * 64, 120L * 64);
        int steps = _wheel / 120; _wheel %= 120;
        if (steps != 0) Step(steps);
        return true;
    }

    internal void Step(int steps)
    {
        if (!CanStep || steps == 0) return;
        var before = VisibleState;
        if (_chain.Count == 1 && (_partial || steps > 0 && _point != _chainPoint)) _chain = [];
        int count = Math.Min(64, Math.Abs(steps));
        if (_chain.Count == 0)
        {
            if (!_active && steps < 0) return;
            for (int i = 0; i < count; i++) _steps.Add(Math.Sign(steps));
            _active = true; _wanted = true;
            _debounce?.Dispose(); _debounce = null;
            StartLookup(); return;
        }
        for (int i = 0; i < count; i++) ApplyStep(Math.Sign(steps), _chain.Count - 1);
        _active = _targetDepth > 0;
        if (!_active)
        {
            Stop(); Depth = 0; Highlight = _root; NotifyChange(before); return;
        }
        if (TryShowTarget())
        {
            if (Finest && _point != _chainPoint) QueueLookup();
            NotifyChange(before); return;
        }
        _wanted = true;
        _debounce?.Dispose(); _debounce = null;
        StartLookup();
    }

    private void ApplyStep(int step, int deepest)
    {
        if (deepest == 0) { _targetDepth = 0; Finest = false; return; }
        if (step > 0)
        {
            if (_targetDepth == 0) { Finest = true; _targetDepth = deepest; }
            else if (!Finest) { _targetDepth = Math.Min(deepest, _targetDepth + 1); Finest = _targetDepth == deepest; }
        }
        else { Finest = false; _targetDepth = Math.Max(0, _targetDepth - 1); }
    }

    private void QueueLookup()
    {
        _wanted = true;
        if (_running) return;
        _debounce?.Dispose();
        TimeSpan remaining = TimeSpan.FromMilliseconds(60) - (_now() - _lastMove);
        if (remaining <= TimeSpan.Zero) { _debounce = null; StartLookup(); return; }
        _debounce = _schedule(remaining, () =>
        { _debounce = null; StartLookup(); });
    }

    private void StartLookup()
    {
        if (_closed || _running || !_wanted || !_active || !CanStep || _window is null) return;
        _wanted = false;
        _running = true;
        long generation = ++_generation;
        WindowCandidate window = _window;
        PointD point = _point;
        RectD root = _root, frame = _frame;
        double scale = _scale;
        var cancellation = _cancellation = new CancellationTokenSource();
        SelectorTimeline.Mark?.Invoke("element-lookup-start");
        _ = Read();
        async Task Read()
        {
            ElementGeometryResult result;
            try { result = await (_source ??= _createSource()).ReadAsync(window, point, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = ElementGeometryResult.Empty(ElementGeometryStatus.Cancelled); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = ElementGeometryResult.Empty(ElementGeometryStatus.Unavailable); }
            cancellation.Dispose();
            _post(() =>
            {
                _running = false;
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
                if (_closed || generation != _generation || window.Handle != _window?.Handle)
                {
                    SelectorTimeline.Mark?.Invoke("element-lookup-drop");
                    if (!_closed && _debounce is null) StartLookup();
                    return;
                }
                SelectorTimeline.Mark?.Invoke("element-lookup-result");
                if (result.Partial) SelectorTimeline.Mark?.Invoke("element-lookup-partial");
                var before = VisibleState;
                _wanted = false;
                if (result.Status != ElementGeometryStatus.Success)
                {
                    if (result.Status == ElementGeometryStatus.TimedOut)
                        _timeouts[window.Handle] = _timeouts.GetValueOrDefault(window.Handle) + 1;
                    Depth = _targetDepth = 0; Finest = false; Highlight = _root; _chain = []; _steps.Clear(); _active = false;
                }
                else
                {
                    _partial = result.Partial;
                    _chain = Filter(root, frame, point, result.Chain, scale);
                    _chainPoint = point;
                    int deepest = _chain.Count - 1;
                    if (Finest) _targetDepth = deepest;
                    else _targetDepth = Math.Min(_targetDepth, deepest);
                    foreach (int step in _steps) ApplyStep(step, deepest);
                    _steps.Clear();
                    _active = _targetDepth > 0;
                    if (deepest > 0) _noElements = false;
                    else if (!result.Partial) _noElements = true;
                    if (!TryShowTarget()) { _active = true; QueueLookup(); }
                    else if (Finest && _point != _chainPoint) QueueLookup();
                }
                NotifyChange(before);
            });
        }
    }

    private (RectD Rectangle, bool Element, bool Hint) VisibleState => (Highlight, HasElement, ShowHint);
    private void NotifyChange((RectD Rectangle, bool Element, bool Hint) before)
    {
        if (VisibleState != before) _changed();
    }

    private bool TryShowTarget()
    {
        if (_chain.Count == 0 || _targetDepth >= _chain.Count) return false;
        // Nested, clipped levels make this equivalent to keeping the containing prefix.
        if (!ElementGeometryBounds.Contains(_chain[_targetDepth], _point)) return false;
        Depth = _targetDepth; Highlight = _chain[Depth];
        _wanted = false; _debounce?.Dispose(); _debounce = null;
        return true;
    }
    internal static IReadOnlyList<RectD> Filter(RectD root, RectD frame, PointD point, IReadOnlyList<ElementGeometry> input, double scale = 1)
    {
        double tolerance = Math.Max(2, 4 * (double.IsFinite(scale) && scale > 0 ? scale : 1));
        root = ElementGeometryBounds.Intersect(root, frame).ToPixelBounds();
        var levels = new List<RectD> { root };
        foreach (ElementGeometry entry in input)
        {
            RectD raw = entry.Bounds, parent = levels[^1];
            if (!ElementGeometryBounds.Valid(raw) || raw.Width > parent.Width + 2 || raw.Height > parent.Height + 2) continue;
            RectD rect = ElementGeometryBounds.Intersect(ElementGeometryBounds.Intersect(raw, root), frame);
            if (rect.Width < 2 || rect.Height < 2 || !ElementGeometryBounds.Contains(rect, point)) continue;
            rect = ElementGeometryBounds.Intersect(rect.ToPixelBounds(), parent);
            if (rect.Width < 2 || rect.Height < 2 || !ElementGeometryBounds.Contains(rect, point)) continue;
            if (Math.Abs(parent.Left - rect.Left) <= tolerance && Math.Abs(parent.Top - rect.Top) <= tolerance
                && Math.Abs(parent.Right - rect.Right) <= tolerance && Math.Abs(parent.Bottom - rect.Bottom) <= tolerance) continue;
            levels.Add(rect);
            if (levels.Count >= 65) break;
        }
        return levels;
    }
    internal void Stop() { Invalidate(); _wanted = false; _active = false; _steps.Clear(); }
    internal void Reset()
    {
        Stop(); _window = null; _chain = []; _wheel = 0; Depth = _targetDepth = 0; Finest = false; _noElements = _partial = false; Highlight = default;
    }
    private void Invalidate()
    {
        _generation++;
        _debounce?.Dispose(); _debounce = null;
        try { _cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        if (_closed) return;
        Reset(); _closed = true; _timeouts.Clear(); _source?.Dispose(); _source = null;
    }
    private sealed class Deferred : IDisposable
    {
        private readonly DispatcherTimer _timer;
        internal Deferred(Dispatcher dispatcher, TimeSpan delay, Action action)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
            _timer.Tick += Tick;
            void Tick(object? sender, EventArgs args) { Dispose(); action(); }
            _timer.Start();
        }
        public void Dispose() => _timer.Stop();
    }
}
