using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MyCapture.App.Themes;

/// <summary>
/// Keeps Korean words whole when a narrow <see cref="TextBlock"/> wraps.
/// </summary>
/// <remarks>
/// WPF treats every Hangul syllable as a break opportunity, so a narrow column splits words
/// such as "보관하십시오" or "24개" across lines. A word joiner (U+2060) between the letters of
/// a word that contains Hangul leaves the spaces as the only places to wrap, which is the
/// keep-all behaviour Korean readers expect. Screen readers get the text without joiners.
/// </remarks>
public static class KoreanWordWrap
{
    internal const char WordJoiner = '⁠';

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(KoreanWordWrap), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject owner) => (string?)owner.GetValue(TextProperty);

    public static void SetText(DependencyObject owner, string? value) => owner.SetValue(TextProperty, value);

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

    private static bool IsHangul(char value) =>
        value is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    private static void OnTextChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not TextBlock block) return;
        string text = e.NewValue as string ?? string.Empty;
        block.Text = KeepWords(text);
        AutomationProperties.SetName(block, text);
    }
}
