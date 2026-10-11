using System.Windows.Input;

namespace MyCapture.App;

/// <summary>A physical key and the modifiers it was typed with.</summary>
internal readonly record struct KeyStroke(Key Key, ModifierKeys Modifiers = ModifierKeys.None);

/// <summary>Maps a raw key event to the key a shortcut table should match.</summary>
internal static class ShortcutInput
{
    // Standard Korean 2-set (두벌식) layout: the compatibility jamo each Latin key types.
    // Shift gives the tense consonants and the two y-vowels.
    private static readonly IReadOnlyDictionary<char, KeyStroke> JamoKeys = new Dictionary<char, KeyStroke>
    {
        ['ㅂ'] = new(Key.Q), ['ㅃ'] = new(Key.Q, ModifierKeys.Shift), ['ㅈ'] = new(Key.W), ['ㅉ'] = new(Key.W, ModifierKeys.Shift),
        ['ㄷ'] = new(Key.E), ['ㄸ'] = new(Key.E, ModifierKeys.Shift), ['ㄱ'] = new(Key.R), ['ㄲ'] = new(Key.R, ModifierKeys.Shift),
        ['ㅅ'] = new(Key.T), ['ㅆ'] = new(Key.T, ModifierKeys.Shift), ['ㅛ'] = new(Key.Y), ['ㅕ'] = new(Key.U),
        ['ㅑ'] = new(Key.I), ['ㅐ'] = new(Key.O), ['ㅒ'] = new(Key.O, ModifierKeys.Shift), ['ㅔ'] = new(Key.P),
        ['ㅖ'] = new(Key.P, ModifierKeys.Shift), ['ㅁ'] = new(Key.A), ['ㄴ'] = new(Key.S), ['ㅇ'] = new(Key.D),
        ['ㄹ'] = new(Key.F), ['ㅎ'] = new(Key.G), ['ㅗ'] = new(Key.H), ['ㅓ'] = new(Key.J), ['ㅏ'] = new(Key.K),
        ['ㅣ'] = new(Key.L), ['ㅋ'] = new(Key.Z), ['ㅌ'] = new(Key.X), ['ㅊ'] = new(Key.C), ['ㅍ'] = new(Key.V),
        ['ㅠ'] = new(Key.B), ['ㅜ'] = new(Key.N), ['ㅡ'] = new(Key.M),
    };

    // Jamo the IME builds from two key presses (ㄱ+ㅅ, ㅗ+ㅏ ...).
    private static readonly IReadOnlyDictionary<char, string> CompoundJamo = new Dictionary<char, string>
    {
        ['ㄳ'] = "ㄱㅅ", ['ㄵ'] = "ㄴㅈ", ['ㄶ'] = "ㄴㅎ", ['ㄺ'] = "ㄹㄱ", ['ㄻ'] = "ㄹㅁ", ['ㄼ'] = "ㄹㅂ",
        ['ㄽ'] = "ㄹㅅ", ['ㄾ'] = "ㄹㅌ", ['ㄿ'] = "ㄹㅍ", ['ㅀ'] = "ㄹㅎ", ['ㅄ'] = "ㅂㅅ",
        ['ㅘ'] = "ㅗㅏ", ['ㅙ'] = "ㅗㅐ", ['ㅚ'] = "ㅗㅣ", ['ㅝ'] = "ㅜㅓ", ['ㅞ'] = "ㅜㅔ", ['ㅟ'] = "ㅜㅣ", ['ㅢ'] = "ㅡㅣ",
    };

    // Unicode syllable order (U+AC00 + (initial * 21 + medial) * 28 + final).
    private const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private const string Medials = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    private const string Finals = "\0ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    /// <summary>
    /// Returns the physical key behind an IME-processed letter so single-letter shortcuts
    /// work while a Korean IME is in Hangul mode.
    /// </summary>
    /// <remarks>
    /// Inside a text input the key stays <see cref="Key.ImeProcessed"/>: there the IME owns
    /// the key, and translating it would let a shortcut steal the Esc or Enter that ends a
    /// composition.
    /// </remarks>
    internal static Key Resolve(Key key, Key imeProcessedKey, bool textInputFocused) =>
        key == Key.ImeProcessed && !textInputFocused && imeProcessedKey != Key.None ? imeProcessedKey : key;

    /// <summary>
    /// True when the IME took a key outside a text input without telling WPF which key it was,
    /// so no shortcut can be matched and the user should hear why nothing happened.
    /// </summary>
    internal static bool IsUnidentifiedImeKey(Key key, Key imeProcessedKey, bool textInputFocused) =>
        key == Key.ImeProcessed && imeProcessedKey == Key.None && !textInputFocused;

    /// <summary>
    /// Returns the keys that type <paramref name="text"/> on the 2-set Korean layout, or
    /// <see langword="null"/> when the text holds anything other than modern Hangul.
    /// </summary>
    /// <remarks>
    /// A Korean IME in Hangul mode turns letter keys into jamo before WPF sees a key, for
    /// example when the IME stays enabled on a control that never takes text. Reading the jamo
    /// back gives the shortcut the user pressed: ㅅ is T, ㄱ is R, 사 is T then K.
    /// </remarks>
    internal static IReadOnlyList<KeyStroke>? HangulKeystrokes(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var strokes = new List<KeyStroke>(text.Length * 2);
        foreach (char c in text)
        {
            if (c is >= '가' and <= '힣')
            {
                int index = c - 0xAC00;
                AppendJamo(strokes, Initials[index / (21 * 28)]);
                AppendJamo(strokes, Medials[index / 28 % 21]);
                if (index % 28 != 0) AppendJamo(strokes, Finals[index % 28]);
            }
            else if (!AppendJamo(strokes, c))
            {
                return null;
            }
        }

        return strokes;
    }

    /// <summary>True when <paramref name="text"/> contains any Hangul syllable or jamo.</summary>
    internal static bool ContainsHangul(string? text) =>
        text is not null && text.Any(static c => c is >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏'
            or >= 'ꥠ' and <= '꥿' or >= '가' and <= '힯' or >= 'ힰ' and <= '퟿');

    private static bool AppendJamo(List<KeyStroke> strokes, char jamo)
    {
        if (JamoKeys.TryGetValue(jamo, out KeyStroke stroke))
        {
            strokes.Add(stroke);
            return true;
        }

        if (!CompoundJamo.TryGetValue(jamo, out string? parts)) return false;
        foreach (char part in parts) strokes.Add(JamoKeys[part]);
        return true;
    }
}
