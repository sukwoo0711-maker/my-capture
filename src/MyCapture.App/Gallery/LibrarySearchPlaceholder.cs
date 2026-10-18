using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyCapture.App.Gallery;

/// <summary>Fits the root search hint to its allocated slot without changing the command bar.</summary>
internal sealed class LibrarySearchPlaceholder : TextBlock
{
    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(
        nameof(FullText), typeof(string), typeof(LibrarySearchPlaceholder),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty CompactTextProperty = DependencyProperty.Register(
        nameof(CompactText), typeof(string), typeof(LibrarySearchPlaceholder),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private static readonly DependencyPropertyKey FittedTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(FittedText), typeof(string), typeof(LibrarySearchPlaceholder), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty FittedTextProperty = FittedTextPropertyKey.DependencyProperty;

    public string FullText
    {
        get => (string)GetValue(FullTextProperty);
        set => SetValue(FullTextProperty, value);
    }

    /// <summary>Empty for a folder-scoped hint, which retains its normal ellipsis behavior.</summary>
    public string CompactText
    {
        get => (string)GetValue(CompactTextProperty);
        set => SetValue(CompactTextProperty, value);
    }

    public string FittedText => (string)GetValue(FittedTextProperty);

    static LibrarySearchPlaceholder()
    {
        foreach (DependencyProperty property in new[]
        {
            FontFamilyProperty, FontSizeProperty, FontWeightProperty, FontStyleProperty,
            FontStretchProperty, LanguageProperty, FlowDirectionProperty, PaddingProperty,
            UseLayoutRoundingProperty, TextOptions.TextFormattingModeProperty,
        })
            property.OverrideMetadata(typeof(LibrarySearchPlaceholder), new FrameworkPropertyMetadata(OnTextChanged));
    }

    public LibrarySearchPlaceholder()
    {
        Loaded += (_, _) => UpdateFit();
        SizeChanged += (_, _) => UpdateFit();
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((LibrarySearchPlaceholder)sender).UpdateFit();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateFit();
    }

    private void UpdateFit()
    {
        string fitted = FullText;
        if (ActualWidth > 0 && !string.IsNullOrEmpty(CompactText))
        {
            // A detached TextBlock uses exactly WPF's glyph metrics and pixel rounding. The
            // probe has no margin: ActualWidth is already the slot inside our own margin.
            var probe = new TextBlock
            {
                Text = FullText, FontFamily = FontFamily, FontSize = FontSize,
                FontWeight = FontWeight, FontStyle = FontStyle, FontStretch = FontStretch,
                Language = Language, FlowDirection = FlowDirection, Padding = Padding,
                UseLayoutRounding = UseLayoutRounding,
            };
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(this));
            VisualTreeHelper.SetRootDpi(probe, VisualTreeHelper.GetDpi(this));
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (probe.DesiredSize.Width > ActualWidth) fitted = CompactText;
        }

        // Bind this as the style's default Text, so the selection trigger still takes priority.
        // Neither Width nor a column definition depends on the chosen hint.
        SetValue(FittedTextPropertyKey, fitted);
    }
}
