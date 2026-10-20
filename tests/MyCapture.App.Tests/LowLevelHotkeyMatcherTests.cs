using System.Collections.Generic;
using MyCapture.Core.Settings;
using MyCapture.Platform.Interop;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LowLevelHotkeyMatcherTests
{
    private static readonly LowLevelHotkeyClaim[] Claims =
    [
        new(1, new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkC)),
        new(2, new Hotkey(HotkeyModifiers.None, Hotkey.VkF3)),
    ];

    [Theory]
    [InlineData(NativeMethods.WM_KEYDOWN)]
    [InlineData(NativeMethods.WM_SYSKEYDOWN)]
    public void ExactModifiersClaimTheFirstDown(uint message)
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, message, HotkeyModifiers.Control | HotkeyModifiers.Shift, Claims,
            new HashSet<uint>(), new HashSet<uint>());

        Assert.True(match.Swallow);
        Assert.Equal(1, match.CommandId);
        Assert.False(match.ReleaseKey);
    }

    [Theory]
    [InlineData(HotkeyModifiers.None)]
    [InlineData(HotkeyModifiers.Control)]
    [InlineData(HotkeyModifiers.Shift)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows)]
    public void MissingOrExtraModifiersPassThrough(HotkeyModifiers modifiers)
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, NativeMethods.WM_KEYDOWN, modifiers, Claims, new HashSet<uint>(), new HashSet<uint>());

        Assert.False(match.Swallow);
        Assert.Null(match.CommandId);
    }

    [Fact]
    public void ShiftF3DoesNotMatchPlainF3()
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYDOWN, HotkeyModifiers.Shift, Claims,
            new HashSet<uint>(), new HashSet<uint>());

        Assert.False(match.Swallow);
        Assert.Null(match.CommandId);
    }

    [Fact]
    public void PlainF3IsClaimed()
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYDOWN, HotkeyModifiers.None, Claims,
            new HashSet<uint>(), new HashSet<uint>());

        Assert.True(match.Swallow);
        Assert.Equal(2, match.CommandId);
    }

    [Theory]
    [InlineData(NativeMethods.WM_KEYDOWN)]
    [InlineData(NativeMethods.WM_SYSKEYDOWN)]
    public void RepeatIsSwallowedWithoutPostingEvenAfterModifiersOrClaimsChange(uint message)
    {
        var held = new HashSet<uint> { Hotkey.VkC };
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, message, HotkeyModifiers.None, [], held, held);

        Assert.True(match.Swallow);
        Assert.Null(match.CommandId);
        Assert.False(match.ReleaseKey);
        Assert.Contains(Hotkey.VkC, held);
    }

    [Theory]
    [InlineData(NativeMethods.WM_KEYUP)]
    [InlineData(NativeMethods.WM_SYSKEYUP)]
    public void SwallowedKeyUpIsSwallowedAndReleasedWithoutPosting(uint message)
    {
        var held = new HashSet<uint> { Hotkey.VkC };
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, message, HotkeyModifiers.None, [], held, held);

        Assert.True(match.Swallow);
        Assert.True(match.ReleaseKey);
        Assert.Null(match.CommandId);
        Assert.Contains(Hotkey.VkC, held);
    }

    [Theory]
    [InlineData(NativeMethods.WM_KEYDOWN)]
    [InlineData(NativeMethods.WM_KEYUP)]
    [InlineData(NativeMethods.WM_SYSKEYDOWN)]
    [InlineData(NativeMethods.WM_SYSKEYUP)]
    public void UnrelatedKeysPassThrough(uint message)
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkX, message, HotkeyModifiers.Control | HotkeyModifiers.Shift, Claims,
            new HashSet<uint> { Hotkey.VkC }, new HashSet<uint> { Hotkey.VkC });

        Assert.False(match.Swallow);
        Assert.Null(match.CommandId);
    }

    [Fact]
    public void KeyUpWithoutSwallowedDownPassesThrough()
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYUP, HotkeyModifiers.None, Claims,
            new HashSet<uint> { Hotkey.VkF3 }, new HashSet<uint>());

        Assert.False(match.Swallow);
        Assert.False(match.ReleaseKey);
    }

    [Fact]
    public void ReleasingAClaimedKeyAllowsTheNextPressToPostAgain()
    {
        var held = new HashSet<uint>();
        LowLevelHotkeyMatch first = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYDOWN, HotkeyModifiers.None, Claims, held, held);
        Assert.Equal(2, first.CommandId);
        held.Add(Hotkey.VkF3);

        LowLevelHotkeyMatch repeat = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYDOWN, HotkeyModifiers.None, Claims, held, held);
        Assert.True(repeat.Swallow);
        Assert.Null(repeat.CommandId);

        LowLevelHotkeyMatch release = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYUP, HotkeyModifiers.None, Claims, held, held);
        Assert.True(release.ReleaseKey);
        held.Remove(Hotkey.VkF3);

        LowLevelHotkeyMatch next = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_KEYDOWN, HotkeyModifiers.None, Claims, held, held);
        Assert.Equal(2, next.CommandId);
    }

    [Fact]
    public void UnrelatedMessagesPassThroughEvenForHeldKeys()
    {
        LowLevelHotkeyMatch match = LowLevelHotkeyMatcher.Match(
            Hotkey.VkF3, NativeMethods.WM_HOTKEY, HotkeyModifiers.None, Claims,
            new HashSet<uint> { Hotkey.VkF3 }, new HashSet<uint> { Hotkey.VkF3 });

        Assert.False(match.Swallow);
        Assert.Null(match.CommandId);
    }

    [Theory]
    [InlineData(NativeMethods.WM_KEYDOWN, NativeMethods.WM_KEYUP)]
    [InlineData(NativeMethods.WM_SYSKEYDOWN, NativeMethods.WM_SYSKEYUP)]
    public void ModifiersPressedWhileAKeyIsAlreadyHeldDoNotPostOnRepeat(uint downMessage, uint upMessage)
    {
        var held = new HashSet<uint>();
        var swallowed = new HashSet<uint>();
        LowLevelHotkeyMatch first = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, downMessage, HotkeyModifiers.None, Claims, held, swallowed);
        Assert.False(first.Swallow);
        held.Add(Hotkey.VkC);

        LowLevelHotkeyMatch repeat = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, downMessage, HotkeyModifiers.Control | HotkeyModifiers.Shift, Claims, held, swallowed);
        Assert.True(repeat.Swallow);
        Assert.Null(repeat.CommandId);
        swallowed.Add(Hotkey.VkC);

        LowLevelHotkeyMatch release = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, upMessage, HotkeyModifiers.None, Claims, held, swallowed);
        Assert.True(release.Swallow);
        Assert.True(release.ReleaseKey);
        Assert.Null(release.CommandId);
        held.Remove(Hotkey.VkC);
        swallowed.Remove(Hotkey.VkC);

        LowLevelHotkeyMatch next = LowLevelHotkeyMatcher.Match(
            Hotkey.VkC, downMessage, HotkeyModifiers.Control | HotkeyModifiers.Shift, Claims, held, swallowed);
        Assert.True(next.Swallow);
        Assert.Equal(1, next.CommandId);
    }
}
