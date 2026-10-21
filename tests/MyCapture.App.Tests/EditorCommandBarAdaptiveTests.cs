using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.Core.Localization;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorCommandBarAdaptiveTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ko-KR", 1)]
    [InlineData("en-US", 1)]
    [InlineData("ko-KR", 1.5)]
    [InlineData("en-US", 2)]
    public void EditorAndCropStayOnOneRowThroughMinimumWidthAndAlt(string language, double scale) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        bool always = false;
        using var preference = new HintPreference(() => always);
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
        VisualTreeHelper.SetRootDpi(host.Window, new DpiScale(scale, scale));
        Assert.Equal(680, host.Window.MinWidth);
        foreach (bool crop in new[] { false, true })
        {
            if (crop) Assert.True(host.Editor.HandleShortcut(System.Windows.Input.Key.X, System.Windows.Input.ModifierKeys.None));
            // Revisit widths in both directions to catch stale compression and layout loops.
            foreach (double width in new[] { 1280d, 960d, 800d, host.Window.MinWidth, 1280d, host.Window.MinWidth })
            {
                host.Window.Width = width;
                double? barHeight = null;
                Visibility[]? restingNames = null;
                foreach ((bool persistent, bool reveal) in new[] { (false, false), (false, true), (false, false), (true, false), (true, true), (false, false) })
                {
                    always = persistent;
                    ShortcutPresentation.SetAltRevealed(host.Window, reveal);
                    host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
                    var bar = Descendants(host.Editor).OfType<CommandGroupsPanel>().Single(panel => panel.IsVisible);
                    var left = (FrameworkElement)bar.Children[0];
                    var right = (FrameworkElement)bar.Children[1];
                    Assert.True(left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width <= bar.ActualWidth + 0.5,
                        $"{language} {width} crop={crop} Alt={reveal}: {left.DesiredSize.Width}+8+{right.DesiredSize.Width}>{bar.ActualWidth}");
                    Assert.Equal(left.TranslatePoint(default, bar).Y, right.TranslatePoint(default, bar).Y, 2);
                    if (barHeight.HasValue) Assert.Equal(barHeight.Value, bar.ActualHeight, 2);
                    else barHeight = bar.ActualHeight;
                    Assert.True(bar.IsMeasureValid && bar.IsArrangeValid);
                    host.Window.UpdateLayout();
                    Assert.True(bar.IsMeasureValid && bar.IsArrangeValid);
                    Button[] buttons = Descendants(bar).OfType<Button>().Where(button => button.IsVisible).ToArray();
                    Visibility[] names = buttons.SelectMany(PrimaryLabels).Select(label => label.Visibility).ToArray();
                    if (restingNames is not null) Assert.Equal(restingNames, names);
                    else restingNames = names;
                    Assert.Equal(crop ? 0 : 3, buttons.Count(button => (bool)button.GetValue(CommandButtonPresentation.AlwaysIconProperty)));
                    foreach (Button button in buttons)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(button.ToolTip)));
                        Assert.Equal(44, button.ActualHeight, 2);
                        var face = Assert.IsType<Grid>(button.Content);
                        string id = AutomationProperties.GetAutomationId(button);
                        if (id is "EditorFlowDone" or "EditorFlowCropApply")
                            Assert.Contains(Descendants(face.Children[0]).OfType<TextBlock>(), label => label.IsVisible && label.Text.Length > 0);
                        if ((bool)button.GetValue(CommandButtonPresentation.AlwaysIconProperty))
                        {
                            Assert.All(PrimaryLabels(button), label => Assert.Equal(Visibility.Collapsed, label.Visibility));
                            Assert.Contains(AutomationProperties.GetName(button), (string)button.ToolTip);
                            string gesture = AutomationProperties.GetAcceleratorKey(button);
                            if (gesture.Length > 0) Assert.Contains(gesture, (string)button.ToolTip);
                        }
                        else if ((!crop && width >= 960) || width == 1280)
                            Assert.All(PrimaryLabels(button), label => Assert.Equal(Visibility.Visible, label.Visibility));
                    }
                }
            }
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ActualWindowWidthsMeetEachStageBoundaryAndKeepNamesFrom960(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var preference = new HintPreference(() => false);
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
        // Measure the bar's natural boundary separately from the supported 680-DIP floor.
        // The preceding test verifies the unchanged real minimum and every supported size.
        Assert.Equal(680, host.Window.MinWidth);
        host.Window.MinWidth = 1;
        var bar = Descendants(host.Editor).OfType<CommandGroupsPanel>().Single(panel => panel.IsVisible);
        bool Fits(int width, int maximumStage)
        {
            host.Window.Width = width;
            host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
            var left = (FrameworkElement)bar.Children[0];
            var right = (FrameworkElement)bar.Children[1];
            bool compactLeft = Descendants(left).OfType<Button>().Where(button => !(bool)button.GetValue(CommandButtonPresentation.AlwaysIconProperty))
                .SelectMany(PrimaryLabels).Any(label => label.Visibility == Visibility.Collapsed);
            bool compactRight = Descendants(right).OfType<Button>().SelectMany(PrimaryLabels).Any(label => label.Visibility == Visibility.Collapsed);
            int stage = compactRight ? 3 : compactLeft ? 2 : 1;
            return stage <= maximumStage && left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width <= bar.ActualWidth + 0.01;
        }
        foreach (int stage in new[] { 1, 2, 3 })
        {
            int low = 300, high = 1280;
            Assert.True(Fits(high, stage));
            Assert.False(Fits(low, stage));
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (Fits(middle, stage)) high = middle;
                else low = middle;
            }
            Assert.False(Fits(high - 1, stage));
            Assert.True(Fits(high, stage));
            output.WriteLine($"{language}: stage {stage} minimum outer width = {high} DIP; panel = {bar.ActualWidth:0.##} DIP");
            if (stage == 1) Assert.InRange(high, 300, 960);
            if (stage == 3) Assert.InRange(high, 300, 680);
        }
    });

    private static IEnumerable<TextBlock> PrimaryLabels(Button button)
    {
        var face = Assert.IsType<Grid>(button.Content);
        return Descendants(face.Children[0]).OfType<TextBlock>();
    }

    private sealed class HintPreference(Func<bool> read) : IDisposable
    {
        private readonly Func<bool>? _previous = Install(read);
        private static Func<bool>? Install(Func<bool> value)
        {
            Func<bool>? previous = ShortcutPresentation.ReadEnabled;
            ShortcutPresentation.ReadEnabled = value;
            ShortcutPresentation.Refresh();
            return previous;
        }
        public void Dispose()
        {
            ShortcutPresentation.ReadEnabled = _previous;
            ShortcutPresentation.Refresh();
        }
    }

    [Fact]
    public void CompressionUsesLeftThenSecondaryRightAndRestoresNamesWithoutChangingAccessibility() => StaTestHost.Run(() =>
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var edit = Command("Edit", 100);
        var copy = Command("Copy", 80);
        var done = Command("Done", 70);
        CommandButtonPresentation.KeepLabel(done);
        left.Children.Add(edit); right.Children.Add(copy); right.Children.Add(done);
        var panel = new CommandGroupsPanel { Children = { left, right } };
        foreach (double width in new[] { 400d, 250d, 180d, 400d, 80d })
        {
            panel.Measure(new Size(width, 200));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            Assert.Equal(width is 250 or 180 or 80 ? Visibility.Collapsed : Visibility.Visible, Label(edit).Visibility);
            Assert.Equal(width is 180 or 80 ? Visibility.Collapsed : Visibility.Visible, Label(copy).Visibility);
            Assert.Equal(Visibility.Visible, Label(done).Visibility);
            Assert.Equal("Copy", AutomationProperties.GetName(copy));
            Assert.Equal("Copy the image (Ctrl+C)", copy.ToolTip);
            Assert.Equal("Ctrl+C", AutomationProperties.GetAcceleratorKey(copy));
            if (width == 80)
            {
                Assert.Equal(0, right.TranslatePoint(default, panel).X);
                Assert.Equal(left.DesiredSize.Height + CommandGroupsPanel.GroupGap, right.TranslatePoint(default, panel).Y);
            }
        }
    });

    private static Button Command(string name, double labelWidth)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { new Border { Width = 16, Height = 16 }, new TextBlock { Text = name, Width = labelWidth } },
            },
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Height = 44,
            ToolTip = name == "Copy" ? "Copy the image (Ctrl+C)" : name,
        };
        AutomationProperties.SetName(button, name);
        if (name == "Copy") AutomationProperties.SetAcceleratorKey(button, "Ctrl+C");
        return button;
    }

    private static TextBlock Label(ButtonBase button) => ((StackPanel)button.Content).Children.OfType<TextBlock>().Single();
}
