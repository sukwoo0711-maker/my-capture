using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// Dragging a row of the PDF order list, the way Windows reorders a list: past the system drag distance the
/// row lifts and follows the pointer, its place stays open, the rows it passes slide aside to open the place
/// it would land in, and on release it settles there. Esc puts it back. While the row moves only render
/// transforms change; the list itself is reordered once, on release.
/// </summary>
internal sealed partial class GalleryContactSheetDialog
{
    /// <summary>The band at the top and bottom of the list in which a dragged row scrolls the list.</summary>
    internal const double AutoScrollBand = 20;

    internal static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(50);

    // The row a slide was last set for; a container recycled for another row starts in place instead of sliding.
    private static readonly DependencyProperty ShiftedRowProperty =
        DependencyProperty.RegisterAttached("ShiftedRow", typeof(object), typeof(GalleryContactSheetDialog));

    private GalleryContactSheetOrderRow? _pressRow;
    private Point _pressPoint;
    private RowDrag? _drag;
    private DispatcherTimer? _autoScroll;
    private bool _releasingCapture;

    private sealed class RowDrag(GalleryContactSheetOrderRow row, int from, double slot, double grab, Panel host, ScrollViewer? scroller)
    {
        internal GalleryContactSheetOrderRow Row { get; } = row;
        internal int From { get; } = from;
        internal int Target { get; set; } = from;
        internal double Slot { get; } = slot;
        // Where in the row the pointer took hold of it.
        internal double Grab { get; } = grab;
        internal Panel Host { get; } = host;
        internal ScrollViewer? Scroller { get; } = scroller;
        internal Point Pointer { get; set; }
        internal double Top { get; set; }
        internal GalleryContactSheetDragAdorner? Lifted { get; set; }
        internal AdornerLayer? Layer { get; set; }
        // The shown rows' places as laid out, refreshed only when the list scrolls: position, top, height.
        internal List<(int Position, double Top, double Height)> Slots { get; } = new(32);
    }

    internal bool IsDraggingRow => _drag is not null;

    /// <summary>The place the dragged row would drop into, or -1 when no row is dragged.</summary>
    internal int DragTarget => _drag?.Target ?? -1;

    internal GalleryContactSheetDragAdorner? LiftedRow => _drag?.Lifted;

    internal bool AutoScrolling => _autoScroll?.IsEnabled == true;

    /// <summary>Whether a press has moved far enough to become a drag (the Windows drag distance).</summary>
    internal static bool PassesDragThreshold(Vector moved) =>
        Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>How far the row at <paramref name="position"/> slides while the row from <paramref name="from"/> hovers at <paramref name="target"/>.</summary>
    internal static double ShiftFor(int position, int from, int target, double slot) =>
        from < target && position > from && position <= target ? -slot
        : target < from && position >= target && position < from ? slot
        : 0;

    /// <summary>-1 to scroll up, 1 to scroll down, 0 to stay, for a pointer at <paramref name="y"/> in a list <paramref name="height"/> tall.</summary>
    internal static int AutoScrollDirection(double y, double height) =>
        y < AutoScrollBand ? -1 : y > height - AutoScrollBand ? 1 : 0;

    /// <summary>The slide the row's container has (or is sliding to).</summary>
    internal double RowShift(GalleryContactSheetOrderRow row) =>
        OrderList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem { RenderTransform: TranslateTransform shift } ? shift.Y : 0;

