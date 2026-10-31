using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

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

    /// <summary>
    /// The text with a word joiner between the letters of every word that contains Hangul, so only
    /// the spaces can break a line. A one-syllable last word is also joined to the whitespace
    /// before it, which keeps it on the line of the previous word. Joiners already in
    /// <paramref name="text"/> are replaced, so the result never doubles them.
    /// </summary>
    internal static string KeepWords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = Strip(text);
        (int gapStart, int wordStart) = FinalSyllableGap(text);
        var builder = new StringBuilder(text.Length * 2);
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            builder.Append(current);
            if (index + 1 < text.Length)
            {
                char next = text[index + 1];
                if ((index >= gapStart && index < wordStart)
                    || (!char.IsWhiteSpace(current) && !char.IsWhiteSpace(next) && (IsHangul(current) || IsHangul(next))))
                {
                    builder.Append(WordJoiner);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Where the whitespace before a one-syllable last word starts, and where that word starts, so
    /// "자동 저장 켬" never leaves "켬" alone on its line. The last word is what follows the last
    /// whitespace; punctuation around its one syllable ("켬.", "(켬)") does not count. Returns
    /// (-1, -1) when the text ends in anything else. A line break before the word keeps its
    /// paragraph, so that case is left alone.
    /// </summary>
    private static (int GapStart, int WordStart) FinalSyllableGap(string text)
    {
        const int None = -1;
        int last = text.Length - 1;
        while (last >= 0 && char.IsWhiteSpace(text[last])) last--;
        if (last < 0) return (None, None);
        int wordStart = last;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1])) wordStart--;
        int syllables = 0;
        for (int index = wordStart; index <= last; index++)
        {
            if (char.IsPunctuation(text[index])) continue;
            if (text[index] is not (>= '가' and <= '힣')) return (None, None);
            syllables++;
        }

        if (syllables != 1) return (None, None);
        int gapStart = wordStart;
        while (gapStart > 0 && char.IsWhiteSpace(text[gapStart - 1]))
        {
            if (text[gapStart - 1] is '\r' or '\n') return (None, None);
            gapStart--;
        }

        return gapStart == 0 || gapStart == wordStart ? (None, None) : (gapStart, wordStart);
    }

    /// <summary>The text as a reader or the clipboard should get it: without the joiners.</summary>
    internal static string Strip(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace(WordJoiner.ToString(), string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Shows <paramref name="text"/> in a code-built <paramref name="block"/>, kept whole per
    /// Korean word in a Korean interface when the block wraps (see <see cref="ForTextBlock"/>);
    /// other text is shown unchanged. The block's accessible name stays the text without the
    /// joiners, and every later status or live update goes through this method too. Reading the
    /// block's <see cref="TextBlock.Text"/> back gives the joined text: use <see cref="Strip"/>.
    /// </summary>
    internal static TextBlock Show(TextBlock block, string? text)
    {
        ArgumentNullException.ThrowIfNull(block);
        string original = Strip(text);
        block.Text = Applies ? ForTextBlock(block, original) : original;
        return block;
    }

    /// <summary>
    /// For a block built from inlines (<see cref="Run"/>, <see cref="Bold"/>, <see cref="Hyperlink"/>):
    /// joins the display text of its runs as one text, in a Korean interface when the block wraps,
    /// so words stay whole across run boundaries, and names the block with the plain text. The runs,
    /// links, emphasis and their event handlers stay as they are. Call it again after the runs change.
    /// </summary>
    internal static TextBlock ShowInlines(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var parts = new List<(Run? Run, string Text)>();
        Collect(block.Inlines);
        string original = string.Concat(parts.Select(part => part.Text));
        string display = Applies && block.TextWrapping != TextWrapping.NoWrap ? KeepWords(original) : original;
        int offset = 0;
        foreach ((Run? run, string text) in parts)
        {
            var shown = new StringBuilder();
            foreach (char _ in text)
            {
                shown.Append(display[offset++]);
                while (offset < display.Length && display[offset] == WordJoiner) shown.Append(display[offset++]);
            }
            if (run is not null) run.Text = shown.ToString();
        }
        AutomationProperties.SetName(block, original);
        return block;

        void Collect(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is Run run) parts.Add((run, Strip(run.Text)));
                else if (inline is Span span) Collect(span.Inlines);
                else if (inline is LineBreak) parts.Add((null, "\n"));
            }
        }
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
                if (Applies && block.TextWrapping != TextWrapping.NoWrap && block.Text == text) block.Text = Join(block, text);
            }

            block.Initialized += OnInitialized;
        }

        return text;
    }

    private static string Join(TextBlock block, string text)
    {
        string display = KeepWords(text);
        if (display.Length != text.Length) NameFromText(block);
        return display;
    }

    /// <summary>
    /// Binds the block's accessible name to its current text minus the joiners, unless the block
    /// already has a name of its own (a name set in code, or bound earlier by this class).
    /// </summary>
    private static void NameFromText(TextBlock block)
    {
        if (block.ReadLocalValue(AutomationProperties.NameProperty) != DependencyProperty.UnsetValue) return;
        BindingOperations.SetBinding(block, AutomationProperties.NameProperty, new Binding
        {
            Source = block,
            Path = new PropertyPath(TextBlock.TextProperty),
            Mode = BindingMode.OneWay,
            Converter = StripConverter.Instance,
        });
    }

    private static bool IsHangul(char value) =>
        value is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static void OnTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not TextBlock block) return;
        Show(block, e.NewValue as string);
        // The attached text names its block in every language, with or without joiners: code
        // that reads the accessible name (and screen readers) get the text in English too.
        NameFromText(block);
    }

    private sealed class StripConverter : IValueConverter
    {
        internal static readonly StripConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Strip(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            DependencyProperty.UnsetValue;
    }
}
