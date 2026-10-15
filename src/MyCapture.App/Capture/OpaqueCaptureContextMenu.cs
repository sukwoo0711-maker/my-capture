using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace MyCapture.App.Capture;

/// <summary>A ContextMenu whose WPF-owned popup is non-layered before its HWND is created.</summary>
internal sealed class OpaqueCaptureContextMenu : ContextMenu
{
    internal OpaqueCaptureContextMenu()
    {
        HasDropShadow = false;
        Background = CaptureWindowExclusion.OpaqueBackground();
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(BorderBrushProperty, "Border.Strong");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(5);
        // Keep the shared menu's content and keyboard navigation, without transparent corners
        // or the outer shadow. HasDropShadow alone does not disable Popup.AllowsTransparency.
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type ContextMenu}">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"
                        SnapsToDevicePixels="True">
                    <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle" />
                </Border>
            </ControlTemplate>
            """);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.Property == IsOpenProperty && (bool)e.NewValue && Parent is null)
        {
            // ContextMenu has no AllowsTransparency property and creates its own Popup.
            // WPF defers creating the HWND when PlacementTarget is not connected to a
            // presentation source. Use that first-open boundary to configure the parent
            // Popup, then restore the real target before the queued creation runs.
            UIElement? target = PlacementTarget;
            PlacementTarget = new Border();
            try
            {
                base.OnPropertyChanged(e);
                if (Parent is Popup popup)
                {
                    popup.AllowsTransparency = false;
                    popup.PopupAnimation = PopupAnimation.None;
                }
            }
            finally
            {
                PlacementTarget = target;
            }
            return;
        }
        base.OnPropertyChanged(e);
    }
}
