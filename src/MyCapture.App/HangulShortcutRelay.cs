using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyCapture.App;

/// <summary>
/// Second line of defence for single-letter shortcuts under a Korean IME: turns Hangul that
/// the IME composes on a control that never takes text back into the keys that typed it.
/// </summary>
/// <remarks>
/// <para>
/// The first line is the key route: a Hangul-mode letter arrives as
/// <see cref="Key.ImeProcessed"/> in PreviewKeyDown and <see cref="ShortcutInput.Resolve"/>
/// recovers the letter. When the owner handles it there, WPF never passes the key on to the
/// text services, so no composition starts. This relay covers the cases where the IME still
/// receives the letter (it was not identified, or another layer let it through): the IME then
/// reports a composition such as "ㅅ" through <see cref="TextCompositionManager"/>.
/// </para>
/// <para>
/// Outside a text input such a composition can only be a lost shortcut. The relay runs the
/// keys it spells once per composition, ends the composition right away so the jamo cannot
/// surface later in the next focused text box, and swallows the final text. A key the key
/// route already ran is remembered so an IME that composes it anyway does not run it twice.
/// </para>
/// </remarks>
internal sealed class HangulShortcutRelay
{
    private readonly Func<bool> _acceptsShortcuts;
    private readonly Func<Key, ModifierKeys, bool> _execute;
    private readonly Action? _unidentified;
    private readonly Dictionary<Key, int> _echoes = [];
    private readonly Dispatcher _dispatcher;
    private TextComposition? _composition;
    private int _consumed;

    /// <param name="owner">The element whose descendants' compositions are relayed.</param>
    /// <param name="acceptsShortcuts">
    /// False while a text input has focus or the owner ignores keys; the composition is then
    /// left alone.
    /// </param>
    /// <param name="execute">Runs one shortcut and returns whether it matched.</param>
    /// <param name="unidentified">Called when Hangul arrives that maps to no key.</param>
    internal HangulShortcutRelay(UIElement owner, Func<bool> acceptsShortcuts, Func<Key, ModifierKeys, bool> execute, Action? unidentified = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _acceptsShortcuts = acceptsShortcuts ?? throw new ArgumentNullException(nameof(acceptsShortcuts));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _unidentified = unidentified;
        _dispatcher = owner.Dispatcher;
        owner.AddHandler(TextCompositionManager.PreviewTextInputStartEvent, new TextCompositionEventHandler(OnComposition));
        owner.AddHandler(TextCompositionManager.PreviewTextInputUpdateEvent, new TextCompositionEventHandler(OnComposition));
        owner.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(OnComposition));
    }

    /// <summary>Counts the Hangul compositions the relay has taken, so a caller can tell whether one arrived.</summary>
    internal int Activity { get; private set; }

    /// <summary>Records what the key route did with a key press.</summary>
    /// <param name="rawKey">The key as WPF reported it (possibly <see cref="Key.ImeProcessed"/>).</param>
    /// <param name="resolvedKey">The key the shortcut table was matched with.</param>
    /// <param name="handled">Whether a shortcut ran.</param>
    internal void NoteKeyDown(Key rawKey, Key resolvedKey, bool handled)
    {
        if (rawKey == Key.ImeProcessed)
        {
            // An IMM32-style IME composes in the message loop whether or not WPF handled the
            // key; its echo of this letter must not run the shortcut a second time.
            if (handled) _echoes[resolvedKey] = _echoes.GetValueOrDefault(resolvedKey) + 1;
        }
        else if (rawKey is >= Key.A and <= Key.Z)
        {
            // A plain letter means the IME is not composing, so no echo can still be pending.
            _echoes.Clear();
        }
    }

    private void OnComposition(object sender, TextCompositionEventArgs e)
    {
        TextComposition composition = e.TextComposition;
        string text = composition.Text + composition.CompositionText;
        if (!ShortcutInput.ContainsHangul(text) || !_acceptsShortcuts()) return;

        Activity++;
        bool final = e.RoutedEvent == TextCompositionManager.PreviewTextInputEvent;
        IReadOnlyList<KeyStroke>? strokes = ShortcutInput.HangulKeystrokes(text);
        if (!ReferenceEquals(composition, _composition))
        {
            _composition = composition;
            _consumed = 0;
        }

        if (strokes is null)
        {
            if (_consumed == 0) _unidentified?.Invoke();
            _consumed = int.MaxValue;
        }
        else
        {
            for (int i = _consumed; i < strokes.Count; i++) Run(strokes[i]);
            _consumed = Math.Max(_consumed, strokes.Count);
        }

        if (final)
        {
            // The text the composition produced was the shortcut; nothing may receive it.
            e.Handled = true;
            _composition = null;
            _consumed = 0;
        }
        else if (composition.AutoComplete == TextCompositionAutoComplete.Off)
        {
            // End the IME composition after the text services release their lock, so a later
            // key starts a fresh one-jamo composition and nothing is left to migrate into the
            // next focused text box.
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, () => Complete(composition));
        }
    }

    private void Run(KeyStroke stroke)
    {
        if (_echoes.TryGetValue(stroke.Key, out int pending) && pending > 0)
        {
            if (pending == 1) _echoes.Remove(stroke.Key);
            else _echoes[stroke.Key] = pending - 1;
            return;
        }

        _ = _execute(stroke.Key, stroke.Modifiers);
    }

    private static void Complete(TextComposition composition)
    {
        try
        {
            composition.Complete();
        }
        catch (InvalidOperationException)
        {
            // Already finished by the IME (for example by a focus change).
        }
    }
}
