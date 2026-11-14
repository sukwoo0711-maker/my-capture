using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// The lifted copy of an order row that follows the pointer while the row is dragged. It is a row of its
/// own, bound to the same data, so its number changes with the places it passes, and the list's
/// containers can be recycled while the list scrolls without taking the lifted row with them.
/// </summary>
internal sealed class GalleryContactSheetDragAdorner : Adorner
{
    /// <summary>How much larger the lifted row is drawn than the row in the list.</summary>
    internal const double LiftScale = 1.02;

    private readonly LiftedRow _row;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _shift = new();
    private readonly Rect _bounds;

    internal GalleryContactSheetDragAdorner(ListBox list, GalleryContactSheetOrderRow row, Rect bounds) : base(list)
    {
        _bounds = bounds;
        // Drawn as the chosen row; it is a picture for the eye, so it stays out of input and automation.
        _row = new LiftedRow
        {
            Content = row, ContentTemplate = list.ItemTemplate, Style = list.ItemContainerStyle,
            HorizontalContentAlignment = list.HorizontalContentAlignment,
            Width = bounds.Width, Height = bounds.Height, IsSelected = true, Focusable = false, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _scale, _shift } },
        };
        IsHitTestVisible = false;
        AddVisualChild(_row);
    }

    /// <summary>The top of the lifted row in list coordinates.</summary>
    internal double Top
    {
        get => _bounds.Top + _shift.Y;
        set => _shift.Y = value - _bounds.Top;
    }

    internal double Scale => _scale.ScaleX;

    /// <summary>Whether the lift is playing as an animation rather than set at once.</summary>
    internal bool LiftAnimates => _scale.HasAnimatedProperties;

    /// <summary>Lifts the row over the Fast token; at once when Windows animation effects are off.</summary>
    internal void Lift()
    {
        FluidMotion.Animate(_scale, ScaleTransform.ScaleXProperty, LiftScale, animate: true, FluidMotion.FastDuration);
        FluidMotion.Animate(_scale, ScaleTransform.ScaleYProperty, LiftScale, animate: true, FluidMotion.FastDuration);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0 ? _row : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size constraint)
    {
        _row.Measure(_bounds.Size);
        return _bounds.Size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _row.Arrange(_bounds);
        return finalSize;
    }

    /// <summary>A row drawn for the eye only: automation keeps reading the real row, which keeps focus.</summary>
    private sealed class LiftedRow : ListBoxItem
    {
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
}
