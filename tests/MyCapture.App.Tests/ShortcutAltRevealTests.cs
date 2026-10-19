using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ShortcutAltRevealTests
{
    [Fact]
    public void BothAltKeysAndAutoRepeatRevealUntilTheLastAltIsReleased()
    {
        var state = new ShortcutAltRevealState();
        Assert.False(state.IsRevealed);
        Assert.True(state.UpdateKey(Key.LeftAlt, isDown: true));
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: true));
        Assert.False(state.UpdateKey(Key.RightAlt, isDown: true));
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: false));
        Assert.True(state.IsRevealed);
        Assert.True(state.UpdateKey(Key.RightAlt, isDown: false));
        Assert.False(state.IsRevealed);
    }

    [Fact]
    public void OtherKeysDoNotToggleOrDismissAltReveal()
    {
        var state = new ShortcutAltRevealState();
        Assert.False(state.UpdateKey(Key.F10, isDown: true));
        Assert.False(state.IsRevealed);
        state.UpdateKey(Key.RightAlt, isDown: true);
        Assert.False(state.UpdateKey(Key.A, isDown: true));
        Assert.False(state.UpdateKey(Key.A, isDown: false));
        Assert.True(state.IsRevealed);
    }

    [Fact]
    public void DeactivationClearsBothKeysAndToleratesDelayedReleases()
    {
        var state = new ShortcutAltRevealState();
        state.UpdateKey(Key.LeftAlt, isDown: true);
        state.UpdateKey(Key.RightAlt, isDown: true);
        Assert.True(state.Reset());
        Assert.False(state.IsRevealed);
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: false));
        Assert.False(state.UpdateKey(Key.RightAlt, isDown: false));
        Assert.False(state.Reset());
        Assert.True(state.UpdateKey(Key.LeftAlt, isDown: true));
    }

    [Fact]
    public void LoadedCaptionsRevealOnlyInTheirWindowAndDoNotHandleAccelerators() => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var first = new HintWindow();
        var second = new HintWindow();
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            TextBlock firstCaption = AddCaption(first);
            TextBlock secondCaption = AddCaption(second);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, secondCaption.Visibility);

            // WPF reports Alt through SystemKey. Synthetic routed events do not send input
            // to the desktop, and neither test window is ever shown or activated.
            KeyEventArgs down = RaiseKey(first, Key.LeftAlt, isDown: true, system: true);
            Assert.False(down.Handled);
            Assert.Equal(Visibility.Visible, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, secondCaption.Visibility);
            Assert.False(ShortcutPresentation.ReadEnabled());
            Assert.False(RaiseKey(first, Key.A, isDown: true, system: true).Handled);
            Assert.Equal(Visibility.Visible, firstCaption.Visibility);

            TextBlock addedWhileHeld = AddCaption(first);
            Assert.Equal(Visibility.Visible, addedWhileHeld.Visibility);
            Assert.False(RaiseKey(first, Key.LeftAlt, isDown: false, system: true).Handled);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, addedWhileHeld.Visibility);
        }
        finally
        {
            first.Close();
            second.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void HandledKeyEventsAndWindowDeactivationCannotLeaveCaptionsStuck() => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            TextBlock caption = AddCaption(window);
            Assert.True(RaiseKey(window, Key.RightAlt, isDown: true, handled: true).Handled);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.True(RaiseKey(window, Key.RightAlt, isDown: false, handled: true).Handled);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            RaiseKey(window, Key.RightAlt, isDown: true);
            window.DeactivateForTest();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.RightAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            window.Close();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
        }
        finally
        {
            window.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void PreferenceChangesAndAltReleasePreserveTheExplicitChoice() => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        bool enabled = true;
        try
        {
            ShortcutPresentation.ReadEnabled = () => enabled;
            TextBlock caption = AddCaption(window);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            enabled = false;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Visible, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            enabled = true;
            ShortcutPresentation.Refresh();
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            window.DeactivateForTest();
            Assert.Equal(Visibility.Visible, caption.Visibility);
        }
        finally
        {
            window.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void PopupKeyReleasesClearRevealedOwnersWithoutRevealingOtherWindows() => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var first = new HintWindow();
        var second = new HintWindow();
        var untouched = new HintWindow();
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            TextBlock firstCaption = AddCaption(first);
            TextBlock secondCaption = AddCaption(second);
            TextBlock untouchedCaption = AddCaption(untouched);
            var menu = new ContextMenu { PlacementTarget = first };
            var popupTarget = new MenuItem { Header = "Command" };
            menu.Items.Add(popupTarget);
            Assert.Null(Window.GetWindow(popupTarget));

            // Exercise the real WPF staging observer with a synthetic source in a separate
            // popup tree. The menu remains closed; ProcessInput does not send desktop input.
            KeyEventArgs popupDown = CreateKey(Key.LeftAlt, Keyboard.KeyDownEvent, system: true);
            popupDown.Source = popupTarget;
            InputManager.Current.ProcessInput(popupDown);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, secondCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, untouchedCaption.Visibility);

            RaiseKey(first, Key.LeftAlt, isDown: true);
            RaiseKey(first, Key.RightAlt, isDown: true);
            RaiseKey(second, Key.LeftAlt, isDown: true);
            KeyEventArgs leftUp = CreateKey(Key.LeftAlt, Keyboard.KeyUpEvent, system: true, handled: true);
            leftUp.Source = popupTarget;
            InputManager.Current.ProcessInput(leftUp);
            Assert.True(leftUp.Handled);
            Assert.Equal(Visibility.Visible, firstCaption.Visibility); // Right Alt is still held.
            Assert.Equal(Visibility.Collapsed, secondCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, untouchedCaption.Visibility);

            KeyEventArgs rightUp = CreateKey(Key.RightAlt, Keyboard.KeyUpEvent, system: true);
            rightUp.Source = popupTarget;
            InputManager.Current.ProcessInput(rightUp);
            Assert.False(rightUp.Handled);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, secondCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, untouchedCaption.Visibility);
        }
        finally
        {
            first.Close();
            second.Close();
            untouched.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    private static TextBlock AddCaption(HintWindow window)
    {
        var button = new Button { Content = "Copy" };
        ShortcutPresentation.Attach(button, ShortcutScope.QuickAccess, "Copy");
        window.Commands.Children.Add(button);
        TextBlock caption = Assert.IsType<Grid>(button.Content).Children.OfType<TextBlock>().Last();
        caption.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, caption));
        return caption;
    }

    private static KeyEventArgs RaiseKey(HintWindow window, Key key, bool isDown, bool system = false, bool handled = false)
    {
        KeyEventArgs args = CreateKey(key, isDown ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent, system, handled);
        window.RaiseEvent(args);
        return args;
    }

    private static KeyEventArgs CreateKey(Key key, RoutedEvent routedEvent, bool system = false, bool handled = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, key)
        {
            RoutedEvent = routedEvent,
            Handled = handled,
        };
        if (system)
        {
            typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, null);
            Assert.Equal(Key.System, args.Key);
            Assert.Equal(key, args.SystemKey);
        }
        return args;
    }

    private sealed class HintWindow : Window
    {
        internal StackPanel Commands { get; } = new();
        internal HintWindow() => Content = Commands;
        internal void DeactivateForTest() => OnDeactivated(EventArgs.Empty);
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
