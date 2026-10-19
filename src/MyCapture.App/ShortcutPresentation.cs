using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace MyCapture.App;

internal sealed class ShortcutPresentation : INotifyPropertyChanged
{
    /// <summary>
    /// The type size of every always-visible key caption: the FontSize.Caption token. A
    /// constant, so a caption built before the window's resources load still reads at 12.
    /// </summary>
    internal const double CaptionFontSize = 12;

    internal static ShortcutPresentation Current { get; } = new();
    internal static Func<bool>? ReadEnabled { get; set; }
    public Visibility HintVisibility => ReadEnabled?.Invoke() == false ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal static void Refresh() => Current.PropertyChanged?.Invoke(Current, new(nameof(HintVisibility)));

    // A window owns its Alt state. Inheritance also covers captions added while Alt is held,
    // without changing the saved preference or revealing another window's captions.
    private static readonly DependencyProperty AltRevealedProperty = DependencyProperty.RegisterAttached(
        "AltRevealed", typeof(bool), typeof(ShortcutPresentation),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    private static readonly ConditionalWeakTable<Window, WindowAltReveal> WindowReveals = new();
    private static readonly HintVisibilityConverter VisibilityConverter = new();

    /// <summary>
    /// Every assigned shortcut follows the same caption rule, including key combinations.
    /// </summary>
    internal static bool ShowsCaption(ShortcutDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return true;
    }

    internal static TextBlock Hint(ShortcutScope scope, string command)
    {
        // 9–11 px captions were too small to read; every always-visible key uses the caption size.
        // Regular weight keeps the key below the command's own name, which buttons set semibold.
        var hint = new TextBlock
        {
            Text = ShortcutCatalog.Get(scope, command).Hint,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Height = CommandButtonPresentation.HintHeight,
            LineHeight = CommandButtonPresentation.HintHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var visibility = new MultiBinding { Converter = VisibilityConverter };
        visibility.Bindings.Add(new Binding(nameof(HintVisibility)) { Source = Current });
        visibility.Bindings.Add(new Binding { Source = hint, Path = new PropertyPath(AltRevealedProperty) });
        hint.SetBinding(UIElement.VisibilityProperty, visibility);
        hint.Loaded += OnHintLoaded;
        return hint;
    }

    private static void OnHintLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow((DependencyObject)sender) is { } window)
            WindowReveals.GetValue(window, static owner => new WindowAltReveal(owner));
    }

    private sealed class HintVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values[0] is Visibility.Visible || values[1] is true ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class WindowAltReveal
    {
        private readonly Window _window;
        private readonly InputManager _inputManager;
        private readonly ShortcutAltRevealState _state = new();

