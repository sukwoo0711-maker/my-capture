using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureModeBarHierarchyTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void KindModesOptionsAndUtilitiesReadInDistinctGroups(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var bar = Create();
        Layout(bar, 1000);
        var row = Assert.IsType<WrapPanel>(bar.Child);
        var kinds = Assert.IsType<Border>(row.Children[0]);
        var kindButtons = Assert.IsType<StackPanel>(kinds.Child);
        Assert.Equal([bar.CaptureKindButton, bar.RecordKindButton], kindButtons.Children.OfType<RadioButton>());
        ButtonBase[] commands = row.Children.OfType<ButtonBase>().ToArray();
        Assert.Equal<ButtonBase>(
            [bar.CaptureButton, bar.WindowButton, bar.FreeformButton, bar.FullScreenButton, bar.ScrollButton,
                bar.TextButton, bar.ColorButton, bar.DelayButton, bar.RatioButton, bar.MoreButton, bar.HelpButton, bar.CancelButton], commands);
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetDirectionalNavigation(row));

        double ordinaryGap = Gap(bar.CaptureButton, bar.WindowButton);
        double kindGap = Gap(bar.RecordKindButton, bar.CaptureButton);
        double optionsGap = Gap(bar.ColorButton, bar.DelayButton);
        double utilitiesGap = Gap(bar.RatioButton, bar.MoreButton);
        Assert.True(kindGap > optionsGap);
        Assert.True(optionsGap > utilitiesGap);
        Assert.True(utilitiesGap > ordinaryGap);
        Assert.All(commands, command =>
        {
            Assert.Equal(CaptureModeBar.ButtonSize, command.ActualWidth);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(command)));
        });
        bar.ShowMode(CaptureSelectorMode.Text);
        Assert.True(bar.TextButton.IsChecked);
        Assert.False(bar.CaptureButton.IsChecked);
        Assert.True(bar.CaptureKindButton.IsChecked);
        Assert.Equal("T", AutomationProperties.GetAcceleratorKey(bar.TextButton));
    });

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(600)]
    public void NarrowBarKeepsEveryVisibleCommandWithinItsMeasuredRows(double width) => StaTestHost.Run(() =>
    {
        var bar = Create();
        Layout(bar, width);
        Assert.True(bar.ActualWidth <= width);
        ButtonBase[] commands =
        [
            bar.CaptureKindButton, bar.RecordKindButton, bar.CaptureButton, bar.WindowButton, bar.FreeformButton,
            bar.FullScreenButton, bar.ScrollButton, bar.TextButton, bar.ColorButton, bar.DelayButton,
            bar.RatioButton, bar.MoreButton, bar.HelpButton, bar.CancelButton,
        ];
        foreach (ButtonBase command in commands.Where(command => command.Visibility == Visibility.Visible))
        {
            Point point = command.TranslatePoint(new Point(), bar);
            Assert.InRange(point.X, 0, bar.ActualWidth - command.ActualWidth);
            Assert.InRange(point.Y, 0, bar.ActualHeight - command.ActualHeight);
        }
        if (bar.ScrollButton.Visibility == Visibility.Collapsed)
        {
            ContextMenu menu = bar.CreateMoreMenu();
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("Text_4DBA30197C0A")));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("CaptureBar_Delay")));
        }
        Layout(bar, 1000);
        Assert.Equal(Visibility.Visible, bar.ScrollButton.Visibility);
        Assert.Equal(Visibility.Visible, bar.DelayButton.Visibility);
    });

    [Fact]
    public void RecordingKeepsTheKindIndicatorAheadOfModesAndHidesUnavailableOptions() => StaTestHost.Run(() =>
    {
        var bar = new CaptureModeBar();
        bar.Configure(false, false, false, recording: true, ratioAvailable: false);
        Layout(bar, 320);
        Assert.Equal(Visibility.Collapsed, bar.TextButton.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.ColorButton.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.DelayButton.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.RatioButton.Visibility);
        var row = Assert.IsType<WrapPanel>(bar.Child);
        FrameworkElement indicator = Assert.IsAssignableFrom<FrameworkElement>(row.Children[1]);
        Assert.Equal(Visibility.Visible, indicator.Visibility);
        Assert.Equal(UiText.Get("Onboard_BarRecord"), AutomationProperties.GetName(indicator));
        Assert.True(indicator.TranslatePoint(new Point(), bar).X < bar.CaptureButton.TranslatePoint(new Point(), bar).X);
    });

    private static CaptureModeBar Create()
    {
        var bar = new CaptureModeBar();
        bar.Configure(true, true, true, otherActions: true);
        return bar;
    }

    private static void Layout(CaptureModeBar bar, double width)
    {
        bar.ConstrainWidth(width);
        bar.Measure(new Size(width, double.PositiveInfinity));
        bar.Arrange(new Rect(new Point(), bar.DesiredSize));
        bar.UpdateLayout();
    }

    private static double Gap(FrameworkElement left, FrameworkElement right) =>
        right.TranslatePoint(new Point(), left).X - left.ActualWidth;
}
