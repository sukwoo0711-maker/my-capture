using MyCapture.Core.Export;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ContactSheetOrderTests
{
    [Fact]
    public void UntouchedOrderIsTheCallersOrderWithEveryItemIncluded()
    {
        var order = new ContactSheetOrder(4);
        Assert.Equal([0, 1, 2, 3], order.Items);
        Assert.Equal([0, 1, 2, 3], order.IncludedItems());
        Assert.True(order.IsOriginalOrder);
        Assert.Equal(4, order.IncludedCount);
        Assert.Empty(new ContactSheetOrder(0).IncludedItems());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContactSheetOrder(-1));
    }

    [Fact]
    public void MoveTakesAnItemToItsTargetPositionAndRejectsBoundaries()
    {
        var order = new ContactSheetOrder(5);
        Assert.True(order.Move(0, 3));
        Assert.Equal([1, 2, 3, 0, 4], order.Items);
        Assert.True(order.Move(4, 0));
        Assert.Equal([4, 1, 2, 3, 0], order.Items);
        Assert.False(order.IsOriginalOrder);
        Assert.Equal(4, order.PositionOf(0));

        Assert.False(order.Move(2, 2));
        Assert.False(order.Move(-1, 0));
        Assert.False(order.Move(0, 5));
        Assert.False(order.MoveUp(0));
        Assert.False(order.MoveDown(4));
        Assert.Equal([4, 1, 2, 3, 0], order.Items);

        Assert.True(order.MoveUp(1));
        Assert.Equal([1, 4, 2, 3, 0], order.Items);
        Assert.True(order.MoveDown(3));
        Assert.Equal([1, 4, 2, 0, 3], order.Items);
    }

    [Fact]
    public void SortsKeepTheCallersOrderForEqualKeysInBothDirections()
    {
        int[] minutes = [30, 10, 30, 20];
        var order = new ContactSheetOrder(4);
        order.SortBy(item => minutes[item]);
        Assert.Equal([1, 3, 0, 2], order.Items);
        order.SortBy(item => minutes[item], descending: true);
        Assert.Equal([0, 2, 3, 1], order.Items);
        string[] titles = ["b", "A", "c", "a"];
        order.SortBy(item => titles[item], StringComparer.OrdinalIgnoreCase);
        Assert.Equal([1, 3, 0, 2], order.Items);
        order.Reset();
        Assert.True(order.IsOriginalOrder);
    }

    [Fact]
    public void ExcludedItemsKeepTheirPlaceButLeaveTheExport()
    {
        var order = new ContactSheetOrder(4);
        order.Move(3, 0);
        order.SetIncluded(1, false);
        Assert.False(order.IsIncluded(1));
        Assert.Equal([3, 0, 1, 2], order.Items);
        Assert.Equal([3, 0, 2], order.IncludedItems());
        Assert.Equal(3, order.IncludedCount);
        order.Reset();
        Assert.Equal([0, 2, 3], order.IncludedItems());
        order.SetIncluded(1, true);
        Assert.Equal([0, 1, 2, 3], order.IncludedItems());
    }
}
