using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    /// <summary>
    /// Whether the details pane is shown. Hiding it leaves a narrow rail with the button that
    /// brings it back, and the library takes the width, so a narrow window fits another column.
    /// The choice lasts while the app runs; the window is reused when it reopens.
    /// </summary>
    internal bool IsDetailsPaneOpen => GalleryDetailsPanel.Visibility == Visibility.Visible;

    private void OnHideDetailsClick(object sender, RoutedEventArgs e) => SetDetailsPaneOpen(false);

    private void OnShowDetailsClick(object sender, RoutedEventArgs e) => SetDetailsPaneOpen(true);

    internal void SetDetailsPaneOpen(bool open)
    {
        if (open == IsDetailsPaneOpen) return;
        // Keyboard focus inside the pane that is about to disappear moves to its counterpart, so
        // the same key toggles straight back and focus never falls to the window.
        bool hadFocus = (open ? GalleryDetailsRail : GalleryDetailsPanel).IsKeyboardFocusWithin;
        GalleryDetailsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        GalleryDetailsRail.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (hadFocus) _ = (open ? HideDetailsButton : ShowDetailsButton).Focus();
        if (open && FluidMotion.AnimationsEnabled)
        {
            // The pane is already laid out and usable; the fade only softens its arrival.
            GalleryDetailsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FluidMotion.NormalDuration)
            {
                EasingFunction = FluidMotion.StandardEasing,
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

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
