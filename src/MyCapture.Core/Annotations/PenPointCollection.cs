using System.Collections.ObjectModel;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>A stroke's points, with notifications for every mutable collection operation.</summary>
public sealed class PenPointCollection : Collection<PointD>
{
    public PenPointCollection() { }

    public PenPointCollection(IEnumerable<PointD> points) : base(new List<PointD>(points)) { }

    // Copy caller-owned lists so mutations cannot bypass the collection's hooks.
    public static implicit operator PenPointCollection(List<PointD> points) => new(points);

    internal event Action? Changed;

    protected override void InsertItem(int index, PointD item)
    {
        base.InsertItem(index, item);
        Changed?.Invoke();
    }

    protected override void SetItem(int index, PointD item)
    {
        base.SetItem(index, item);
        Changed?.Invoke();
    }

    protected override void RemoveItem(int index)
    {
        base.RemoveItem(index);
        Changed?.Invoke();
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        Changed?.Invoke();
    }

    internal void Transform(Func<PointD, PointD> transform)
    {
        // A transform is one edit; avoid one property notification per sampled point.
        for (int i = 0; i < Count; i++) Items[i] = transform(Items[i]);
        Changed?.Invoke();
    }
}
