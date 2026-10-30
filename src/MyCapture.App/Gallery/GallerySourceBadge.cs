using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;

namespace MyCapture.App.Gallery;

/// <summary>Realized cards render cached provenance without looking up files, applications or icons.</summary>
public static class GallerySourceBadge
{
    private static readonly DependencyProperty DismissedProperty = DependencyProperty.RegisterAttached(
        "Dismissed", typeof(bool), typeof(GallerySourceBadge), new PropertyMetadata(false));
    private static readonly DependencyProperty ActionsProperty = DependencyProperty.RegisterAttached(
        "Actions", typeof(CaptureSourceActions), typeof(GallerySourceBadge), new PropertyMetadata(null));
    private static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached(
        "Status", typeof(Action<string>), typeof(GallerySourceBadge), new PropertyMetadata(null));
    // The badge's menu is built when it is first needed (a right click or the click of a source
    // without a page to open), not for every card that scrolls into view.
    private static readonly DependencyProperty MenuFactoryProperty = DependencyProperty.RegisterAttached(
        "MenuFactory", typeof(Func<ContextMenu>), typeof(GallerySourceBadge), new PropertyMetadata(null));

    public static readonly DependencyProperty ProvenanceProperty = DependencyProperty.RegisterAttached(
        "Provenance", typeof(CaptureProvenance), typeof(GallerySourceBadge),
        new PropertyMetadata(CaptureProvenance.None, OnProvenanceChanged));

    public static CaptureProvenance GetProvenance(DependencyObject target) =>
        (CaptureProvenance)target.GetValue(ProvenanceProperty);

    public static void SetProvenance(DependencyObject target, CaptureProvenance value) =>
        target.SetValue(ProvenanceProperty, value);

