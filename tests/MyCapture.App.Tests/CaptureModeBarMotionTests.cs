using System.Windows;
using System.Windows.Controls;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The capture mode bar fades its hover and selected washes instead of swapping backgrounds.</summary>
public sealed class CaptureModeBarMotionTests
{
    [Fact]
    public void SelectedModeMovesBetweenFadingLayers() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            var bar = new CaptureModeBar();
            bar.Measure(new Size(1200, 80));
            bar.Arrange(new Rect(0, 0, 1200, 80));

            bar.CaptureButton.IsChecked = true;
            Border captureOn = Layer(bar.CaptureButton, "CheckedLayer");
            Assert.Equal(1, captureOn.Opacity);
            Assert.Equal(MotionSpeed.Normal, FluidMotion.GetTransition(captureOn));

            bar.TextButton.IsChecked = true;
            Assert.Equal(0, captureOn.Opacity);
            Assert.Equal(1, Layer(bar.TextButton, "CheckedLayer").Opacity);

            // The chrome keeps the button's own background; only the layers carry the states.
            Border hover = Layer(bar.HelpButton, "HoverLayer");
            Assert.Equal(0, hover.Opacity);
            Assert.False(hover.IsHitTestVisible);
            Assert.Equal(MotionSpeed.Fast, FluidMotion.GetTransition(hover));
            Assert.Null(bar.HelpButton.Template.FindName("CheckedLayer", bar.HelpButton));
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    private static Border Layer(Control control, string name)
    {
        control.ApplyTemplate();
        return Assert.IsType<Border>(control.Template.FindName(name, control));
    }
}
