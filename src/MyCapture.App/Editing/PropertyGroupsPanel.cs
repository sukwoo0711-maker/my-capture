using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MyCapture.App.Editing;

/// <summary>Wrapping property groups, with rules only in the gaps within a row.</summary>
internal sealed class PropertyGroupsPanel : WrapPanel
{
    private static readonly DependencyProperty RuleBrushProperty = DependencyProperty.Register(
        "RuleBrush", typeof(Brush), typeof(PropertyGroupsPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private readonly List<Rect> _dividers = [];
    internal IReadOnlyList<Rect> DividerBounds => _dividers;

    internal PropertyGroupsPanel() => SetResourceReference(RuleBrushProperty, "Border.Subtle");

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size result = base.ArrangeOverride(finalSize);
        _dividers.Clear();
        double? previousRow = null;
        foreach (FrameworkElement group in Children.OfType<FrameworkElement>())
        {
            if (group.Visibility != Visibility.Visible || group.ActualWidth <= 0) continue;
            Rect slot = LayoutInformation.GetLayoutSlot(group);
            Point point = group.TranslatePoint(default, this);
            if (previousRow is double row && Math.Abs(row - slot.Top) < 0.5)
                _dividers.Add(new Rect(point.X - 6.5, point.Y + Math.Max(0, (group.ActualHeight - 20) / 2), 1, 20));
            previousRow = slot.Top;
        }
        InvalidateVisual();
        return result;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (GetValue(RuleBrushProperty) is not Brush brush) return;
        foreach (Rect divider in _dividers) drawingContext.DrawRectangle(brush, null, divider);
    }
}
