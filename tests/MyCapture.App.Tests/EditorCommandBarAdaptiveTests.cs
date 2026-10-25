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
    public void EditorAndCropStayOnOneRowAtEverySupportedWidthThroughAlt(string language, double scale) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var motion = new ShortcutMotionTestScope();
        bool always = false;
        using var preference = new HintPreference(() => always);
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
        VisualTreeHelper.SetRootDpi(host.Window, new DpiScale(scale, scale));
        Assert.Equal(680, host.Window.MinWidth);
        foreach (bool crop in new[] { false, true })
        {
            if (crop) Assert.True(host.Editor.HandleShortcut(System.Windows.Input.Key.X, System.Windows.Input.ModifierKeys.None));
            host.Window.Width = 1280;
            host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
            var fullBar = Descendants(host.Editor).OfType<CommandGroupsPanel>().Single(panel => panel.IsVisible);
            double fullLabelWidth = RevealedWidth(fullBar) + host.Window.Width - fullBar.ActualWidth;
            // Revisit widths in both directions to catch stale compression and layout loops.
            foreach (double width in new[] { 1280d, Math.Ceiling(fullLabelWidth) + 1, 960d, 800d, host.Window.MinWidth, 1280d, host.Window.MinWidth })
            {
                host.Window.Width = width;
                double? barHeight = null;
                Visibility[]? restingNames = null;
                foreach ((bool persistent, bool reveal) in new[] { (false, false), (false, true), (false, false), (true, false), (true, true), (false, false) })
                {
                    always = persistent;
                    ShortcutPresentation.SetAltRevealed(host.Window, reveal);
                    ShortcutPresentation.Refresh();
                    host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
                    var bar = Descendants(host.Editor).OfType<CommandGroupsPanel>().Single(panel => panel.IsVisible);
                    var left = (FrameworkElement)bar.Children[0];
                    var right = (FrameworkElement)bar.Children[1];
                    double combinedWidth = left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width;
                    Assert.True(combinedWidth <= bar.ActualWidth + 0.5,
                        $"{language} {width} crop={crop} Alt={reveal}: {combinedWidth}>{bar.ActualWidth}");
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
                    Assert.Equal(crop ? 0 : 4, buttons.Count(button => (bool)button.GetValue(CommandButtonPresentation.AlwaysIconProperty)));
                    foreach (Button button in buttons)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(button.ToolTip)));
                        Assert.Equal(44, button.ActualHeight, 2);
                        var face = Assert.IsType<Grid>(button.Content);
                        string id = AutomationProperties.GetAutomationId(button);
                        if (id is "EditorFlowDone" or "EditorFlowCropApply")
                            Assert.Contains(Descendants(face.Children[0]).OfType<TextBlock>(), label => label.IsVisible && label.Text.Length > 0);
                        if ((bool)button.GetValue(CommandButtonPresentation.AlwaysIconProperty)
                            || PrimaryLabels(button).Any(label => label.Visibility == Visibility.Collapsed))
                        {
                            // An icon-only command reserves nothing for its hidden chord. While the
                            // caption shows, the command is wide enough for the whole chord.
                            double chrome = button.Padding.Left + button.Padding.Right
                                + button.BorderThickness.Left + button.BorderThickness.Right;
                            double content = ((FrameworkElement)face.Children[0]).DesiredSize.Width;
                            var chord = (TextBlock)face.Children[1];
                            if (ShortcutPresentation.IsRevealed(chord))
                            {
                                // The reserved caption is a whole-DIP ceiling of a detached
                                // measurement; laid out at a fractional scale the chord can be up
                                // to one DIP narrower. It must still show whole, and the command
                                // is as wide as the chord it shows.
                                Assert.True(chord.DesiredSize.Width >= CommandButtonPresentation.CaptionWidth(face) - 1,
                                    $"{language} {width} {AutomationProperties.GetName(button)}: chord {chord.DesiredSize.Width} < {CommandButtonPresentation.CaptionWidth(face)}");
                                content = Math.Max(content, chord.DesiredSize.Width);
                            }
                            double expected = Math.Max(button.MinWidth, content + chrome);
                            Assert.InRange(button.ActualWidth, expected - 1, expected + 1);
                        }
                        if ((bool)button.GetValue(CommandButtonPresentation.AlwaysIconProperty))
                        {
                            Assert.All(PrimaryLabels(button), label => Assert.Equal(Visibility.Collapsed, label.Visibility));
                            Assert.Contains(AutomationProperties.GetName(button), (string)button.ToolTip);
                            string gesture = AutomationProperties.GetAcceleratorKey(button);
                            if (gesture.Length > 0) Assert.Contains(gesture, (string)button.ToolTip);
                        }
                        else if (width >= Math.Ceiling(fullLabelWidth) + 1)
                            Assert.All(PrimaryLabels(button), label => Assert.Equal(Visibility.Visible, label.Visibility));
                    }
                }
            }
        }
    });

    [Theory]
    // Measured with the editing commands as borderless tool buttons (no 1-DIP outline).
    [InlineData("ko-KR", new[] { 969, 935, 889, 862, 779, 721, 648 })]
    [InlineData("en-US", new[] { 933, 888, 852, 822, 744, 709, 648 })]
    public void NamesCollapseOneCommandAtATimeInTheirDeclaredOrder(string language, int[] boundaries) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var preference = new HintPreference(() => false);
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Window.Resources = SystemHighContrastThemeTests.LoadTheme();
        VisualTreeHelper.SetRootDpi(host.Window, new DpiScale(1, 1));
        // Measure the bar's natural boundary separately from the supported 680-DIP floor.
        // The preceding test verifies the unchanged real minimum and every supported size.
        Assert.Equal(680, host.Window.MinWidth);
        host.Window.MinWidth = 1;
        host.Window.Width = 1280;
        host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
        var bar = Descendants(host.Editor).OfType<CommandGroupsPanel>().Single(panel => panel.IsVisible);
        double fullLabelWidth = RevealedWidth(bar) + host.Window.Width - bar.ActualWidth;
        Button[] ordered = Descendants(bar).OfType<Button>()
            .Where(button => (int)button.GetValue(CommandButtonPresentation.CollapseOrderProperty) > 0)
            .OrderBy(button => (int)button.GetValue(CommandButtonPresentation.CollapseOrderProperty)).ToArray();
        // Cancel, Crop, Copy, Save to file, Auto-redact, Copy text.
        Assert.Equal(["EditorFlowCancel", "Capture410Crop", "EditorFlowCopy"], ordered.Take(3).Select(AutomationProperties.GetAutomationId));
        Assert.Equal("EditorFlowCopyText", AutomationProperties.GetAutomationId(ordered[^1]));
        Assert.Equal(6, ordered.Length);
        // How many names a width hides, or null when the bar cannot hold Alt's widths on one row.
        int? Hidden(int width)
        {
            host.Window.Width = width;
            host.Window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
            if (RevealedWidth(bar) > bar.ActualWidth + 0.01) return null;
            bool[] hidden = ordered.Select(button => PrimaryLabels(button).Any(label => label.Visibility == Visibility.Collapsed)).ToArray();
            int count = hidden.Count(value => value);
            // Names go strictly in order: the hidden ones are always the first in the order.
            Assert.All(hidden.Take(count), value => Assert.True(value));
            return count;
        }
        var measuredWidths = new List<int>();
        for (int hidden = 0; hidden <= ordered.Length; hidden++)
        {
            int low = 300, high = 1280;
            bool Fits(int width) => Hidden(width) is { } count && count <= hidden;
            Assert.True(Fits(high));
            Assert.False(Fits(low));
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (Fits(middle)) high = middle;
                else low = middle;
            }
            Assert.False(Fits(high - 1));
            Assert.True(Fits(high));
            output.WriteLine($"{language}: {hidden} hidden names, minimum outer width = {high} DIP; panel = {bar.ActualWidth:0.##} DIP");
            measuredWidths.Add(high);
            if (hidden == 0) Assert.InRange(high, Math.Ceiling(fullLabelWidth) - 1, Math.Ceiling(fullLabelWidth) + 1);
        }
        Assert.Equal(boundaries, measuredWidths);
    });

    // Stages are chosen for this width: every command as wide as it is while its caption shows.
    private static double RevealedWidth(CommandGroupsPanel bar)
    {
        var left = (FrameworkElement)bar.Children[0];
        var right = (FrameworkElement)bar.Children[1];
        return left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width
            + CommandButtonPresentation.RevealGrowth(left) + CommandButtonPresentation.RevealGrowth(right);
    }

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

    [Fact]
    public void OrderedCommandsGiveUpTheirNamesOneAtATimeBeforeTheirGroups() => StaTestHost.Run(() =>
    {
        Button edit = Command("Edit", 100), crop = Command("Crop", 60), copy = Command("Copy", 80);
        Button done = Command("Done", 70), cancel = Command("Cancel", 50);
        CommandButtonPresentation.KeepLabel(done);
        CommandButtonPresentation.CollapseOrder(cancel, 1);
        CommandButtonPresentation.CollapseOrder(crop, 2);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, crop } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { copy, done, cancel } };
        var panel = new CommandGroupsPanel { Children = { left, right } };
        panel.Measure(new Size(double.PositiveInfinity, 200));
        double full = left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width;

        string Named(double width)
        {
            panel.Measure(new Size(width, 200));
            panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
            Assert.Equal(Visibility.Visible, Label(done).Visibility);
            return string.Join(",", new[] { edit, crop, copy, cancel }
                .Where(button => Label(button).Visibility == Visibility.Visible).Select(button => AutomationProperties.GetName(button)));
        }

        Assert.Equal("Edit,Crop,Copy,Cancel", Named(full));
        // Cancel goes first and alone, even though it sits in the second group.
        Assert.Equal("Edit,Crop,Copy", Named(full - 1));
        Assert.Equal("Edit,Crop,Copy", Named(full - 50));
        Assert.Equal("Edit,Copy", Named(full - 51));
        Assert.Equal("Edit,Copy", Named(full - 110));
        // Unordered names then follow the group rule: the first group, then the second.
        Assert.Equal("Copy", Named(full - 111));
        Assert.Equal("Copy", Named(full - 210));
        Assert.Equal(string.Empty, Named(full - 211));
        Assert.Equal("Edit,Crop,Copy,Cancel", Named(full));
    });

    [Fact]
    public void StagesHoldTheWidthIconCommandsGrowToWhileTheirCaptionsShow() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            Button privacy = IconCommand("Privacy"), crop = IconCommand("Crop");
            var done = new Button { Content = "Done" };
            ShortcutPresentation.Attach(done, ShortcutScope.Annotation, "Done");
            var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { privacy, crop } };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { done } };
            var panel = new CommandGroupsPanel { Children = { left, right }, Resources = SystemHighContrastThemeTests.LoadTheme() };
            panel.Measure(new Size(double.PositiveInfinity, 200));
            double resting = left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width;
            double revealed = RevealedWidth(panel);
            Assert.True(revealed > resting + 20, $"Ctrl+Shift+R should need room beyond the resting face: {resting} → {revealed}");
            double privacyRest = privacy.DesiredSize.Width, cropRest = crop.DesiredSize.Width;
            Assert.Equal(cropRest, privacyRest, 2);

            double Layout(double width, bool alt)
            {
                ShortcutPresentation.SetAltRevealed(panel, alt);
                panel.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
                panel.Measure(new Size(width, 200));
                panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
                panel.UpdateLayout();
                return right.TranslatePoint(default, panel).Y;
            }

            // The width that holds every revealed caption keeps one row through Alt.
            double fits = Math.Ceiling(revealed);
            foreach (bool alt in new[] { false, true, false })
            {
                Assert.Equal(0, Layout(fits, alt));
                Assert.True(left.DesiredSize.Width + CommandGroupsPanel.GroupGap + right.DesiredSize.Width <= fits + 0.01);
                // Only the command whose chord does not fit its resting face widens.
                Assert.Equal(cropRest, crop.DesiredSize.Width, 2);
                if (alt) Assert.True(privacy.DesiredSize.Width > privacyRest + 20);
                else Assert.Equal(privacyRest, privacy.DesiredSize.Width, 2);
            }

            // Even though the resting commands would fit, a narrower bar cannot hold Alt's widths,
            // so it already uses its final row arrangement before Alt is pressed.
            Assert.True(resting < fits - 2);
            foreach (bool alt in new[] { false, true, false })
                Assert.True(Layout(fits - 2, alt) > 0);
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    private static Button IconCommand(string command)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Viewbox { Width = 20, Height = 20, Child = new Border { Width = 16, Height = 16 } },
                    new TextBlock { Text = command, Margin = new Thickness(6, 0, 0, 0) },
                },
            },
        };
        AutomationProperties.SetName(button, command);
        ShortcutPresentation.Attach(button, ShortcutScope.Annotation, command);
        CommandButtonPresentation.AlwaysIcon(button);
        return button;
    }

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
