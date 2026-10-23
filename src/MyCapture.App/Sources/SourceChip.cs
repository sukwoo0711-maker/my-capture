using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;

namespace MyCapture.App.Sources;

/// <summary>Small source navigation control; constructing it never touches the file system.</summary>
internal static class SourceChip
{
    internal static string GlyphFor(CaptureProvenanceKind kind) => kind switch
    {
        CaptureProvenanceKind.Web => "\uE774",
        CaptureProvenanceKind.Document => "\uE8A5",
        CaptureProvenanceKind.App => "\uE737",
        _ => string.Empty,
    };

    internal static Button CreateButton(CaptureProvenance source, Action<string>? status = null,
        Action<string>? search = null, CaptureSourceActions? actions = null)
    {
        actions ??= CaptureSourceActions.Default;
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var symbol = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        symbol.Children.Add(new TextBlock
        {
            Text = GlyphFor(source.Kind), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
        });
        if (source.Kind == CaptureProvenanceKind.Document && Path.GetExtension(source.DocumentPath) is { Length: > 0 } extension)
            symbol.Children.Add(new TextBlock
            {
                Text = extension.TrimStart('.').ToUpperInvariant(), FontSize = 10, MaxWidth = 46,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        content.Children.Add(symbol);
        var text = new TextBlock
        {
            Text = source.DisplayText, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        var button = new Button
        {
            Name = "CaptureSourceChip", Content = content, ToolTip = source.Tooltip, MaxWidth = 320,
            MinWidth = 0, MinHeight = 24, Height = 26, Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = true, Focusable = true, Visibility = source.HasSource ? Visibility.Visible : Visibility.Collapsed,
            ContextMenu = CreateMenu(source, status, search, actions),
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Button.GhostCompact");
        AutomationProperties.SetAutomationId(button, "CaptureSourceChip");
        AutomationProperties.SetName(button, UiText.Format(source.CanOpen ? "Source.OpenAutomation" : "Source.Label", source.DisplayText));
        AutomationProperties.SetHelpText(button, source.Tooltip);
        button.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!source.CanOpen)
            {
                ShowMenu(button);
                return;
            }
            string result = await actions.OpenAsync(source);
            status?.Invoke(result);
            if (result.Length > 0 && source.Kind == CaptureProvenanceKind.Document) ShowMenu(button);
        };
        button.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                ShowMenu(button);
                e.Handled = true;
            }
        };
        return button;
    }

    internal static ContextMenu CreateMenu(CaptureProvenance source, Action<string>? status = null,
        Action<string>? search = null, CaptureSourceActions? actions = null, ContextMenu? menu = null)
    {
        actions ??= CaptureSourceActions.Default;
        menu ??= new ContextMenu();
        if (!source.HasSource) return menu;
        var open = Item("Source.Open", async () =>
        {
            string result = await actions.OpenAsync(source);
            status?.Invoke(result);
        });
        open.IsEnabled = source.CanOpen;
        menu.Items.Add(open);
        if (source.Kind == CaptureProvenanceKind.Web)
            menu.Items.Add(Item("Source.CopyUrl", () =>
            {
                string result = actions.CopyUrl(source.Url);
                status?.Invoke(result);
            }));
        if (source.Kind == CaptureProvenanceKind.Document)
        {
            menu.Items.Add(Item("Source.CopyPath", () =>
            {
                string result = actions.CopyPath(source.DocumentPath);
                status?.Invoke(result);
            }));
            menu.Items.Add(Item("Source.OpenLocation", async () =>
            {
                string result = await actions.OpenLocationAsync(source, allowMissingParent: true);
                status?.Invoke(result);
            }));
        }
        if (search is not null && source.SearchQuery.Length > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Source.ShowSame", () => search(source.SearchQuery)));
        }
        return menu;
    }

    private static MenuItem Item(string key, Action action)
    {
        string label = UiText.Get(key);
        var item = new MenuItem { Header = label };
        AutomationProperties.SetName(item, label);
        item.Click += (_, e) => { e.Handled = true; action(); };
        return item;
    }

    private static void ShowMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
