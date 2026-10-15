namespace MyCapture.Core.Queue;

public enum LibraryActivityKind { Import, ZipExport, FolderExport, Pdf, Ocr }

public sealed record LibraryActivity(Guid Id, LibraryActivityKind Kind, int Completed, int? Total,
    bool CanCancel, bool CancellationRequested);

/// <summary>One shared view of running work. Reports after completion are ignored.</summary>
public sealed class LibraryActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (LibraryActivity State, Action? Cancel)> _running = [];
    public event EventHandler? Changed;
    public IReadOnlyList<LibraryActivity> Running
    {
        get { lock (_gate) return _running.Values.Select(item => item.State).ToArray(); }
    }

    public LibraryActivityScope Start(LibraryActivityKind kind, Action? cancel = null, int? total = null)
    {
        var state = new LibraryActivity(Guid.NewGuid(), kind, 0, total is >= 0 ? total : null, cancel is not null, false);
        lock (_gate) _running.Add(state.Id, (state, cancel));
        Changed?.Invoke(this, EventArgs.Empty);
        return new LibraryActivityScope(this, state.Id);
    }

    public bool Cancel(Guid id)
    {
        Action? cancel;
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var item) || !item.State.CanCancel) return false;
            cancel = item.Cancel;
            _running[id] = (item.State with { CanCancel = false, CancellationRequested = true }, null);
            // Completion cannot dispose the cancellation source while its callback is running.
            cancel?.Invoke();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void Report(Guid id, int completed, int? total)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var item)) return;
            int? maximum = total is >= 0 ? total : item.State.Total;
            int value = Math.Max(0, completed);
            if (maximum is { } count) value = Math.Min(value, count);
            _running[id] = (item.State with { Completed = value, Total = maximum }, item.Cancel);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Complete(Guid id)
    {
        lock (_gate) { if (!_running.Remove(id)) return; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class LibraryActivityScope : IDisposable
{
    private readonly LibraryActivityTracker _owner;
    private readonly Guid _id;
    internal LibraryActivityScope(LibraryActivityTracker owner, Guid id) { _owner = owner; _id = id; }
    public void Report(int completed, int? total = null) => _owner.Report(_id, completed, total);
    public void Dispose() => _owner.Complete(_id);
}
