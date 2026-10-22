using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;

namespace MyCapture.App;

/// <summary>Fixed command sizing with an optional shortcut line inside the button.</summary>
internal static class CommandButtonPresentation
{
    internal const double IconSize = ShortcutRevealMotion.CommandIconSize;
    internal const double IconLabelGap = 6;
    internal const double HintHeight = 14;

    private static readonly DependencyProperty StandardTierProperty = DependencyProperty.RegisterAttached(
        "StandardTier", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    private static readonly DependencyProperty LabelCompactedProperty = DependencyProperty.RegisterAttached(
        "LabelCompacted", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    internal static void Apply(ButtonBase button, bool compact = false)
    {
        string tier = compact ? "Compact" : "Standard";
        button.SetValue(StandardTierProperty, !compact);
        button.SetResourceReference(FrameworkElement.HeightProperty, $"CommandButton.Height.{tier}");
        button.SetResourceReference(FrameworkElement.MinHeightProperty, $"CommandButton.Height.{tier}");
        string paddingTier = !compact && (bool)button.GetValue(LabelCompactedProperty) ? "StandardIconOnly" : tier;
        button.SetResourceReference(Control.PaddingProperty, $"CommandButton.Padding.{paddingTier}");
        button.SetResourceReference(Control.FontSizeProperty, "CommandButton.FontSize");
        button.MinWidth = 36;
        button.Margin = new Thickness(3, 0, 3, 0);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Loaded -= OnCommandLoaded;
        button.Loaded += OnCommandLoaded;
        ShortcutRevealMotion.PrepareCommandIcon(button.Content);
    }

    private static void OnCommandLoaded(object sender, RoutedEventArgs e) => RefreshHint((ButtonBase)sender);

    internal static void RefreshHint(ButtonBase button)
    {
        ShortcutRevealMotion.PrepareCommandIcon(button.Content);
        string gesture = AutomationProperties.GetAcceleratorKey(button);
        if (string.IsNullOrWhiteSpace(gesture) && button is Button action && (action.IsDefault || action.IsCancel))
        {
            gesture = action.IsDefault && action.IsCancel ? "Enter / Esc" : action.IsDefault ? "Enter" : "Esc";
            AutomationProperties.SetAcceleratorKey(button, gesture);
            if (button.ToolTip is string tooltip && !tooltip.Contains(gesture, StringComparison.Ordinal))
                button.ToolTip = $"{tooltip} ({gesture})";
        }
        if (button.Content is Grid { Name: "CommandButtonFace", Children.Count: > 1 } face)
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                face.Children.RemoveAt(1);
                face.MinWidth = 0;
                ShortcutRevealMotion.ResetPrimary((FrameworkElement)face.Children[0]);
            }
            else if (face.Children[1] is TextBlock current)
            {
                current.Text = gesture;
                ReserveHintWidth(face, current);
                ShortcutRevealMotion.Update(current, animate: false);
            }
            return;
        }
        if (string.IsNullOrWhiteSpace(gesture)) return;
        // Several surfaces describe shortcuts directly instead of using the catalog. They
        // join the same window-owned Alt behavior after their metadata/content is ready.
        TextBlock hint = ShortcutPresentation.Hint(gesture);
        hint.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        ReserveHint(button, hint);
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
                ShortcutRevealMotion.Configure(hint, (FrameworkElement)face.Children[0], corner: false);
                ReserveHintWidth(face, hint);
            }
            return;
        }

        object? previous = button.Content;
        button.Content = null;
        var content = previous as FrameworkElement ?? new TextBlock
        {
            Text = previous?.ToString() ?? string.Empty,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        face = new Grid
        {
            Name = "CommandButtonFace",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IconSize) });
        face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HintHeight) });
        ShortcutRevealMotion.PreparePrimary(content);
        face.Children.Add(content);
        if (hint is not null)
        {
            Grid.SetRow(hint, 1);
            face.Children.Add(hint);
            ShortcutRevealMotion.Configure(hint, content, corner: false);
            ReserveHintWidth(face, hint);
        }
        button.Content = face;
    }

    private static void ReserveHintWidth(Grid face, TextBlock hint)
    {
        // Reserve the full chord independently of reveal and refresh its inherited font/DPI
        // on load or a real metadata change. Alt never enters this measurement path.
        var probe = new TextBlock
        {
            Text = hint.Text,
            FontFamily = hint.FontFamily,
            FontSize = hint.FontSize,
            FontWeight = hint.FontWeight,
            FontStyle = hint.FontStyle,
            FontStretch = hint.FontStretch,
            FlowDirection = hint.FlowDirection,
            Language = hint.Language,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(hint));
        VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(hint));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        face.MinWidth = Math.Ceiling(probe.DesiredSize.Width);
        // Refresh the actual caption as well as the detached width probe after DPI changes.
        hint.InvalidateMeasure();
        hint.InvalidateVisual();
    }

    // Primary completion commands keep their name even at the narrowest supported size.
    internal static readonly DependencyProperty KeepLabelProperty = DependencyProperty.RegisterAttached(
        "KeepLabel", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    internal static void KeepLabel(ButtonBase button) => button.SetValue(KeepLabelProperty, true);

    internal static readonly DependencyProperty AlwaysIconProperty = DependencyProperty.RegisterAttached(
        "AlwaysIcon", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    internal static void AlwaysIcon(ButtonBase button)
    {
        button.SetValue(AlwaysIconProperty, true);
        CompactLabel(button, true);
    }

    internal static bool CompactLabel(ButtonBase button, bool compact)
    {
        if ((bool)button.GetValue(KeepLabelProperty)) return false;
        compact |= (bool)button.GetValue(AlwaysIconProperty);
        object? content = button.Content;
        if (content is Grid { Name: "CommandButtonFace" } face && face.Children.Count > 0)
            content = face.Children[0];
        // Only icon-and-name faces can become icon-only. Text-only controls remain usable.
        if (content is not StackPanel row || row.Orientation != Orientation.Horizontal
            || !row.Children.OfType<TextBlock>().Any()
            || !row.Children.Cast<UIElement>().Any(child => child is not TextBlock)) return false;
        bool changed = false;
        if ((bool)button.GetValue(LabelCompactedProperty) != compact)
        {
            button.SetValue(LabelCompactedProperty, compact);
            if ((bool)button.GetValue(StandardTierProperty))
            {
                // Only standard command faces trade horizontal padding for space. Keep the
                // resource reference on expansion so theme changes still reach labeled faces.
                button.SetResourceReference(Control.PaddingProperty,
                    compact ? "CommandButton.Padding.StandardIconOnly" : "CommandButton.Padding.Standard");
                changed = true;
            }
        }
        foreach (TextBlock label in row.Children.OfType<TextBlock>())
        {
            Visibility visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            if (label.Visibility == visibility) continue;
            label.SetCurrentValue(UIElement.VisibilityProperty, visibility);
            changed = true;
            // WPF normally queues ancestor measurement. These candidate widths are needed
            // inside the current measure call, before that queued propagation runs.
            for (DependencyObject? parent = label; parent is UIElement visual; parent = VisualTreeHelper.GetParent(parent))
            {
                visual.InvalidateMeasure();
                if (ReferenceEquals(parent, button)) break;
            }
        }
        if (changed) button.InvalidateMeasure();
        return changed;
    }
}
