using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CommandButtonSpecificationTests
{
    [Theory]
    [InlineData(false, 44, 4)]
    [InlineData(true, 36, 2)]
    public void EachTierKeepsEqualButtonHeightsAndLabelPositionsWithEveryHintState(bool compact, double height, double verticalPadding) => StaTestHost.Run(() =>
    {
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        bool enabled = true;
        try
        {
            ShortcutPresentation.ReadEnabled = () => enabled;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Resources = SystemHighContrastThemeTests.LoadTheme(),
            };
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

            foreach (bool show in new[] { true, false, true })
            {
                enabled = show;
                ShortcutPresentation.Refresh();
                row.Measure(new Size(1000, 100));
                row.Arrange(new Rect(0, 0, row.DesiredSize.Width, row.DesiredSize.Height));
                double? labelTop = null;
                foreach (Button button in buttons)
                {
                    Assert.Equal(height, button.ActualHeight, 2);
                    Assert.Equal(new Thickness(10, verticalPadding, 10, verticalPadding), button.Padding);
                    Assert.Equal(new Thickness(3, 0, 3, 0), button.Margin);
                    Assert.Equal(12, button.FontSize);
                    Grid face = Assert.IsType<Grid>(button.Content);
                    Assert.Equal("CommandButtonFace", face.Name);
                    Assert.Equal(2, face.RowDefinitions.Count);
                    Assert.Equal(14, face.RowDefinitions[1].ActualHeight, 2);
                    var label = Assert.IsType<TextBlock>(face.Children[0]);
                    double top = label.TranslatePoint(default, row).Y;
                    if (labelTop is { } expectedTop) Assert.Equal(expectedTop, top, 2);
                    else labelTop = top;
                    if (ReferenceEquals(button, withoutHint)) Assert.Single(face.Children);
                    else
                    {
                        TextBlock hint = Assert.IsType<TextBlock>(face.Children[1]);
                        Assert.Equal(show ? Visibility.Visible : Visibility.Collapsed, hint.Visibility);
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