    private void OnOrderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressRow = null;
        if (_exportCancellation is not null || _drag is not null || e.OriginalSource is not DependencyObject source) return;
        // The include box keeps its own click; only the rest of the row picks the row up.
        for (DependencyObject? node = source; node is not null && node != OrderList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ToggleButton) return;
            if (node is ListBoxItem { DataContext: GalleryContactSheetOrderRow row })
            {
                _pressRow = row;
                _pressPoint = e.GetPosition(OrderList);
                return;
            }
        }
    }

    private void OnOrderMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(OrderList);
        if (_drag is null)
        {
            if (_pressRow is null || !PassesDragThreshold(point - _pressPoint)) return;
            GalleryContactSheetOrderRow row = _pressRow;
            _pressRow = null;
            if (!BeginRowDrag(row, _pressPoint)) return;
            OrderList.CaptureMouse();
        }
        UpdateRowDrag(point);
        e.Handled = true;
    }

    private void OnOrderMouseUp(object sender, MouseButtonEventArgs e)
    {
        _pressRow = null;
        if (_drag is not null) EndRowDrag(commit: true);
    }

    private void OnOrderLostCapture(object sender, MouseEventArgs e)
    {
        // Another window took the mouse (Alt+Tab, a dialog): the row goes back.
        if (!_releasingCapture && _drag is not null) EndRowDrag(commit: false);
    }

    /// <summary>Lifts <paramref name="row"/>, pressed at <paramref name="press"/> (list coordinates).</summary>
    internal bool BeginRowDrag(GalleryContactSheetOrderRow row, Point press)
    {
        if (_drag is not null || _exportCancellation is not null) return false;
        int from = _rows.IndexOf(row);
        if (from < 0 || OrderList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem { IsVisible: true } container
            || FindItemsHost(OrderList) is not { } host)
            return false;
        // A row still settling from the last drop lands now, so the picture and the places are final.
        foreach (ListBoxItem shown in ShownContainers(host))
        {
            Slide(shown, 0, animate: false);
            shown.ClearValue(OpacityProperty);
            shown.ClearValue(ShiftedRowProperty);
        }
        OrderList.UpdateLayout();
        double top = LayoutTop(container);
        var drag = new RowDrag(row, from, container.ActualHeight, press.Y - top, host, FindScrollViewer(OrderList))
        {
            Pointer = press, Top = top,
        };
        if (AdornerLayer.GetAdornerLayer(OrderList) is { } layer)
        {
            Point left = container.TranslatePoint(new Point(), OrderList);
            drag.Lifted = new GalleryContactSheetDragAdorner(OrderList, row,
                new Rect(left.X, top, container.ActualWidth, container.ActualHeight));
            drag.Layer = layer;
            layer.Add(drag.Lifted);
            drag.Lifted.Lift();
        }
        _drag = drag;
        RefreshSlots(drag);
        ApplyShifts(drag, animate: false);
        if (drag.Scroller is not null) drag.Scroller.ScrollChanged += OnDragScrollChanged;
        return true;
    }

    /// <summary>Moves the lifted row with a pointer at <paramref name="point"/> (list coordinates).</summary>
    internal void UpdateRowDrag(Point point)
    {
        if (_drag is not { } drag) return;
        drag.Pointer = point;
        MoveLifted(drag);
        if (AutoScrollDirection(point.Y, OrderList.ActualHeight) == 0) _autoScroll?.Stop();
        else
        {
            _autoScroll ??= new DispatcherTimer(AutoScrollInterval, DispatcherPriority.Input, (_, _) => AutoScrollStep(), Dispatcher);
            if (!_autoScroll.IsEnabled) _autoScroll.Start();
        }
    }

    /// <summary>One step of the scroll a row held near the top or bottom of the list keeps up.</summary>
    /// <returns>Whether the list scrolled.</returns>
    internal bool AutoScrollStep()
    {
        if (_drag is not { Scroller: { } scroller } drag) { _autoScroll?.Stop(); return false; }
        int direction = AutoScrollDirection(drag.Pointer.Y, OrderList.ActualHeight);
        if (direction == 0) { _autoScroll?.Stop(); return false; }
        double before = scroller.VerticalOffset;
        if (direction < 0) scroller.LineUp(); else scroller.LineDown();
        OrderList.UpdateLayout();
        // Rows came into view or left it: take their places again and keep the pointer's row under it.
        RefreshSlots(drag);
        ApplyShifts(drag, animate: true);
        MoveLifted(drag);
        return scroller.VerticalOffset != before;
    }

    private void OnDragScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_drag is not { } drag || e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) return;
        RefreshSlots(drag);
        ApplyShifts(drag, animate: true);
        MoveLifted(drag);
    }

    private void MoveLifted(RowDrag drag)
    {
        // The lifted row stays over the list: half of it may pass either edge, which is where the list scrolls.
        double low = -drag.Slot / 2, high = Math.Max(low, OrderList.ActualHeight - drag.Slot / 2);
        drag.Top = Math.Clamp(drag.Pointer.Y - drag.Grab, low, high);
        if (drag.Lifted is not null) drag.Lifted.Top = drag.Top;
        int target = SlotAt(drag, drag.Top + drag.Slot / 2);
        if (target < 0 || target == drag.Target) return;
        int previous = drag.Target;
        drag.Target = target;
        ApplyShifts(drag, animate: true);
        RenumberDrag(drag, previous);
    }

    /// <summary>The place whose laid-out slot holds <paramref name="y"/>, or the nearest shown place.</summary>
    private static int SlotAt(RowDrag drag, double y)
    {
        int nearest = -1;
        double nearestDistance = double.PositiveInfinity;
        foreach ((int position, double top, double height) in drag.Slots)
        {
            if (y >= top && y < top + height) return position;
            double distance = y < top ? top - y : y - (top + height);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = position; }
        }
        return nearest;
    }

    private void RefreshSlots(RowDrag drag)
    {
        drag.Slots.Clear();
        foreach (ListBoxItem container in ShownContainers(drag.Host))
        {
            int position = OrderList.ItemContainerGenerator.IndexFromContainer(container);
            if (position >= 0 && container.IsVisible) drag.Slots.Add((position, LayoutTop(container), container.ActualHeight));
        }
    }

    /// <summary>Slides every shown row to where it goes while the dragged row hovers at its target.</summary>
    private void ApplyShifts(RowDrag drag, bool animate)
    {
        foreach (ListBoxItem container in ShownContainers(drag.Host))
        {
            object? row = container.DataContext;
            if (ReferenceEquals(row, drag.Row))
            {
                // Its place stays open; the lifted copy is what moves.
                container.Opacity = 0;
                Slide(container, 0, animate: false);
                container.SetValue(ShiftedRowProperty, row);
                continue;
            }
            container.ClearValue(OpacityProperty);
            bool sameRow = ReferenceEquals(container.GetValue(ShiftedRowProperty), row);
            container.SetValue(ShiftedRowProperty, row);
            int position = OrderList.ItemContainerGenerator.IndexFromContainer(container);
            Slide(container, position < 0 ? 0 : ShiftFor(position, drag.From, drag.Target, drag.Slot), animate && sameRow);
        }
    }

    /// <summary>Numbers the rows between the old and new targets as they would be after the drop.</summary>
    private void RenumberDrag(RowDrag drag, int previous)
    {
        int low = Math.Min(drag.From, Math.Min(previous, drag.Target));
        int high = Math.Max(drag.From, Math.Max(previous, drag.Target));
        int number = 0;
        for (int position = 0; position < low; position++)
            if (_rows[position].IsIncluded) number++;
        for (int position = low; position <= high; position++)
        {
            GalleryContactSheetOrderRow row = RowAt(position, drag.From, drag.Target, drag.Row);
            row.SetPlace(position + 1, row.IsIncluded ? ++number : 0);
        }
    }

    /// <summary>The row at <paramref name="position"/> once the row from <paramref name="from"/> is placed at <paramref name="target"/>.</summary>
    private GalleryContactSheetOrderRow RowAt(int position, int from, int target, GalleryContactSheetOrderRow moved)
    {
        if (position == target) return moved;
        if (from < target && position >= from && position < target) return _rows[position + 1];
        if (target < from && position > target && position <= from) return _rows[position - 1];
        return _rows[position];
    }

    /// <summary>Drops the dragged row at its target, or with <paramref name="commit"/> false puts it back.</summary>
    internal void EndRowDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        double droppedTop = drag.Top;
        DropDragState();
        int target = commit ? drag.Target : drag.From;
        foreach (ListBoxItem container in ShownContainers(drag.Host))
        {
            container.ClearValue(OpacityProperty);
            container.ClearValue(ShiftedRowProperty);
            if (ReferenceEquals(container.DataContext, drag.Row)) continue;
            // A drop reorders the list to what the slid rows already show; put back, they slide home.
            Slide(container, 0, animate: !commit);
        }
        if (target == drag.From || !MoveRow(drag.From, target)) Renumber();
        OrderList.UpdateLayout();
        // The row settles from where it was let go into its place.
        if (OrderList.ItemContainerGenerator.ContainerFromItem(drag.Row) is ListBoxItem { IsVisible: true } settled)
        {
            Slide(settled, droppedTop - LayoutTop(settled), animate: false);
            Slide(settled, 0, animate: true);
        }
    }

    /// <summary>Ends the gesture's own state: the lifted copy, the mouse capture, the scroll timer.</summary>
    private void DropDragState()
    {
        _pressRow = null;
        _autoScroll?.Stop();
        if (_drag is not { } drag) return;
        _drag = null;
        if (drag.Scroller is not null) drag.Scroller.ScrollChanged -= OnDragScrollChanged;
        if (drag.Lifted is not null) drag.Layer?.Remove(drag.Lifted);
        if (OrderList.IsMouseCaptured)
        {
            _releasingCapture = true;
            try { OrderList.ReleaseMouseCapture(); }
            finally { _releasingCapture = false; }
        }
    }

    /// <summary>Slides a row container to <paramref name="y"/> over the Normal token, or puts it there.</summary>
    private static void Slide(ListBoxItem container, double y, bool animate)
    {
        if (container.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            if (y == 0 && !animate) return;
            shift = new TranslateTransform();
            container.RenderTransform = shift;
        }
        FluidMotion.Animate(shift, TranslateTransform.YProperty, y, animate, FluidMotion.NormalDuration);
    }

    /// <summary>The top of a container's laid-out slot in list coordinates, leaving out its slide.</summary>
    private double LayoutTop(ListBoxItem container) =>
        container.TranslatePoint(new Point(), OrderList).Y - ((container.RenderTransform as TranslateTransform)?.Y ?? 0);

    private static IEnumerable<ListBoxItem> ShownContainers(Panel host)
    {
        UIElementCollection children = host.Children;
        for (int index = 0; index < children.Count; index++)
            if (children[index] is ListBoxItem container) yield return container;
    }

    private static Panel? FindItemsHost(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is Panel { IsItemsHost: true } host) return host;
            if (FindItemsHost(child) is { } nested) return nested;
        }
        return null;
    }
}
