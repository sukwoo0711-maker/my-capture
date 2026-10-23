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
    private static readonly DependencyProperty ActionsProperty = DependencyProperty.RegisterAttached(
        "Actions", typeof(CaptureSourceActions), typeof(GallerySourceBadge), new PropertyMetadata(null));

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
        button.Click -= OnClick;
        button.Click += OnClick;
        button.PreviewMouseDoubleClick -= OnPreviewDoubleClick;
        button.PreviewMouseDoubleClick += OnPreviewDoubleClick;
        button.ToolTip = provenance.Tooltip;
        AutomationProperties.SetHelpText(button, provenance.Tooltip);
        AutomationProperties.SetName(button, UiText.Format(provenance.CanOpen
            ? "Source.OpenAutomation" : "Source.Label", provenance.BadgeText));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(new TextBlock
        {
            Text = SourceChip.GlyphFor(provenance.Kind),
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        var label = new TextBlock
        {
            Text = provenance.BadgeText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        button.Content = content;
        button.ContextMenu = SourceChip.CreateMenu(provenance, status, search, actions);
    }

    private static async void OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var button = (Button)sender;
        CaptureProvenance provenance = GetProvenance(button);
        if (!provenance.CanOpen)
        {
            ShowMenu(button);
            return;
        }
        GalleryWindow? gallery = Window.GetWindow(button) as GalleryWindow;
        var actions = (CaptureSourceActions)button.GetValue(ActionsProperty);
        string status = await actions.OpenAsync(provenance);
        gallery?.SetSourceActionStatus(status);
        if (status.Length > 0 && provenance.Kind == CaptureProvenanceKind.Document) ShowMenu(button);
    }

    private static void OnPreviewDoubleClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private static void ShowMenu(Button button)
    {
        if (button.ContextMenu is not { HasItems: true } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
