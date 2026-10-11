using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>The folder "save here" switch slides instead of jumping between states.</summary>
public sealed class FluidMotionSwitchTests
{
    private const string Template = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
            <Grid x:Name="PART_SwitchTrack" Width="36" Height="20">
                <Border x:Name="PART_SwitchFill" Opacity="0" Background="Purple" />
                <Grid x:Name="PART_SwitchThumb" Width="12" Height="12" Margin="4,0,0,0" HorizontalAlignment="Left">
                    <Ellipse Fill="Gray" />
                    <Ellipse x:Name="PART_SwitchThumbOn" Fill="Black" Opacity="0" />
                </Grid>
            </Grid>
        </ControlTemplate>
        """;

    [Fact]
    public void SwitchPlacesThumbAndFillForEachState() => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            CheckBox toggle = Create();
            toggle.IsChecked = true;
            Assert.Equal(16, Shift(toggle).X);
            Assert.Equal(1, Part(toggle, "PART_SwitchFill").Opacity);
            Assert.Equal(1, Part(toggle, "PART_SwitchThumbOn").Opacity);

            toggle.IsChecked = false;
            Assert.Equal(0, Shift(toggle).X);
            Assert.Equal(0, Part(toggle, "PART_SwitchFill").Opacity);
            Assert.False(Shift(toggle).HasAnimatedProperties);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchAnimatesOnlyWhenWindowsMotionIsOn(bool motion) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        try
        {
            CheckBox toggle = Create();
            toggle.IsChecked = true;
            // A loaded switch slides on its own toggle event; this unloaded fixture drives the
            // same step directly, with the handlers detached so the state change does not jump first.
            FluidMotion.SetSwitchMotion(toggle, false);
            toggle.IsChecked = false;
            FluidMotion.MoveSwitch(toggle, animate: true);

            // The target state is committed at once; only the visual travel is animated.
            Assert.Equal(0, (double)Shift(toggle).GetAnimationBaseValue(TranslateTransform.XProperty));
            Assert.Equal(motion, Shift(toggle).HasAnimatedProperties);
            Assert.Equal(motion, Part(toggle, "PART_SwitchFill").HasAnimatedProperties);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
        }
    });

    private static CheckBox Create()
    {
        var toggle = new CheckBox { Template = (ControlTemplate)XamlReader.Parse(Template) };
        FluidMotion.SetSwitchMotion(toggle, true);
        toggle.Measure(new Size(100, 40));
        toggle.Arrange(new Rect(0, 0, 100, 40));
        return toggle;
    }

    private static UIElement Part(CheckBox toggle, string name) => (UIElement)toggle.Template.FindName(name, toggle);

    private static TranslateTransform Shift(CheckBox toggle) =>
        Assert.IsType<TranslateTransform>(((FrameworkElement)Part(toggle, "PART_SwitchThumb")).RenderTransform);
}
