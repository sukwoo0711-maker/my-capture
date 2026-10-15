using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Editing;

/// <summary>Keeps the commit actions at the right edge and wraps only when both groups cannot fit.</summary>
internal sealed class CommandGroupsPanel : Panel
{
    internal const double GroupGap = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (InternalChildren.Count != 2) return default;
        Size left = InternalChildren[0].DesiredSize, right = InternalChildren[1].DesiredSize;
        bool wrap = left.Width + GroupGap + right.Width > availableSize.Width;
        return new Size(Math.Min(availableSize.Width, left.Width + GroupGap + right.Width),
            wrap ? left.Height + GroupGap + right.Height : Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        Size left = InternalChildren[0].DesiredSize, right = InternalChildren[1].DesiredSize;
        bool wrap = left.Width + GroupGap + right.Width > finalSize.Width;
        double rowHeight = Math.Max(left.Height, right.Height);
        InternalChildren[0].Arrange(new Rect(0, wrap ? 0 : (rowHeight - left.Height) / 2, left.Width, left.Height));
        InternalChildren[1].Arrange(new Rect(Math.Max(0, finalSize.Width - right.Width),
            wrap ? left.Height + GroupGap : (rowHeight - right.Height) / 2, right.Width, right.Height));
        return finalSize;
    }
}
