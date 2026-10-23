using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyCapture.App;

internal sealed class ShortcutPresentation : INotifyPropertyChanged
{
    /// <summary>
    /// The type size of every Alt-revealed key caption: the FontSize.Caption token. A
    /// constant, so a caption built before the window's resources load still reads at 12.
    /// </summary>
    internal const double CaptionFontSize = 12;

    internal static ShortcutPresentation Current { get; } = new();
    // The app supplies the explicit always-show preference; absent a setting, Alt reveals hints.
    internal static Func<bool>? ReadEnabled { get; set; }
    public Visibility HintVisibility => ReadEnabled?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal static void Refresh()
    {
        Current.PropertyChanged?.Invoke(Current, new(nameof(HintVisibility)));
        ShortcutRevealMotion.SettleAll();
    }

    // A window owns its Alt state. Inheritance also covers captions added while Alt is held,
    // without changing the saved preference or revealing another window's captions.
    private static readonly DependencyProperty AltRevealedProperty = DependencyProperty.RegisterAttached(
        "AltRevealed", typeof(bool), typeof(ShortcutPresentation),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    private static readonly ConditionalWeakTable<Window, WindowAltReveal> WindowReveals = new();
    private static readonly HintRevealConverter RevealConverter = new();
    private static readonly DependencyProperty RevealedProperty = DependencyProperty.RegisterAttached(
        "Revealed", typeof(bool), typeof(ShortcutPresentation), new PropertyMetadata(false,
            (target, _) => ShortcutRevealMotion.Update((TextBlock)target, animate: true)));

    internal static bool IsRevealed(TextBlock hint) => (bool)hint.GetValue(RevealedProperty);

    internal static void SetAltRevealed(DependencyObject target, bool revealed)
    {
        target.SetValue(AltRevealedProperty, revealed);
    }

    /// <summary>Shares an owner's Alt gesture with a separate surface that does not activate.</summary>
    internal static void FollowOwner(Window surface, Window owner)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(owner);
        WindowAltReveal ownerReveal = WindowReveals.GetValue(owner, static window => new WindowAltReveal(window));
        WindowReveals.GetValue(surface, static window => new WindowAltReveal(window)).Follow(ownerReveal);
    }

    /// <summary>
    /// Every assigned shortcut follows the same caption rule, including key combinations.
    /// </summary>
    internal static bool ShowsCaption(ShortcutDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return true;
    }

    internal static TextBlock Hint(ShortcutScope scope, string command) => Hint(ShortcutCatalog.Get(scope, command).Hint);

