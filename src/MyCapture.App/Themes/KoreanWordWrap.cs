using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace MyCapture.App.Themes;

/// <summary>
/// Keeps Korean words whole when a narrow <see cref="TextBlock"/> wraps.
/// </summary>
/// <remarks>
/// WPF treats every Hangul syllable as a break opportunity, so a narrow column splits words
/// such as "보관하십시오" or "24개" across lines. A word joiner (U+2060) between the letters of
/// a word that contains Hangul leaves the spaces as the only places to wrap, which is the
/// keep-all behaviour Korean readers expect. Screen readers get the text without joiners.
/// <para>
/// The joiners are for display only. They are added to text that a <see cref="TextBlock"/>
/// shows, never to a <see cref="TextBox"/>, a value that is copied or saved, or an
/// <see cref="AutomationProperties.NameProperty"/>.
/// </para>
/// </remarks>
public static class KoreanWordWrap
{
    internal const char WordJoiner = '⁠';

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(KoreanWordWrap), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject owner) => (string?)owner.GetValue(TextProperty);

    public static void SetText(DependencyObject owner, string? value) => owner.SetValue(TextProperty, value);

    /// <summary>True while the interface is Korean; English text is shown unchanged.</summary>
    internal static bool Applies => EnabledOverrideForTest ?? UiText.Culture.TwoLetterISOLanguageName == "ko";

    /// <summary>Layout-cost measurement switches word keeping off and on for the same Korean UI.</summary>
    internal static bool? EnabledOverrideForTest { get; set; }

    internal static string KeepWords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length * 2);
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            builder.Append(current);
            if (index + 1 < text.Length)
            {
                char next = text[index + 1];
                if (!char.IsWhiteSpace(current) && !char.IsWhiteSpace(next) && (IsHangul(current) || IsHangul(next)))
                {
                    builder.Append(WordJoiner);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>The text as a reader or the clipboard should get it: without the joiners.</summary>
    internal static string Strip(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace(WordJoiner.ToString(), string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Shows <paramref name="text"/> in a code-built <paramref name="block"/>, kept whole per
    /// Korean word in a Korean interface when the block wraps. The block's accessible name stays
    /// the original text.
    /// </summary>
    internal static TextBlock Show(TextBlock block, string? text)
    {
        ArgumentNullException.ThrowIfNull(block);
        string original = text ?? string.Empty;
        string display = Applies && block.TextWrapping != TextWrapping.NoWrap ? KeepWords(original) : original;
        block.Text = display;
        if (display.Length != original.Length) AutomationProperties.SetName(block, original);
        return block;
    }

    /// <summary>
    /// The value a <c>{loc:Text}</c> markup extension gives a <see cref="TextBlock.Text"/>: the
    /// joined text, with the block's accessible name bound to its current text minus the
    /// joiners, so a later code-behind update reads correctly too. An accessible name the
    /// markup already set is kept.
    /// </summary>
    /// <remarks>
    /// Joined text takes WPF's slower complex-script layout (about ten times the measure time of
    /// the plain text), so only a block that wraps gets it. A wrapping set later in the markup
    /// or by a style is picked up when the block finishes initializing, before its first layout.
    /// </remarks>
    internal static string ForTextBlock(TextBlock block, string text)
    {
        if (block.TextWrapping != TextWrapping.NoWrap) return Join(block, text);
        if (!block.IsInitialized)
        {
            void OnInitialized(object? sender, EventArgs e)
            {
                block.Initialized -= OnInitialized;
                if (block.TextWrapping != TextWrapping.NoWrap && block.Text == text) block.Text = Join(block, text);
            }

            block.Initialized += OnInitialized;
        }

        return text;
    }

    private static string Join(TextBlock block, string text)
    {
        string display = KeepWords(text);
        if (display.Length != text.Length && block.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue)
        {
            BindingOperations.SetBinding(block, AutomationProperties.NameProperty, new Binding
            {
                Source = block,
                Path = new PropertyPath(TextBlock.TextProperty),
                Mode = BindingMode.OneWay,
                Converter = StripConverter.Instance,
            });
        }

        return display;
    }

    private static bool IsHangul(char value) =>
        value is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static void OnTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not TextBlock block) return;
        string text = e.NewValue as string ?? string.Empty;
        block.Text = KeepWords(text);
        AutomationProperties.SetName(block, text);
    }

    private sealed class StripConverter : IValueConverter
    {
        internal static readonly StripConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Strip(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            DependencyProperty.UnsetValue;
    }
}
