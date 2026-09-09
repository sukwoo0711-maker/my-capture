namespace MyCapture.App.Gallery;

/// <summary>Selection follows the filtered display order, independently of realized rows.</summary>
public sealed class GallerySelection
{
    private readonly HashSet<Guid> _selected = [];
    private Guid? _anchor;
    private IReadOnlyList<Guid> _visible = [];
    public IReadOnlyList<Guid> SelectedIds => _visible.Where(_selected.Contains).ToArray();
    public Guid? Anchor => _anchor;
    public bool Contains(Guid id) => _selected.Contains(id);

    public void SetVisible(IEnumerable<Guid> ids)
    {
        _visible = ids.ToArray();
        _selected.IntersectWith(_visible);
        if (_anchor is Guid anchor && !_visible.Contains(anchor)) _anchor = null;
    }

    public void Select(Guid id, bool control = false, bool shift = false)
    {
        int end = IndexOf(id);
        if (end < 0) return;
        int start = _anchor is Guid anchor ? IndexOf(anchor) : -1;
        if (shift && start >= 0)
        {
            if (!control) _selected.Clear();
            for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) _selected.Add(_visible[i]);
            return;
        }
        if (!control) _selected.Clear();
        if (!control || !_selected.Remove(id)) _selected.Add(id);
        _anchor = id;
    }

    public void SelectGroup(IEnumerable<Guid> ids, bool control)
    {
        Guid[] group = ids.Where(_visible.Contains).Distinct().ToArray();
        if (group.Length == 0) return;
        bool remove = control && group.All(_selected.Contains);
        if (!control) _selected.Clear();
        foreach (Guid id in group) { if (remove) _selected.Remove(id); else _selected.Add(id); }
        _anchor = group[0];
    }

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < _visible.Count; i++) if (_visible[i] == id) return i;
        return -1;
    }
}
