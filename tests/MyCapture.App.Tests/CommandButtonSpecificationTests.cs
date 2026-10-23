using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CommandButtonSpecificationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void CollapsedStandardFaceWidensForItsChordOnlyWhileRevealedAndRestoresLivePadding(double scale) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            Button button = IconAndLabel();
            VisualTreeHelper.SetRootDpi(button, new DpiScale(scale, scale));
            ShortcutPresentation.Attach(button, ShortcutScope.Annotation, "Privacy");
            Grid face = Assert.IsType<Grid>(button.Content);
            var label = ((StackPanel)face.Children[0]).Children.OfType<TextBlock>().Single();
            var hint = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
            double reservedWidth = face.MinWidth;
            Assert.True(reservedWidth > 0);
            Assert.Equal(reservedWidth, CommandButtonPresentation.CaptionWidth(face));
            Assert.Equal(new Thickness(10, 4, 10, 4), button.Padding);
            Assert.True(CommandButtonPresentation.CompactLabel(button, true));
            Assert.Equal(Visibility.Collapsed, label.Visibility);
            Assert.Equal(new Thickness(4), button.Padding);
            // The icon-only face reserves nothing for its hidden chord.
            Assert.Equal(0, face.MinWidth);
            foreach (bool reveal in new[] { false, true, false })
            {
                ShortcutPresentation.SetAltRevealed(button, reveal);
                button.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                button.Measure(new Size(500, 100));
                button.Arrange(new Rect(0, 0, button.DesiredSize.Width, button.DesiredSize.Height));
                Assert.Equal(44, button.ActualHeight, 2);
                Assert.Equal(36, button.MinWidth);
                Assert.Equal(0, face.MinWidth);
                ShortcutMotionTestScope.AssertState(hint, reveal);
                if (reveal)
                {
                    // Ctrl+Shift+R does not fit the resting face, so the command widens until it does.
                    // The reserved chord is rounded up to a whole DIP.
                    Assert.True(face.ActualWidth >= reservedWidth - 1, $"face {face.ActualWidth} < chord {reservedWidth}");
                    Assert.Null(VisualTreeHelper.GetClip(hint));
                    Assert.True(button.ActualWidth > 36 + 0.5);
                }
                else Assert.Equal(36, button.ActualWidth, 2);
            }

            // Expanding must resolve today's theme token, not a value saved before compaction.
            button.Resources["CommandButton.Padding.Standard"] = new Thickness(12, 4, 12, 4);
            Assert.True(CommandButtonPresentation.CompactLabel(button, false));
            Assert.Equal(Visibility.Visible, label.Visibility);
            Assert.Equal(reservedWidth, face.MinWidth);
            Assert.Equal(new Thickness(12, 4, 12, 4), button.Padding);
            button.Resources["CommandButton.Padding.Standard"] = new Thickness(11, 4, 11, 4);
            Assert.Equal(new Thickness(11, 4, 11, 4), button.Padding);
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void AlwaysIconRetainsTightPaddingWhenStandardPresentationIsReapplied() => StaTestHost.Run(() =>
    {
        Button button = IconAndLabel();
        CommandButtonPresentation.Apply(button);
        CommandButtonPresentation.AlwaysIcon(button);
        CommandButtonPresentation.Apply(button);
        CommandButtonPresentation.CompactLabel(button, false);
        Assert.Equal(new Thickness(4), button.Padding);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)button.Content).Children.OfType<TextBlock>().Single().Visibility);
        button.Resources["CommandButton.Padding.StandardIconOnly"] = new Thickness(3, 4, 3, 4);
        Assert.Equal(new Thickness(3, 4, 3, 4), button.Padding);
    });

    [Fact]
    public void CompactionPreservesCompactTierCustomTextOnlyAndPrimaryCommandPadding() => StaTestHost.Run(() =>
    {
        Button compact = IconAndLabel();
        CommandButtonPresentation.Apply(compact, compact: true);
        Button custom = IconAndLabel();
        custom.Padding = new Thickness(7, 2, 7, 2);
        Button textOnly = IconAndLabel();
        textOnly.Content = new TextBlock { Text = "Command" };
        CommandButtonPresentation.Apply(textOnly);
        Button iconOnly = IconAndLabel();
        ((StackPanel)iconOnly.Content).Children.RemoveAt(1);
        CommandButtonPresentation.Apply(iconOnly);
        Button primary = IconAndLabel();
        CommandButtonPresentation.Apply(primary);
        CommandButtonPresentation.KeepLabel(primary);
        foreach (Button button in new[] { compact, custom, textOnly, iconOnly, primary })
        {
            Thickness padding = button.Padding;
            foreach (bool collapse in new[] { true, false })
            {
                CommandButtonPresentation.CompactLabel(button, collapse);
                Assert.Equal(padding, button.Padding);
            }
        }
        Assert.Equal(Visibility.Visible, ((StackPanel)primary.Content).Children.OfType<TextBlock>().Single().Visibility);
    });

    private static Button IconAndLabel() => new()
    {
        Resources = SystemHighContrastThemeTests.LoadTheme(),
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Viewbox { Width = 20, Height = 20, Child = new Border { Width = 16, Height = 16 } },
                new TextBlock { Text = "Command", Margin = new Thickness(6, 0, 0, 0) },
            },
        },
    };

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
                    Assert.Equal(new GridLength(14), face.RowDefinitions[1].Height);
                    Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight, 2);
                    var label = Assert.IsType<TextBlock>(face.Children[0]);
                    Assert.Equal(14, label.FontSize);
                    Assert.True(face.ActualHeight <= button.ActualHeight - button.Padding.Top - button.Padding.Bottom
                        - button.BorderThickness.Top - button.BorderThickness.Bottom + 0.5,
                        $"Command content {face.ActualHeight:0.##} exceeds the {height}-DIP button's inner height.");
                    double top = label.TranslatePoint(default, row).Y;
                    if (labelTop is { } expectedTop) Assert.Equal(expectedTop, top, 2);
                    else labelTop = top;
                    if (!show)
                    {
                        if (restingTops.TryGetValue(button, out double resting)) Assert.Equal(resting, top, 2);
                        else restingTops.Add(button, top);
                    }
                    else Assert.True(top < restingTops[button] - 5, "Every name moves up inside the fixed-height button.");
                    Assert.Equal(2, face.Children.Count);
                    TextBlock hint = Assert.IsAssignableFrom<TextBlock>(face.Children[1]);
                    Assert.Equal(12, hint.FontSize);
                    ShortcutMotionTestScope.AssertState(hint, show);
                    if (ReferenceEquals(button, withoutHint))
                    {
                        Assert.Empty(hint.Text);
                        Assert.Equal(0, face.MinWidth);
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
