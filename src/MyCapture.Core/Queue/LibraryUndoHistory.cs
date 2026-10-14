namespace MyCapture.Core.Queue;

public enum LibraryUndoKind { Move, Tags, Pin, Title, Delete }
public enum LibraryUndoResult { Restored, Invalidated, RetryableFailure }

/// <summary>Session-only, bounded history. Invalid entries are skipped; failed writes remain retryable.</summary>
public sealed class LibraryUndoHistory
{
    private sealed record Entry(LibraryUndoKind Kind, Func<Task<LibraryUndoResult>> Restore);
    private readonly List<Entry> _entries = [];
    public bool IsUndoing { get; private set; }
    public bool CanUndo => !IsUndoing && _entries.Count > 0;
    public int Count => _entries.Count;
    public LibraryUndoKind? NextKind => _entries.LastOrDefault()?.Kind;
    public event EventHandler? Changed;
    public event EventHandler? Recorded;

    public void Record(LibraryUndoKind kind, Func<Task<bool>> restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        Record(kind, async () => await restore() ? LibraryUndoResult.Restored : LibraryUndoResult.RetryableFailure);
    }

    public void Record(LibraryUndoKind kind, Func<Task<LibraryUndoResult>> restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        _entries.Add(new(kind, restore));
        if (_entries.Count > 50) _entries.RemoveAt(0);
        Changed?.Invoke(this, EventArgs.Empty);
        Recorded?.Invoke(this, EventArgs.Empty);
    }

    public async Task<LibraryUndoKind?> UndoAsync()
    {
        if (!CanUndo) return null;
        IsUndoing = true;
        try
        {
            while (_entries.Count > 0)
            {
                Entry entry = _entries[^1];
                LibraryUndoResult result = await entry.Restore();
                if (result == LibraryUndoResult.RetryableFailure) return null;
                _entries.Remove(entry);
                if (result == LibraryUndoResult.Restored) return entry.Kind;
            }
            return null;
        }
        finally { IsUndoing = false; Changed?.Invoke(this, EventArgs.Empty); }
    }
}
