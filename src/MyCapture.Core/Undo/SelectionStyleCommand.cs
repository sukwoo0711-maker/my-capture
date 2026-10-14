using MyCapture.Core.Annotations;

namespace MyCapture.Core.Undo;

/// <summary>Coalesces continuous inspector changes for the same selection and property.</summary>
public sealed class SelectionStyleCommand : IUndoableCommand
{
    private readonly HashSet<AnnotationItem> _selection;
    private readonly List<IUndoableCommand> _changes;

    public SelectionStyleCommand(string description, IEnumerable<AnnotationItem> selection, IEnumerable<IUndoableCommand> changes)
    {
        Description = description;
        _selection = selection.ToHashSet();
        _changes = changes.ToList();
    }

    public string Description { get; }
    public IEnumerable<string> ReferencedImageAssets => _changes.SelectMany(change => change.ReferencedImageAssets);
    public void Execute()
    {
        foreach (IUndoableCommand change in _changes) change.Execute();
    }

    public void Undo()
    {
        for (int i = _changes.Count - 1; i >= 0; i--) _changes[i].Undo();
    }

    public bool TryMergeWith(IUndoableCommand next)
    {
        if (next is not SelectionStyleCommand other || Description != other.Description
            || !_selection.SetEquals(other._selection)) return false;
        foreach (IUndoableCommand change in other._changes)
        {
            if (!_changes.Any(previous => previous.TryMergeWith(change))) _changes.Add(change);
        }
        return true;
    }
}
