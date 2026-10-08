using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Editing;

/// <summary>Keeps the commit actions at the right edge and wraps only when both groups cannot fit.</summary>
internal sealed class CommandGroupsPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (InternalChildren.Count != 2) return default;
        Size left = InternalChildren[0].DesiredSize, right = InternalChildren[1].DesiredSize;
        bool wrap = left.Width + right.Width > availableSize.Width;
        return new Size(Math.Min(availableSize.Width, left.Width + right.Width),
            wrap ? left.Height + right.Height : Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        Size left = InternalChildren[0].DesiredSize, right = InternalChildren[1].DesiredSize;
        bool wrap = left.Width + right.Width > finalSize.Width;
        InternalChildren[0].Arrange(new Rect(0, 0, left.Width, left.Height));
        InternalChildren[1].Arrange(new Rect(Math.Max(0, finalSize.Width - right.Width),
            wrap ? left.Height : 0, right.Width, right.Height));
        return finalSize;
    }
}
