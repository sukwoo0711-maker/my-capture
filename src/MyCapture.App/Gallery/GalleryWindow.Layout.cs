using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    private bool _detailsPaneOpen;

    /// <summary>How much of the content toolbar's detail is shown (see <see cref="FitContentToolbar"/>).</summary>
    internal enum ToolbarDensity
    {
        /// <summary>Icon and label on each command.</summary>
        Labelled,

        /// <summary>Icons only; names and tooltips stay.</summary>
        IconsOnly,

        /// <summary>Icons only, a narrower selection count and sort box.</summary>
        Compact,
    }

    private ToolbarDensity _toolbarDensity;

    internal ToolbarDensity ContentToolbarDensity => _toolbarDensity;

    private void OnContentCommandBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) FitContentToolbar();
    }

    /// <summary>
    /// Fits the toolbar to its width without ever wrapping to a second row: the commands show icon and label
    /// while that fits; otherwise the labels go (icons keep their names and tooltips); otherwise the selection count
    /// and the sort box narrow too. The rule is checked when the window loads, is shown, changes DPI or width
    /// (which the details pane opening or closing does) and when the language or text size changes, and it never
    /// changes the bar's height, so a selection never moves the list.
    /// </summary>
    internal void FitContentToolbar()
    {
        if (SelectionCommands is null || SortCommands is null || LibraryContentCommandBar.ActualWidth <= 0) return;
        double available = LibraryContentCommandBar.ActualWidth;
        foreach (ToolbarDensity density in new[] { ToolbarDensity.Labelled, ToolbarDensity.IconsOnly, ToolbarDensity.Compact })
        {
            ApplyToolbarDensity(density);
            var probe = new Size(double.PositiveInfinity, LibraryContentCommandBar.RowDefinitions[0].ActualHeight);
            // A label's Visibility/Text invalidates its own measure immediately, but WPF propagates that
            // to its templated button and the outer panel later. A repeated fit at the same width must
            // not accept Labelled using cached icon-only sizes. Refresh just these small toolbar trees
            // before measuring; a full UpdateLayout here would re-enter the window's SizeChanged path.
            InvalidateToolbarMeasure(SelectionCommands);
            InvalidateToolbarMeasure(SortCommands);
            SelectionCommands.Measure(probe);
            SortCommands.Measure(probe);
            if (SelectionCommands.DesiredSize.Width + SortCommands.DesiredSize.Width <= available) break;
        }
    }

    private static void InvalidateToolbarMeasure(DependencyObject element)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            InvalidateToolbarMeasure(VisualTreeHelper.GetChild(element, index));
        if (element is UIElement visual) visual.InvalidateMeasure();
    }

    private void ApplyToolbarDensity(ToolbarDensity density)
    {
        _toolbarDensity = density;
        Visibility label = density == ToolbarDensity.Labelled ? Visibility.Visible : Visibility.Collapsed;
        foreach (TextBlock text in new[] { FloatSelectedLabel, CopySelectedLabel, EditSelectedLabel, PreviewSelectedLabel, CreatePdfSelectedLabel, KeepSelectedLabel })
            text.Visibility = label;
        // A sixth command still fits beside the details pane at the minimum window width.
        // Keep each command's hit target; only trim the gaps and the ellipsized count.
        foreach (Button button in SelectionCommands.Children.OfType<Button>())
        {
            if (density == ToolbarDensity.Compact) button.Margin = new Thickness(1, 0, 1, 0);
            else button.ClearValue(MarginProperty);
        }
        SelectionCountLabel.Width = density == ToolbarDensity.Compact ? 40 : 96;
        SelectionCountLabel.ToolTip = density == ToolbarDensity.Compact ? _viewModel.SelectionText : null;
        SortBox.Width = density == ToolbarDensity.Compact ? 92 : 112;
    }

    private void InitializeCommandBarLayout()
    {
        LibraryContentCommandBar.SizeChanged += OnContentCommandBarSizeChanged;
        Loaded += (_, _) => { FitSelectionCommands(); FitContentToolbar(); };
        DpiChanged += (_, _) => FitContentToolbar();
        IsVisibleChanged += (_, _) => { if (IsVisible) FitContentToolbar(); };
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or "" or nameof(GalleryViewModel.HasSelection)
                or nameof(GalleryViewModel.CanCompareSelected) or nameof(GalleryViewModel.SelectionText))
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(FitSelectionCommands));
        };
    }

    private void FitSelectionCommands()
    {
        ArchiveOverflowMenu.IsEnabled = _archiveCancellation is null && _viewModel.HasSelection;
        MoveOverflowMenu.IsEnabled = _viewModel.HasSelection;
        // Keep says what it will do to the selection: keep it, or release it when every card in it is kept.
        bool release = _viewModel.HasSelection && _viewModel.SelectedTiles.All(tile => tile.IsPinned);
        string keep = UiText.Get(release ? "LibraryUx_Unkeep" : "LibraryUx_Keep");
        KeepSelectedLabel.Text = keep;
        System.Windows.Automation.AutomationProperties.SetName(KeepSelectedButton, keep);
        KeepSelectedButton.ToolTip = UiText.Get(release ? "LibraryUx_Unkeep" : "LibraryUx_KeepHint");
        if (SelectionCountLabel.ToolTip is not null) SelectionCountLabel.ToolTip = _viewModel.SelectionText;
        // "보관 해제" is wider than "보관": the bar is fitted again for the new label.
        FitContentToolbar();
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

        // WPF's template can impose a larger minimum than the themed width (17 versus
        // 16 DIP). Reserve that actual track even when the scrollbar is collapsed.
        double scrollBar = scroll.Template?.FindName("PART_VerticalScrollBar", scroll) is ScrollBar bar
            && !double.IsNaN(bar.Width) && bar.Width > 0
                ? Math.Max(bar.Width, bar.MinWidth) : SystemParameters.VerticalScrollBarWidth;
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