    private static void OnProvenanceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Button button) return;
        var provenance = (CaptureProvenance?)e.NewValue ?? CaptureProvenance.None;
        Configure(button, provenance,
            status => (Window.GetWindow(button) as GalleryWindow)?.SetSourceActionStatus(status),
            query => (Window.GetWindow(button) as GalleryWindow)?.SearchSource(query));
    }

    internal static void Configure(Button button, CaptureProvenance provenance,
        Action<string>? status, Action<string>? search, CaptureSourceActions? actions = null)
    {
        button.SetValue(ActionsProperty, actions ?? CaptureSourceActions.Default);
        button.SetValue(StatusProperty, status);
        button.Click -= OnClick;
        button.Click += OnClick;
        button.PreviewMouseDoubleClick -= OnPreviewDoubleClick;
        button.PreviewMouseDoubleClick += OnPreviewDoubleClick;
        ClosePreview(button);
        button.SetValue(DismissedProperty, false);
        string actionText = provenance.HasSource
            ? UiText.Format(provenance.CanOpen ? "Source.BadgeOpen" : "Source.BadgeDetails", provenance.BadgeText)
            : string.Empty;
        var tip = new ToolTip
        {
            Content = new TextBlock { Text = actionText, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
            Placement = PlacementMode.Bottom,
        };
        button.ToolTip = tip;
        GalleryToolTip.DismissOnEscape(button, tip, () =>
        {
            button.SetValue(DismissedProperty, true);
            tip.IsOpen = false;
        });
        button.GotKeyboardFocus -= OnGotKeyboardFocus;
        button.GotKeyboardFocus += OnGotKeyboardFocus;
        button.LostKeyboardFocus -= OnLostKeyboardFocus;
        button.LostKeyboardFocus += OnLostKeyboardFocus;
        button.PreviewKeyDown -= OnPreviewKeyDown;
        button.PreviewKeyDown += OnPreviewKeyDown;
        button.Unloaded -= OnUnloaded;
        button.Unloaded += OnUnloaded;
        button.MouseLeave -= OnMouseLeave;
        button.MouseLeave += OnMouseLeave;
        button.ToolTipOpening -= OnToolTipOpening;
        button.ToolTipOpening += OnToolTipOpening;
        AutomationProperties.SetHelpText(button, actionText);
        AutomationProperties.SetName(button, actionText);

        button.Content = new TextBlock
        {
            Text = SourceChip.GlyphFor(provenance.Kind),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.ContextMenu = null;
        button.SetValue(MenuFactoryProperty, (Func<ContextMenu>)(() => SourceChip.CreateMenu(provenance, status, search, actions)));
        button.ContextMenuOpening -= OnContextMenuOpening;
        button.ContextMenuOpening += OnContextMenuOpening;
    }

    /// <summary>Builds the badge's menu if it has not been built yet; null when the badge has none to show.</summary>
    internal static ContextMenu? EnsureMenu(Button button)
    {
        if (button.ContextMenu is null && button.GetValue(MenuFactoryProperty) is Func<ContextMenu> factory)
            button.ContextMenu = factory();
        return button.ContextMenu;
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var button = (Button)sender;
        e.Handled = true;
        if (EnsureMenu(button) is not { HasItems: true } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = e.CursorLeft < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static async void OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var button = (Button)sender;
        ClosePreview(button);
        CaptureProvenance provenance = GetProvenance(button);
        GalleryWindow? gallery = Window.GetWindow(button) as GalleryWindow;
        GalleryItemViewModel? tile = button.DataContext as GalleryItemViewModel;
        var actions = (CaptureSourceActions)button.GetValue(ActionsProperty);
        var status = (Action<string>?)button.GetValue(StatusProperty);
        if (!await TryOpenAsync(provenance, actions, status))
        {
            // An asynchronous document check may finish after a virtualized card was reused.
            if (!ReferenceEquals(GetProvenance(button), provenance) || !ReferenceEquals(button.DataContext, tile)) return;
            if (gallery is not null && tile is not null)
                gallery.ShowSourceDetails(tile);
            else
                ShowMenu(button);
        }
    }

    /// <summary>The badge and card menu share source opening and the document-failure fallback.</summary>
    internal static async Task<bool> TryOpenAsync(CaptureProvenance provenance, CaptureSourceActions actions, Action<string>? status)
    {
        if (!provenance.CanOpen) return false;
        string result = await actions.OpenAsync(provenance);
        status?.Invoke(result);
        return result.Length == 0 || provenance.Kind != CaptureProvenanceKind.Document;
    }

    private static void OnPreviewDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button button && ReferenceEquals(e.NewFocus, button))
            ShowForFocus(button, InputManager.Current.MostRecentInputDevice is KeyboardDevice
                && Mouse.LeftButton != MouseButtonState.Pressed);
    }

    internal static void ShowForFocus(Button button, bool keyboardInput)
    {
        if (keyboardInput && !(bool)button.GetValue(DismissedProperty)
            && GetProvenance(button).HasSource && button.ToolTip is ToolTip tip)
        {
            tip.PlacementTarget = button;
            GalleryToolTip.OpenForKeyboard(tip);
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ClosePreview((Button)sender);
        ((Button)sender).SetValue(DismissedProperty, false);
    }
    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        var button = (Button)sender;
        button.SetValue(DismissedProperty, false);
        if (button.ToolTip is ToolTip tip) GalleryToolTip.CloseKeyboardPreview(tip);
    }
    private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if ((bool)((Button)sender).GetValue(DismissedProperty)) e.Handled = true;
    }
    private static void OnUnloaded(object sender, RoutedEventArgs e) => ClosePreview((Button)sender);

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ((Button)sender).ToolTip is not ToolTip { IsOpen: true }) return;
        ((Button)sender).SetValue(DismissedProperty, true);
        ClosePreview((Button)sender);
        e.Handled = true;
    }

    private static void ClosePreview(Button button)
    {
        if (button.ToolTip is ToolTip tip) tip.IsOpen = false;
    }

    private static void ShowMenu(Button button)
    {
        if (EnsureMenu(button) is not { HasItems: true } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
