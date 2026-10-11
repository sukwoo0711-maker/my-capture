using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Recording;

/// <summary>
/// Lays out the trim bar's two groups: the actions with their result (start here, end here,
/// kept length) and the range (typed start and end, full length). They share one row while
/// both fit; a narrower window puts the range on a second row under the actions, aligned to
/// the same left edge, instead of letting single controls wrap one by one.
/// </summary>
internal sealed class TrimBarPanel : Panel
{
    /// <summary>Space between the two groups when they share a row.</summary>
    internal const double GroupGap = 8;

    /// <summary>Space between the rows when the range moves under the actions.</summary>
    internal const double RowGap = 4;

    /// <summary>Whether the last layout put the range on its own row.</summary>
    internal bool IsTwoRows { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (InternalChildren.Count != 2) return default;
        Size actions = InternalChildren[0].DesiredSize, range = InternalChildren[1].DesiredSize;
        IsTwoRows = actions.Width + GroupGap + range.Width > availableSize.Width;
        return IsTwoRows
            ? new Size(Math.Min(availableSize.Width, Math.Max(actions.Width, range.Width)), actions.Height + RowGap + range.Height)
            : new Size(actions.Width + GroupGap + range.Width, Math.Max(actions.Height, range.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        Size actions = InternalChildren[0].DesiredSize, range = InternalChildren[1].DesiredSize;
        IsTwoRows = actions.Width + GroupGap + range.Width > finalSize.Width + 0.5;
        if (IsTwoRows)
        {
            InternalChildren[0].Arrange(new Rect(0, 0, actions.Width, actions.Height));
            InternalChildren[1].Arrange(new Rect(0, actions.Height + RowGap, range.Width, range.Height));
        }
        else
        {
            // One row: both groups centre on the taller one, so the inputs line up with the buttons.
            double height = Math.Max(actions.Height, range.Height);
            InternalChildren[0].Arrange(new Rect(0, (height - actions.Height) / 2, actions.Width, actions.Height));
            InternalChildren[1].Arrange(new Rect(actions.Width + GroupGap, (height - range.Height) / 2, range.Width, range.Height));
        }

        return finalSize;
    }
}
