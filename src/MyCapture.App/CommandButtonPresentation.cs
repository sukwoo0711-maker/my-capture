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
    [ThreadStatic] private static Dictionary<object, double>? s_hintWidths;

    private static readonly DependencyProperty StandardTierProperty = DependencyProperty.RegisterAttached(
        "StandardTier", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    private static readonly DependencyProperty LabelCompactedProperty = DependencyProperty.RegisterAttached(
        "LabelCompacted", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    // The measured shortcut width, kept apart from the face's MinWidth: a named face reserves
    // it, while an icon-only face grows to it only as its caption is revealed.
    private static readonly DependencyProperty CaptionWidthProperty = DependencyProperty.RegisterAttached(
        "CaptionWidth", typeof(double), typeof(CommandButtonPresentation), new PropertyMetadata(0d));

    internal static double CaptionWidth(Grid face) => (double)face.GetValue(CaptionWidthProperty);

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
        RefitCaption(button);
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
        if (string.IsNullOrWhiteSpace(gesture)) gesture = string.Empty;
        if (button.Content is Grid { Name: "CommandButtonFace", Children.Count: > 1 } face)
        {
            if (face.Children[1] is TextBlock current)
            {
                current.Text = gesture;
                ReserveHintWidth(face, current);
                ShortcutRevealMotion.Update(current, animate: false);
            }
            return;
        }
        // Several surfaces describe shortcuts directly instead of using the catalog. They
        // join the same window-owned Alt behavior after their metadata/content is ready.
        TextBlock hint = ShortcutPresentation.Hint(gesture);
        hint.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        ReserveHint(button, hint);
    }

    internal static void ReserveHint(ButtonBase button, TextBlock? hint = null)
    {
        // An empty caption owns the same render-only motion and fixed-height slot as a
        // keyed face. Keep it visible in the tree so visible commands animate together.
        if (hint is null)
        {
            if (button.Content is Grid { Name: "CommandButtonFace", Children.Count: > 1 }) return;
            hint = ShortcutPresentation.Hint(string.Empty);
            hint.SetBinding(TextBlock.ForegroundProperty,
                new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        }
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
        if (hint.Text.Length == 0)
        {
            SetCaptionWidth(face, hint, 0);
            return;
        }
        // Measure the full chord independently of reveal and refresh its inherited font/DPI
        // on load or a real metadata change. Alt never enters this measurement path.
        DpiScale dpi = VisualTreeHelper.GetDpi(hint);
        object key = (hint.Text, hint.FontFamily, hint.FontSize, hint.FontWeight, hint.FontStyle,
            hint.FontStretch, hint.FlowDirection, hint.Language.IetfLanguageTag,
            TextOptions.GetTextFormattingMode(hint), dpi.DpiScaleX, dpi.DpiScaleY);
        var widths = s_hintWidths ??= [];
        if (widths.TryGetValue(key, out double width))
        {
            SetCaptionWidth(face, hint, width);
            hint.InvalidateMeasure();
            hint.InvalidateVisual();
            return;
        }
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
        VisualTreeHelper.SetRootDpi(probe, dpi);
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        width = Math.Ceiling(probe.DesiredSize.Width);
        if (widths.Count >= 256) widths.Clear();
        widths[key] = width;
        SetCaptionWidth(face, hint, width);
        // Refresh the actual caption as well as the detached width probe after DPI changes.
        hint.InvalidateMeasure();
        hint.InvalidateVisual();
    }

    private static void SetCaptionWidth(Grid face, TextBlock hint, double width)
    {
        face.SetValue(CaptionWidthProperty, width);
        FitCaption(face, hint);
    }

    private static bool IsIconOnly(ButtonBase button) =>
        (bool)button.GetValue(LabelCompactedProperty) || (bool)button.GetValue(AlwaysIconProperty);

    /// <summary>Fits a command caption whose reveal progress changed.</summary>
    internal static void FitCaption(TextBlock hint)
    {
        if (hint.Parent is Grid { Name: "CommandButtonFace", Children.Count: > 1 } face && ReferenceEquals(face.Children[1], hint))
            FitCaption(face, hint);
    }

    /// <summary>
    /// A named face reserves its caption, so Alt never moves it. An icon-only face keeps the
    /// command's own width at rest and widens with the caption's reveal progress instead.
    /// </summary>
    private static bool FitCaption(Grid face, TextBlock hint)
    {
        double caption = (double)face.GetValue(CaptionWidthProperty);
        double minimum = caption, maximum = double.PositiveInfinity;
        if (face.Parent is ButtonBase button && IsIconOnly(button))
        {
            minimum = 0;
            // Growth starts from the width the resting button already has, so the reveal
            // widens it from the first frame. Captions that fit there never move anything.
            double rest = RestingCaptionWidth(button);
            if (caption > rest) maximum = rest + (caption - rest) * ShortcutRevealMotion.WidthProgress(hint);
        }
        if (face.MinWidth.Equals(minimum) && hint.MaxWidth.Equals(maximum)) return false;
        face.MinWidth = minimum;
        hint.MaxWidth = maximum;
        return true;
    }

    private static double RestingCaptionWidth(ButtonBase button) => Math.Max(0, button.MinWidth
        - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right);

    /// <summary>Whether this caption's command widens while the caption shows.</summary>
    internal static bool Widens(TextBlock hint) =>
        hint.Parent is Grid { Name: "CommandButtonFace", Children.Count: > 1 } face && ReferenceEquals(face.Children[1], hint)
        && face.Parent is ButtonBase button && IsIconOnly(button)
        && (double)face.GetValue(CaptionWidthProperty) > RestingCaptionWidth(button);

    private static bool RefitCaption(ButtonBase button)
    {
        if (button.Content is not Grid { Name: "CommandButtonFace", Children.Count: > 1 } face
            || face.Children[1] is not TextBlock hint || !FitCaption(face, hint)) return false;
        // Like a label change, the candidate width is needed inside the panel's current measure.
        for (DependencyObject? parent = hint; parent is UIElement visual; parent = VisualTreeHelper.GetParent(parent))
        {
            visual.InvalidateMeasure();
            if (ReferenceEquals(parent, button)) break;
        }
        return true;
    }

    /// <summary>
    /// How much wider the commands in <paramref name="element"/> become while their captions
    /// show. Zero once revealed, so the sum of current and growth widths stays constant.
    /// </summary>
    internal static double RevealGrowth(UIElement element)
    {
        if (element.Visibility == Visibility.Collapsed) return 0;
        switch (element)
        {
            case ButtonBase button:
                if (!IsIconOnly(button) || button.Content is not Grid { Name: "CommandButtonFace", Children.Count: > 1 } face)
                    return 0;
                double caption = (double)face.GetValue(CaptionWidthProperty);
                if (caption <= 0) return 0;
                double content = Math.Max(caption, face.Children[0].DesiredSize.Width);
                double revealed = Math.Max(button.MinWidth, content + button.Padding.Left + button.Padding.Right
                    + button.BorderThickness.Left + button.BorderThickness.Right) + button.Margin.Left + button.Margin.Right;
                return Math.Max(0, revealed - button.DesiredSize.Width);
            case Panel panel:
                double growth = 0;
                foreach (UIElement child in panel.Children) growth += RevealGrowth(child);
                return growth;
            case Decorator { Child: { } child }:
                return RevealGrowth(child);
            default:
                return 0;
        }
    }

    // Primary completion commands keep their name even at the narrowest supported size.
    internal static readonly DependencyProperty KeepLabelProperty = DependencyProperty.RegisterAttached(
        "KeepLabel", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    internal static void KeepLabel(ButtonBase button) => button.SetValue(KeepLabelProperty, true);

    // When a command bar runs short, ordered commands give up their names one at a time,
    // lowest first. Unordered commands (0) keep the group rule: first group, then second.
    internal static readonly DependencyProperty CollapseOrderProperty = DependencyProperty.RegisterAttached(
        "CollapseOrder", typeof(int), typeof(CommandButtonPresentation), new PropertyMetadata(0));

    internal static void CollapseOrder(ButtonBase button, int order) => button.SetValue(CollapseOrderProperty, order);

    internal static readonly DependencyProperty AlwaysIconProperty = DependencyProperty.RegisterAttached(
        "AlwaysIcon", typeof(bool), typeof(CommandButtonPresentation), new PropertyMetadata(false));

    internal static void AlwaysIcon(ButtonBase button)
    {
        button.SetValue(AlwaysIconProperty, true);
        CompactLabel(button, true);
        // Glyph-only faces have no name to collapse but still widen only for their caption.
        RefitCaption(button);
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
        changed |= RefitCaption(button);
        if (changed) button.InvalidateMeasure();
        return changed;
    }
}
