using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Floating windows leave with a short exit that never delays the caller's state: input stops at
/// once, the close runs exactly once, and everything snaps when Windows animation effects are off.
/// </summary>
public sealed class FloatingWindowExitTests
{
    [Fact]
    public void ClosesAtOnceWhenMotionIsOff() => Run(motion: false, layered: true, (window, surface) =>
    {
        int closes = 0;
        FluidMotion.BeginExit(window, ExitMotion.FadeDown, () => closes++);

        Assert.Equal(1, closes);
        Assert.False(FluidMotion.IsExiting(window));
        Assert.False(surface.HasAnimatedProperties);
    });

    [Fact]
    public void OpaqueWindowClosesAtOnce() => Run(motion: true, layered: false, (window, _) =>
    {
        // An opaque window would fade its content into its own background, not the desktop.
        int closes = 0;
        FluidMotion.BeginExit(window, ExitMotion.FadeDown, () => closes++);

        Assert.Equal(1, closes);
    });

    [Theory]
    [InlineData(ExitMotion.FadeDown, typeof(TranslateTransform))]
    [InlineData(ExitMotion.FadeRight, typeof(TranslateTransform))]
    [InlineData(ExitMotion.Shrink, typeof(ScaleTransform))]
    public void LayeredWindowStopsInputAndClosesOnce(ExitMotion motion, Type transform) => Run(motion: true, layered: true, (window, surface) =>
    {
        int closes = 0;
        FluidMotion.BeginExit(window, motion, () => closes++);

        Assert.Equal(0, closes);
        Assert.True(FluidMotion.IsExiting(window));
        Assert.False(window.IsHitTestVisible);
        Assert.True(surface.HasAnimatedProperties);
        Assert.IsType(transform, surface.RenderTransform);

        // A second request while leaving does not close twice.
        FluidMotion.BeginExit(window, motion, () => closes++);
        FluidMotion.CompleteExit(window);
        FluidMotion.CompleteExit(window);

        Assert.Equal(1, closes);
        Assert.False(FluidMotion.IsExiting(window));
        Assert.True(window.IsHitTestVisible);
        Assert.Equal(1, surface.Opacity);
        Assert.Same(Transform.Identity, surface.RenderTransform);
    });

    [Fact]
    public void KeepsATransformTheScreenOwns() => Run(motion: true, layered: true, (window, surface) =>
    {
        var own = new ScaleTransform(1.5, 1.5);
        surface.RenderTransform = own;

        FluidMotion.BeginExit(window, ExitMotion.Shrink, static () => { });

        Assert.Same(own, surface.RenderTransform);
        FluidMotion.CompleteExit(window);
        Assert.Same(own, surface.RenderTransform);
    });

    [Fact]
    public void ExitEndsOnItsOwn() => Run(motion: true, layered: true, (window, _) =>
    {
        int closes = 0;
        FluidMotion.BeginExit(window, ExitMotion.FadeDown, () => closes++);

        // The animation or its fallback timer (83 + 250 ms) closes the window without help.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (closes == 0 && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, window.Dispatcher);
            Dispatcher.PushFrame(frame);
            tick.Stop();
        }

        Assert.Equal(1, closes);
        Assert.True(window.IsHitTestVisible);
    });

    private static void Run(bool motion, bool layered, Action<Window, Border> body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        var surface = new Border { Width = 120, Height = 60, Background = Brushes.Gray };
        var window = new Window
        {
            Content = surface, Width = 140, Height = 80, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, AllowsTransparency = layered, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        try
        {
            window.Show();
            ControlStateMotionTests.Drain(window);
            body(window, surface);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });
}
