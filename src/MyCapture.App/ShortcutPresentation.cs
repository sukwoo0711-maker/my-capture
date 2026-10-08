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
        var hint = new TextBlock { Text = ShortcutCatalog.Get(scope, command).Hint, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center };
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
        // Filled primary actions use their on-accent text token for AA contrast.
        // Secondary text remains appropriate on ordinary surface buttons.
        if (button.TryFindResource("Button.Primary") is Style primary && ReferenceEquals(button.Style, primary))
            hint.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
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
        string label = AutomationProperties.GetName(button);
        if (string.IsNullOrEmpty(label)) label = previous as string ?? command;
        button.ToolTip = $"{label} ({ShortcutCatalog.Get(scope, command).Hint})";
        AutomationProperties.SetAcceleratorKey(button, ShortcutCatalog.Get(scope, command).Hint);
    }
}
