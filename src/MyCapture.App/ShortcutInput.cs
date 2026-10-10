using System.Windows.Input;

namespace MyCapture.App;

/// <summary>Maps a raw key event to the key a shortcut table should match.</summary>
internal static class ShortcutInput
{
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
}
