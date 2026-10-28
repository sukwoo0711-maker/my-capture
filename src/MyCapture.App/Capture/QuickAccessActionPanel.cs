using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>Keeps every action name below its icon, or hides all names when the card is too narrow.</summary>
internal sealed class QuickAccessActionPanel : Panel
{
    // The six named actions fit at the default caption size in Korean and English.
    // The card reserves this width even when the captured image is only a few pixels wide.
    internal const double MinimumDefaultTextWidth = 310;

    private readonly List<(Button Button, TextBlock Label)> _actions = [];

    internal void AddAction(Button button, SymbolIcon icon, string name)
    {
        var label = new TextBlock
        {
            Text = name, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
            Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Caption");
        var primary = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.SetBinding(SymbolIcon.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        // The icon sits in its own cell, so the check that replaces it fades in over the same spot
        // and the name below it never moves.
        var iconCell = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        iconCell.Children.Add(icon);
        primary.Children.Add(iconCell);
        primary.Children.Add(label);
        button.Content = primary;
        CommandButtonPresentation.Apply(button, compact: true);
        button.Height = double.NaN;
        button.MinHeight = 36;
        button.Padding = new Thickness(4, 0, 4, 0);
        button.Margin = new Thickness(2, 0, 2, 0);
        CommandButtonPresentation.ReserveHint(button);
        // The shared shortcut line remains below the icon/name stack, including at larger text sizes.
        ((Grid)button.Content).RowDefinitions[0].Height = GridLength.Auto;
        _actions.Add((button, label));
        Children.Add(button);
    }

    /// <summary>The symbol on an action's face, or null for a button this panel does not hold.</summary>
    internal static SymbolIcon? IconOf(Button button) =>
        button.Content is Grid { Name: "CommandButtonFace", Children.Count: > 0 } face
        && face.Children[0] is StackPanel stack
        ? stack.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<SymbolIcon>().FirstOrDefault() : null;

    /// <summary>The name shown under an action's icon, or null for a button this panel does not hold.</summary>
    internal static TextBlock? LabelOf(Button button) =>
        button.Content is Grid { Name: "CommandButtonFace", Children.Count: > 0 } face
        && face.Children[0] is StackPanel stack ? stack.Children.OfType<TextBlock>().SingleOrDefault() : null;

    /// <summary>True while every action shows its name; false while every action shows only its icon.</summary>
    internal bool NamesShown => _actions.Count > 0 && _actions.All(action => action.Label.Visibility == Visibility.Visible);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Every name must fit beside the others, or none is shown: there is no in-between.
        SetNames(Visibility.Visible);
        Size desired = MeasureActions();
        if (desired.Width > availableSize.Width)
        {
            SetNames(Visibility.Collapsed);
            desired = MeasureActions();
        }
        return desired;
    }

    private void SetNames(Visibility visibility)
    {
        foreach ((Button button, TextBlock label) in _actions)
        {
            if (label.Visibility != visibility)
            {
                label.Visibility = visibility;
                label.InvalidateMeasure();
                button.InvalidateMeasure();
            }

            // Candidate sizes are needed synchronously, before WPF drains its layout queue.
            // Propagate real dirtiness from any descendant (including the shortcut caption),
            // without invalidating an unchanged name just because another branch changed.
            PropagateMeasureInvalidation(button);
        }
    }

    private static bool PropagateMeasureInvalidation(DependencyObject node)
    {
        bool invalid = node is UIElement current && !current.IsMeasureValid;
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
            invalid |= PropagateMeasureInvalidation(VisualTreeHelper.GetChild(node, index));

        if (invalid && node is UIElement element && element.IsMeasureValid)
            element.InvalidateMeasure();
        return invalid;
    }

    private Size MeasureActions()
    {
        double width = 0, height = 0;
        foreach (UIElement action in InternalChildren)
        {
            action.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width += action.DesiredSize.Width;
            height = Math.Max(height, action.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = InternalChildren.Cast<UIElement>().Sum(action => action.DesiredSize.Width);
        double left = Math.Max(0, (finalSize.Width - width) / 2);
        foreach (UIElement action in InternalChildren)
        {
            action.Arrange(new Rect(left, 0, action.DesiredSize.Width, finalSize.Height));
            left += action.DesiredSize.Width;
        }
        return finalSize;
    }
}
