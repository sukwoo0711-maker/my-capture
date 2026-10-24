using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using Xunit;
using Shapes = System.Windows.Shapes;

namespace MyCapture.App.Tests;

/// <summary>
/// The countdown posed at a moment of its motion (the tutorial clip generator's video clock): each
/// new digit lands over the fading previous one, the ring runs down linearly through the second,
/// and at zero the panel shrinks away. The pose follows the motion tokens, not the wall clock or
/// the Windows animation setting, and leaves nothing animating.
/// </summary>
public sealed class CountdownWindowPoseTests
{
    // Cubic ease-out (Motion.Ease.Standard) and quadratic ease-out (Motion.Ease.SoftLanding).
    private static double Cubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static double Quadratic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 2);

    [Fact]
    public void PosesEachSecondAndTheExitAsTheMotionShowsThem() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        // The generator renders with Windows animation effects off and still poses the motion.
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var window = new CountdownWindow(3);
        try
        {
            TextBlock label = Field<TextBlock>(window, "_label");
            TextBlock leaving = Field<TextBlock>(window, "_leaving");
            Shapes.Path ring = Field<Shapes.Path>(window, "_ring");
            double length = Field<double>(window, "_ringLength");
            var panel = (FrameworkElement)window.Content;
            var labelScale = (ScaleTransform)label.RenderTransform;
            var leavingScale = (ScaleTransform)leaving.RenderTransform;

            // The first digit lands with the window: nothing fades out under it, and the ring is full.
            window.PoseAt(3, TimeSpan.Zero);
            Assert.Equal("3", label.Text);
            Assert.Equal(0, label.Opacity, 6);
            Assert.Equal(1.22, labelScale.ScaleX, 6);
            Assert.Equal(string.Empty, leaving.Text);
            Assert.Equal(0, ring.StrokeDashOffset, 6);

            // 40 ms after 2 appeared: it lands (1.22 to 1 over 200 ms, fading in over 167 ms) while
            // 3 fades and shrinks to 0.88 over 83 ms, and the ring has run down 4% of the second.
            window.PoseAt(2, TimeSpan.FromMilliseconds(40));
            Assert.Equal("2", label.Text);
            Assert.Equal(Quadratic(40 / 167.0), label.Opacity, 6);
            Assert.Equal(1.22 - (0.22 * Cubic(40 / 200.0)), labelScale.ScaleX, 6);
            Assert.Equal(labelScale.ScaleX, labelScale.ScaleY, 6);
            Assert.Equal("3", leaving.Text);
            Assert.Equal(1 - Cubic(40 / 83.0), leaving.Opacity, 6);
            Assert.Equal(1 - (0.12 * Cubic(40 / 83.0)), leavingScale.ScaleX, 6);
            Assert.Equal((length + 1) * 0.04, ring.StrokeDashOffset, 6);
            Assert.Equal(1, panel.Opacity, 6);

            // Half a second in, the digit is at rest, the previous one is gone and half the ring is left.
            window.PoseAt(2, TimeSpan.FromMilliseconds(500));
            Assert.Equal(1, label.Opacity, 6);
            Assert.Equal(1, labelScale.ScaleX, 6);
            Assert.Equal(string.Empty, leaving.Text);
            Assert.Equal(0, leaving.Opacity, 6);
            Assert.Equal((length + 1) / 2, ring.StrokeDashOffset, 6);

            // Zero: the last digit over the empty ring, shrinking to 0.97 and fading over 83 ms.
            window.PoseAt(0, TimeSpan.Zero);
            Assert.Equal("1", label.Text);
            Assert.Equal(length + 1, ring.StrokeDashOffset, 6);
            Assert.Equal(1, panel.Opacity, 6);
            window.PoseAt(0, TimeSpan.FromMilliseconds(40));
            var shrink = Assert.IsType<ScaleTransform>(panel.RenderTransform);
            Assert.Equal(new Point(0.5, 0.5), panel.RenderTransformOrigin);
            Assert.Equal(1 - Cubic(40 / 83.0), panel.Opacity, 6);
            Assert.Equal(1 - (0.03 * Cubic(40 / 83.0)), shrink.ScaleX, 6);
            window.PoseAt(0, TimeSpan.FromMilliseconds(83));
            Assert.Equal(0, panel.Opacity, 6);
            Assert.Equal(FluidMotion.ShrinkExitScale, ((ScaleTransform)panel.RenderTransform).ScaleY, 6);

            // Back to a second: the panel is whole again.
            window.PoseAt(1, TimeSpan.FromMilliseconds(300));
            Assert.Equal(1, panel.Opacity, 6);
            Assert.Equal(DependencyProperty.UnsetValue, panel.ReadLocalValue(UIElement.RenderTransformProperty));

            // A pose is a still picture.
            Assert.False(label.HasAnimatedProperties);
            Assert.False(labelScale.HasAnimatedProperties);
            Assert.False(leaving.HasAnimatedProperties);
            Assert.False(leavingScale.HasAnimatedProperties);
            Assert.False(ring.HasAnimatedProperties);
            Assert.False(panel.HasAnimatedProperties);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    private static T Field<T>(CountdownWindow window, string name) =>
        (T)typeof(CountdownWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
