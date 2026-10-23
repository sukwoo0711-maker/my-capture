using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Geometry/input tests settle feedback; timing tests exercise real animation clocks.</summary>
internal sealed class ShortcutMotionTestScope : IDisposable
{
    private readonly bool _previous = ShortcutRevealMotion.SettleImmediatelyForTest;
    internal ShortcutMotionTestScope() => ShortcutRevealMotion.SettleImmediatelyForTest = true;
    public void Dispose() => ShortcutRevealMotion.SettleImmediatelyForTest = _previous;

    internal static void AssertState(TextBlock caption, bool revealed)
    {
        Assert.Equal(revealed, ShortcutPresentation.IsRevealed(caption));
        Assert.Equal(Visibility.Visible, caption.Visibility);
        Assert.Equal(revealed ? 1 : 0, caption.Opacity);
        Assert.False(caption.IsHitTestVisible);
    }
}
