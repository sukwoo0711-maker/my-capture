using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MyCapture.App.Gallery;

/// <summary>Reveals truncated titles without attaching pointer hints to the entire card.</summary>
public static class GalleryCaptionPreview
{
    private static readonly DependencyProperty DismissedProperty = DependencyProperty.RegisterAttached(
        "Dismissed", typeof(bool), typeof(GalleryCaptionPreview), new PropertyMetadata(false));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GalleryCaptionPreview), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(DependencyObject owner) => (bool)owner.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject owner, bool value) => owner.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is TextBlock title)
        {
            if ((bool)e.NewValue)
            {
                var content = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
                content.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = title });
                var tip = new ToolTip
                {
                    MaxWidth = 420, Placement = PlacementMode.Bottom,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content,
                    },
                };
                title.ToolTip = tip;
                GalleryToolTip.DismissOnEscape(title, tip, () =>
                {
                    title.SetValue(DismissedProperty, true);
                    tip.IsOpen = false;
                });
                ToolTipService.SetInitialShowDelay(title, 600);
                ToolTipService.SetShowDuration(title, 120000);
                title.ToolTipOpening += OnToolTipOpening;
                title.MouseLeave += OnTitleMouseLeave;
            }
            else
            {
                title.ToolTipOpening -= OnToolTipOpening;
                title.MouseLeave -= OnTitleMouseLeave;
                if (title.ToolTip is ToolTip tip) tip.IsOpen = false;
                title.ClearValue(FrameworkElement.ToolTipProperty);
            }
            return;
        }
        if (owner is not ListBoxItem item) return;
        if ((bool)e.NewValue)
        {
            item.GotKeyboardFocus += OnGotKeyboardFocus;
            item.LostKeyboardFocus += OnLostKeyboardFocus;
            item.PreviewKeyDown += OnPreviewKeyDown;
            item.Unloaded += OnUnloaded;
            item.IsVisibleChanged += OnVisibilityChanged;
            item.DataContextChanged += OnDataContextChanged;
            item.SizeChanged += OnSizeChanged;
        }
        else
        {
            item.GotKeyboardFocus -= OnGotKeyboardFocus;
            item.LostKeyboardFocus -= OnLostKeyboardFocus;
            item.PreviewKeyDown -= OnPreviewKeyDown;
            item.Unloaded -= OnUnloaded;
            item.IsVisibleChanged -= OnVisibilityChanged;
            item.DataContextChanged -= OnDataContextChanged;
            item.SizeChanged -= OnSizeChanged;
            Close(item);
        }
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBoxItem item && ReferenceEquals(e.NewFocus, item))
            ShowForFocus(item, InputManager.Current.MostRecentInputDevice is KeyboardDevice
                && Mouse.LeftButton != MouseButtonState.Pressed);
    }

    internal static void ShowForFocus(ListBoxItem item, bool keyboardInput)
    {
        if (keyboardInput && FindTitle(item) is { ToolTip: ToolTip tip } title
            && !(bool)title.GetValue(DismissedProperty) && IsTruncated(title))
        {
            tip.PlacementTarget = title;
            GalleryToolTip.OpenForKeyboard(tip);
        }
    }

    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var title = (TextBlock)sender;
        if ((bool)title.GetValue(DismissedProperty) || !IsTruncated(title)) e.Handled = true;
    }

    private static void OnTitleMouseLeave(object sender, MouseEventArgs e)
    {
        var title = (TextBlock)sender;
        title.SetValue(DismissedProperty, false);
        if (title.ToolTip is ToolTip tip) GalleryToolTip.CloseKeyboardPreview(tip);
    }

    internal static bool IsTruncated(TextBlock? title)
    {
        if (title is null || !title.IsVisible || title.ActualWidth <= 0
            || string.IsNullOrEmpty(title.Text) || title.TextTrimming == TextTrimming.None) return false;
        var measure = new TextBlock
        {
            Text = title.Text, FontFamily = title.FontFamily, FontSize = title.FontSize,
            FontWeight = title.FontWeight, FontStyle = title.FontStyle, FontStretch = title.FontStretch,
            Language = title.Language, FlowDirection = title.FlowDirection, Padding = title.Padding,
        };
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return measure.DesiredSize.Width > title.ActualWidth + .5;
    }

    private static TextBlock? FindTitle(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock { Name: "TileTitle" } title) return title;
            if (FindTitle(child) is { } descendant) return descendant;
        }
        return null;
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var item = (ListBoxItem)sender;
        Close(item);
        FindTitle(item)?.SetValue(DismissedProperty, false);
    }
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Close((ListBoxItem)sender);

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var item = (ListBoxItem)sender;
        Close(item);
        FindTitle(item)?.SetValue(DismissedProperty, false);
    }
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Close((ListBoxItem)sender);
    private static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) Close((ListBoxItem)sender);
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || FindTitle((ListBoxItem)sender)?.ToolTip is not ToolTip { IsOpen: true }) return;
        FindTitle((ListBoxItem)sender)?.SetValue(DismissedProperty, true);
        Close((ListBoxItem)sender);
        e.Handled = true;
    }

    internal static void Close(ListBoxItem item)
    {
        if (FindTitle(item)?.ToolTip is ToolTip tip) tip.IsOpen = false;
    }
}
