using MyCapture.Core.Annotations;

namespace MyCapture.Core.Undo;

/// <summary>Translates a selection as one step without scaling its point geometry.</summary>
public sealed class TranslateAnnotationsCommand : IUndoableCommand
{
    private readonly AnnotationItem[] _items;
    private readonly double _dx;
    private readonly double _dy;

    public TranslateAnnotationsCommand(IEnumerable<AnnotationItem> items, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.Distinct().ToArray();
        _dx = dx;
        _dy = dy;
    }

    public string Description => UiText.Get("EditorSelection_Move");
    public IEnumerable<string> ReferencedImageAssets => _items.SelectMany(AnnotationCommandAssets.For);
    public void Execute() => Translate(_dx, _dy);
    public void Undo() => Translate(-_dx, -_dy);

    private void Translate(double dx, double dy)
    {
        foreach (AnnotationItem item in _items) item.Translate(dx, dy);
    }
}
