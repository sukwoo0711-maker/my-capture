using System.Diagnostics;

namespace MyCapture.Core.Undo;

/// <summary>
/// Undo and redo history for one editing session.
/// </summary>
/// <remarks>
/// <para>
/// Merging is time-gated as well as command-gated. Two style changes to the same
/// property are only collapsed when they arrive close together, so dragging a slider
/// becomes one step while deliberately changing a colour, pausing, and changing it
/// again stays two — which is what the user means in each case.
/// </para>
/// <para>
/// Not thread-safe; all edits originate on the UI thread.
/// </para>
/// </remarks>
public sealed class UndoStack
{
    /// <summary>
    /// Maximum retained steps.
    /// </summary>
    /// <remarks>
    /// Generous because commands are small deltas rather than document snapshots.
    /// The cap exists to bound a pathological session (holding the pen tool down for
    /// minutes), not to ration normal editing.
    /// </remarks>
    public const int DefaultDepthLimit = 500;

    /// <summary>
    /// Window within which two compatible commands may be collapsed.
    /// </summary>
    public static readonly TimeSpan DefaultMergeWindow = TimeSpan.FromMilliseconds(600);

    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private readonly int _depthLimit;
    private readonly TimeSpan _mergeWindow;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextStateId;
    private long _savedStateId;

    /// <summary>
    /// When the top command was pushed, or <see langword="null"/> when the top
    /// command must not absorb the next one.
    /// </summary>
    /// <remarks>
    /// Nullable rather than a sentinel timestamp. A sentinel of
    /// <see cref="TimeSpan.MinValue"/> overflows on subtraction, which is exactly the
    /// bug this replaced.
    /// </remarks>
    private TimeSpan? _lastPushAt;

    private int _batchDepth;
    private List<IUndoableCommand>? _batch;
    private string _batchDescription = string.Empty;

    public UndoStack(int depthLimit = DefaultDepthLimit, TimeSpan? mergeWindow = null)
    {
        if (depthLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(depthLimit), depthLimit, "Depth limit must be at least 1.");
        }

        _depthLimit = depthLimit;
        _mergeWindow = mergeWindow ?? DefaultMergeWindow;
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    /// <summary>
    /// Identity of the current document state, independent of the retained history depth.
    /// Undo and redo restore the identity of the state they return to.
    /// </summary>
    public long CurrentStateId { get; private set; }

    /// <summary>Whether recorded edits differ from the last saved or initial state.</summary>
    public bool HasUnsavedChanges => CurrentStateId != _savedStateId || _batch is { Count: > 0 };

    /// <summary>
    /// Records a successful save and prevents subsequent edits from merging across it.
    /// Clearing history alone never changes this save point.
    /// </summary>
    public void MarkSaved()
    {
        if (_batchDepth != 0)
        {
            throw new InvalidOperationException("Finish the edit batch before marking it saved.");
        }

        _savedStateId = CurrentStateId;
        _lastPushAt = null;
        RaiseChanged();
    }

    /// <summary>Assets reachable through undo, redo, or an unfinished composite operation.</summary>
    public IEnumerable<string> ReferencedImageAssets => _undo.Concat(_redo)
        .Select(entry => entry.Command)
        .Concat(_batch ?? [])
        .SelectMany(command => command.ReferencedImageAssets);

    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].Command.Description : null;

    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].Command.Description : null;

    /// <summary>
    /// Raised after any change to the history, so commands and menu items can
    /// refresh their enabled state.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Runs <paramref name="command"/> and records it.
    /// </summary>
    public void Execute(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Execute();
        Push(command);
    }

    /// <summary>
    /// Records a command that has already been applied.
    /// </summary>
    /// <remarks>
    /// Needed for direct-manipulation gestures: the shape has already followed the
    /// mouse by the time the drag ends, so re-executing would be a no-op at best and
    /// a double-apply at worst.
    /// </remarks>
    public void Push(IUndoableCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_batch is not null)
        {
            _batch.Add(command);
            return;
        }

        // Any new edit invalidates the redo branch.
        _redo.Clear();

        TimeSpan now = _clock.Elapsed;
        bool withinWindow = _lastPushAt.HasValue && (now - _lastPushAt.Value) <= _mergeWindow;

        if (withinWindow && _undo.Count > 0 && _undo[^1].Command.TryMergeWith(command))
        {
            CurrentStateId = ++_nextStateId;
            _undo[^1] = _undo[^1] with { AfterStateId = CurrentStateId };
            _lastPushAt = now;
            RaiseChanged();
            return;
        }

        long beforeStateId = CurrentStateId;
        CurrentStateId = ++_nextStateId;
        _undo.Add(new HistoryEntry(command, beforeStateId, CurrentStateId));
        _lastPushAt = now;

        if (_undo.Count > _depthLimit)
        {
            _undo.RemoveRange(0, _undo.Count - _depthLimit);
        }

        RaiseChanged();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        HistoryEntry entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        entry.Command.Undo();
        _redo.Add(entry);
        CurrentStateId = entry.BeforeStateId;

        // Prevent the next edit from merging into a command that is no longer on top.
        _lastPushAt = null;

        RaiseChanged();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        HistoryEntry entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        entry.Command.Execute();
        _undo.Add(entry);
        CurrentStateId = entry.AfterStateId;
        _lastPushAt = null;

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Groups every command pushed until the returned scope is disposed into one
    /// undo step.
    /// </summary>
    /// <remarks>
    /// Used for operations that are one action to the user but several to the model,
    /// such as deleting a multi-selection or pasting several images at once.
    /// </remarks>
    public IDisposable BeginBatch(string description)
    {
        if (_batchDepth == 0)
        {
            _batch = [];
            _batchDescription = description ?? string.Empty;
        }

        _batchDepth++;
        return new BatchScope(this);
    }

    private void EndBatch()
    {
        _batchDepth--;
        if (_batchDepth > 0)
        {
            return;
        }

        List<IUndoableCommand>? batch = _batch;
        _batch = null;

        if (batch is null || batch.Count == 0)
        {
            return;
        }

        if (batch.Count == 1)
        {
            Push(batch[0]);
            return;
        }

        Push(new CompositeCommand(_batchDescription, batch));
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0)
        {
            return;
        }

        _undo.Clear();
        _redo.Clear();
        _lastPushAt = null;
        RaiseChanged();
    }

    /// <summary>
    /// Reverses every recorded step, oldest last.
    /// </summary>
    /// <remarks>
    /// Backs the "remove all edits" command. Implemented by replaying undo rather
    /// than by clearing the document so that the result is itself reachable by redo.
    /// </remarks>
    public void UndoAll()
    {
        while (_undo.Count > 0)
        {
            Undo();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private readonly record struct HistoryEntry(IUndoableCommand Command, long BeforeStateId, long AfterStateId);

    private sealed class BatchScope(UndoStack owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.EndBatch();
        }
    }
}
