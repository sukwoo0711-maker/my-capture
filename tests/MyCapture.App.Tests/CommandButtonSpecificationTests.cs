using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CommandButtonSpecificationTests
{
    [Theory]
    [InlineData(false, 44, 4, 1)]
    [InlineData(false, 44, 4, 1.25)]
    [InlineData(false, 44, 4, 1.5)]
    [InlineData(false, 44, 4, 2)]
    [InlineData(true, 36, 0, 1)]
    [InlineData(true, 36, 0, 1.25)]
    [InlineData(true, 36, 0, 1.5)]
    [InlineData(true, 36, 0, 2)]
    public void EachTierKeepsEqualButtonHeightsAndLabelPositionsWithEveryHintState(bool compact, double height, double verticalPadding, double scale) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Resources = SystemHighContrastThemeTests.LoadTheme(),
            };
            VisualTreeHelper.SetRootDpi(row, new DpiScale(scale, scale));
            var withoutHint = new Button { Content = "Command" };
            var singleKey = new Button { Content = "Command" };
            var combination = new Button { Content = "Command" };
            CommandButtonPresentation.ReserveHint(withoutHint);
            ShortcutPresentation.Attach(singleKey, ShortcutScope.Annotation, "Crop");
            ShortcutPresentation.Attach(combination, ShortcutScope.Annotation, "Privacy");
            Button[] buttons = [withoutHint, singleKey, combination];
            foreach (Button button in buttons)
            {
                CommandButtonPresentation.Apply(button, compact);
                row.Children.Add(button);
            }

            var restingTops = new Dictionary<Button, double>();
            foreach (bool show in new[] { false, true, false })
            {
                ShortcutPresentation.SetAltRevealed(row, show);
                row.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                row.Measure(new Size(1000, 100));
                row.Arrange(new Rect(0, 0, row.DesiredSize.Width, row.DesiredSize.Height));
                double? labelTop = null;
                foreach (Button button in buttons)
                {
                    Assert.Equal(height, button.ActualHeight, 2);
                    Assert.Equal(new Thickness(10, verticalPadding, 10, verticalPadding), button.Padding);
                    Assert.Equal(new Thickness(3, 0, 3, 0), button.Margin);
                    Assert.Equal(14, button.FontSize);
                    Grid face = Assert.IsType<Grid>(button.Content);
                    Assert.Equal("CommandButtonFace", face.Name);
                    Assert.Equal(2, face.RowDefinitions.Count);
                    bool hasHint = !ReferenceEquals(button, withoutHint);
                    Assert.Equal(new GridLength(14), face.RowDefinitions[1].Height);
                    Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight, 2);
                    var label = Assert.IsType<TextBlock>(face.Children[0]);
                    Assert.Equal(14, label.FontSize);
                    Assert.True(face.ActualHeight <= button.ActualHeight - button.Padding.Top - button.Padding.Bottom
                        - button.BorderThickness.Top - button.BorderThickness.Bottom + 0.5,
                        $"Command content {face.ActualHeight:0.##} exceeds the {height}-DIP button's inner height.");
                    double top = label.TranslatePoint(default, row).Y;
                    if (!show)
                    {
                        if (labelTop is { } expectedTop) Assert.Equal(expectedTop, top, 2);
                        else labelTop = top;
                        if (restingTops.TryGetValue(button, out double resting)) Assert.Equal(resting, top, 2);
                        else restingTops.Add(button, top);
                    }
                    else if (hasHint) Assert.True(top < restingTops[button] - 5, "The name moves up inside the fixed-height button.");
                    else Assert.Equal(restingTops[button], top, 2);
                    if (ReferenceEquals(button, withoutHint)) Assert.Single(face.Children);
                    else
                    {
                        TextBlock hint = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                        Assert.Equal(12, hint.FontSize);
                        ShortcutMotionTestScope.AssertState(hint, show);
                    }
                }
            }
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });
}
