using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryStateMotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotationOwnsItsTransformEvenWhenAnExistingTransformIsMutable(bool frozen) => StaTestHost.Run(() =>
    {
        var shared = new RotateTransform(12);
        if (frozen) shared.Freeze();
        var first = new Border { RenderTransform = shared };
        var second = new Border { RenderTransform = shared };
        FluidMotion.SetRotateTo(first, 90);

        var owned = Assert.IsType<RotateTransform>(first.RenderTransform);
        Assert.NotSame(shared, owned);
        Assert.Same(shared, second.RenderTransform);
        Assert.Equal(12, shared.Angle);
        Assert.Equal(90, owned.Angle);
        Assert.False(owned.HasAnimatedProperties);
        Assert.Equal(new Point(0.5, 0.5), first.RenderTransformOrigin);
        FluidMotion.SetRotateTo(first, 0);
        Assert.Same(owned, first.RenderTransform);
    });

    [Fact]
    public void InitialStateAppearsWholeBeforeLoading() => ControlStateMotionTests.Run(true, (_, host) =>
    {
        var layer = new Border { Opacity = 0 };
        FluidMotion.SetFadeTo(layer, 1);
        FluidMotion.SetRotateTo(layer, 90);
        var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
        Assert.Equal(1, layer.Opacity);
        Assert.Equal(90, rotate.Angle);
        Assert.False(layer.HasAnimatedProperties);
        Assert.False(rotate.HasAnimatedProperties);
        host.Children.Add(layer);
        ControlStateMotionTests.Drain(host);
        Assert.True(layer.IsLoaded);
        Assert.Equal(1, layer.Opacity);
        Assert.Equal(90, rotate.Angle);
        Assert.False(rotate.HasAnimatedProperties);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RapidReversalCommitsTheLatestStateAndSettlesWithoutAStaleClock(bool motion) =>
        ControlStateMotionTests.Run(motion, (_, host) =>
        {
            var layer = new Border { Opacity = 0 };
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            FluidMotion.SetTransition(layer, MotionSpeed.Normal);
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetRotateTo(layer, 90);
            var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
            Assert.Equal(1, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(90, (double)rotate.GetAnimationBaseValue(RotateTransform.AngleProperty));
            Assert.Equal(motion, layer.HasAnimatedProperties);
            Assert.Equal(motion, rotate.HasAnimatedProperties);
            PumpFor(layer, TimeSpan.FromMilliseconds(45));
            double opacity = layer.Opacity, angle = rotate.Angle;

            FluidMotion.SetFadeTo(layer, 0);
            FluidMotion.SetRotateTo(layer, 0);
            Assert.Equal(0, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(0, (double)rotate.GetAnimationBaseValue(RotateTransform.AngleProperty));
            Assert.Equal(motion, layer.HasAnimatedProperties);
            Assert.Equal(motion, rotate.HasAnimatedProperties);
            // A replacement WPF clock starts on the next render tick. Sample that
            // rendered transition rather than its pre-tick dependency-property value.
            if (motion)
            {
                PumpFor(layer, TimeSpan.FromMilliseconds(20));
                Assert.InRange(layer.Opacity, double.Epsilon, opacity);
                Assert.InRange(rotate.Angle, double.Epsilon, angle);
            }
            PumpFor(layer, TimeSpan.FromMilliseconds(300));
            Assert.Equal(0, layer.Opacity);
            Assert.Equal(0, rotate.Angle);
            // Stop fill releases the animated value; the committed target remains.
            Assert.Equal(0, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(0, (double)rotate.GetAnimationBaseValue(RotateTransform.AngleProperty));
        });

    [Fact]
    public void TurningMotionOffBeforeTheNextGestureCancelsAnInFlightTransition() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var layer = new Border { Opacity = 0 };
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetRotateTo(layer, 90);
            Assert.True(layer.HasAnimatedProperties);
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            FluidMotion.SetFadeTo(layer, 0);
            FluidMotion.SetRotateTo(layer, 0);
            var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
            Assert.Equal(0, layer.Opacity);
            Assert.Equal(0, rotate.Angle);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(rotate.HasAnimatedProperties);
        });

    [Fact]
    public void UnloadingCommitsTheFinalStateBeforeTheSamePartIsLoadedAgain() =>
        ControlStateMotionTests.Run(true, (_, host) =>
        {
            var layer = new Border { Opacity = 0 };
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            FluidMotion.SetTransition(layer, MotionSpeed.Deliberate);
            FluidMotion.SetFadeTo(layer, 1);
            FluidMotion.SetRotateTo(layer, 90);
            var rotate = Assert.IsType<RotateTransform>(layer.RenderTransform);
            Assert.True(layer.HasAnimatedProperties);
            Assert.True(rotate.HasAnimatedProperties);
            host.Children.Remove(layer);
            ControlStateMotionTests.Drain(host);
            Assert.False(layer.IsLoaded);
            Assert.Equal(1, layer.Opacity);
            Assert.Equal(90, rotate.Angle);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(rotate.HasAnimatedProperties);
            host.Children.Add(layer);
            ControlStateMotionTests.Drain(host);
            Assert.True(layer.IsLoaded);
            Assert.Equal(1, layer.Opacity);
            Assert.Equal(90, rotate.Angle);
            Assert.False(layer.HasAnimatedProperties);
            Assert.False(rotate.HasAnimatedProperties);
        });

    private static void PumpFor(DispatcherObject owner, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
