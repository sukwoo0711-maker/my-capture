using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MyCapture.App.Gallery;

/// <summary>
/// Shows a long value in a wrapping <see cref="TextBlock"/> as a summary of a few lines that
/// ends in an ellipsis, with an optional toggle that shows the rest only when something is hidden.
/// </summary>
/// <remarks>
/// <para>
/// The library details panel uses this for page addresses, folder paths and titles instead of a
/// read-only text box with its own scrollbar, so the metadata area is the panel's only
/// scrolling surface. Lines use a fixed block height, which keeps the summary exactly
/// <see cref="GetMaxLines"/> lines tall for mixed Korean and Latin text.
/// </para>
/// <para>
/// Set the value through <see cref="TextProperty"/> rather than <see cref="TextBlock.Text"/>: a
/// new value (another capture selected) always starts as a summary again. A toggle names its
/// block with <see cref="ToggleTargetProperty"/>; it is shown only while the block overflows and
/// its checked state is the block's expanded state.
/// </para>
/// </remarks>
public static class GalleryTextClamp
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(GalleryTextClamp), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.RegisterAttached(
        "MaxLines", typeof(int), typeof(GalleryTextClamp), new PropertyMetadata(0, OnLayoutInputChanged));

    public static readonly DependencyProperty MiddleEllipsisProperty = DependencyProperty.RegisterAttached(
        "MiddleEllipsis", typeof(bool), typeof(GalleryTextClamp), new PropertyMetadata(false, OnLayoutInputChanged));

    /// <summary>True shows the whole value instead of the summary.</summary>
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool), typeof(GalleryTextClamp), new PropertyMetadata(false, OnLayoutInputChanged));

    private static readonly DependencyPropertyKey IsOverflowingPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsOverflowing", typeof(bool), typeof(GalleryTextClamp), new PropertyMetadata(false));

    /// <summary>True while the value needs more than <see cref="MaxLinesProperty"/> lines at the current width.</summary>
    public static readonly DependencyProperty IsOverflowingProperty = IsOverflowingPropertyKey.DependencyProperty;

    /// <summary>On a toggle button: the clamped block it expands and collapses.</summary>
    public static readonly DependencyProperty ToggleTargetProperty = DependencyProperty.RegisterAttached(
        "ToggleTarget", typeof(TextBlock), typeof(GalleryTextClamp), new PropertyMetadata(null, OnToggleTargetChanged));

    private static readonly DependencyProperty ToggleProperty = DependencyProperty.RegisterAttached(
        "Toggle", typeof(ToggleButton), typeof(GalleryTextClamp), new PropertyMetadata(null));

    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached", typeof(bool), typeof(GalleryTextClamp), new PropertyMetadata(false));

    private static readonly DependencyProperty AutoLineHeightProperty = DependencyProperty.RegisterAttached(
        "AutoLineHeight", typeof(bool), typeof(GalleryTextClamp), new PropertyMetadata(false));

    public static string GetText(DependencyObject owner) => (string)owner.GetValue(TextProperty);
    public static void SetText(DependencyObject owner, string value) => owner.SetValue(TextProperty, value);
    public static int GetMaxLines(DependencyObject owner) => (int)owner.GetValue(MaxLinesProperty);
    public static void SetMaxLines(DependencyObject owner, int value) => owner.SetValue(MaxLinesProperty, value);
    public static bool GetMiddleEllipsis(DependencyObject owner) => (bool)owner.GetValue(MiddleEllipsisProperty);
    public static void SetMiddleEllipsis(DependencyObject owner, bool value) => owner.SetValue(MiddleEllipsisProperty, value);
    public static bool GetIsExpanded(DependencyObject owner) => (bool)owner.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject owner, bool value) => owner.SetValue(IsExpandedProperty, value);
    public static bool GetIsOverflowing(DependencyObject owner) => (bool)owner.GetValue(IsOverflowingProperty);
    public static TextBlock? GetToggleTarget(DependencyObject owner) => (TextBlock?)owner.GetValue(ToggleTargetProperty);
    public static void SetToggleTarget(DependencyObject owner, TextBlock? value) => owner.SetValue(ToggleTargetProperty, value);

    private static void OnTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not TextBlock text) return;
        text.Text = e.NewValue as string ?? string.Empty;
        text.SetCurrentValue(IsExpandedProperty, false);
        Apply(text);
    }

    private static void OnLayoutInputChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is TextBlock text) Apply(text);
    }

    private static void OnToggleTargetChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not ToggleButton toggle) return;
        if (e.OldValue is TextBlock previous) previous.ClearValue(ToggleProperty);
        toggle.Checked -= OnToggleChanged;
        toggle.Unchecked -= OnToggleChanged;
        if (e.NewValue is not TextBlock text) return;
        text.SetValue(ToggleProperty, toggle);
        toggle.Checked += OnToggleChanged;
        toggle.Unchecked += OnToggleChanged;
        Apply(text);
    }

    private static void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        var toggle = (ToggleButton)sender;
        GetToggleTarget(toggle)?.SetCurrentValue(IsExpandedProperty, toggle.IsChecked == true);
    }

    private static void Apply(TextBlock text)
    {
        text.Text = GetText(text);
        int lines = GetMaxLines(text);
        bool overflowing = false;
        if (lines <= 0)
        {
            text.ClearValue(FrameworkElement.MaxHeightProperty);
        }
        else
        {
            Attach(text);
            // Follow the inherited font size, which is only known once the block is in the tree.
            if ((bool)text.GetValue(AutoLineHeightProperty)) text.LineHeight = Math.Ceiling(text.FontSize * 1.4);
            double summary = lines * text.LineHeight;
            // Before the first layout the width is unknown; SizeChanged measures again then.
            overflowing = text.ActualWidth > 0 && FullHeight(text) > summary + 0.5;
            if (overflowing && GetMiddleEllipsis(text) && !GetIsExpanded(text))
            {
                string full = GetText(text);
                int low = 5, high = full.Length - 1;
                string shortened = MyCapture.Core.Capture.SourceDocument.ShortenPath(full, 5);
                while (low <= high)
                {
                    int middle = low + (high - low) / 2;
                    text.Text = MyCapture.Core.Capture.SourceDocument.ShortenPath(full, middle);
                    if (FullHeight(text) <= summary + 0.5) { shortened = text.Text; low = middle + 1; }
                    else high = middle - 1;
                }
                text.Text = shortened;
            }
            text.MaxHeight = GetIsExpanded(text) ? double.PositiveInfinity : summary;
        }

        text.SetValue(IsOverflowingPropertyKey, overflowing);
        if (text.GetValue(ToggleProperty) is ToggleButton toggle)
        {
            toggle.Visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
            bool expanded = GetIsExpanded(text);
            if ((toggle.IsChecked == true) != expanded) toggle.IsChecked = expanded;
        }
    }

    private static void Attach(TextBlock text)
    {
        if ((bool)text.GetValue(IsAttachedProperty)) return;
        text.SetValue(IsAttachedProperty, true);
        text.SetValue(AutoLineHeightProperty, double.IsNaN(text.LineHeight));
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        // Height changes from expanding never re-measure; a new width can change the line count.
        text.SizeChanged += (_, e) => { if (e.WidthChanged) Apply(text); };
    }

    /// <summary>The height the whole value needs at the block's current width.</summary>
    private static double FullHeight(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            FontStretch = text.FontStretch,
            Padding = text.Padding,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = text.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            UseLayoutRounding = text.UseLayoutRounding,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(text));
        probe.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }
}
