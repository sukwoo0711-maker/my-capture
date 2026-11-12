namespace MyCapture.Core.Export;

/// <summary>
/// The order and inclusion of the images in one contact-sheet export. Items are the caller's
/// indexes, and the initial order is the caller's order: an export whose order nobody touched
/// writes exactly the pages it wrote before ordering existed.
/// </summary>
public sealed class ContactSheetOrder
{
    private readonly List<int> _order;
    private readonly bool[] _excluded;

    public ContactSheetOrder(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _order = [.. Enumerable.Range(0, count)];
        _excluded = new bool[count];
    }

    public int Count => _excluded.Length;

    /// <summary>The item at each position, all items included or not.</summary>
    public IReadOnlyList<int> Items => _order.AsReadOnly();

    public bool IsOriginalOrder => _order.Select((item, position) => item == position).All(same => same);

    public int PositionOf(int item) => _order.IndexOf(item);

    /// <summary>Moves the item at <paramref name="from"/> so that it ends at <paramref name="to"/>.</summary>
    /// <returns><see langword="false"/> when either position is outside the list or nothing moves.</returns>
    public bool Move(int from, int to)
    {
        if ((uint)from >= (uint)Count || (uint)to >= (uint)Count || from == to) return false;
        int item = _order[from];
        _order.RemoveAt(from);
        _order.Insert(to, item);
        return true;
    }

    public bool MoveUp(int position) => Move(position, position - 1);

    public bool MoveDown(int position) => Move(position, position + 1);

    /// <summary>
    /// Sorts every item by <paramref name="key"/>. Equal keys keep the caller's original order in
    /// both directions, so the same sort always gives the same pages.
    /// </summary>
    public void SortBy<TKey>(Func<int, TKey> key, IComparer<TKey>? comparer = null, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        IEnumerable<int> items = Enumerable.Range(0, Count);
        int[] sorted = (descending ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer))
            .ThenBy(item => item).ToArray();
        _order.Clear();
        _order.AddRange(sorted);
    }

    /// <summary>Restores the caller's order; inclusion is left as it is.</summary>
    public void Reset()
    {
        _order.Clear();
        _order.AddRange(Enumerable.Range(0, Count));
    }

    public bool IsIncluded(int item) => !_excluded[item];

    public void SetIncluded(int item, bool included) => _excluded[item] = !included;

    public int IncludedCount => _excluded.Count(excluded => !excluded);

    /// <summary>The items to export, in page order.</summary>
    public IReadOnlyList<int> IncludedItems() => _order.Where(item => !_excluded[item]).ToArray();
}
