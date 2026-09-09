using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyCapture.App.Recording;

internal static class MediaExportVisuals
{
    internal static Button Button(FrameworkElement scope, string caption, string? icon = null, bool primary = false)
    {
        var button = new Button { MinHeight = 36, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "Button.Primary" : "Button.Secondary");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            var path = new Path { Width = 20, Height = 20, Stretch = Stretch.None, Margin = new Thickness(0, 0, 6, 0) };
            path.SetResourceReference(Path.DataProperty, icon);
            path.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground)) { Source = button });
            panel.Children.Add(path);
        }
        panel.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        button.Content = panel;
        AutomationProperties.SetName(button, caption);
        button.ToolTip = caption;
        return button;
    }
}
