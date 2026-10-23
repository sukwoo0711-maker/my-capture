using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace MyCapture.App.Editing;

/// <summary>
/// Draws technical text in the bundled Pretendard exactly as typed.
/// </summary>
/// <remarks>
/// <para>
/// Pretendard's default ligatures and contextual alternates rewrite text a caption often holds:
/// "0x02D9" and "1344x680" get a multiplication sign, "->", "&lt;-" and "=&gt;" become arrows and
/// "!=" changes shape. A caption on a register map would then show a different value from the
/// one typed.
/// </para>
/// <para>
/// The live editing box turns the three features off. <see cref="System.Windows.Media.FormattedText"/>,
/// which draws and sizes the committed caption, has no typography settings, so the same glyphs
/// come from an invisible word joiner (U+2060) inside each rewritten pair. It has no width and
/// breaks only the substitution, so both paths lay the caption out to the same width.
/// </para>
/// <para>
/// Colons and hyphens between digits or capitals are only raised, at the same width and as the
/// same characters, so they keep the font's form in the committed caption: a joiner beside them
/// would also drop their kerning and widen the caption.
/// </para>
/// </remarks>
internal static class LiteralShaping
{
    internal const char WordJoiner = '⁠';

    private const string Operators = "-<>=!";

    /// <summary>Whether <paramref name="family"/> is drawn literally.</summary>
    internal static bool Applies(string? family) =>
        string.Equals(family?.Trim(), "Pretendard", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns off the features that rewrite text in the live editing box.</summary>
    internal static void Apply(DependencyObject element)
    {
        Typography.SetStandardLigatures(element, false);
        Typography.SetContextualAlternates(element, false);
        Typography.SetContextualLigatures(element, false);
    }

    /// <summary>
    /// <paramref name="text"/> with a word joiner inside every pair the features would replace
    /// with another glyph: a digit before x and two adjacent operator characters.
    /// </summary>
    internal static string Prepare(string text)
    {
        StringBuilder? result = null;
        for (int index = 1; index < text.Length; index++)
        {
            bool breaks = Breaks(text[index - 1], text[index]);
            if (breaks && result is null) result = new StringBuilder(text, 0, index, text.Length + 8);
            if (result is null) continue;
            if (breaks) result.Append(WordJoiner);
            result.Append(text[index]);
        }
        return result?.ToString() ?? text;
    }

    private static bool Breaks(char previous, char next) =>
        (char.IsAsciiDigit(previous) && next is 'x' or 'X')
        || (Operators.Contains(previous) && Operators.Contains(next));
}
