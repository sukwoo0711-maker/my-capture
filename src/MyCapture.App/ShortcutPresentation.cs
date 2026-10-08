using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace MyCapture.App;

internal sealed class ShortcutPresentation : INotifyPropertyChanged
{
    internal static ShortcutPresentation Current { get; } = new();
    internal static Func<bool>? ReadEnabled { get; set; }
    public Visibility HintVisibility => ReadEnabled?.Invoke() == false ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal static void Refresh() => Current.PropertyChanged?.Invoke(Current, new(nameof(HintVisibility)));

    internal static TextBlock Hint(ShortcutScope scope, string command)
    {
        var hint = new TextBlock { Text = ShortcutCatalog.Get(scope, command).Hint, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        hint.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(HintVisibility)) { Source = Current });
        return hint;
    }

    internal static void Attach(ButtonBase button, ShortcutScope scope, string command, bool corner = false)
    {
        object? previous = button.Content;
        button.Content = null;
        UIElement content = previous as UIElement ?? new TextBlock { Text = previous?.ToString() ?? string.Empty, HorizontalAlignment = HorizontalAlignment.Center };
        var hint = Hint(scope, command);
        hint.FontSize = corner ? 10 : scope is ShortcutScope.Annotation or ShortcutScope.Crop ? 11 : 9;
        hint.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        // Neutral disabled colours remove the action accent without fading small
        // shortcuts below 3:1. Keep the shared template's opacity from fading them again.
        if (scope is ShortcutScope.Annotation or ShortcutScope.Crop)
        {
            button.Opacity = 1;
            var style = new Style(button.GetType(), button.Style);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text.Disabled")));
            disabled.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("Surface.Sunken")));
            disabled.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("Border.Subtle")));
            style.Triggers.Add(disabled);
            button.Style = style;
        }
        if (corner)
        {
            var grid = new Grid { Width = 32, Height = 32 };
            if (content is FrameworkElement glyph) { glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Top; }
            grid.Children.Add(content);
            hint.HorizontalAlignment = HorizontalAlignment.Right; hint.VerticalAlignment = VerticalAlignment.Bottom;
            grid.Children.Add(hint); button.Content = grid;
        }
        else
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(content);
            stack.Children.Add(hint); button.Content = stack;
        }
        string label = button.ToolTip as string ?? AutomationProperties.GetName(button);
        if (string.IsNullOrEmpty(label)) label = previous as string ?? command;
        string suffix = $"({ShortcutCatalog.Get(scope, command).Hint})";
        button.ToolTip = label.EndsWith(suffix, StringComparison.Ordinal) ? label : $"{label} {suffix}";
        AutomationProperties.SetAcceleratorKey(button, ShortcutCatalog.Get(scope, command).Hint);
    }

}
