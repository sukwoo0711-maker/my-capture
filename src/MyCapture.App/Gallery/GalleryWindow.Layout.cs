using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    /// <summary>
    /// The width a tile row can fill: the list's scrolling area inside its chrome, less the
    /// vertical scrollbar. The scrollbar is always reserved, so a row never runs under it and
    /// showing or hiding it never changes the column count.
    /// </summary>
    private double RowsContentWidth(double listWidth)
    {
        ScrollViewer? scroll = FindVisualChild<ScrollViewer>(RowsList);
        if (scroll is null || scroll.ActualWidth <= 0)
        {
            return listWidth - RowsList.BorderThickness.Left - RowsList.BorderThickness.Right
                - RowsList.Padding.Left - RowsList.Padding.Right - SystemParameters.VerticalScrollBarWidth;
        }

        double scrollBar = scroll.Template?.FindName("PART_VerticalScrollBar", scroll) is ScrollBar bar
            && !double.IsNaN(bar.Width) ? bar.Width : 0;
        return scroll.ActualWidth - scroll.Padding.Left - scroll.Padding.Right
            - Math.Max(scrollBar, SystemParameters.VerticalScrollBarWidth);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }

        return null;
    }
}
