using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace MyCapture.App;

internal sealed class ShortcutPresentation : INotifyPropertyChanged
{
    /// <summary>
    /// The type size of every always-visible key caption: the FontSize.Caption token. A
    /// constant, so a caption built before the window's resources load still reads at 12.
    /// </summary>
    internal const double CaptionFontSize = 12;

    internal static ShortcutPresentation Current { get; } = new();
    internal static Func<bool>? ReadEnabled { get; set; }
    public Visibility HintVisibility => ReadEnabled?.Invoke() == false ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal static void Refresh() => Current.PropertyChanged?.Invoke(Current, new(nameof(HintVisibility)));

    /// <summary>
    /// Whether a key is printed under its command at all times: only a single key (V, X,
    /// Space, Home). Key combinations stay in the tooltip and the accelerator key. Ctrl+Z or
    /// Ctrl+C are the same in every program, and at a readable 12 px even the short ones
    /// widened the video editor's bar until its trim and text tools no longer fit at 760 DIP.
    /// </summary>
    internal static bool ShowsCaption(ShortcutDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Modifiers == ModifierKeys.None;
    }

    internal static TextBlock Hint(ShortcutScope scope, string command)
    {
        // 9–11 px captions were too small to read; every always-visible key uses the caption size.
        // Regular weight keeps the key below the command's own name, which buttons set semibold.
        var hint = new TextBlock
        {
            Text = ShortcutCatalog.Get(scope, command).Hint,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        hint.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(HintVisibility)) { Source = Current });
        return hint;
    }

    internal static void Attach(ButtonBase button, ShortcutScope scope, string command, bool corner = false)
    {
        ShortcutDefinition definition = ShortcutCatalog.Get(scope, command);
        if (!ShowsCaption(definition))
        {
            Describe(button, scope, command);
            return;
        }

        object? previous = button.Content;
        button.Content = null;
        UIElement content = previous as UIElement ?? new TextBlock { Text = previous?.ToString() ?? string.Empty, HorizontalAlignment = HorizontalAlignment.Center };
        var hint = Hint(scope, command);
        hint.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        // A disabled button keeps its tier's shape: the shared template only fades it by
        // Button.DisabledOpacity. Adding a sunken fill and a border here used to turn a
        // borderless tool button into a bordered one the moment it was disabled.
        if (corner)
        {
            var grid = new Grid { Width = 32, Height = 32 };
            if (content is FrameworkElement glyph) { glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Top; }
            grid.Children.Add(content);
            // The letter sits in the corner; its line box hangs below the glyph, not over it.
            hint.HorizontalAlignment = HorizontalAlignment.Right; hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.LineHeight = 12;
            hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            grid.Children.Add(hint); button.Content = grid;
        }
        else
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(content);
            stack.Children.Add(hint); button.Content = stack;
        }

        Describe(button, scope, command, previous as string);
    }

    /// <summary>
    /// Gives a command its key without a caption: the tooltip names the key after the
    /// command, and UI Automation reads it as the accelerator key.
    /// </summary>
    internal static void Describe(ButtonBase button, ShortcutScope scope, string command, string? fallbackLabel = null)
    {
        string hint = ShortcutCatalog.Get(scope, command).Hint;
        string label = button.ToolTip as string ?? AutomationProperties.GetName(button);
        if (string.IsNullOrEmpty(label)) label = fallbackLabel ?? button.Content as string ?? command;
        button.ToolTip = NamesKey(label, hint) ? label : $"{label} ({hint})";
        AutomationProperties.SetAcceleratorKey(button, hint);
    }

    /// <summary>A label that already ends with its key in parentheses (Undo (Ctrl+Z)) is kept.</summary>
    private static bool NamesKey(string label, string hint)
    {
        if (!label.EndsWith(')')) return false;
        int open = label.LastIndexOf('(');
        if (open < 0) return false;
        // Older labels spell the minus key with a hyphen and Delete in full.
        string named = label[open..].Replace('−', '-');
        return named.Contains(hint.Replace('−', '-'), StringComparison.Ordinal);
    }
}
