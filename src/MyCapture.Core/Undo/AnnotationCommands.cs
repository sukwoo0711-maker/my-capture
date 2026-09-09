using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Undo;

/// <summary>
/// Runs several commands as one step.
/// </summary>
public sealed class CompositeCommand : IUndoableCommand
{
    private readonly List<IUndoableCommand> _commands;

    public CompositeCommand(string description, IEnumerable<IUndoableCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        _commands = [.. commands];
        Description = string.IsNullOrWhiteSpace(description) ? UiText.Get("Text_728F6813B235") : description;
    }

    public string Description { get; }

    public IEnumerable<string> ReferencedImageAssets => _commands.SelectMany(command => command.ReferencedImageAssets);

    public void Execute()
    {
        // Forward order: later commands may depend on earlier ones having run.
        foreach (IUndoableCommand c in _commands)
        {
            c.Execute();
        }
    }

    public void Undo()
    {
        // Reverse order, mirroring how nested state must be unwound.
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            _commands[i].Undo();
        }
    }
}

/// <summary>
/// Adds an annotation to a document.
/// </summary>
public sealed class AddAnnotationCommand : IUndoableCommand
{
    private readonly AnnotationDocument _document;
    private readonly AnnotationItem _item;
    private int _index = -1;

    public AddAnnotationCommand(AnnotationDocument document, AnnotationItem item)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _item = item ?? throw new ArgumentNullException(nameof(item));
    }

    public string Description => UiText.Format("Text_47E781C43156", _item.DisplayName);

    public IEnumerable<string> ReferencedImageAssets => AnnotationCommandAssets.For(_item);

    public void Execute()
    {
        if (_index < 0)
        {
            _document.Add(_item);
            _index = _document.IndexOf(_item);
        }
        else
        {
            // Redo path: restore the original paint position rather than appending,
            // so redo is a true inverse of undo.
            _document.Insert(_index, _item);
        }
    }

    public void Undo() => _document.Remove(_item);
}

/// <summary>
/// Removes an annotation, remembering its paint position.
/// </summary>
public sealed class RemoveAnnotationCommand : IUndoableCommand
{
    private readonly AnnotationDocument _document;
    private readonly AnnotationItem _item;
    private int _index;

    public RemoveAnnotationCommand(AnnotationDocument document, AnnotationItem item)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _index = _document.IndexOf(item);
    }

    public string Description => UiText.Format("Text_8F8C3D476B1D", _item.DisplayName);

    public IEnumerable<string> ReferencedImageAssets => AnnotationCommandAssets.For(_item);

    public void Execute()
    {
        _index = _document.IndexOf(_item);
        _document.Remove(_item);
    }

    public void Undo() => _document.Insert(_index < 0 ? _document.Items.Count : _index, _item);
}

/// <summary>
/// Moves or resizes an annotation.
/// </summary>
/// <remarks>
/// Records the bounds before and after the whole gesture. Consecutive moves of the
/// same item collapse, so one drag is one undo step no matter how many mouse-move
/// events it produced.
/// </remarks>
public sealed class TransformAnnotationCommand : IUndoableCommand
{
    public IEnumerable<string> ReferencedImageAssets => AnnotationCommandAssets.For(_item);
    private readonly AnnotationItem _item;
    private readonly RectD _before;
    private RectD _after;

    public TransformAnnotationCommand(AnnotationItem item, RectD before, RectD after)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _before = before;
        _after = after;
    }

    public string Description => UiText.Format("Text_DDB4DA1FC0AE", _item.DisplayName);

    public void Execute() => _item.SetBounds(_after);

    public void Undo() => _item.SetBounds(_before);

    public bool TryMergeWith(IUndoableCommand next)
    {
        if (next is not TransformAnnotationCommand other || !ReferenceEquals(other._item, _item))
        {
            return false;
        }

        // Keep this command's "before" and adopt the newer "after": the pair still
        // describes exactly one reversible transition.
        _after = other._after;
        return true;
    }
}

