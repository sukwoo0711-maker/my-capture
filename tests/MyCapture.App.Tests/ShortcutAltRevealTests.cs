using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Settings;
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
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: true, isRepeat: true));
        Assert.False(state.UpdateKey(Key.RightAlt, isDown: true));
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: false));
        Assert.True(state.IsRevealed);
        Assert.True(state.UpdateKey(Key.RightAlt, isDown: false));
        Assert.False(state.IsRevealed);
    }

    [Theory]
    [InlineData(Key.A, true)]
    [InlineData(Key.A, false)]
    [InlineData(Key.Escape, true)]
    [InlineData(Key.F10, true)]
    public void OtherKeysDismissUntilBothAltKeysHaveBeenReleased(Key key, bool isDown)
    {
        var state = new ShortcutAltRevealState();
        Assert.False(state.UpdateKey(Key.F10, isDown: true));
        Assert.False(state.IsRevealed);
        state.UpdateKey(Key.RightAlt, isDown: true);
        Assert.True(state.UpdateKey(key, isDown));
        Assert.False(state.IsRevealed);
        Assert.False(state.UpdateKey(Key.RightAlt, isDown: true, isRepeat: true));
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: true));
        Assert.False(state.UpdateKey(Key.RightAlt, isDown: false));
        Assert.False(state.IsRevealed);
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: false));
        Assert.True(state.UpdateKey(Key.LeftAlt, isDown: true));
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
        Assert.False(state.UpdateKey(Key.LeftAlt, isDown: true, isRepeat: true));
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
            TextBlock addedWhileHeld = AddCaption(first);
            Assert.Equal(Visibility.Visible, addedWhileHeld.Visibility);
            Assert.False(RaiseKey(first, Key.A, isDown: true, system: true).Handled);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, addedWhileHeld.Visibility);
            Assert.False(RaiseKey(first, Key.LeftAlt, isDown: true, system: true, repeat: true).Handled);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.False(RaiseKey(first, Key.LeftAlt, isDown: false, system: true).Handled);
            Assert.Equal(Visibility.Collapsed, firstCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, addedWhileHeld.Visibility);
            RaiseKey(first, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, firstCaption.Visibility);
            Assert.Equal(Visibility.Visible, addedWhileHeld.Visibility);
            Assert.Equal(Visibility.Collapsed, secondCaption.Visibility);
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
            RaiseKey(window, Key.RightAlt, isDown: true, repeat: true);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.RightAlt, isDown: false);
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
    public void AlwaysShowPreferenceRefreshesOpenWindowsAndCombinesWithTheCurrentAltGesture() => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        var other = new HintWindow();
        var settings = new AppSettings();
        try
        {
            ShortcutPresentation.ReadEnabled = () => settings.General.AlwaysShowShortcutHints;
            TextBlock caption = AddCaption(window);
            TextBlock otherCaption = AddCaption(other);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, otherCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, ShortcutPresentation.Current.HintVisibility);

            settings.General.AlwaysShowShortcutHints = true;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.Equal(Visibility.Visible, otherCaption.Visibility);
            Assert.Equal(Visibility.Visible, ShortcutPresentation.Current.HintVisibility);
            Assert.Equal(Visibility.Visible, AddCaption(other).Visibility);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            settings.General.AlwaysShowShortcutHints = false;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, otherCaption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            settings.General.AlwaysShowShortcutHints = true;
            ShortcutPresentation.Refresh();
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            RaiseKey(window, Key.C, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            window.RaiseEvent(CreateMouse("down", handled: true));
            Assert.Equal(Visibility.Visible, caption.Visibility);
            window.DeactivateForTest();
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.Equal(Visibility.Visible, otherCaption.Visibility);

            ShortcutPresentation.ReadEnabled = null;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, otherCaption.Visibility);
        }
        finally
        {
            window.Close();
            other.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPreferenceDoesNotRevealHintsOrOverrideTheNewPreference(bool legacyEnabled) => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        var settings = new AppSettings { General = { ShowShortcutHints = legacyEnabled } };
        try
        {
            ShortcutPresentation.ReadEnabled = () => settings.General.AlwaysShowShortcutHints;
            TextBlock caption = AddCaption(window);
            Assert.False(settings.General.AlwaysShowShortcutHints);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            settings.General.ShowShortcutHints = !legacyEnabled;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            settings.General.AlwaysShowShortcutHints = true;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Visible, caption.Visibility);
            settings.General.ShowShortcutHints = legacyEnabled;
            ShortcutPresentation.Refresh();
            Assert.Equal(Visibility.Visible, caption.Visibility);
        }
        finally
        {
            window.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Theory]
    [InlineData("down", false)]
    [InlineData("down", true)]
    [InlineData("up", false)]
    [InlineData("up", true)]
    [InlineData("move", false)]
    [InlineData("move", true)]
    [InlineData("wheel", false)]
    [InlineData("wheel", true)]
    public void MouseInputDismissesWithoutConsumingInputOrRevealingOnRepeat(string input, bool handled) => StaTestHost.Run(() =>
    {
        var window = new HintWindow();
        try
        {
            TextBlock caption = AddCaption(window);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            MouseEventArgs mouse = CreateMouse(input, handled);
            window.RaiseEvent(mouse);
            Assert.Equal(handled, mouse.Handled);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: true, repeat: true);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LayoutCursorQueriesDoNotDismissTheCurrentAltGesture() => StaTestHost.Run(() =>
    {
        var window = new HintWindow();
        try
        {
            TextBlock caption = AddCaption(window);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = Mouse.QueryCursorEvent,
                Source = window,
                Handled = true,
            };
            InputManager.Current.ProcessInput(query);
            Assert.True(query.Handled);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonActivatingSurfaceFollowsCaptionlessOwnerEvenWhenItsHintsAlreadyLoaded(bool loadBeforeFollowing) => StaTestHost.Run(() =>
    {
        var owner = new HintWindow();
        var surface = new HintWindow { ShowActivated = false, Focusable = false };
        var unrelated = new HintWindow();
        try
        {
            TextBlock unrelatedCaption = AddCaption(unrelated);
            TextBlock? caption = loadBeforeFollowing ? AddCaption(surface) : null;
            if (caption is not null)
            {
                RaiseKey(surface, Key.LeftAlt, isDown: true);
                Assert.Equal(Visibility.Visible, caption.Visibility);
            }
            Assert.Empty(owner.Commands.Children);
            ShortcutPresentation.FollowOwner(surface, owner);
            ShortcutPresentation.FollowOwner(surface, owner); // Repeated Loaded notifications are harmless.
            caption ??= AddCaption(surface);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.False(RaiseKey(surface, Key.RightAlt, isDown: true).Handled);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);

            Assert.True(RaiseKey(owner, Key.LeftAlt, isDown: true, system: true, handled: true).Handled);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            TextBlock addedWhileHeld = AddCaption(surface);
            Assert.Equal(Visibility.Visible, addedWhileHeld.Visibility);
            Assert.Equal(Visibility.Collapsed, unrelatedCaption.Visibility);
            surface.DeactivateForTest();
            Assert.Equal(Visibility.Visible, caption.Visibility);
            RaiseKey(owner, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, addedWhileHeld.Visibility);

            RaiseKey(owner, Key.RightAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            owner.DeactivateForTest();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, unrelatedCaption.Visibility);
        }
        finally { surface.Close(); owner.Close(); unrelated.Close(); }
    });

    [Theory]
    [InlineData("down")]
    [InlineData("up")]
    [InlineData("move")]
    [InlineData("wheel")]
    public void SurfaceMouseInputDismissesItsOwnerButClosingSurfaceLeavesTheOwnerGestureIntact(string input) => StaTestHost.Run(() =>
    {
        var owner = new HintWindow();
        var surface = new HintWindow { ShowActivated = false };
        try
        {
            TextBlock ownerCaption = AddCaption(owner);
            ShortcutPresentation.FollowOwner(surface, owner);
            TextBlock surfaceCaption = AddCaption(surface);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, ownerCaption.Visibility);
            Assert.Equal(Visibility.Visible, surfaceCaption.Visibility);
            MouseEventArgs mouse = CreateMouse(input, handled: true);
            surface.RaiseEvent(mouse);
            Assert.True(mouse.Handled);
            Assert.Equal(Visibility.Collapsed, ownerCaption.Visibility);
            Assert.Equal(Visibility.Collapsed, surfaceCaption.Visibility);
            RaiseKey(owner, Key.LeftAlt, isDown: true, repeat: true);
            Assert.Equal(Visibility.Collapsed, surfaceCaption.Visibility);
            RaiseKey(owner, Key.LeftAlt, isDown: false);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, surfaceCaption.Visibility);

            surface.Close();
            Assert.Equal(Visibility.Collapsed, surfaceCaption.Visibility);
            Assert.Equal(Visibility.Visible, ownerCaption.Visibility);
            surface.RaiseEvent(CreateMouse(input, handled: false));
            Assert.Equal(Visibility.Visible, ownerCaption.Visibility);
            RaiseKey(owner, Key.LeftAlt, isDown: false);
            Assert.Equal(Visibility.Collapsed, ownerCaption.Visibility);
        }
        finally { surface.Close(); owner.Close(); }
    });

    [Fact]
    public void ClosingOwnerClearsItsFollowingSurfaceAndSurfaceCannotStartAnotherGesture() => StaTestHost.Run(() =>
    {
        var owner = new HintWindow();
        var surface = new HintWindow { ShowActivated = false };
        try
        {
            ShortcutPresentation.FollowOwner(surface, owner);
            TextBlock caption = AddCaption(surface);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            owner.Close();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            RaiseKey(surface, Key.RightAlt, isDown: true);
            RaiseKey(owner, Key.RightAlt, isDown: true);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
        }
        finally { surface.Close(); owner.Close(); }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PopupInputDismissesAndItsLaterAltReleaseAllowsTheNextGesture(bool mouseInput) => StaTestHost.Run(() =>
    {
        var owner = new HintWindow();
        var untouched = new HintWindow();
        try
        {
            TextBlock caption = AddCaption(owner);
            TextBlock otherCaption = AddCaption(untouched);
            var popupTarget = new MenuItem { Header = "Command" };
            var menu = new ContextMenu { PlacementTarget = owner };
            menu.Items.Add(popupTarget);
            Assert.Null(Window.GetWindow(popupTarget));
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);

            InputEventArgs input = mouseInput
                ? CreateMouse("down", handled: true)
                : CreateKey(Key.C, Keyboard.KeyDownEvent, system: true, handled: true);
            input.Source = popupTarget;
            InputManager.Current.ProcessInput(input);
            Assert.True(input.Handled);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, otherCaption.Visibility);
            RaiseKey(owner, Key.LeftAlt, isDown: true, repeat: true);
            Assert.Equal(Visibility.Collapsed, caption.Visibility);

            KeyEventArgs release = CreateKey(Key.LeftAlt, Keyboard.KeyUpEvent, system: true);
            release.Source = popupTarget;
            InputManager.Current.ProcessInput(release);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.Equal(Visibility.Collapsed, otherCaption.Visibility);
        }
        finally { owner.Close(); untouched.Close(); }
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

    private static KeyEventArgs RaiseKey(HintWindow window, Key key, bool isDown, bool system = false, bool handled = false, bool repeat = false)
    {
        KeyEventArgs args = CreateKey(key, isDown ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent, system, handled, repeat);
        window.RaiseEvent(args);
        return args;
    }

    private static KeyEventArgs CreateKey(Key key, RoutedEvent routedEvent, bool system = false, bool handled = false, bool repeat = false)
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
        if (repeat)
        {
            typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
            Assert.True(args.IsRepeat);
        }
        return args;
    }

    private static MouseEventArgs CreateMouse(string input, bool handled)
    {
        MouseEventArgs args = input switch
        {
            "down" => new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent },
            "up" => new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent },
            "move" => new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.PreviewMouseMoveEvent },
            "wheel" => new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent },
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
        args.Handled = handled;
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