    internal static TextBlock Hint(string gesture)
    {
        // 9–11 px captions were too small to read; every revealed key uses the caption size.
        // Regular weight keeps the key below the command's own name, which buttons set semibold.
        var hint = new ShortcutCaption
        {
            Text = gesture,
            FontSize = CaptionFontSize,
            FontWeight = FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Height = CommandButtonPresentation.HintHeight,
            LineHeight = CommandButtonPresentation.HintHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            IsHitTestVisible = false,
            Focusable = false,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        ShortcutRevealMotion.Register(hint);
        var reveal = new MultiBinding { Converter = RevealConverter };
        reveal.Bindings.Add(new Binding(nameof(HintVisibility)) { Source = Current });
        reveal.Bindings.Add(new Binding { Source = hint, Path = new PropertyPath(AltRevealedProperty) });
        hint.SetBinding(RevealedProperty, reveal);
        hint.Loaded += OnHintLoaded;
        return hint;
    }

    private static void OnHintLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow((DependencyObject)sender) is { } window)
            WindowReveals.GetValue(window, static owner => new WindowAltReveal(owner));
    }

    private sealed class ShortcutCaption : TextBlock
    {
        // The owning button already exposes the chord through AcceleratorKey and its tooltip.
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }

    private sealed class HintRevealConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values[0] is Visibility.Visible || values[1] is true;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private const int WmSysCommand = 0x0112;
    private const int ScKeyMenu = 0xF100;
    private static readonly HwndSourceHook KeyboardMenuHook = SuppressBareKeyboardMenu;

    /// <summary>
    /// Alt (or F10) pressed and released on its own asks Windows for the keyboard menu: SC_KEYMENU
    /// without a character. These windows have no menu bar, so Windows would hold the system menu
    /// in a modal loop that swallows the next keys, including the next Alt that reveals captions.
    /// Alt+Space carries its character and still opens the system menu.
    /// </summary>
    internal static bool IsBareKeyboardMenu(int message, IntPtr wParam, IntPtr lParam) =>
        message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScKeyMenu && lParam == IntPtr.Zero;

    private static IntPtr SuppressBareKeyboardMenu(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (IsBareKeyboardMenu(message, wParam, lParam)) handled = true;
        return IntPtr.Zero;
    }

    private sealed class WindowAltReveal
    {
        private readonly Window _window;
        private readonly InputManager _inputManager;
        private readonly ShortcutAltRevealState _state = new();
        private WindowAltReveal? _owner;
        private DispatcherOperation? _dpiRefresh;
        private HwndSource? _source;

        internal WindowAltReveal(Window window)
        {
            _window = window;
            _inputManager = InputManager.Current;
            if (SourceOf(window) is { } source) HookSource(source);
            else window.SourceInitialized += OnSourceInitialized;
            // Observe handled events too, but never consume them: Alt accelerators and WPF
            // menu access keys continue through their existing input handlers.
            window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown), true);
            window.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnKeyUp), true);
            window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseButton), true);
            window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseButton), true);
            window.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouse), true);
            window.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel), true);
            // ContextMenu and Popup route keys through a separate tree without necessarily
            // deactivating their owner. Observe releases at the dispatcher input stage too.
            // The weak subscription cannot keep an abandoned window alive through InputManager.
            WeakEventManager<InputManager, ProcessInputEventArgs>.AddHandler(
                _inputManager, nameof(InputManager.PostProcessInput), OnPostProcessInput);
            window.Deactivated += OnDeactivated;
            window.DpiChanged += OnDpiChanged;
            window.Closed += OnClosed;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            if (SourceOf(_window) is { } source) HookSource(source);
        }

        // The handle exists before the window is shown and gets its root visual.
        private static HwndSource? SourceOf(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            return handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
        }

        private void HookSource(HwndSource source)
        {
            _source = source;
            source.AddHook(KeyboardMenuHook);
        }

        internal void Follow(WindowAltReveal owner)
        {
            for (WindowAltReveal? ancestor = owner; ancestor is not null; ancestor = ancestor._owner)
                if (ReferenceEquals(ancestor, this)) throw new ArgumentException("A shortcut surface cannot follow itself.", nameof(owner));
            if (ReferenceEquals(_owner, owner)) return;
            _owner = owner;
            // Hints may have loaded before their surface received its owner. Discard that
            // surface's old gesture and keep every subsequent update on the owner's state.
            _state.Reset();
            BindingOperations.SetBinding(_window, AltRevealedProperty, new Binding
            {
                Source = owner._window,
                Path = new PropertyPath(AltRevealedProperty),
                Mode = BindingMode.OneWay,
            });
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_owner is { } owner)
            {
                if (key is not (Key.LeftAlt or Key.RightAlt)) owner.Dismiss();
                return;
            }
            Update(_state.UpdateKey(key, isDown: true, isRepeat: e.IsRepeat));
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (_owner is { } owner) owner.OnKeyUp(sender, e);
            else Update(_state.UpdateKey(e.Key == Key.System ? e.SystemKey : e.Key, isDown: false));
        }

        private void Dismiss()
        {
            if (_owner is { } owner) owner.Dismiss();
            else Update(_state.Dismiss());
        }

        private void OnMouseButton(object sender, MouseButtonEventArgs e) => Dismiss();
        private void OnMouse(object sender, MouseEventArgs e) => Dismiss();
        private void OnMouseWheel(object sender, MouseWheelEventArgs e) => Dismiss();

        private void OnPostProcessInput(object? sender, ProcessInputEventArgs e)
        {
            if (_owner is not null) return; // The owner's observer already receives dispatcher input.
            if (e.StagingItem.Input is KeyEventArgs key)
            {
                // Observe releases even after dismissal so a popup cannot leave Alt
                // tracked as down. Global observations never begin another window's reveal.
                if (key.RoutedEvent == Keyboard.PreviewKeyUpEvent || key.RoutedEvent == Keyboard.KeyUpEvent)
                    OnKeyUp(_window, key);
                else if ((key.RoutedEvent == Keyboard.PreviewKeyDownEvent || key.RoutedEvent == Keyboard.KeyDownEvent)
                    && (key.Key == Key.System ? key.SystemKey : key.Key) is not (Key.LeftAlt or Key.RightAlt))
                    Update(_state.Dismiss());
            }
            // Cursor queries also derive from MouseEventArgs and can be generated by a
            // layout change while the pointer is stationary. Only actual mouse input dismisses.
            else if (e.StagingItem.Input is MouseEventArgs mouse
                && (mouse.RoutedEvent == Mouse.PreviewMouseDownEvent || mouse.RoutedEvent == Mouse.MouseDownEvent
                    || mouse.RoutedEvent == Mouse.PreviewMouseUpEvent || mouse.RoutedEvent == Mouse.MouseUpEvent
                    || mouse.RoutedEvent == Mouse.PreviewMouseMoveEvent || mouse.RoutedEvent == Mouse.MouseMoveEvent
                    || mouse.RoutedEvent == Mouse.PreviewMouseWheelEvent || mouse.RoutedEvent == Mouse.MouseWheelEvent))
                Update(_state.Dismiss());
        }

        private void OnDeactivated(object? sender, EventArgs e)
        {
            if (_owner is null) Update(_state.Reset());
        }

        private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
        {
            if (_dpiRefresh is { Status: DispatcherOperationStatus.Pending }) return;
            // The window's notification can precede its children's DPI update. One queued
            // pass measures the final subtree scale and settles the visual state.
            _dpiRefresh = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _dpiRefresh = null;
                RefreshCommandWidths(_window);
            }));
        }

        private static void RefreshCommandWidths(DependencyObject root)
        {
            if (root is ShortcutCaption hint)
                ShortcutRevealMotion.Update(hint, animate: false);
            // Corner tool-rail hints use their own 32-DIP face and must not be wrapped.
            if (root is ButtonBase { Content: Grid { Name: "CommandButtonFace" } } button)
                CommandButtonPresentation.RefreshHint(button);
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                RefreshCommandWidths(VisualTreeHelper.GetChild(root, index));
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _dpiRefresh?.Abort();
            _dpiRefresh = null;
            if (_owner is null) Update(_state.Reset());
            else
            {
                _owner = null;
                BindingOperations.ClearBinding(_window, AltRevealedProperty);
                SetAltRevealed(_window, false);
            }
            _window.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKeyDown));
            _window.RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnKeyUp));
            _window.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseButton));
            _window.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseButton));
            _window.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnMouse));
            _window.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel));
            WeakEventManager<InputManager, ProcessInputEventArgs>.RemoveHandler(
                _inputManager, nameof(InputManager.PostProcessInput), OnPostProcessInput);
            _window.Deactivated -= OnDeactivated;
            _window.DpiChanged -= OnDpiChanged;
            _window.Closed -= OnClosed;
            _window.SourceInitialized -= OnSourceInitialized;
            _source?.RemoveHook(KeyboardMenuHook);
            _source = null;
            WindowReveals.Remove(_window);
        }

        private void Update(bool changed)
        {
            if (!changed) return;
            SetAltRevealed(_window, _state.IsRevealed);
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
            var grid = new Grid { Width = ShortcutRevealMotion.CornerFaceSize, Height = ShortcutRevealMotion.CornerFaceSize };
            if (content is FrameworkElement glyph) { glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center; }
            grid.Children.Add(content);
            // The letter sits in the corner; its line box hangs below the glyph, not over it.
            hint.HorizontalAlignment = HorizontalAlignment.Right; hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Height = 12;
            hint.LineHeight = 12;
            hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            grid.Children.Add(hint); button.Content = grid;
            if (content is FrameworkElement primary) ShortcutRevealMotion.Configure(hint, primary, corner: true);
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
    private bool _dismissed;

    internal bool IsRevealed => (_leftAlt || _rightAlt) && !_dismissed;

    internal bool UpdateKey(Key key, bool isDown, bool isRepeat = false)
    {
        if (key is not (Key.LeftAlt or Key.RightAlt)) return Dismiss();
        bool before = IsRevealed;
        // A repeat after activation was lost is still part of an old gesture. When
        // dismissed, retain both down flags until their releases; repeats cannot reveal.
        if (isDown && !_leftAlt && !_rightAlt) _dismissed = isRepeat;
        if (key == Key.LeftAlt) _leftAlt = isDown;
        else if (key == Key.RightAlt) _rightAlt = isDown;
        if (!_leftAlt && !_rightAlt) _dismissed = false;
        return before != IsRevealed;
    }

    internal bool Dismiss()
    {
        bool before = IsRevealed;
        _dismissed = true;
        return before;
    }

    internal bool Reset()
    {
        bool before = IsRevealed;
        _leftAlt = _rightAlt = false;
        _dismissed = false;
        return before;
    }
}
