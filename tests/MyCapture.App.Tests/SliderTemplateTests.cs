using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The themed slider (4px track, 16px round thumb) keeps the parts Slider drives, so keyboard
/// steps, tick snapping and the RangeValue automation pattern behave as before.
/// </summary>
public sealed class SliderTemplateTests
{
    [Fact]
    public void TemplateKeepsTheTrackPartsAndTheirCommands() => ControlStateMotionTests.Run(false, (theme, host) =>
    {
        var slider = new Slider { Minimum = 1, Maximum = 24, Value = 6, Style = (Style)theme[typeof(Slider)] };
        host.Children.Add(slider);
        ControlStateMotionTests.Drain(host);

        Track track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        Assert.Same(Slider.DecreaseLarge, track.DecreaseRepeatButton.Command);
        Assert.Same(Slider.IncreaseLarge, track.IncreaseRepeatButton.Command);
        Assert.False(track.DecreaseRepeatButton.Focusable);
        Assert.False(track.Thumb.Focusable);
        Assert.Equal(16, track.Thumb.ActualWidth);
        Assert.True(slider.ActualHeight >= 32, $"slider hit target {slider.ActualHeight}");
        // The value part is filled with the slider's foreground (accent by default).
        Assert.Same(slider.Foreground, track.DecreaseRepeatButton.Background);
        Assert.Equal(6, track.Value);
        Assert.Null(slider.FocusVisualStyle);
    });

    [Fact]
    public void KeyboardStepsSnapsAndJumpsToTheEnds() => ControlStateMotionTests.Run(false, (theme, host) =>
    {
        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = 0, SmallChange = 1, LargeChange = 10,
            Style = (Style)theme[typeof(Slider)],
        };
        host.Children.Add(slider);
        ControlStateMotionTests.Drain(host);

        Press(slider, Key.Right);
        Assert.Equal(1, slider.Value);
        Press(slider, Key.PageUp);
        Assert.Equal(11, slider.Value);
        Press(slider, Key.End);
        Assert.Equal(100, slider.Value);
        Press(slider, Key.Left);
        Assert.Equal(99, slider.Value);
        Press(slider, Key.Home);
        Assert.Equal(0, slider.Value);

        slider.TickFrequency = 5;
        slider.IsSnapToTickEnabled = true;
        Press(slider, Key.Right);
        Assert.Equal(5, slider.Value);
        Press(slider, Key.PageDown);
        Assert.Equal(0, slider.Value);
    });

    [Fact]
    public void AutomationStillExposesTheRangeValue() => ControlStateMotionTests.Run(false, (theme, host) =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 90, Value = 30, Style = (Style)theme[typeof(Slider)] };
        AutomationProperties.SetName(slider, "목표 용량");
        host.Children.Add(slider);
        ControlStateMotionTests.Drain(host);

        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(slider);
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue));
        Assert.Equal(30, range.Value);
        Assert.Equal(90, range.Maximum);
        range.SetValue(45);
        Assert.Equal(45, slider.Value);
        Assert.Equal("목표 용량", peer.GetName());
        Assert.True(peer.IsKeyboardFocusable());
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThumbCentreGrowsOnHoverOverTheFastToken(bool motion) => ControlStateMotionTests.Run(motion, (theme, host) =>
    {
        var slider = new Slider { Minimum = 0, Maximum = 10, Value = 5, Style = (Style)theme[typeof(Slider)] };
        host.Children.Add(slider);
        ControlStateMotionTests.Drain(host);
        Track track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        var centre = (UIElement)track.Thumb.Template.FindName("Centre", track.Thumb);

        // The hover trigger sets this value; the centre settles there at once and animates only with motion on.
        FluidMotion.SetScaleTo(centre, 1.2);

        var scale = Assert.IsType<ScaleTransform>(centre.RenderTransform);
        Assert.Equal(1.2, (double)scale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty), 3);
        Assert.Equal(motion, scale.HasAnimatedProperties);
        Trigger hover = Assert.Single(track.Thumb.Template.Triggers.OfType<Trigger>(), trigger => trigger.Property == UIElement.IsMouseOverProperty);
        Assert.Contains(hover.Setters.OfType<Setter>(), setter => setter.Property == FluidMotion.ScaleToProperty);
    });

    private static void Press(Slider slider, Key key)
    {
        // The probe window is never activated, so the key goes straight to the slider.
        PresentationSource source = PresentationSource.FromVisual(slider)!;
        slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
    }
}
