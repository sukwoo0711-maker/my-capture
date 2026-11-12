using System.Runtime.InteropServices;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ElementGeometryTraversalTests
{
    private static readonly RectD Root = new(0, 0, 500, 400), Container = new(20, 20, 400, 300);
    private sealed class Node(RectD bounds)
    {
        internal readonly RectD Bounds = bounds;
        internal Node? Child, Next;
        internal Exception? Error;
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("depth")]
    [InlineData("budget")]
    [InlineData("timeout")]
    public void LimitsReturnBestKnownChainAsPartialSuccess(string limit)
    {
        var root = new Node(Root); var container = root.Child = new Node(Container);
        Node previous = container;
        for (int i = 0; i < 2200; i++)
        {
            var next = new Node(Container.Inflate(-10));
            if (limit == "depth" || i == 0) previous.Child = next; else previous.Next = next;
            previous = next;
        }
        int reads = 0;
        var result = ElementGeometrySource.Walk(root, new PointD(50, 50), n => { reads++; return new(n.Bounds, 50026); },
            n => limit == "timeout" && n == container ? throw new TimeoutException() : n.Child,
            n => n.Next, () => TimeSpan.FromMilliseconds(limit == "budget" && reads >= 2 ? 600 : 0), default);
        Assert.Equal(ElementGeometryStatus.Success, result.Status); Assert.True(result.Partial);
        Assert.Contains(result.Chain, e => e.Bounds == Container);
        if (limit == "nodes") Assert.Equal(2048, reads);
        if (limit == "depth") Assert.Equal(65, reads);
        if (limit == "budget") Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableSubtreeDoesNotDiscardUsableSibling(bool accessDenied)
    {
        var root = new Node(Root);
        root.Child = new Node(Container) { Error = accessDenied ? new UnauthorizedAccessException() : new COMException("gone", unchecked((int)0x80040201)) };
        root.Child.Next = new Node(Container.Inflate(-20));
        var result = ElementGeometrySource.Walk(root, new PointD(50, 50), n => n.Error is { } e ? throw e : new(n.Bounds, 50026),
            n => n.Child, n => n.Next, () => TimeSpan.Zero, default);
        Assert.Equal(ElementGeometryStatus.Success, result.Status); Assert.True(result.Partial);
        Assert.Equal(Container.Inflate(-20), result.Chain[^1].Bounds);
    }
}
