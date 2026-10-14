namespace MyCapture.Core.Queue;

public enum LibraryUndoKind { Move, Tags, Pin, Title, Delete }

/// <summary>Session-only, bounded history. Failed restores remain available for retry.</summary>
public sealed class LibraryUndoHistory
{
    private sealed record Entry(LibraryUndoKind Kind, Func<Task<bool>> Restore);
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
        _entries.Add(new(kind, restore));
        if (_entries.Count > 50) _entries.RemoveAt(0);
        Changed?.Invoke(this, EventArgs.Empty);
        Recorded?.Invoke(this, EventArgs.Empty);
    }

    public async Task<LibraryUndoKind?> UndoAsync()
    {
        if (!CanUndo) return null;
        Entry entry = _entries[^1];
        IsUndoing = true;
        try
        {
            if (!await entry.Restore()) return null;
            _entries.Remove(entry);
            return entry.Kind;
        }
        finally { IsUndoing = false; Changed?.Invoke(this, EventArgs.Empty); }
    }
}
