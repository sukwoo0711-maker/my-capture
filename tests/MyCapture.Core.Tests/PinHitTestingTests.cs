using System.Collections.Generic;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Selecting the pin under the cursor, including the case a click-through pin creates.
/// </summary>
public sealed class PinHitTestingTests
{
    [Fact]
    public void TopmostIndexAt_NoPins_ReturnsMinusOne()
    {
        Assert.Equal(-1, PinHitTesting.TopmostIndexAt([], 10, 10));
    }

    [Fact]
    public void TopmostIndexAt_PointOutsideAll_ReturnsMinusOne()
    {
        var pins = new List<PinBounds> { new(0, 0, 100, 100, IsHidden: false) };
        Assert.Equal(-1, PinHitTesting.TopmostIndexAt(pins, 200, 200));
    }

    [Fact]
    public void TopmostIndexAt_OverlappingPins_PicksMostRecentlyOpened()
    {
        // Two overlapping pins; the later one (index 1) is on top.
        var pins = new List<PinBounds>
        {
            new(0, 0, 100, 100, IsHidden: false),
            new(50, 50, 150, 150, IsHidden: false),
        };

        Assert.Equal(1, PinHitTesting.TopmostIndexAt(pins, 60, 60));
        // A point only inside the earlier pin still selects it.
        Assert.Equal(0, PinHitTesting.TopmostIndexAt(pins, 10, 10));
    }

    [Fact]
    public void TopmostIndexAt_HiddenPinIsSkipped()
    {
        var pins = new List<PinBounds>
        {
            new(0, 0, 100, 100, IsHidden: false),
            new(0, 0, 100, 100, IsHidden: true),
        };

        // The top pin is hidden, so the visible one below it is selected.
        Assert.Equal(0, PinHitTesting.TopmostIndexAt(pins, 10, 10));
    }

    [Fact]
    public void TopmostIndexAt_ClickThroughPinStillReachableByBounds()
    {
        // A click-through pin is NOT hidden — it is visible but transparent to hit testing.
        // Selection by bounds still finds it, which is exactly how a global command turns
        // click-through back off.
        var pins = new List<PinBounds> { new(0, 0, 100, 100, IsHidden: false) };
        Assert.Equal(0, PinHitTesting.TopmostIndexAt(pins, 50, 50));
    }

    [Fact]
    public void PinBounds_Contains_UsesHalfOpenInterval()
    {
        var b = new PinBounds(0, 0, 100, 100, IsHidden: false);
        Assert.True(b.Contains(0, 0));
        Assert.True(b.Contains(99, 99));
        Assert.False(b.Contains(100, 100)); // right/bottom exclusive
    }
}
