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
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoEditorCommandButtonTests
{
    [Theory]
    [InlineData("ko-KR", 760)]
    [InlineData("en-US", 760)]
    [InlineData("ko-KR", 1400)]
    [InlineData("en-US", 1400)]
    public void CommandAndTransportButtonsKeepTheirHeightWithHintsOnAndOff(string language, int width) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        VideoEditorWindow? editor = null;
        try
        {
            bool hints = false;
            ShortcutPresentation.ReadEnabled = () => hints;
            ShortcutPresentation.Refresh();
            editor = OpenLayout(width);
            var layout = (Grid)editor.Content;
            Grid bar = Assert.Single(Descendants(layout).OfType<Grid>(), element => element.Name == "VideoEditorCommandBar");
            StackPanel transport = Assert.Single(Descendants(layout).OfType<StackPanel>(), element => element.Name == "VideoTransport");
            StackPanel view = Assert.Single(Descendants(layout).OfType<StackPanel>(), element => element.Name == "VideoTimelineViewTools");
            foreach (bool enabled in new[] { false, true, false, true })
            {
                hints = enabled;
                ShortcutPresentation.Refresh();
                editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                foreach (FrameworkElement row in new FrameworkElement[] { bar, transport, view })
                {
                    ButtonBase[] buttons = Descendants(row).OfType<ButtonBase>().Where(button => button.IsVisible).ToArray();
                    Assert.NotEmpty(buttons);
                    foreach (ButtonBase button in buttons)
                    {
                        Assert.Equal(44, button.ActualHeight);
                        Assert.Equal(new Thickness(10, 4, 10, 4), button.Padding);
                        Assert.Equal(new Thickness(3, 0, 3, 0), button.Margin);
                        Grid face = Assert.IsType<Grid>(button.Content);
                        Assert.Equal("CommandButtonFace", face.Name);
                        RowDefinition hintRow = face.RowDefinitions[1];
                        Assert.Equal(new GridLength(14), hintRow.Height);
                        double scale = VisualTreeHelper.GetDpi(face).DpiScaleY;
                        Assert.Equal(Math.Round(14 * scale), hintRow.ActualHeight * scale, 6);
                        AssertInside(button, layout, layout.ActualHeight);
                    }
                }
                foreach (ButtonBase button in Descendants(bar).OfType<ButtonBase>().Where(button => button.IsVisible))
                {
                    string gesture = AutomationProperties.GetAcceleratorKey(button);
                    if (string.IsNullOrEmpty(gesture)) continue;
                    TextBlock hint = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == gesture);
                    Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, hint.Visibility);
                }
                double transportRight = transport.TranslatePoint(new Point(transport.ActualWidth, 0), layout).X;
                double viewLeft = view.TranslatePoint(default, layout).X;
                Assert.True(viewLeft > transportRight, $"timeline view starts at {viewLeft}, transport ends at {transportRight}");
                ButtonBase[] commands = Descendants(bar).OfType<ButtonBase>().Where(button => button.IsVisible)
                    .OrderBy(button => button.TranslatePoint(default, bar).X).ToArray();
                for (int index = 1; index < commands.Length; index++)
                {
                    double right = commands[index - 1].TranslatePoint(new Point(commands[index - 1].ActualWidth, 0), bar).X;
                    double left = commands[index].TranslatePoint(default, bar).X;
                    Assert.True(left >= right + 5.5, $"command gap {left - right} with hints {enabled}");
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
    public void NarrowToolbarKeepsNamedTrimAndSplitAndMovesSecondaryActionsToMore(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            foreach (string field in new[] { "_trimToolButton", "_splitToolButton" })
            {
                Button tool = Field<Button>(editor, field);
                Assert.True(tool.IsVisible);
                Assert.Contains(Descendants(tool).OfType<TextBlock>(), text => text.IsVisible && text.Text == AutomationProperties.GetName(tool));
            }
            Button more = Field<Button>(editor, "_moreToolsButton");
            Assert.True(more.IsVisible);
            ContextMenu menu = Assert.IsType<ContextMenu>(more.ContextMenu);
            foreach ((string key, string hint) in new[]
            {
                ("VideoEditorUx_AddText", "Ctrl+T"),
                ("VideoEditorUx_FrameImageEdit", "E"),
                ("VideoEditorUx_LayerPanel", string.Empty),
            })
            {
                MenuItem item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => (string)candidate.Header == UiText.Get(key));
                Assert.Equal(Visibility.Visible, item.Visibility);
                Assert.Equal(hint, item.InputGestureText);
            }
            MenuItem layers = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.IsCheckable);
            Assert.False(editor.IsLayerPanelVisibleForTest);
            layers.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            editor.UpdateLayout();
            Assert.True(editor.IsLayerPanelVisibleForTest);
            Assert.True(layers.IsChecked);
            layers.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            editor.UpdateLayout();
            Assert.False(editor.IsLayerPanelVisibleForTest);
            Button frame = Field<Button>(editor, "_frameEditToolButton");
            MenuItem frameItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "E");
            frame.IsEnabled = false;
            Assert.False(frameItem.IsEnabled);
            frame.IsEnabled = true;
            Assert.True(frameItem.IsEnabled);

            editor.FitCommandBarForTest(1360);
            editor.UpdateLayout();
            Assert.False(more.IsVisible);
            Assert.True(frame.IsVisible);
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.Equal(Visibility.Collapsed, item.Visibility));
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void LayerCountRefitsTheToolbarEvenWhenPanelVisibilityWasChosenByTheUser() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = OpenLayout(1400);
        try
        {
            Grid bar = Assert.Single(Descendants(editor).OfType<Grid>(), element => element.Name == "VideoEditorCommandBar");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo sync = typeof(VideoEditorWindow).GetMethod("SyncLayerPanelToLayers", flags)!;
            FieldInfo refitField = typeof(VideoEditorWindow).GetField("_refitCommandBar", flags)!;
            Action originalRefit = Field<Action>(editor, "_refitCommandBar");
            ToggleButton layers = Field<ToggleButton>(editor, "_layerPanelToggle");
            layers.IsChecked = true;
            layers.IsChecked = false;
            sync.Invoke(editor, [0]);
            editor.FitCommandBarForTest(2000);
            double available = bar.Children.OfType<UIElement>().Sum(child => child.DesiredSize.Width) + 2;
            try
            {
                refitField.SetValue(editor, new Action(() => editor.FitCommandBarForTest(available)));
                editor.FitCommandBarForTest(available);
                Assert.Equal(0, editor.CommandBarLevelForTest);
                sync.Invoke(editor, [100]);
                Assert.True(editor.CommandBarLevelForTest > 0);
                Assert.False(layers.IsChecked);
                Assert.Equal("100", Field<TextBlock>(editor, "_layerPanelCount").Text);
                sync.Invoke(editor, [0]);
                Assert.Equal(0, editor.CommandBarLevelForTest);
                Assert.False(layers.IsChecked);
            }
            finally { refitField.SetValue(editor, originalRefit); }
        }
        finally { editor.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void HistoryAndLayerActionsKeepTheirShortLabelsAndSharedHeight(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            Field<ToggleButton>(editor, "_layerPanelToggle").IsChecked = true;
            editor.UpdateLayout();
            foreach ((string field, string key, double height) in new[]
            {
                ("_undoButton", "Text_CE706412FA75", 44d),
                ("_redoButton", "Text_078C44D5A66E", 44d),
                ("_editTextButton", "Text_87B0ACEF85A7", 36d),
                ("_deleteTextButton", "Text_6139B6C3ED73", 36d),
            })
            {
                Button button = Field<Button>(editor, field);
                if (!button.IsVisible)
                {
                    Assert.Contains(field, new[] { "_undoButton", "_redoButton" });
                    MenuItem item = Assert.Single(Field<Button>(editor, "_moreToolsButton").ContextMenu!.Items.OfType<MenuItem>(), item =>
                        (string)item.Header == UiText.Get(key));
                    Assert.Equal(Visibility.Visible, item.Visibility);
                    Assert.Equal(AutomationProperties.GetAcceleratorKey(button), item.InputGestureText);
                    continue;
                }
                TextBlock label = Assert.Single(Descendants(button).OfType<TextBlock>(), text => text.Text == UiText.Get(key));
                Assert.True(label.IsVisible);
                Assert.Equal(height, button.ActualHeight);
                AssertInside(button, (Grid)editor.Content, ((Grid)editor.Content).ActualHeight);
                Assert.True(label.ActualWidth + 0.5 >= label.DesiredSize.Width - label.Margin.Left - label.Margin.Right);
            }
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void OverflowZoomOpensTheDetailedTimelineLikeItsVisibleButton() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            Button more = Assert.Single(Descendants(editor).OfType<Button>(), button => button.Name == "VideoTimelineMoreTools");
            Assert.True(more.IsVisible);
            foreach (string command in new[] { "ZoomIn", "ZoomOut" })
            {
                Field<ToggleButton>(editor, "_detailToggle").IsChecked = false;
                Assert.False(editor.TimelineForTest.DetailVisible);
                MenuItem zoom = Assert.Single(more.ContextMenu!.Items.OfType<MenuItem>(), item =>
                    item.InputGestureText == ShortcutCatalog.Get(ShortcutScope.Video, command).Hint);
                Assert.True(zoom.IsEnabled);
                zoom.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(editor.TimelineForTest.DetailVisible);
                Assert.True(Field<ToggleButton>(editor, "_detailToggle").IsChecked);
            }
        }
        finally { editor.Close(); }
    });

    [Fact]
    public void OverflowExportOpensTheDefaultVideoDialog() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = OpenLayout(760);
        VideoExportDialog? hosted = null;
        try
        {
            editor.ExportDialogHostForTest = dialog => hosted = dialog;
            editor.FitCommandBarForTest(500);
            editor.UpdateLayout();
            Button more = Field<Button>(editor, "_moreToolsButton");
            MenuItem export = Assert.Single(more.ContextMenu!.Items.OfType<MenuItem>(), item =>
                (string)item.Header == UiText.Get("MediaExport_ExportFormats"));
            Assert.True(more.IsVisible);
            Assert.Equal(Visibility.Visible, export.Visibility);
            Assert.True(export.IsEnabled);
            export.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.NotNull(hosted);
            Assert.False(hosted.IsGif);
            Assert.Same(editor, hosted.Owner);
        }
        finally
        {
            hosted?.Close();
            editor.Close();
        }
    });

    [Fact]
    public void TrimAndSplitUseDifferentNamedGeometryResources() => StaTestHost.Run(() =>
    {
        VideoEditorWindow editor = OpenLayout(760);
        try
        {
            SymbolIcon trim = Assert.Single(Descendants(Field<Button>(editor, "_trimToolButton")).OfType<SymbolIcon>());
            SymbolIcon split = Assert.Single(Descendants(Field<Button>(editor, "_splitToolButton")).OfType<SymbolIcon>());
            Assert.Same(editor.FindResource("Icon.Trim"), trim.Data);
            Assert.Same(editor.FindResource("Icon.Split"), split.Data);
            Assert.NotEqual(Assert.IsAssignableFrom<Geometry>(trim.Data).ToString(), Assert.IsAssignableFrom<Geometry>(split.Data).ToString());
            Assert.Equal(CommandButtonPresentation.IconSize, trim.Size);
            Assert.Equal(CommandButtonPresentation.IconSize, split.Size);
        }
        finally { editor.Close(); }
    });

    private static VideoEditorWindow OpenLayout(int width) => OpenReadyEditor(
        Path.Combine(AppContext.BaseDirectory, "button-spec-in-memory"), 10_000, show: true, width, 760);
}
