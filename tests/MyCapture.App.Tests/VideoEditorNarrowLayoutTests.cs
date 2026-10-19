using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

/// <summary>
/// In a small video editor window the preview keeps its space: the trim bar fits its
/// range and summary, lengths and layer facts are shown once, and the layer panel is the
/// list of layers with commands for the selected one.
/// </summary>
public sealed class VideoEditorNarrowLayoutTests
{
    [Theory]
    [InlineData("ko-KR", 760, 555, false)]
    [InlineData("ko-KR", 780, 560, false)]
    [InlineData("en-US", 760, 555, false)]
    [InlineData("ko-KR", 1180, 760, false)]
    [InlineData("en-US", 1400, 900, false)]
    public void TrimBarFitsStartEndResetAndSummaryWithoutMarkButtons(string language, int width, int height, bool twoRows) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-trim-rows-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            var layout = (Grid)editor.Content;
            Panel bar = Assert.Single(Descendants(layout).OfType<Panel>(), panel => panel.Name == "VideoTrimBar");
            if (editor.ActualWidth < width - 1) return; // A runner screen smaller than the window clamps it.
            Assert.Equal(twoRows, editor.TrimBarHasTwoRowsForTest);

            Assert.DoesNotContain(Descendants(bar).OfType<Button>(), button =>
                AutomationProperties.GetName(button) == UiText.Get("VideoEditorUx_MarkInHere")
                || AutomationProperties.GetName(button) == UiText.Get("VideoEditorUx_MarkOutHere"));
            Button reset = Button(layout, "VideoEditorUx_ResetTrim");
            StackPanel summary = Assert.Single(Descendants(bar).OfType<StackPanel>(), panel => panel.Name == "VideoTrimSummary");
            TextBox start = Assert.Single(Descendants(bar).OfType<TextBox>(), box => AutomationProperties.GetName(box) == UiText.Get("Video.TrimIn"));
            TextBox end = Assert.Single(Descendants(bar).OfType<TextBox>(), box => AutomationProperties.GetName(box) == UiText.Get("Video.TrimOut"));
            double Middle(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), bar).Y;
            double Left(FrameworkElement element) => element.TranslatePoint(default, bar).X;
            double Right(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth, 0), bar).X;

            // The typed range and the reset button always share one row: reset is never left alone.
            Assert.Equal(Middle(start), Middle(end), precision: 0);
            Assert.Equal(Middle(start), Middle(reset), precision: 0);
            Assert.True(Left(end) > Right(start) - 0.5 && Left(reset) > Right(end) - 0.5);
            if (twoRows)
            {
                Assert.True(summary.TranslatePoint(default, bar).Y >= start.TranslatePoint(new Point(0, start.ActualHeight), bar).Y - 0.5,
                    "the summary sits under the range");
                // Both rows start at the same left edge.
                Assert.Equal(Left(summary), Left((FrameworkElement)VisualTreeHelper.GetParent(start)), precision: 0);
            }
            else
            {
                Assert.Equal(Middle(summary), Middle(start), precision: 0);
                Assert.True(Left(summary) >= Right(reset) + TrimBarPanel.GroupGap - 0.5);
            }

            foreach (FrameworkElement control in new FrameworkElement[] { summary, reset, start, end })
                Assert.True(Right(control) <= bar.ActualWidth + 0.5, $"{AutomationProperties.GetName(control)} leaves the bar at {width}");
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void TrimBarKeepsGroupsTogetherWhenNarrowedAndRefitsWhenWidened() => StaTestHost.Run(() =>
    {
        var range = new Border { Width = 410, Height = 36 };
        var summary = new Border { Width = 180, Height = 40 };
        var bar = new TrimBarPanel();
        bar.Children.Add(range);
        bar.Children.Add(summary);
        foreach (double width in new[] { 700d, 500d, 700d })
        {
            bar.Measure(new Size(width, double.PositiveInfinity));
            bar.Arrange(new Rect(0, 0, width, bar.DesiredSize.Height));
            Assert.Equal(width == 500, bar.IsTwoRows);
            Point summaryOrigin = summary.TranslatePoint(default, bar);
            Assert.Equal(width == 500 ? 0 : width - 180, summaryOrigin.X);
            Assert.Equal(width == 500 ? 36 + TrimBarPanel.RowGap : 0, summaryOrigin.Y);
            Assert.True(summaryOrigin.X + summary.ActualWidth <= width);
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void LengthsAndLayerFactsAppearOnce(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-no-repeat-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 780, 560);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", flags)!.Invoke(editor, [false]);
            Assert.True(editor.TimelineForTest.SetTrimRange(2_000, 8_000));
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
            // The layout-only clip reports a media failure after opening; the idle status is
            // what every later refresh writes.
            typeof(VideoEditorWindow).GetMethod("UpdateStatusForMode", flags)!.Invoke(editor, null);
            var layout = (Grid)editor.Content;
            TextBlock[] visible = Descendants(layout).OfType<TextBlock>().Where(text => text.IsVisible && text.Text.Length > 0).ToArray();

            // The kept and cut lengths: only in the trim bar's summary.
            string kept = TrimPresentation.KeptLength(2_000, 8_000);
            string removed = TrimPresentation.RemovedLength(10_000, 2_000, 8_000);
            Assert.Single(visible, text => text.Text.Contains(kept, StringComparison.Ordinal));
            Assert.Single(visible, text => text.Text.Contains(removed, StringComparison.Ordinal));
            TextBlock status = Field<TextBlock>(editor, "_statusLabel");
            Assert.Equal(UiText.Get("VideoEditorUx_StatusTrimmed"), status.Text);

            // The layer panel: no properties block repeating the selected row.
            Border panel = Assert.Single(Descendants(layout).OfType<Border>(), border => border.Name == "VideoLayersPanel");
            Assert.DoesNotContain(Descendants(panel).OfType<TextBlock>(), text => text.IsVisible && text.Text == UiText.Get("Workspace.Properties"));
            Assert.Equal(new[] { UiText.Get("Video.AddLayer") },
                Descendants(panel).OfType<Button>().Where(button => button.IsVisible && Descendants(button).OfType<TextBlock>().Any(
                    text => text.Text == UiText.Get("Video.AddLayer"))).Select(AutomationProperties.GetName));
            ListBox list = Assert.Single(Descendants(panel).OfType<ListBox>());
            var item = Assert.IsType<ListBoxItem>(list.Items[0]);
            TextBlock[] lines = Descendants(item).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^\d+:\d\d\.\d{3}–\d+:\d\d\.\d{3}$", lines[1].Text);
            // Edit and delete for the selected row sit under the list.
            foreach (string key in new[] { "Text_AEB8B9AA4E6F", "Text_9163C86EDB22" })
                Assert.True(Assert.Single(Descendants(panel).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(key)).IsVisible, key);

            // A text layer alone gets one track: no empty "no frame layers" lane under it.
            VideoLayerTimeline tracks = Assert.Single(Descendants(layout).OfType<VideoLayerTimeline>());
            int layers = Field<VideoEditDocument>(editor, "_editDocument").TextOverlays.Count + Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers.Count;
            Assert.Equal(1, layers);
            Assert.Equal(36, tracks.Height);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Theory]
    [InlineData("ko-KR", 760, 555)]
    [InlineData("en-US", 760, 555)]
    [InlineData("ko-KR", 920, 660)]
    [InlineData("en-US", 1400, 900)]
    public void LayerHeadingKeepsItsWidthBesideAddLayerAndSelectionCommandsSitUnderTheList(string language, int width, int height) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-layer-heading-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
            var layout = (Grid)editor.Content;
            Border panel = Assert.Single(Descendants(layout).OfType<Border>(), border => border.Name == "VideoLayersPanel");
            Assert.True(panel.IsVisible);

            // Edit and delete beside the heading left the 14 DIP heading 42 DIP in the narrowest
            // panel and cut its last letter. An element given less than it needs keeps its own size
            // and is clipped to its slot, so the slot is what shows whether the heading fits.
            TextBlock heading = Assert.Single(Descendants(panel).OfType<TextBlock>(), text => text.Text == UiText.Get("Video.Layers"));
            Rect slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(heading);
            Assert.True(slot.Width - heading.Margin.Left - heading.Margin.Right + 0.5 >= heading.ActualWidth,
                $"heading slot {slot.Width:0.#} for {heading.ActualWidth:0.#} DIP");
            // The UX review flags a text whose measured size exceeds its arranged size.
            Assert.True(heading.DesiredSize.Width <= heading.ActualWidth + 1);

            Button Command(string key) => Assert.Single(Descendants(panel).OfType<Button>(), button => AutomationProperties.GetName(button) == UiText.Get(key));
            Button add = Command("Video.AddLayer");
            double headingRight = heading.TranslatePoint(new Point(heading.ActualWidth, 0), panel).X;
            Point addTop = add.TranslatePoint(new Point(), panel);
            Assert.True(addTop.X >= headingRight + 8 - 0.5, $"Add layer at {addTop.X:0.#}, heading ends at {headingRight:0.#}");

            ListBox list = Assert.Single(Descendants(panel).OfType<ListBox>());
            Assert.True(addTop.Y + add.ActualHeight <= list.TranslatePoint(new Point(), panel).Y + 0.5, "Add layer sits in the heading row");
            double listBottom = list.TranslatePoint(new Point(0, list.ActualHeight), panel).Y;
            foreach (Button command in new[] { add, Command("Text_AEB8B9AA4E6F"), Command("Text_9163C86EDB22") })
            {
                Assert.True(command.IsVisible);
                Point bottomRight = command.TranslatePoint(new Point(command.ActualWidth, command.ActualHeight), panel);
                Assert.InRange(bottomRight.X, 0, panel.ActualWidth - panel.Padding.Right + 0.5);
                Assert.InRange(bottomRight.Y, 0, panel.ActualHeight - panel.Padding.Bottom + 0.5);
                if (!ReferenceEquals(command, add))
                    Assert.True(command.TranslatePoint(new Point(), panel).Y >= listBottom - 0.5, "Edit and delete sit under the list");
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
    [InlineData(780, 560)]
    [InlineData(920, 680)]
    public void ThePreviewKeepsMoreThanATinyStripWithALayerOpen(int width, int height) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-preview-room-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, width, height);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
            var layout = (Grid)editor.Content;
            Grid viewport = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoPreviewViewport");
            // With a layer open, the 780x560 preview used to get 126 DIP and the 920x680 one 274.
            // The repeated properties block, the empty track lane and the reset button's lone
            // row are gone, and that height now goes to the preview.
            double expected = height switch { <= 560 => 145, _ => 290 };
            Assert.True(viewport.ActualHeight >= expected, $"preview {viewport.ActualHeight:0.#} DIP at {width}x{height}");
            // The commands that finish the edit stay inside the window.
            foreach (string key in new[] { "MediaExport_SaveEdits", "Text_9A9C87658130" })
                AssertInside(Button(layout, key), (FrameworkElement)VisualTreeHelper.GetParent(layout), ((FrameworkElement)VisualTreeHelper.GetParent(layout)).ActualHeight);
            Button export = Button(layout, "MediaExport_ExportFormats");
            if (export.IsVisible)
                AssertInside(export, (FrameworkElement)VisualTreeHelper.GetParent(layout), ((FrameworkElement)VisualTreeHelper.GetParent(layout)).ActualHeight);
            else
                Assert.Contains(Field<Button>(editor, "_moreToolsButton").ContextMenu!.Items.OfType<MenuItem>(), item =>
                    item.Visibility == Visibility.Visible && (string)item.Header == UiText.Get("MediaExport_ExportFormats"));
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void OnlyTheFinishingCommandIsFilled() => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-roles-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var layout = (Grid)editor.Content;
            object primary = editor.FindResource("Button.Primary");
            // Save to library finishes the edit; play is a wider transport target but not
            // a second filled accent button competing with it.
            Button filled = Assert.Single(Descendants(layout).OfType<Button>(), button => ReferenceEquals(button.Style, primary));
            Assert.Equal(UiText.Get("MediaExport_SaveEdits"), AutomationProperties.GetName(filled));
            Button play = Button(layout, "Text_9A9C87658130");
            Assert.Same(editor.FindResource("Button.Secondary"), play.Style);
            Assert.True(play.ActualWidth >= 56);
            Assert.Equal(44, play.ActualHeight);
            Assert.Same(editor.FindResource("Button.Secondary"), Button(layout, "MediaExport_ExportFormats").Style);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    private static Button Button(DependencyObject root, string key) =>
        Assert.Single(Descendants(root).OfType<Button>(), button => AutomationProperties.GetName(button) == UiText.Get(key));
}
