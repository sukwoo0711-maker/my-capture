using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private bool _detailsPaneOpen = true;

    private void InitializeCommandBarLayout()
    {
        ArchiveOverflowMenu.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = ArchiveExportButton });
        MoveOverflowMenu.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = MoveCapturesButton });
        LibraryCommandBar.SizeChanged += (_, _) => FitSelectionCommands();
        Loaded += (_, _) => FitSelectionCommands();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or "" or nameof(GalleryViewModel.HasSelection)
                or nameof(GalleryViewModel.CanCompareSelected) or nameof(GalleryViewModel.SelectionText))
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(FitSelectionCommands));
        };
    }

    private void FitSelectionCommands()
    {
        ArchiveExportButton.Visibility = Visibility.Visible;
        MoveCapturesButton.Visibility = Visibility.Visible;
        ArchiveOverflowMenu.Visibility = Visibility.Collapsed;
        MoveOverflowMenu.Visibility = Visibility.Collapsed;
        if (!_viewModel.HasSelection || LibraryCommandBar.ActualWidth <= 0) return;

        double WidthOf(FrameworkElement element)
        {
            element.InvalidateMeasure();
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Width;
        }

        // Keep a useful search field while secondary commands move into the existing menu.
        double available = LibraryCommandBar.ActualWidth - WidthOf(CompactCaptureCommands)
            - WidthOf(MoreCommandsButton) - SearchField.Margin.Left - SearchField.Margin.Right - 160;
        foreach ((Button button, MenuItem menu) in new[]
        {
            (ArchiveExportButton, ArchiveOverflowMenu),
            (MoveCapturesButton, MoveOverflowMenu),
        })
        {
            if (WidthOf(SelectionCommands) <= available) break;
            if (button.IsKeyboardFocusWithin) MoreCommandsButton.Focus();
            button.Visibility = Visibility.Collapsed;
            menu.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Whether the details pane is shown. Hiding it leaves a narrow rail with the button that
    /// brings it back, and the library takes the width, so a narrow window fits another column.
    /// The choice lasts while the app runs; the window is reused when it reopens. An empty
    /// library has nothing to describe, so neither the pane nor its rail is shown then.
    /// </summary>
    internal bool IsDetailsPaneOpen => _detailsPaneOpen;

    private void OnHideDetailsClick(object sender, RoutedEventArgs e) => SetDetailsPaneOpen(false);

    private void OnShowDetailsClick(object sender, RoutedEventArgs e) => SetDetailsPaneOpen(true);

    /// <summary>Keeps the empty state's shortcut current and the details pane off an empty library.</summary>
    private void InitializeEmptyLibrary()
    {
        _viewModel.CaptureShortcutText = () => ReadHotkeys().Capture is { IsAssigned: true } capture ? capture.ToString() : string.Empty;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or "" or nameof(GalleryViewModel.QueueIsEmpty)) ApplyDetailsPaneVisibility();
        };
        ApplyDetailsPaneVisibility();
    }

    private void ApplyDetailsPaneVisibility()
    {
        bool empty = _viewModel.QueueIsEmpty;
        GalleryDetailsPanel.Visibility = !empty && _detailsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        GalleryDetailsRail.Visibility = !empty && !_detailsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetDetailsPaneOpen(bool open)
    {
        if (open == _detailsPaneOpen) return;
        // Keyboard focus inside the pane that is about to disappear moves to its counterpart, so
        // the same key toggles straight back and focus never falls to the window.
        bool hadFocus = (open ? GalleryDetailsRail : GalleryDetailsPanel).IsKeyboardFocusWithin;
        _detailsPaneOpen = open;
        ApplyDetailsPaneVisibility();
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
    /// The width the column count is chosen from: the list's scrolling area inside its chrome,
    /// less the vertical scrollbar. The scrollbar is always reserved, so showing or hiding it
    /// never changes the column count; the card rows themselves fill whatever width is left.
    /// </summary>
    private double RowsContentWidth(double listWidth)
    {
        ScrollViewer? scroll = FindVisualChild<ScrollViewer>(RowsList);
        if (scroll is null || scroll.ActualWidth <= 0)
        {
            return listWidth - RowsList.BorderThickness.Left - RowsList.BorderThickness.Right
                - RowsList.Padding.Left - RowsList.Padding.Right - SystemParameters.VerticalScrollBarWidth;
        }

        // The themed scrollbar's own width; the system metric only when the template has none.
        double scrollBar = scroll.Template?.FindName("PART_VerticalScrollBar", scroll) is ScrollBar bar
            && !double.IsNaN(bar.Width) && bar.Width > 0 ? bar.Width : SystemParameters.VerticalScrollBarWidth;
        return scroll.ActualWidth - scroll.Padding.Left - scroll.Padding.Right - scrollBar;
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
