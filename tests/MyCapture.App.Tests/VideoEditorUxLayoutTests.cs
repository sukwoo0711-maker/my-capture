using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The recording editor says what it is and groups its commands by purpose: identity,
/// history, editing tools, then save/export; narrow windows compact instead of clipping.
/// </summary>
public sealed class VideoEditorUxLayoutTests
{
    [Fact]
    public void TitleAndHeaderNameTheRecordedVideoEditor() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-title-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 1400, 820);
            FitWide(editor);
            // WindowIdentity puts the product first, as in every MyCapture window.
            Assert.Equal("MyCapture — 동영상 편집", editor.Title);
            Assert.Equal("동영상 편집 창", AutomationProperties.GetName(editor));
            var bar = Assert.Single(Descendants((DependencyObject)editor.Content).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            TextBlock[] texts = Descendants(bar).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
            Assert.DoesNotContain(texts, text => text.Text == "동영상 편집");
            Assert.DoesNotContain(texts, text => text.Text == "동영상 0:10.000 · 640×360 · 30fps");
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    /// <summary>
    /// Fits the bar to a 1400 DIP window even where the runner screen is smaller and clamps the
    /// real window; the fitting rule, not the screen, is under test.
    /// </summary>
    private static void FitWide(VideoEditorWindow editor)
    {
        editor.Width = 1400;
        editor.UpdateLayout();
    }

    [Fact]
    public void CommandBarGroupsHistoryToolsAndOutputInOrder() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-groups-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 1400, 820);
            FitWide(editor);
            var bar = Assert.Single(Descendants((DependencyObject)editor.Content).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            Assert.Equal(UiText.Get("VideoEditorUx_CommandBarName"), AutomationProperties.GetName(bar));
            double X(string name)
            {
                Button button = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == name);
                Assert.True(button.IsVisible, name);
                return button.TranslatePoint(new Point(), bar).X;
            }

            // History stays first; capture is beside export and save in the right-hand group.
            double[] order =
            [
                X(UiText.Get("Text_CE706412FA75")), X(UiText.Get("Text_078C44D5A66E")),
                X(UiText.Get("VideoEditorUx_Trim")), X(UiText.Get("VideoEditorUx_AddText")),
                X(UiText.Get("CaptureEntry.NewCapture")), X(UiText.Get("CaptureEntry.MoreOptions")),
                X(UiText.Get("MediaExport_ExportFormats")), X(UiText.Get("MediaExport_SaveEdits")),
            ];
            Assert.Equal(order.Order().ToArray(), order);
            var groups = Assert.Single(Descendants(bar).OfType<MyCapture.App.Editing.CommandGroupsPanel>());
            var editing = Assert.IsType<StackPanel>(groups.Children[0]);
            Assert.Same(Field<Button>(editor, "_undoButton"), editing.Children[0]);
            Assert.Same(Field<Button>(editor, "_redoButton"), editing.Children[1]);
            var output = Assert.IsType<StackPanel>(groups.Children[1]);
            Assert.Equal("NewCaptureSplit", Assert.IsType<StackPanel>(output.Children[0]).Name);
            Assert.Equal("VideoExport", Assert.IsType<Button>(output.Children[1]).Name);
            // Saving into the library finishes the edit, so it carries the emphasis, as Done does.
            Button saveEdits = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_SaveEdits"));
            Button exportFile = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_ExportFormats"));
            Assert.Same(saveEdits.FindResource("Button.Primary"), saveEdits.Style);
            Assert.Same(exportFile.FindResource("Button.Secondary"), exportFile.Style);
            foreach (var (name, gesture) in new[]
            {
                ("VideoEditorUx_Trim", "X"), ("VideoEditorUx_AddText", "Ctrl+T"),
            })
            {
                Button tool = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(name));
                Assert.Equal(gesture, AutomationProperties.GetAcceleratorKey(tool));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(tool)), name);
                Assert.Contains(Descendants(tool).OfType<TextBlock>(), text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text));
            }
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void RemovedSegmentEnablesRestoreInMoreWithoutHidingOtherTools(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-ux-cut-tools-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 760, 555);
            var layout = (Grid)editor.Content;
            Grid bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            ContextMenu menu = Field<Button>(editor, "_moreToolsButton").ContextMenu!;
            MenuItem split = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "Ctrl+K");
            Button restoreButton = Field<Button>(editor, "_restoreSegmentButton");
            MenuItem restore = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, AutomationProperties.GetName(restoreButton)));
            Assert.Equal(Visibility.Visible, split.Visibility);
            Assert.Equal(Visibility.Visible, restore.Visibility);
            Assert.False(restore.IsEnabled);
            foreach (double position in new[] { 2000d, 4000d })
            {
                editor.TimelineForTest.SetPlayhead(position);
                Assert.True(editor.HandleShortcut(Key.K, ModifierKeys.Control));
            }
            editor.TimelineForTest.SelectSegmentAt(3000);
            Assert.True(editor.HandleShortcut(Key.Delete, ModifierKeys.None));
            editor.TimelineForTest.SelectSegmentAt(3000);
            editor.UpdateLayout();
            Assert.Equal(Visibility.Visible, split.Visibility);
            Assert.Equal(Visibility.Visible, restore.Visibility);
            Assert.True(restore.IsEnabled);
            AssertToolsFit();

            restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            editor.UpdateLayout();
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").RemovedRanges);
            Assert.Equal(Visibility.Visible, split.Visibility);
            Assert.Equal(Visibility.Visible, restore.Visibility);
            Assert.False(restore.IsEnabled);
            AssertToolsFit();

            void AssertToolsFit()
            {
                Assert.True(Field<Button>(editor, "_trimToolButton").IsVisible);
                Button addText = Field<Button>(editor, "_addTextToolButton");
                Assert.True(addText.IsVisible || Field<Button>(editor, "_moreToolsButton").ContextMenu!.Items.OfType<MenuItem>().Any(
                    item => item.Visibility == Visibility.Visible && (string)item.Header == UiText.Get("VideoEditorUx_AddText")));
                foreach (Button button in Descendants(bar).OfType<Button>().Where(button => button.IsVisible))
                {
                    AssertInside(button, layout, layout.ActualHeight);
                    foreach (TextBlock text in Descendants(button).OfType<TextBlock>().Where(text => text.IsVisible))
                        Assert.True(text.ActualWidth + 0.5 >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right,
                            $"{language}: clipped '{text.Text}'");
                }
            }
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData(760, 555)]
    [InlineData(960, 516)]
    [InlineData(1180, 760)]
    public void NarrowWindowsCompactTheCommandBarInsteadOfClippingIt(int width, int height) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-compact-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            var layout = (Grid)editor.Content;
            var bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            foreach (Button button in Descendants(bar).OfType<Button>().Where(b => b.IsVisible))
            {
                AssertInside(button, layout, layout.ActualHeight);
                foreach (TextBlock text in Descendants(button).OfType<TextBlock>().Where(t => t.IsVisible))
                    Assert.True(text.ActualWidth + 0.5 >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right,
                        $"clipped '{text.Text}' at {width}x{height}");
            }

            // Primary edit tools and the finishing action keep full labels and hit targets.
            // Secondary actions remain reachable from More when space is tight.
            foreach (string key in new[] { "VideoEditorUx_Trim", "VideoEditorUx_AddText", "MediaExport_ExportFormats", "MediaExport_SaveEdits" })
                Assert.True(Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(key)).IsVisible, key);
            foreach (string key in new[] { "VideoMulticut_Split", "VideoEditorUx_FrameImageEdit" })
            {
                Assert.True(Field<Button>(editor, "_moreToolsButton").ContextMenu!.Items.OfType<MenuItem>().Any(
                    item => item.Visibility == Visibility.Visible && (string)item.Header == UiText.Get(key)), key);
            }
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ResizingRefitsTheCommandBarBothWays() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-resize-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 1400, 820);
            var layout = (Grid)editor.Content;
            var bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            // A runner screen smaller than 1400x820 clamps the window, so the widest level is
            // whatever that screen allows; the bar must compact when narrowed and return to it.
            int wide = Descendants(bar).OfType<Button>().Count(button => button.IsVisible);
            if (editor.ActualWidth >= 1399)
            {
                // At the full width the editing tools stay inline; only a clamped window may need More.
                Assert.Equal(9, wide);
            }

            int narrowest = wide;
            foreach (var (width, height) in new[] { (760, 555), (960, 516), (1180, 760), (760, 555), (1400, 820) })
            {
                editor.MinHeight = Math.Min(editor.MinHeight, height);
                editor.Width = width;
                editor.Height = height;
                editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                Button save = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_SaveEdits"));
                AssertInside(save, layout, layout.ActualHeight);
                if (width == 760) narrowest = Descendants(bar).OfType<Button>().Count(button => button.IsVisible);
            }

            Assert.Equal(wide, narrowest);
            Assert.Equal(wide, Descendants(bar).OfType<Button>().Count(button => button.IsVisible));
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void LayerPanelKeepsTheUsersDetailsChoiceWhenLayersChange() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-ux-panel-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var layout = (Grid)editor.Content;
            Border panel = Field<Border>(editor, "_layerPanel");
            Grid viewport = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoPreviewViewport");
            void Settle() => editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
            void AddShape() => typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            void Undo() => typeof(VideoEditorWindow).GetMethod("RestoreEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            double wide = viewport.ActualWidth;
            Assert.False(panel.IsVisible);
            AddShape(); Settle();
            Assert.False(panel.IsVisible);
            Assert.Equal(wide, viewport.ActualWidth, precision: 0);
            editor.SetDetailsExpandedForTest(true); Settle();
            Assert.True(panel.IsVisible);
            Assert.True(editor.IsLayerPanelVisibleForTest);
            Undo(); Settle();
            Assert.True(panel.IsVisible);
            Assert.True(editor.AreDetailsExpandedForTest);
            Assert.True(viewport.ActualWidth > 0 && viewport.ActualWidth < wide);
            Border details = Assert.Single(Descendants(layout).OfType<Border>(), border => border.Name == "VideoDetailsPanel");
            Assert.False(new Rect(viewport.TranslatePoint(new Point(), layout), viewport.RenderSize)
                .IntersectsWith(new Rect(details.TranslatePoint(new Point(), layout), details.RenderSize)));
            editor.SetDetailsExpandedForTest(false); Settle();
            Assert.False(panel.IsVisible);
            Assert.False(editor.IsLayerPanelVisibleForTest);
            Assert.Equal(wide, viewport.ActualWidth, precision: 0);
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void DetailsToggleSitsAtTheRightOfTheSingleTransportRow() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-video-ux-transport-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var layout = (Grid)editor.Content;
            Grid transport = Assert.Single(Descendants(layout).OfType<Grid>(), panel => panel.Name == "VideoTransport");
            var details = Assert.Single(Descendants(layout).OfType<System.Windows.Controls.Primitives.ToggleButton>(), item => item.Name == "VideoDetailsToggle");
            Border time = Assert.Single(Descendants(transport).OfType<Border>(), border => border.Name == "VideoPositionChip");
            double transportRight = time.TranslatePoint(new Point(time.ActualWidth, 0), layout).X;
            double detailsLeft = details.TranslatePoint(new Point(), layout).X;
            Assert.True(detailsLeft > transportRight, $"details at {detailsLeft}, transport ends at {transportRight}");
            Assert.Single(Descendants(transport).OfType<Button>());
            Assert.Contains(Descendants(transport).OfType<Border>(), border => border.Name == "VideoPositionChip");
            Assert.Equal(2, editor.ControlRowCountForTest); // The second row is the collapsed save progress.
        }
        finally { editor?.Close(); OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void IdleStatusDoesNotRepeatInstructionsAlreadyAvailableInTooltips() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-status-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            TextBlock status = Field<TextBlock>(editor, "_statusLabel");
            // The layout-only clip reports a media failure after opening; the idle status is
            // what every later refresh writes.
            // The lengths live in the trim bar's summary right above, and the layer count on the
            // layer switch, so the status line no longer repeats them.
            typeof(VideoEditorWindow).GetMethod("UpdateStatusForMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.True(string.IsNullOrEmpty(status.Text));
            Assert.DoesNotContain("녹화 드롭", status.Text);
            Assert.True(editor.TimelineForTest.SetTrimRange(2_000, 8_000));
            typeof(VideoEditorWindow).GetMethod("UpdateStatusForMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.True(string.IsNullOrEmpty(status.Text));
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            typeof(VideoEditorWindow).GetMethod("UpdateStatusForMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.True(string.IsNullOrEmpty(status.Text));
            // Recording health moved to the clip facts in the command bar.
            TextBlock details = Field<TextBlock>(editor, "_identityDetails");
            Assert.Equal(UiText.Get("Text_C1BE53C8ADAF"), details.ToolTip);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void LayerTracksStayVisibleBeforeAndAfterTheLastLayer() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-tracks-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            ScrollViewer tracks = Field<ScrollViewer>(editor, "_layerTracksScroll");
            Assert.Equal(Visibility.Visible, tracks.Visibility);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Equal(Visibility.Visible, tracks.Visibility);
            typeof(VideoEditorWindow).GetMethod("RestoreEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.Equal(Visibility.Visible, tracks.Visibility);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });
}
