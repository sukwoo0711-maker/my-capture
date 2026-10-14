using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MyCapture.App;

/// <summary>Shared command sizing and a reserved shortcut line, independent of the button's state style.</summary>
internal static class CommandButtonPresentation
{
    internal const double IconSize = 16;
    internal const double IconLabelGap = 6;
    internal const double HintHeight = 14;

    internal static void Apply(ButtonBase button, bool compact = false)
    {
        string tier = compact ? "Compact" : "Standard";
        button.SetResourceReference(FrameworkElement.HeightProperty, $"CommandButton.Height.{tier}");
        button.SetResourceReference(FrameworkElement.MinHeightProperty, $"CommandButton.Height.{tier}");
        button.SetResourceReference(Control.PaddingProperty, $"CommandButton.Padding.{tier}");
        button.SetResourceReference(Control.FontSizeProperty, "CommandButton.FontSize");
        button.MinWidth = 36;
        button.Margin = new Thickness(3, 0, 3, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
    }

    internal static void ReserveHint(ButtonBase button, TextBlock? hint = null)
    {
        if (button.Content is Grid { Name: "CommandButtonFace" } face)
        {
            if (hint is not null)
            {
                if (face.Children.Count > 1) face.Children.RemoveAt(1);
                Grid.SetRow(hint, 1);
                face.Children.Add(hint);
            }
            return;
        }

        object? previous = button.Content;
        button.Content = null;
        var content = previous as UIElement ?? new TextBlock
        {
            Text = previous?.ToString() ?? string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        face = new Grid { Name = "CommandButtonFace", HorizontalAlignment = HorizontalAlignment.Center };
        face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HintHeight) });
        face.Children.Add(content);
        if (hint is not null)
        {
            Grid.SetRow(hint, 1);
            face.Children.Add(hint);
        }
        button.Content = face;
    }
}
