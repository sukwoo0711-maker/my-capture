using System.Windows;
using System.Windows.Controls;

namespace MyCapture.App.Recording;

/// <summary>
/// Lays out the typed range and the kept-length summary. They share one row while
/// both fit; a narrower window puts the summary on a second row under the range, aligned to
/// the same left edge, instead of letting single controls wrap one by one.
/// </summary>
internal sealed class TrimBarPanel : Panel
{
    /// <summary>Space between the two groups when they share a row.</summary>
    internal const double GroupGap = 8;

    /// <summary>Space between the rows when the summary moves under the range.</summary>
    internal const double RowGap = 4;

    /// <summary>Whether the last layout put the summary on its own row.</summary>
    internal bool IsTwoRows { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (InternalChildren.Count != 2) return default;
        Size range = InternalChildren[0].DesiredSize, summary = InternalChildren[1].DesiredSize;
        IsTwoRows = range.Width + GroupGap + summary.Width > availableSize.Width;
        return IsTwoRows
            ? new Size(Math.Min(availableSize.Width, Math.Max(range.Width, summary.Width)), range.Height + RowGap + summary.Height)
            : new Size(range.Width + GroupGap + summary.Width, Math.Max(range.Height, summary.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        Size range = InternalChildren[0].DesiredSize, summary = InternalChildren[1].DesiredSize;
        IsTwoRows = range.Width + GroupGap + summary.Width > finalSize.Width + 0.5;
        if (IsTwoRows)
        {
            InternalChildren[0].Arrange(new Rect(0, 0, range.Width, range.Height));
            InternalChildren[1].Arrange(new Rect(0, range.Height + RowGap, summary.Width, summary.Height));
        }
        else
        {
            // Keep the summary at the right, vertically centred with the inputs.
            double height = Math.Max(range.Height, summary.Height);
            InternalChildren[0].Arrange(new Rect(0, (height - range.Height) / 2, range.Width, range.Height));
            InternalChildren[1].Arrange(new Rect(finalSize.Width - summary.Width, (height - summary.Height) / 2, summary.Width, summary.Height));
        }

        return finalSize;
    }
}