/// <summary>
/// Changes one property of one annotation.
/// </summary>
/// <typeparam name="TItem">The annotation type.</typeparam>
/// <typeparam name="TValue">The property type.</typeparam>
/// <remarks>
/// Generic over an accessor pair rather than using reflection so that a renamed
/// property becomes a compile error instead of a silently dead undo entry.
/// </remarks>
public sealed class PropertyChangeCommand<TItem, TValue> : IUndoableCommand
    where TItem : AnnotationItem
{
    public IEnumerable<string> ReferencedImageAssets
    {
        get
        {
            foreach (string name in AnnotationCommandAssets.For(_item)) yield return name;
            // A string property may be the image asset name. Conservatively retain both
            // values, even when another string property is edited, rather than drop undo pixels.
            if (_item is ImageAnnotation)
            {
                if (_before is string before) yield return before;
                if (_after is string after) yield return after;
            }
        }
    }
    private readonly TItem _item;
    private readonly Action<TItem, TValue> _setter;
    private readonly TValue _before;
    private readonly string _propertyLabel;
    private TValue _after;

    public PropertyChangeCommand(
        TItem item,
        string propertyLabel,
        Action<TItem, TValue> setter,
        TValue before,
        TValue after)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        _propertyLabel = propertyLabel ?? string.Empty;
        _before = before;
        _after = after;
    }

    public string Description => UiText.Format("Text_97B224926F71", _item.DisplayName, _propertyLabel);

    public void Execute() => _setter(_item, _after);

    public void Undo() => _setter(_item, _before);

    public bool TryMergeWith(IUndoableCommand next)
    {
        if (next is not PropertyChangeCommand<TItem, TValue> other ||
            !ReferenceEquals(other._item, _item) ||
            !string.Equals(other._propertyLabel, _propertyLabel, StringComparison.Ordinal))
        {
            return false;
        }

        _after = other._after;
        return true;
    }
}

/// <summary>
/// Changes paint order.
/// </summary>
public sealed class ReorderAnnotationCommand : IUndoableCommand
{
    public IEnumerable<string> ReferencedImageAssets => AnnotationCommandAssets.For(_item);
    private readonly AnnotationDocument _document;
    private readonly AnnotationItem _item;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public ReorderAnnotationCommand(AnnotationDocument document, AnnotationItem item, int toIndex)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _fromIndex = _document.IndexOf(item);
        _toIndex = Math.Clamp(toIndex, 0, Math.Max(0, _document.Items.Count - 1));
    }

    public string Description => UiText.Format("Text_67781174A42E", _item.DisplayName);

    public void Execute() => Move(_fromIndex, _toIndex);

    public void Undo() => Move(_toIndex, _fromIndex);

    private void Move(int from, int to)
    {
        if (from < 0 || from >= _document.Items.Count)
        {
            return;
        }

        int target = Math.Clamp(to, 0, _document.Items.Count - 1);
        if (from != target)
        {
            _document.Items.Move(from, target);
        }
    }
}

/// <summary>
/// Replaces the point list of a polyline or pen stroke.
/// </summary>
/// <remarks>
/// Used when a vertex is dragged and when a committed pen stroke is simplified, so
/// that simplification is itself undoable rather than a silent mutation of the user's
/// stroke.
/// </remarks>
public sealed class ReplacePointsCommand : IUndoableCommand
{
    public IEnumerable<string> ReferencedImageAssets => AnnotationCommandAssets.For(_item);
    private readonly AnnotationItem _item;
    private readonly List<PointD> _before;
    private List<PointD> _after;

    public ReplacePointsCommand(AnnotationItem item, IReadOnlyList<PointD> before, IReadOnlyList<PointD> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        _item = item ?? throw new ArgumentNullException(nameof(item));
        _before = [.. before];
        _after = [.. after];
    }

    public string Description => UiText.Format("Text_F6249F8FCEAE", _item.DisplayName);

    public void Execute() => Apply(_after);

    public void Undo() => Apply(_before);

    public bool TryMergeWith(IUndoableCommand next)
    {
        if (next is not ReplacePointsCommand other || !ReferenceEquals(other._item, _item))
        {
            return false;
        }

        _after = other._after;
        return true;
    }

    private void Apply(List<PointD> points)
    {
        switch (_item)
        {
            case PolylineAnnotation polyline:
                polyline.Points = [.. points];
                break;
            case PenAnnotation pen:
                pen.Points = [.. points];
                break;
            default:
                throw new InvalidOperationException(
                    $"{_item.GetType().Name} does not carry an editable point list.");
        }
    }
}

internal static class AnnotationCommandAssets
{
    internal static IEnumerable<string> For(AnnotationItem item) =>
        item is ImageAnnotation image ? [image.AssetFileName] : [];
}
