using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MyCapture.App.Editing;

/// <summary>
/// Fits two command groups by hiding names, one ordered command at a time and then group by
/// group, before falling back to two left-aligned rows.
/// </summary>
internal sealed class CommandGroupsPanel : Panel
{
    internal const double GroupGap = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2) return default;
        // Each stage is chosen for its revealed width: named faces hold their caption, and
        // icon-only faces count the width they grow to while it shows. Alt and the persistent
        // hint preference therefore only use space the selected stage already holds.
        // A bounded measure pass chooses the presentation from desired widths. There are no
        // SizeChanged/LayoutUpdated handlers or ActualWidth feedback loops to schedule another pass.
        Compact(InternalChildren[0], false);
        Compact(InternalChildren[1], false);
        MeasureGroups();
        if (RevealedWidth > availableSize.Width)
        {
            // A bar that is only slightly short gives up only as many names as it must.
            foreach (ButtonBase button in OrderedCommands())
            {
                if (!Collapse(button)) continue;
                MeasureGroups();
                if (RevealedWidth <= availableSize.Width) break;
            }
        }
        if (RevealedWidth > availableSize.Width)
        {
            Compact(InternalChildren[0], true);
            MeasureGroups();
            if (RevealedWidth > availableSize.Width)
            {
                Compact(InternalChildren[1], true);
                MeasureGroups();
            }
        }
        Size left = InternalChildren[0].DesiredSize, right = InternalChildren[1].DesiredSize;
        bool wrap = RevealedWidth > availableSize.Width;
        return new Size(Math.Min(availableSize.Width, left.Width + GroupGap + right.Width),
            wrap ? left.Height + GroupGap + right.Height : Math.Max(left.Height, right.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        Size left = InternalChildren[0].DesiredSize, right = InternalChildren[1].DesiredSize;
        bool wrap = RevealedWidth > finalSize.Width;
        double rowHeight = Math.Max(left.Height, right.Height);
        InternalChildren[0].Arrange(new Rect(0, wrap ? 0 : (rowHeight - left.Height) / 2, left.Width, left.Height));
        InternalChildren[1].Arrange(new Rect(wrap ? 0 : Math.Max(0, finalSize.Width - right.Width),
            wrap ? left.Height + GroupGap : (rowHeight - right.Height) / 2, right.Width, right.Height));
        return finalSize;
    }

    private double CombinedWidth => InternalChildren[0].DesiredSize.Width + GroupGap + InternalChildren[1].DesiredSize.Width;

    private double RevealedWidth => CombinedWidth + CommandButtonPresentation.RevealGrowth(InternalChildren[0])
        + CommandButtonPresentation.RevealGrowth(InternalChildren[1]);

    private void MeasureGroups()
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    private IEnumerable<ButtonBase> OrderedCommands()
    {
        var commands = new List<ButtonBase>();
        Collect(InternalChildren[0]);
        Collect(InternalChildren[1]);
        // OrderBy is stable, so commands sharing an order keep their position in the bar.
        return commands.OrderBy(button => (int)button.GetValue(CommandButtonPresentation.CollapseOrderProperty));

        void Collect(UIElement element)
        {
            switch (element)
            {
                case ButtonBase button when (int)button.GetValue(CommandButtonPresentation.CollapseOrderProperty) > 0:
                    commands.Add(button);
                    break;
                case Panel panel:
                    foreach (UIElement child in panel.Children) Collect(child);
                    break;
                case Decorator { Child: { } child }:
                    Collect(child);
                    break;
            }
        }
    }

    private bool Collapse(ButtonBase button)
    {
        if (!CommandButtonPresentation.CompactLabel(button, true)) return false;
        // The groups measure again inside this pass, so every ancestor up to them must too.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(button);
             parent is UIElement element && !ReferenceEquals(parent, this);
             parent = VisualTreeHelper.GetParent(parent))
            element.InvalidateMeasure();
        return true;
    }

    private static bool Compact(UIElement element, bool compact)
    {
        bool changed = false;
        switch (element)
        {
            case ButtonBase button:
                changed = CommandButtonPresentation.CompactLabel(button, compact);
                break;
            case Panel panel:
                foreach (UIElement child in panel.Children) changed |= Compact(child, compact);
                break;
            case Decorator { Child: { } child }:
                changed = Compact(child, compact);
                break;
        }
        if (changed) element.InvalidateMeasure();
        return changed;
    }
}