        internal WindowAltReveal(Window window)
        {
            _window = window;
            _inputManager = InputManager.Current;
            // Observe handled events too, but never consume them: Alt accelerators and WPF
            // menu access keys continue through their existing input handlers.
            window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            window.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnKeyUp), true);
            // ContextMenu and Popup route keys through a separate tree without necessarily
            // deactivating their owner. Observe releases at the dispatcher input stage too.
            // The weak subscription cannot keep an abandoned window alive through InputManager.
            WeakEventManager<InputManager, ProcessInputEventArgs>.AddHandler(
                _inputManager, nameof(InputManager.PostProcessInput), OnPostProcessInput);
            window.Deactivated += OnDeactivated;
            window.Closed += OnClosed;
        }

        private void OnKeyDown(object sender, KeyEventArgs e) =>
            Update(_state.UpdateKey(e.Key == Key.System ? e.SystemKey : e.Key, isDown: true));

        private void OnKeyUp(object sender, KeyEventArgs e) =>
            Update(_state.UpdateKey(e.Key == Key.System ? e.SystemKey : e.Key, isDown: false));

        private void OnPostProcessInput(object? sender, ProcessInputEventArgs e)
        {
            if (_state.IsRevealed && e.StagingItem.Input is KeyEventArgs key
                && (key.RoutedEvent == Keyboard.PreviewKeyUpEvent || key.RoutedEvent == Keyboard.KeyUpEvent))
                OnKeyUp(_window, key);
        }

        private void OnDeactivated(object? sender, EventArgs e) => Update(_state.Reset());

        private void OnClosed(object? sender, EventArgs e)
        {
            Update(_state.Reset());
            _window.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
            _window.RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnKeyUp));
            WeakEventManager<InputManager, ProcessInputEventArgs>.RemoveHandler(
                _inputManager, nameof(InputManager.PostProcessInput), OnPostProcessInput);
            _window.Deactivated -= OnDeactivated;
            _window.Closed -= OnClosed;
            WindowReveals.Remove(_window);
        }

        private void Update(bool changed)
        {
            if (!changed) return;
            _window.SetValue(AltRevealedProperty, _state.IsRevealed);
            // Existing command rows refit when this notification fires. Notify after the
            // inherited flag changes so they measure the revealed captions.
            Refresh();
        }
    }

    internal static void Attach(ButtonBase button, ShortcutScope scope, string command, bool corner = false)
    {
        object? previous = button.Content;
        var hint = Hint(scope, command);
        hint.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        // A disabled button keeps its tier's shape: the shared template only fades it by
        // Button.DisabledOpacity. Adding a sunken fill and a border here used to turn a
        // borderless tool button into a bordered one the moment it was disabled.
        if (corner)
        {
            button.Content = null;
            UIElement content = previous as UIElement ?? new TextBlock { Text = previous?.ToString() ?? string.Empty };
            var grid = new Grid { Width = 32, Height = 32 };
            if (content is FrameworkElement glyph) { glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Top; }
            grid.Children.Add(content);
            // The letter sits in the corner; its line box hangs below the glyph, not over it.
            hint.HorizontalAlignment = HorizontalAlignment.Right; hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Height = 12;
            hint.LineHeight = 12;
            hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            grid.Children.Add(hint); button.Content = grid;
        }
        else
        {
            CommandButtonPresentation.Apply(button);
            CommandButtonPresentation.ReserveHint(button, hint);
        }

        Describe(button, scope, command, previous as string);
    }

    /// <summary>
    /// Gives a command its key without a caption: the tooltip names the key after the
    /// command, and UI Automation reads it as the accelerator key.
    /// </summary>
    internal static void Describe(ButtonBase button, ShortcutScope scope, string command, string? fallbackLabel = null)
    {
        string hint = ShortcutCatalog.Get(scope, command).Hint;
        string label = button.ToolTip as string ?? AutomationProperties.GetName(button);
        if (string.IsNullOrEmpty(label)) label = fallbackLabel ?? button.Content as string ?? command;
        button.ToolTip = NamesKey(label, hint) ? label : $"{label} ({hint})";
        AutomationProperties.SetAcceleratorKey(button, hint);
    }

    /// <summary>A label that already ends with its key in parentheses (Undo (Ctrl+Z)) is kept.</summary>
    private static bool NamesKey(string label, string hint)
    {
        if (!label.EndsWith(')')) return false;
        int open = label.LastIndexOf('(');
        if (open < 0) return false;
        // Older labels spell the minus key with a hyphen and Delete in full.
        string named = label[open..].Replace('−', '-');
        return named.Contains(hint.Replace('−', '-'), StringComparison.Ordinal);
    }
}

/// <summary>Tracks both Alt keys independently; losing activation clears a missed release.</summary>
internal sealed class ShortcutAltRevealState
{
    private bool _leftAlt;
    private bool _rightAlt;

    internal bool IsRevealed => _leftAlt || _rightAlt;

    internal bool UpdateKey(Key key, bool isDown)
    {
        bool before = IsRevealed;
        if (key == Key.LeftAlt) _leftAlt = isDown;
        else if (key == Key.RightAlt) _rightAlt = isDown;
        return before != IsRevealed;
    }

    internal bool Reset()
    {
        bool before = IsRevealed;
        _leftAlt = _rightAlt = false;
        return before;
    }
}
