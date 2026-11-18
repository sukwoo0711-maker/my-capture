using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyCapture.App.Themes;

/// <summary>
/// Keeps keyboard focus separate from its cue. Pointer input hides the cue without moving focus;
/// keyboard input makes it available to every control, including windows and popups opened by it.
/// </summary>
public static class KeyboardFocusCue
{
    private static readonly DependencyPropertyKey IsVisibleKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsVisible", typeof(bool), typeof(KeyboardFocusCue),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits
            | FrameworkPropertyMetadataOptions.AffectsRender, OnVisibilityChanged));

    public static readonly DependencyProperty IsVisibleProperty = IsVisibleKey.DependencyProperty;

    // A direct event lets a control that owns DrawingVisual children redraw them when the inherited
    // cue changes. Handlers belong to the control, not to a static collection of open windows.
    private static readonly RoutedEvent VisibilityChangedEvent = EventManager.RegisterRoutedEvent(
        "VisibilityChanged", RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(KeyboardFocusCue));
    private static readonly ConditionalWeakTable<HwndSource, object> HookedSources = new();

    [ThreadStatic]
    private static bool s_keyboardInput;

    static KeyboardFocusCue()
    {
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnKeyDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPointerDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(OnTouchDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), Stylus.PreviewStylusDownEvent,
            new StylusDownEventHandler(OnStylusDown), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotFocus), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded), handledEventsToo: true);
    }

    public static bool GetIsVisible(DependencyObject element) => (bool)element.GetValue(IsVisibleProperty);

    internal static void NotifyKeyboardInput(DependencyObject? origin = null) => SetInputMode(true, origin);

    internal static void NotifyPointerInput(DependencyObject? origin = null) => SetInputMode(false, origin);

    internal static void AddVisibilityChangedHandler(UIElement element, RoutedEventHandler handler) =>
        element.AddHandler(VisibilityChangedEvent, handler);

    internal static void RemoveVisibilityChangedHandler(UIElement element, RoutedEventHandler handler) =>
        element.RemoveHandler(VisibilityChangedEvent, handler);

    private static void OnKeyDown(object sender, KeyEventArgs e) => NotifyKeyboardInput((DependencyObject)sender);
    private static void OnPointerDown(object sender, MouseButtonEventArgs e) => NotifyPointerInput((DependencyObject)sender);
    private static void OnTouchDown(object? sender, TouchEventArgs e) => NotifyPointerInput(sender as DependencyObject);
    private static void OnStylusDown(object sender, StylusDownEventArgs e) => NotifyPointerInput((DependencyObject)sender);

    private static void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SynchronizeRoot((DependencyObject)sender);

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        SynchronizeRoot((DependencyObject)sender);
        if (PresentationSource.FromVisual((Window)sender) is HwndSource source)
        {
            // The static hook retains no window. HwndSource releases it on disposal and the table's
            // keys are weak, so closing a window does not leave it in an application-wide registry.
            HookedSources.GetValue(source, static value =>
            {
                value.AddHook(OnNativeWindowInput);
                return new object();
            });
        }
    }

    private static void SetInputMode(bool keyboard, DependencyObject? origin)
    {
        bool changed = s_keyboardInput != keyboard;
        s_keyboardInput = keyboard;
        // Popup roots have their own presentation source and do not inherit from their owner HWND.
        // Only this dispatcher's sources are read or updated; no cross-thread window references persist.
        if (changed)
        {
            foreach (PresentationSource source in PresentationSource.CurrentSources)
            {
                if (source.Dispatcher.CheckAccess() && source.RootVisual is DependencyObject root)
                    root.SetValue(IsVisibleKey, keyboard);
            }
        }

        if (origin is not null) SynchronizeRoot(origin);
    }

    private static void SynchronizeRoot(DependencyObject element)
    {
        DependencyObject root = element;
        while (Parent(root) is { } parent) root = parent;
        if (GetIsVisible(root) != s_keyboardInput) root.SetValue(IsVisibleKey, s_keyboardInput);
    }

    private static DependencyObject? Parent(DependencyObject element) => element is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private static void OnVisibilityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement element) element.RaiseEvent(new RoutedEventArgs(VisibilityChangedEvent, element));
    }

    private static nint OnNativeWindowInput(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // System captions are outside the WPF element tree. A caption press still ends keyboard
        // cues, just like a client-area press; the native message and focus behavior are untouched.
        if (message is 0x00A1 or 0x00A4 or 0x00A7 or 0x00AB) NotifyPointerInput();
        return 0;
    }
}
