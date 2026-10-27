using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

    [Theory]
    [InlineData(0x0112, 0xF100, 0, true)]     // SC_KEYMENU from Alt or F10 alone
    [InlineData(0x0112, 0xF103, 0, true)]     // Windows keeps four flag bits below the command
    [InlineData(0x0112, 0xF100, 0x20, false)] // Alt+Space opens the system menu
    [InlineData(0x0112, 0xF100, 0x51, false)] // Alt+Q, an access key
    [InlineData(0x0112, 0xF060, 0, false)]    // SC_CLOSE (Alt+F4)
    [InlineData(0x0105, 0x12, 0, false)]      // WM_SYSKEYUP for Alt itself
    public void OnlyABareKeyboardMenuRequestIsSuppressed(int message, int wParam, int lParam, bool suppressed) =>
        Assert.Equal(suppressed, ShortcutPresentation.IsBareKeyboardMenu(message, wParam, lParam));

    [Fact]
    public void BareAltNeverOpensTheSystemMenuLoopThatSwallowsTheNextKeys() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            // A handle without showing the window, so nothing reaches the desktop. HwndSource
            // calls the newest hook first, so this probe, added before the captions load, sees
            // only the system commands the product passes on toward DefWindowProc, and stops
            // them there so no menu can open.
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            var reached = new List<(long Command, long Character)>();
            HwndSource.FromHwnd(handle).AddHook((IntPtr _, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != 0x0112) return IntPtr.Zero;
                reached.Add((wParam.ToInt64(), lParam.ToInt64()));
                handled = true;
                return IntPtr.Zero;
            });
            AddCaption(window);

            SendMessage(handle, 0x0112, 0xF100, IntPtr.Zero);
            SendMessage(handle, 0x0112, 0xF100, ' ');
            Assert.Equal(new[] { (0xF100L, (long)' ') }, reached);
        }
        finally
        {
            window.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void LoadedCaptionsRevealOnlyInTheirWindowAndDoNotHandleAccelerators() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var first = new HintWindow();
        var second = new HintWindow();
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            TextBlock firstCaption = AddCaption(first);
            TextBlock secondCaption = AddCaption(second);
            ShortcutMotionTestScope.AssertState(firstCaption, false);
            ShortcutMotionTestScope.AssertState(secondCaption, false);

            // WPF reports Alt through SystemKey. Synthetic routed events do not send input
            // to the desktop, and neither test window is ever shown or activated.
            KeyEventArgs down = RaiseKey(first, Key.LeftAlt, isDown: true, system: true);
            Assert.False(down.Handled);
            ShortcutMotionTestScope.AssertState(firstCaption, true);
            ShortcutMotionTestScope.AssertState(secondCaption, false);
            Assert.False(ShortcutPresentation.ReadEnabled());
            TextBlock addedWhileHeld = AddCaption(first);
            ShortcutMotionTestScope.AssertState(addedWhileHeld, true);
            Assert.False(RaiseKey(first, Key.A, isDown: true, system: true).Handled);
            ShortcutMotionTestScope.AssertState(firstCaption, false);
            ShortcutMotionTestScope.AssertState(addedWhileHeld, false);
            Assert.False(RaiseKey(first, Key.LeftAlt, isDown: true, system: true, repeat: true).Handled);
            ShortcutMotionTestScope.AssertState(firstCaption, false);
            Assert.False(RaiseKey(first, Key.LeftAlt, isDown: false, system: true).Handled);
            ShortcutMotionTestScope.AssertState(firstCaption, false);
            ShortcutMotionTestScope.AssertState(addedWhileHeld, false);
            RaiseKey(first, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(firstCaption, true);
            ShortcutMotionTestScope.AssertState(addedWhileHeld, true);
            ShortcutMotionTestScope.AssertState(secondCaption, false);
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
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            TextBlock caption = AddCaption(window);
            Assert.True(RaiseKey(window, Key.RightAlt, isDown: true, handled: true).Handled);
            ShortcutMotionTestScope.AssertState(caption, true);
            Assert.True(RaiseKey(window, Key.RightAlt, isDown: false, handled: true).Handled);
            ShortcutMotionTestScope.AssertState(caption, false);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            RaiseKey(window, Key.RightAlt, isDown: true);
            window.DeactivateForTest();
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.RightAlt, isDown: true, repeat: true);
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.RightAlt, isDown: false);
            RaiseKey(window, Key.RightAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            window.Close();
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, false);
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
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        var other = new HintWindow();
        var settings = new AppSettings();
        try
        {
            ShortcutPresentation.ReadEnabled = () => settings.General.AlwaysShowShortcutHints;
            TextBlock caption = AddCaption(window);
            TextBlock otherCaption = AddCaption(other);
            ShortcutMotionTestScope.AssertState(caption, false);
            ShortcutMotionTestScope.AssertState(otherCaption, false);
            Assert.Equal(Visibility.Collapsed, ShortcutPresentation.Current.HintVisibility);

            settings.General.AlwaysShowShortcutHints = true;
            ShortcutPresentation.Refresh();
            ShortcutMotionTestScope.AssertState(caption, true);
            ShortcutMotionTestScope.AssertState(otherCaption, true);
            Assert.Equal(Visibility.Visible, ShortcutPresentation.Current.HintVisibility);
            ShortcutMotionTestScope.AssertState(AddCaption(other), true);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            settings.General.AlwaysShowShortcutHints = false;
            ShortcutPresentation.Refresh();
            ShortcutMotionTestScope.AssertState(caption, true);
            ShortcutMotionTestScope.AssertState(otherCaption, false);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            ShortcutMotionTestScope.AssertState(caption, false);

            RaiseKey(window, Key.LeftAlt, isDown: true);
            settings.General.AlwaysShowShortcutHints = true;
            ShortcutPresentation.Refresh();
            RaiseKey(window, Key.LeftAlt, isDown: false);
            ShortcutMotionTestScope.AssertState(caption, true);
            RaiseKey(window, Key.C, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            window.RaiseEvent(CreateMouse("down", handled: true));
            ShortcutMotionTestScope.AssertState(caption, true);
            window.DeactivateForTest();
            ShortcutMotionTestScope.AssertState(caption, true);
            ShortcutMotionTestScope.AssertState(otherCaption, true);

            ShortcutPresentation.ReadEnabled = null;
            ShortcutPresentation.Refresh();
            ShortcutMotionTestScope.AssertState(caption, false);
            ShortcutMotionTestScope.AssertState(otherCaption, false);
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
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        var window = new HintWindow();
        var settings = new AppSettings { General = { ShowShortcutHints = legacyEnabled } };
        try
        {
            ShortcutPresentation.ReadEnabled = () => settings.General.AlwaysShowShortcutHints;
            TextBlock caption = AddCaption(window);
            Assert.False(settings.General.AlwaysShowShortcutHints);
            ShortcutMotionTestScope.AssertState(caption, false);
            settings.General.ShowShortcutHints = !legacyEnabled;
            ShortcutPresentation.Refresh();
            ShortcutMotionTestScope.AssertState(caption, false);
            settings.General.AlwaysShowShortcutHints = true;
            ShortcutPresentation.Refresh();
            ShortcutMotionTestScope.AssertState(caption, true);
            settings.General.ShowShortcutHints = legacyEnabled;
            ShortcutPresentation.Refresh();
            ShortcutMotionTestScope.AssertState(caption, true);
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
    [InlineData("wheel", false)]
    [InlineData("wheel", true)]
    public void MouseInputDismissesWithoutConsumingInputOrRevealingOnRepeat(string input, bool handled) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var window = new HintWindow();
        try
        {
            TextBlock caption = AddCaption(window);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            MouseEventArgs mouse = CreateMouse(input, handled);
            window.RaiseEvent(mouse);
            Assert.Equal(handled, mouse.Handled);
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.LeftAlt, isDown: true, repeat: true);
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerMovementKeepsEveryHeldAltGestureVisibleInOwnerAndFollowingSurface(bool handled) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var owner = new HintWindow();
        var surface = new HintWindow { ShowActivated = false };
        try
        {
            TextBlock caption = AddCaption(owner);
            ShortcutPresentation.FollowOwner(surface, owner);
            TextBlock surfaceCaption = AddCaption(surface);
            var popupTarget = new MenuItem { Header = "Command" };
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Key alt = attempt % 2 == 0 ? Key.LeftAlt : Key.RightAlt;
                Assert.False(RaiseKey(owner, alt, isDown: true, system: true).Handled);
                foreach (HintWindow target in new[] { owner, surface })
                {
                    MouseEventArgs move = CreateMouse("move", handled);
                    target.RaiseEvent(move);
                    Assert.Equal(handled, move.Handled);
                }
                foreach (RoutedEvent routedEvent in new[] { Mouse.PreviewMouseMoveEvent, Mouse.MouseMoveEvent })
                {
                    MouseEventArgs move = CreateMouse("move", handled);
                    move.RoutedEvent = routedEvent;
                    move.Source = popupTarget;
                    InputManager.Current.ProcessInput(move);
                    Assert.Equal(handled, move.Handled);
                    ShortcutMotionTestScope.AssertState(caption, true);
                    ShortcutMotionTestScope.AssertState(surfaceCaption, true);
                }
                RaiseKey(owner, alt, isDown: true, system: true, repeat: true);
                ShortcutMotionTestScope.AssertState(caption, true);
                KeyEventArgs release = CreateKey(alt, Keyboard.KeyUpEvent, system: true);
                release.Source = popupTarget;
                InputManager.Current.ProcessInput(release);
                ShortcutMotionTestScope.AssertState(caption, false);
                ShortcutMotionTestScope.AssertState(surfaceCaption, false);
            }
        }
        finally { surface.Close(); owner.Close(); }
    });

    [Theory]
    [InlineData(Key.Tab)]
    [InlineData(Key.Space)]
    [InlineData(Key.F4)]
    public void SystemChordsDismissHintsWithoutConsumingTheCommand(Key command) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var window = new HintWindow();
        try
        {
            TextBlock caption = AddCaption(window);
            RaiseKey(window, Key.LeftAlt, isDown: true, system: true);
            Assert.False(RaiseKey(window, command, isDown: true, system: true).Handled);
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(window, Key.LeftAlt, isDown: false, system: true);
            RaiseKey(window, Key.LeftAlt, isDown: true, system: true);
            ShortcutMotionTestScope.AssertState(caption, true);
        }
        finally { window.Close(); }
    });
    [Fact]
    public void LayoutCursorQueriesDoNotDismissTheCurrentAltGesture() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var window = new HintWindow();
        try
        {
            TextBlock caption = AddCaption(window);
            RaiseKey(window, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            var query = new QueryCursorEventArgs(Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = Mouse.QueryCursorEvent,
                Source = window,
                Handled = true,
            };
            InputManager.Current.ProcessInput(query);
            Assert.True(query.Handled);
            ShortcutMotionTestScope.AssertState(caption, true);
            RaiseKey(window, Key.LeftAlt, isDown: false);
            ShortcutMotionTestScope.AssertState(caption, false);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonActivatingSurfaceFollowsCaptionlessOwnerEvenWhenItsHintsAlreadyLoaded(bool loadBeforeFollowing) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
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
                ShortcutMotionTestScope.AssertState(caption, true);
            }
            Assert.Empty(owner.Commands.Children);
            ShortcutPresentation.FollowOwner(surface, owner);
            ShortcutPresentation.FollowOwner(surface, owner); // Repeated Loaded notifications are harmless.
            caption ??= AddCaption(surface);
            ShortcutMotionTestScope.AssertState(caption, false);
            Assert.False(RaiseKey(surface, Key.RightAlt, isDown: true).Handled);
            ShortcutMotionTestScope.AssertState(caption, false);

            Assert.True(RaiseKey(owner, Key.LeftAlt, isDown: true, system: true, handled: true).Handled);
            ShortcutMotionTestScope.AssertState(caption, true);
            TextBlock addedWhileHeld = AddCaption(surface);
            ShortcutMotionTestScope.AssertState(addedWhileHeld, true);
            ShortcutMotionTestScope.AssertState(unrelatedCaption, false);
            surface.DeactivateForTest();
            ShortcutMotionTestScope.AssertState(caption, true);
            RaiseKey(owner, Key.LeftAlt, isDown: false);
            ShortcutMotionTestScope.AssertState(caption, false);
            ShortcutMotionTestScope.AssertState(addedWhileHeld, false);

            RaiseKey(owner, Key.RightAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            owner.DeactivateForTest();
            ShortcutMotionTestScope.AssertState(caption, false);
            ShortcutMotionTestScope.AssertState(unrelatedCaption, false);
        }
        finally { surface.Close(); owner.Close(); unrelated.Close(); }
    });

    [Theory]
    [InlineData("down")]
    [InlineData("up")]
    [InlineData("wheel")]
    public void SurfaceMouseInputDismissesItsOwnerButClosingSurfaceLeavesTheOwnerGestureIntact(string input) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var owner = new HintWindow();
        var surface = new HintWindow { ShowActivated = false };
        try
        {
            TextBlock ownerCaption = AddCaption(owner);
            ShortcutPresentation.FollowOwner(surface, owner);
            TextBlock surfaceCaption = AddCaption(surface);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(ownerCaption, true);
            ShortcutMotionTestScope.AssertState(surfaceCaption, true);
            MouseEventArgs mouse = CreateMouse(input, handled: true);
            surface.RaiseEvent(mouse);
            Assert.True(mouse.Handled);
            ShortcutMotionTestScope.AssertState(ownerCaption, false);
            ShortcutMotionTestScope.AssertState(surfaceCaption, false);
            RaiseKey(owner, Key.LeftAlt, isDown: true, repeat: true);
            ShortcutMotionTestScope.AssertState(surfaceCaption, false);
            RaiseKey(owner, Key.LeftAlt, isDown: false);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(surfaceCaption, true);

            surface.Close();
            ShortcutMotionTestScope.AssertState(surfaceCaption, false);
            ShortcutMotionTestScope.AssertState(ownerCaption, true);
            surface.RaiseEvent(CreateMouse(input, handled: false));
            ShortcutMotionTestScope.AssertState(ownerCaption, true);
            RaiseKey(owner, Key.LeftAlt, isDown: false);
            ShortcutMotionTestScope.AssertState(ownerCaption, false);
        }
        finally { surface.Close(); owner.Close(); }
    });

    [Fact]
    public void ClosingOwnerClearsItsFollowingSurfaceAndSurfaceCannotStartAnotherGesture() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        var owner = new HintWindow();
        var surface = new HintWindow { ShowActivated = false };
        try
        {
            ShortcutPresentation.FollowOwner(surface, owner);
            TextBlock caption = AddCaption(surface);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            owner.Close();
            ShortcutMotionTestScope.AssertState(caption, false);
            RaiseKey(surface, Key.RightAlt, isDown: true);
            RaiseKey(owner, Key.RightAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, false);
        }
        finally { surface.Close(); owner.Close(); }
    });

    [Fact]
    public void PopupKeyReleasesClearRevealedOwnersWithoutRevealingOtherWindows() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
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
            ShortcutMotionTestScope.AssertState(firstCaption, false);
            ShortcutMotionTestScope.AssertState(secondCaption, false);
            ShortcutMotionTestScope.AssertState(untouchedCaption, false);

            RaiseKey(first, Key.LeftAlt, isDown: true);
            RaiseKey(first, Key.RightAlt, isDown: true);
            RaiseKey(second, Key.LeftAlt, isDown: true);
            KeyEventArgs leftUp = CreateKey(Key.LeftAlt, Keyboard.KeyUpEvent, system: true, handled: true);
            leftUp.Source = popupTarget;
            InputManager.Current.ProcessInput(leftUp);
            Assert.True(leftUp.Handled);
            ShortcutMotionTestScope.AssertState(firstCaption, true); // Right Alt is still held.
            ShortcutMotionTestScope.AssertState(secondCaption, false);
            ShortcutMotionTestScope.AssertState(untouchedCaption, false);

            KeyEventArgs rightUp = CreateKey(Key.RightAlt, Keyboard.KeyUpEvent, system: true);
            rightUp.Source = popupTarget;
            InputManager.Current.ProcessInput(rightUp);
            Assert.False(rightUp.Handled);
            ShortcutMotionTestScope.AssertState(firstCaption, false);
            ShortcutMotionTestScope.AssertState(secondCaption, false);
            ShortcutMotionTestScope.AssertState(untouchedCaption, false);
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
        using var motion = new ShortcutMotionTestScope();
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
            ShortcutMotionTestScope.AssertState(caption, true);

            InputEventArgs input = mouseInput
                ? CreateMouse("down", handled: true)
                : CreateKey(Key.C, Keyboard.KeyDownEvent, system: true, handled: true);
            input.Source = popupTarget;
            InputManager.Current.ProcessInput(input);
            Assert.True(input.Handled);
            ShortcutMotionTestScope.AssertState(caption, false);
            ShortcutMotionTestScope.AssertState(otherCaption, false);
            RaiseKey(owner, Key.LeftAlt, isDown: true, repeat: true);
            ShortcutMotionTestScope.AssertState(caption, false);

            KeyEventArgs release = CreateKey(Key.LeftAlt, Keyboard.KeyUpEvent, system: true);
            release.Source = popupTarget;
            InputManager.Current.ProcessInput(release);
            RaiseKey(owner, Key.LeftAlt, isDown: true);
            ShortcutMotionTestScope.AssertState(caption, true);
            ShortcutMotionTestScope.AssertState(otherCaption, false);
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

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

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
