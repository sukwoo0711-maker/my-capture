using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoEditorCommandButtonTests
{
    [Theory]
    [InlineData("ko-KR", 760)]
    [InlineData("en-US", 760)]
    [InlineData("ko-KR", 800)]
    [InlineData("en-US", 960)]
    [InlineData("ko-KR", 1280)]
    [InlineData("ko-KR", 1400)]
    [InlineData("en-US", 1400)]
    public void CommandAndTransportButtonsKeepTheirHeightWithAltRevealedAndCollapsedHints(string language, int width) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var culture = UiText.UseLanguage(language);
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        VideoEditorWindow? editor = null;
        try
        {
            ShortcutPresentation.ReadEnabled = () => false;
            ShortcutPresentation.Refresh();
            editor = OpenLayout(width);
            editor.SetDetailsExpandedForTest(true);
            editor.UpdateLayout();
            var layout = (Grid)editor.Content;
            Grid bar = Named<Grid>(editor, "VideoEditorCommandBar");
            Grid transport = Named<Grid>(editor, "VideoTransport");
            StackPanel precise = Named<StackPanel>(editor, "VideoPreciseTransport");
            foreach (bool enabled in new[] { false, true, false, true })
            {
                ShortcutPresentation.SetAltRevealed(editor, enabled);
                editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                var groups = Assert.Single(Descendants(bar).OfType<MyCapture.App.Editing.CommandGroupsPanel>());
                Assert.Equal(44, groups.ActualHeight);
                var left = (FrameworkElement)groups.Children[0];
                var right = (FrameworkElement)groups.Children[1];
                Assert.Equal(left.TranslatePoint(default, groups).Y, right.TranslatePoint(default, groups).Y, 2);
                foreach (FrameworkElement row in new FrameworkElement[] { bar, transport, precise })
                {
                    ButtonBase[] buttons = Descendants(row).OfType<ButtonBase>().Where(button => button.IsVisible).ToArray();
                    Assert.NotEmpty(buttons);
                    foreach (ButtonBase button in buttons)
                    {
                        Assert.Equal(44, button.ActualHeight);
                        bool collapsedLabel = button.Content is Grid { Name: "CommandButtonFace" } commandFace
                            && commandFace.Children[0] is StackPanel primaryRow
                            && primaryRow.Children.OfType<TextBlock>().Any(label => label.Visibility == Visibility.Collapsed);
                        double horizontalPadding = collapsedLabel ? 4 : 10;
                        Assert.Equal(new Thickness(horizontalPadding, 4, horizontalPadding, 4), button.Padding);
                        Assert.Equal(new Thickness(3, 0, 3, 0), button.Margin);
                        AssertInside(button, layout, layout.ActualHeight);
                        string gesture = AutomationProperties.GetAcceleratorKey(button);
                        if (!string.IsNullOrEmpty(gesture))
                        {
                            Assert.Contains(gesture, Assert.IsType<string>(button.ToolTip));
                            TextBlock hint = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == gesture);
                            ShortcutMotionTestScope.AssertState(hint, enabled);
                            Grid face = Assert.IsType<Grid>(button.Content);
                            Assert.Equal(new GridLength(14), face.RowDefinitions[1].Height);
                            Assert.Equal(CommandButtonPresentation.HintHeight, face.RowDefinitions[1].ActualHeight, 2);
                            if (enabled) AssertInside(hint, button, button.ActualHeight);
                            else
                            {
                                FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(face.Children[0]);
                                double center = content.TranslatePoint(new Point(0, content.ActualHeight / 2), button).Y;
                                Assert.InRange(center, button.ActualHeight / 2 - 1, button.ActualHeight / 2 + 1);
                            }
                        }
                    }
                }
                ButtonBase[] commands = Descendants(bar).OfType<ButtonBase>().Where(button => button.IsVisible).ToArray();
                for (int first = 0; first < commands.Length; first++)
                {
                    Rect firstBounds = commands[first].TransformToAncestor(bar).TransformBounds(new Rect(commands[first].RenderSize));
                    for (int second = first + 1; second < commands.Length; second++)
                    {
                        Rect secondBounds = commands[second].TransformToAncestor(bar).TransformBounds(new Rect(commands[second].RenderSize));
                        Assert.False(firstBounds.IntersectsWith(secondBounds), $"Commands overlap with hints {enabled}: {firstBounds}, {secondBounds}.");
                        if (Math.Abs(firstBounds.Top - secondBounds.Top) < 0.5)
                        {
                            double gap = Math.Max(firstBounds.Left, secondBounds.Left) - Math.Min(firstBounds.Right, secondBounds.Right);
                            Assert.True(gap >= 5.5, $"command gap {gap} with hints {enabled}");
                        }
                    }
                }
            }
        }
        finally
        {
            editor?.Close();
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ToolbarKeepsTrimAndTextWhileRareCommandsStayInMoreAtEveryWidth(string language) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            foreach (int width in new[] { 760, 1400, 760 })
            {
                editor.Width = width;
                editor.UpdateLayout();
                foreach (string field in new[] { "_trimToolButton", "_addTextToolButton", "_exportToolButton" })
                {
                    Button tool = Field<Button>(editor, field);
                    Assert.True(tool.IsVisible);
                    AssertInside(tool, (Grid)editor.Content, ((Grid)editor.Content).ActualHeight);
                }
                Button more = Field<Button>(editor, "_moreToolsButton");
                Assert.True(more.IsVisible);
                ContextMenu menu = Assert.IsType<ContextMenu>(more.ContextMenu);
                foreach ((string key, string hint) in new[]
                {
                    ("VideoMulticut_Split", "Ctrl+K"), ("VideoEditorUx_FrameImageEdit", "E"),
                })
                {
                    MenuItem item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => (string)candidate.Header == UiText.Get(key));
                    Assert.Equal(Visibility.Visible, item.Visibility);
                    Assert.Equal(hint, item.InputGestureText);
                    Assert.Contains(hint, Assert.IsType<string>(item.ToolTip));
                }
                Assert.False(Field<Button>(editor, "_splitToolButton").IsVisible);
                Assert.False(Field<Button>(editor, "_frameEditToolButton").IsVisible);
                Button frame = Field<Button>(editor, "_frameEditToolButton");
                MenuItem frameItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "E");
                frame.IsEnabled = false;
                Assert.False(frameItem.IsEnabled);
                frame.IsEnabled = true;
                Assert.True(frameItem.IsEnabled);
            }
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void LayerChangesKeepToolbarActionsAndTheDetailsChoiceStable() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            Grid bar = Named<Grid>(editor, "VideoEditorCommandBar");
            string[] before = Descendants(bar).OfType<Button>().Where(button => button.IsVisible).Select(AutomationProperties.GetName).ToArray();
            foreach (bool details in new[] { false, true, false })
            {
                editor.SetDetailsExpandedForTest(details);
                typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
                Assert.Single(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
                Assert.True(editor.HandleShortcut(System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control));
                editor.UpdateLayout();
                Assert.Equal(details, editor.AreDetailsExpandedForTest);
                Assert.Equal(before, Descendants(bar).OfType<Button>().Where(button => button.IsVisible).Select(AutomationProperties.GetName).ToArray());
            }
        }
        finally { editor.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void HistoryUsesIconsAndLayerActionsKeepTheirShortLabelsAndSharedHeight(string language) => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            editor.SetDetailsExpandedForTest(true);
            foreach (bool revealed in new[] { false, true, false })
            {
                ShortcutPresentation.SetAltRevealed(editor, revealed);
                editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                foreach (string field in new[] { "_undoButton", "_redoButton" })
                {
                    Button button = Field<Button>(editor, field);
                    Assert.True(button.IsVisible);
                    Assert.Equal(44, button.ActualHeight);
                    string gesture = AutomationProperties.GetAcceleratorKey(button);
                    TextBlock hint = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == gesture);
                    ShortcutMotionTestScope.AssertState(hint, revealed);
                    Assert.DoesNotContain(Descendants(button).OfType<TextBlock>(), text => text.IsVisible
                        && !string.IsNullOrEmpty(text.Text) && text.Text != gesture);
                    Assert.Contains(gesture, Assert.IsType<string>(button.ToolTip));
                    if (revealed) AssertInside(hint, button, button.ActualHeight);
                }
                foreach ((string field, string key) in new[] { ("_editTextButton", "Text_87B0ACEF85A7"), ("_deleteTextButton", "Text_6139B6C3ED73") })
                {
                    Button button = Field<Button>(editor, field);
                    TextBlock label = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == UiText.Get(key));
                    Assert.True(label.IsVisible);
                    Assert.Equal(36, button.ActualHeight);
                    Assert.True(label.ActualWidth + 0.5 >= label.DesiredSize.Width - label.Margin.Left - label.Margin.Right);
                    AssertInside(label, button, button.ActualHeight);
                    TextBlock hint = Assert.Single(Descendants(button).OfType<TextBlock>(), text =>
                        text.Text == AutomationProperties.GetAcceleratorKey(button));
                    ShortcutMotionTestScope.AssertState(hint, revealed);
                    if (revealed) AssertInside(hint, button, button.ActualHeight);
                }
            }
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void OverflowZoomOpensTheDetailedTimelineWithoutOpeningTheInspector() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            Button more = Field<Button>(editor, "_moreToolsButton");
            Assert.True(more.IsVisible);
            foreach (string command in new[] { "ZoomIn", "ZoomOut" })
            {
                editor.TimelineForTest.SetDetailVisible(false);
                MenuItem zoom = Assert.Single(more.ContextMenu!.Items.OfType<MenuItem>(), item =>
                    item.InputGestureText == ShortcutCatalog.Get(ShortcutScope.Video, command).Hint);
                Assert.True(zoom.IsEnabled);
                zoom.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(editor.TimelineForTest.DetailVisible);
                Assert.False(editor.AreDetailsExpandedForTest);
            }
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void VisibleExportOpensTheDefaultVideoDialog() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        VideoEditorWindow editor = OpenLayout(760);
        VideoExportDialog? hosted = null;
        try
        {
            editor.ExportDialogHostForTest = dialog => hosted = dialog;
            Button export = Field<Button>(editor, "_exportToolButton");
            Assert.True(export.IsVisible && export.IsEnabled);
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(hosted);
            Assert.False(hosted.IsGif);
            Assert.Same(editor, hosted.Owner);
        }
        finally { hosted?.Close(); editor.Close(); }
    });

    [Fact]
    public void TrimAndSplitUseDifferentNamedGeometryResources() => StaTestHost.Run(() =>
    {
        using var motion = new ShortcutMotionTestScope();
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            SymbolIcon trim = Assert.Single(Descendants(Field<Button>(editor, "_trimToolButton")).OfType<SymbolIcon>());
            Button splitButton = Field<Button>(editor, "_splitToolButton");
            splitButton.Resources.MergedDictionaries.Add(editor.Resources);
            SymbolIcon split = Assert.Single(Descendants((DependencyObject)splitButton.Content).OfType<SymbolIcon>());
            Assert.Same(editor.FindResource("Icon.Trim"), trim.Data);
            // The menu-only command is not in the window's visual tree, so resolve its dynamic
            // icon resource against the owning editor explicitly before checking the geometry.
            Assert.Same(editor.FindResource("Icon.Split"), split.Data);
            Assert.NotEqual(Assert.IsAssignableFrom<Geometry>(trim.Data).ToString(), Assert.IsAssignableFrom<Geometry>(editor.FindResource("Icon.Split")).ToString());
            Assert.Equal(CommandButtonPresentation.IconSize, trim.Size);
            Assert.Equal(CommandButtonPresentation.IconSize, split.Size);
        }
        finally { editor.Close(); }
    });

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Assert.Single(Descendants(root).OfType<T>(), element => element.Name == name);

    private static VideoEditorWindow OpenLayout(int width) => OpenReadyEditor(
        Path.Combine(AppContext.BaseDirectory, "button-spec-in-memory"), 10_000, show: true, width, 760);
}
