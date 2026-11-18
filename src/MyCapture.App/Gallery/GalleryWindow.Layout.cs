using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;

namespace MyCapture.App.Gallery;

internal sealed partial class GalleryWindow
{
    // The details pane is part of the library's layout: it stays until the user folds it away.
    private bool _detailsPaneOpen = true;
    private Action<bool>? _saveDetailsCollapsed;

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
    private System.Windows.Threading.DispatcherOperation? _selectionCommandsRefresh;

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
        double needed = 0;
        foreach (ToolbarDensity density in new[] { ToolbarDensity.Labelled, ToolbarDensity.IconsOnly, ToolbarDensity.Compact })
        {
            ApplyToolbarDensity(density);
            needed = MeasureContentToolbar();
            ContentToolbarShortfall = needed - available;
            if (needed <= available) return;
        }
        // Not even the compact bar fits (English at 150 % text beside the details pane at the minimum width):
        // the sort box gives back the width its longest choice asked for, down to the compact width, rather
        // than slide under the commands.
        double overflow = needed - available;
        double sortRoom = Math.Max(0, SortBox.Width - CompactSortBoxWidth);
        double fromSort = Math.Min(overflow, sortRoom);
        SortBox.Width = Math.Floor(SortBox.Width - fromSort);
        // Only when that is not enough does the count give back the rest, down to the width it had before it
        // followed its text; it then ends in an ellipsis with its tooltip, as the compact bar always allowed.
        if (overflow - fromSort > 0)
            SelectionCountLabel.Width = Math.Max(MinimumCompactCountWidth, Math.Floor(SelectionCountLabel.Width - (overflow - fromSort)));
    }

    private const double CompactSortBoxWidth = 92;
    private const double MinimumCompactCountWidth = 40;

    /// <summary>
    /// The compact bar's selection count is as wide as its longest text ("No selection", or 999 selected), measured in
    /// the label's own font and DPI, so the count is never cut to an ellipsis and a language or text size change is followed.
    /// </summary>
    private double CompactCountWidth()
    {
        var probe = new TextBlock
        {
            FontFamily = SelectionCountLabel.FontFamily,
            FontSize = SelectionCountLabel.FontSize,
            FontWeight = SelectionCountLabel.FontWeight,
            FontStyle = SelectionCountLabel.FontStyle,
            FontStretch = SelectionCountLabel.FontStretch,
            Language = SelectionCountLabel.Language,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(SelectionCountLabel));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(SelectionCountLabel));
        double widest = 0;
        foreach (string text in new[] { UiText.Get("LibraryUx_NoSelection"), UiText.Format("LibrarySelection_Count", 999) })
        {
            probe.Text = text;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, probe.DesiredSize.Width);
        }
        return Math.Ceiling(widest) + 1;
    }

    /// <summary>How far the content toolbar still overflows its row after the last fit; zero or less means it fits.</summary>
    internal double ContentToolbarShortfall { get; private set; }

    private double MeasureContentToolbar()
    {
        var probe = new Size(double.PositiveInfinity, LibraryContentCommandBar.RowDefinitions[0].ActualHeight);
        // A label's Visibility/Text invalidates its own measure immediately, but WPF propagates that
        // to its templated button and the outer panel later. A repeated fit at the same width must
        // not accept Labelled using cached icon-only sizes. Refresh just these small toolbar trees
        // before measuring; a full UpdateLayout here would re-enter the window's SizeChanged path.
        InvalidateToolbarMeasure(SelectionCommands);
        InvalidateToolbarMeasure(SortCommands);
        SelectionCommands.Measure(probe);
        SortCommands.Measure(probe);
        return (SelectionCommands.Visibility == Visibility.Visible ? SelectionCommands.DesiredSize.Width : 0)
            + SortCommands.DesiredSize.Width;
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
        foreach (TextBlock text in new[] { FloatSelectedLabel, CopySelectedLabel, EditSelectedLabel })
            text.Visibility = label;
        // Three direct actions stay within the same row beside the details pane.
        // Keep each command's hit target; only trim the gaps and the ellipsized count.
        foreach (Button button in SelectionCommands.Children.OfType<Button>())
        {
            if (density == ToolbarDensity.Compact) button.Margin = new Thickness(1, 0, 1, 0);
            else button.ClearValue(MarginProperty);
        }
        SelectionCountLabel.Width = density == ToolbarDensity.Compact ? CompactCountWidth() : 96;
        SelectionCountLabel.ToolTip = density == ToolbarDensity.Compact ? _viewModel.SelectionText : null;
        // No narrower than the longest choice (at 112 DIP English showed "Newest firs") unless the compact bar
        // still does not fit; see FitContentToolbar.
        SortBox.Width = Math.Max(density == ToolbarDensity.Compact ? CompactSortBoxWidth : 112, SortChoiceWidth());
    }

    /// <summary>
    /// The width that shows the sort box's longest choice whole between the box's padding, which holds the
    /// arrow. It is measured in the box's own font and DPI, so a language or text size change is followed.
    /// </summary>
    private double SortChoiceWidth()
    {
        var probe = new TextBlock
        {
            FontFamily = SortBox.FontFamily,
            FontSize = SortBox.FontSize,
            FontWeight = SortBox.FontWeight,
            FontStyle = SortBox.FontStyle,
            FontStretch = SortBox.FontStretch,
            FlowDirection = SortBox.FlowDirection,
            Language = SortBox.Language,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(SortBox));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(SortBox));
        double widest = 0;
        foreach (ComboBoxItem item in SortBox.Items.OfType<ComboBoxItem>())
        {
            probe.Text = item.Content as string ?? string.Empty;
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, probe.DesiredSize.Width);
        }
        return Math.Ceiling(widest + SortBox.Padding.Left + SortBox.Padding.Right);
    }

    private void InitializeCommandBarLayout()
    {
        MoreCommandsButton.ContextMenu.PlacementTarget = MoreCommandsButton;
        LibraryContentCommandBar.SizeChanged += OnContentCommandBarSizeChanged;
        Loaded += (_, _) => FitSelectionCommands();
        DpiChanged += (_, _) => FitContentToolbar();
        IsVisibleChanged += (_, _) => { if (IsVisible) FitContentToolbar(); };
        _viewModel.PropertyChanged += OnSelectionCommandsPropertyChanged;
        Closed += OnCommandBarLayoutClosed;
    }

    private void OnSelectionCommandsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or "" or nameof(GalleryViewModel.HasSelection)
            or nameof(GalleryViewModel.CanCompareSelected) or nameof(GalleryViewModel.SelectionText))) return;
        if (_selectionCommandsRefresh is { Status: System.Windows.Threading.DispatcherOperationStatus.Pending }) return;

        // One queued fit reads the latest selection after its related property notifications.
        _selectionCommandsRefresh = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _selectionCommandsRefresh = null;
            FitSelectionCommands();
        }));
    }

    private void OnCommandBarLayoutClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnSelectionCommandsPropertyChanged;
        _selectionCommandsRefresh?.Abort();
        _selectionCommandsRefresh = null;
        Closed -= OnCommandBarLayoutClosed;
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
    /// The pane is open by default and only the user's fold/unfold buttons change that, never a selection,
    /// a search, a folder change or an empty library (it then says nothing is selected). The folded choice is
    /// saved through <see cref="ConfigureDetailsPanePersistence"/> and restored on the next run.
    /// </summary>
    internal bool IsDetailsPaneOpen => _detailsPaneOpen;

    private void OnHideDetailsClick(object sender, RoutedEventArgs e) => UserSetDetailsPaneOpen(false);

    private void OnShowDetailsClick(object sender, RoutedEventArgs e) => UserSetDetailsPaneOpen(true);

    private void UserSetDetailsPaneOpen(bool open)
    {
        SetDetailsPaneOpen(open);
        _saveDetailsCollapsed?.Invoke(!open);
    }

    /// <summary>Restores the saved fold state without motion and keeps saving the user's later choices.</summary>
    internal void ConfigureDetailsPanePersistence(bool collapsed, Action<bool> save)
    {
        _saveDetailsCollapsed = save;
        _detailsPaneOpen = !collapsed;
        ApplyDetailsPaneVisibility();
    }

    /// <summary>Keeps the empty state's shortcut current.</summary>
    private void InitializeEmptyLibrary()
    {
        _viewModel.CaptureShortcutText = () => ReadHotkeys().Capture is { IsAssigned: true } capture ? capture.ToString() : string.Empty;
        ApplyDetailsPaneVisibility();
    }

    private void ApplyDetailsPaneVisibility()
    {
        GalleryDetailsPanel.Visibility = _detailsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        GalleryDetailsRail.Visibility = _detailsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
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
