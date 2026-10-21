using System.Windows.Input;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ReceiptKeyboardSessionTests
{
    private static readonly nint Receipt = 101;
    private static readonly nint Previous = 202;
    private static readonly nint Other = 303;

    [Fact]
    public void EntryCapturesThePreviousWindowBeforeActivationAndFocus()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        Assert.Equal(["foreground", "passive:False", "activate", "focus-first"], fixture.Events);
        Assert.False(fixture.Passive);

        fixture.Events.Clear();
        nint target = fixture.Session.TakeReturnTarget(Receipt);
        Assert.Equal(Previous, target);
        Assert.Empty(fixture.Restored);
        fixture.Events.Add("close");
        fixture.Session.Return(target);
        Assert.Equal(["foreground", "close", "restore:202"], fixture.Events);
        Assert.Equal([Previous], fixture.Restored);
    }

    [Fact]
    public void RepeatedEntryWhileTheReceiptIsForegroundKeepsTheOriginalReturnTarget()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Events.Clear();
        Assert.True(fixture.Session.Enter(Receipt));
        Assert.Equal(["foreground", "passive:False", "activate", "focus-first"], fixture.Events);
        Assert.Equal(Previous, fixture.Session.TakeReturnTarget(Receipt));
    }

    [Fact]
    public void ReentryAfterTheUserSwitchesWindowsReturnsToTheNewForegroundWindow()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Foreground = Other;
        fixture.Events.Clear();
        Assert.True(fixture.Session.Enter(Receipt));
        Assert.Equal(["foreground", "passive:False", "activate", "focus-first"], fixture.Events);
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        Assert.Equal([Other], fixture.Restored);
        Assert.Equal(Other, fixture.Foreground);
    }

    [Fact]
    public void FailedActivationRestoresPassiveModeAndForgetsTheAttempt()
    {
        var fixture = new Fixture { Foreground = Previous, ActivationSucceeds = false };
        Assert.False(fixture.Session.Enter(Receipt));
        Assert.Equal(["foreground", "passive:False", "activate", "passive:True"], fixture.Events);
        Assert.True(fixture.Passive);
        fixture.Foreground = Receipt;
        Assert.Equal((nint)0, fixture.Session.TakeReturnTarget(Receipt));
        Assert.Empty(fixture.Restored);

        fixture.ActivationSucceeds = true;
        fixture.Foreground = Other;
        Assert.True(fixture.Session.Enter(Receipt));
        Assert.Equal(Other, fixture.Session.TakeReturnTarget(Receipt));
    }

    [Fact]
    public void FailedInitialFocusAlsoResetsEntryAndDoesNotRouteCommands()
    {
        var fixture = new Fixture { Foreground = Previous, FocusSucceeds = false };
        Assert.False(fixture.Session.Enter(Receipt));
        Assert.Equal(["foreground", "passive:False", "activate", "focus-first", "passive:True"], fixture.Events);
        Assert.True(fixture.Passive);
        Assert.False(fixture.Session.HandleKey(Key.Enter, ModifierKeys.None,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
        Assert.Equal((nint)0, fixture.Session.TakeReturnTarget(Receipt));
    }

    [Fact]
    public void ClosingAfterTheUserChangesForegroundDoesNotStealFocusBack()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Foreground = Other;
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        Assert.Empty(fixture.Restored);
        fixture.Foreground = Receipt;
        Assert.Equal((nint)0, fixture.Session.TakeReturnTarget(Receipt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void MissingOrOwnPreviousWindowIsNeverRestored(int previous)
    {
        var fixture = new Fixture { Foreground = previous };
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        Assert.Empty(fixture.Restored);
    }

    [Fact]
    public void ReturnTargetCanOnlyBeTakenOnceAndANewEntryGetsANewTarget()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        fixture.Session.Return(0);
        Assert.Equal([Previous], fixture.Restored);

        fixture.Foreground = Other;
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        Assert.Equal([Previous, Other], fixture.Restored);
    }

    [Fact]
    public void TabAndShiftTabMoveInOrderAndEnterInvokesWithoutDismissing()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        List<string> actions = [];
        void Move(bool forward) => actions.Add(forward ? "next" : "previous");
        void Invoke() => actions.Add("invoke");
        void Dismiss() => actions.Add("dismiss");
        Assert.True(fixture.Session.HandleKey(Key.Tab, ModifierKeys.None, Move, Invoke, Dismiss));
        Assert.True(fixture.Session.HandleKey(Key.Tab, ModifierKeys.Shift, Move, Invoke, Dismiss));
        Assert.True(fixture.Session.HandleKey(Key.Enter, ModifierKeys.None, Move, Invoke, Dismiss));
        Assert.Equal(["next", "previous", "invoke"], actions);
        Assert.Empty(fixture.Restored);
    }

    [Fact]
    public void EscapeDismissesAndReturnsFocusAfterTheReceiptCloses()
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        fixture.Events.Clear();
        Assert.True(fixture.Session.HandleKey(Key.Escape, ModifierKeys.None,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () =>
            {
                fixture.Events.Add("dismiss");
                nint target = fixture.Session.TakeReturnTarget(Receipt);
                fixture.Events.Add("close");
                fixture.Session.Return(target);
            }));
        Assert.Equal(["dismiss", "foreground", "close", "restore:202"], fixture.Events);
        Assert.False(fixture.Session.HandleKey(Key.Escape, ModifierKeys.None,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData(Key.Tab, ModifierKeys.Control)]
    [InlineData(Key.Tab, ModifierKeys.Alt)]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.Enter, ModifierKeys.Shift)]
    [InlineData(Key.Enter, ModifierKeys.Control)]
    [InlineData(Key.Escape, ModifierKeys.Alt)]
    [InlineData(Key.Space, ModifierKeys.None)]
    public void UnhandledKeysAndModifiedCommandsDoNotRunReceiptActions(Key key, ModifierKeys modifiers)
    {
        var fixture = new Fixture { Foreground = Previous };
        Assert.True(fixture.Session.Enter(Receipt));
        Assert.False(fixture.Session.HandleKey(key, modifiers,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
    }

    [Fact]
    public void APassiveReceiptDoesNotHandleKeysOrReturnFocus()
    {
        var fixture = new Fixture { Foreground = Receipt };
        Assert.False(fixture.Session.HandleKey(Key.Enter, ModifierKeys.None,
            _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => throw new InvalidOperationException()));
        fixture.Session.Return(fixture.Session.TakeReturnTarget(Receipt));
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Restored);
    }

    private sealed class Fixture
    {
        internal nint Foreground { get; set; }
        internal bool ActivationSucceeds { get; set; } = true;
        internal bool FocusSucceeds { get; set; } = true;
        internal bool Passive { get; private set; } = true;
        internal List<string> Events { get; } = [];
        internal List<nint> Restored { get; } = [];
        internal ReceiptKeyboardSession Session { get; }

        internal Fixture()
        {
            Session = new ReceiptKeyboardSession(
                () => { Events.Add("foreground"); return Foreground; },
                passive => { Passive = passive; Events.Add($"passive:{passive}"); },
                () =>
                {
                    Events.Add("activate");
                    if (ActivationSucceeds) Foreground = Receipt;
                    return ActivationSucceeds;
                },
                () => { Events.Add("focus-first"); return FocusSucceeds; },
                target => { Events.Add($"restore:{target}"); Restored.Add(target); Foreground = target; });
        }
    }
}
