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
/// In a small video editor window the preview keeps its space: the trim bar is two
/// deliberate rows, lengths and layer facts are shown once, and the layer panel is the
/// list of layers with commands for the selected one.
/// </summary>
public sealed class VideoEditorNarrowLayoutTests
{
    [Theory]
    [InlineData("ko-KR", 760, 555, true)]
    [InlineData("ko-KR", 780, 560, true)]
    [InlineData("en-US", 760, 555, true)]
    [InlineData("ko-KR", 1180, 760, false)]
    [InlineData("en-US", 1400, 900, false)]
    public void TrimBarIsActionsAndSummaryOverStartEndAndReset(string language, int width, int height, bool twoRows) => StaTestHost.Run(() =>
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

            Button markIn = Button(layout, "VideoEditorUx_MarkInHere");
            Button markOut = Button(layout, "VideoEditorUx_MarkOutHere");
            Button reset = Button(layout, "VideoEditorUx_ResetTrim");
            StackPanel summary = Assert.Single(Descendants(bar).OfType<StackPanel>(), panel => panel.Name == "VideoTrimSummary");
            TextBox start = Assert.Single(Descendants(bar).OfType<TextBox>(), box => AutomationProperties.GetName(box) == UiText.Get("Video.TrimIn"));
            TextBox end = Assert.Single(Descendants(bar).OfType<TextBox>(), box => AutomationProperties.GetName(box) == UiText.Get("Video.TrimOut"));
            double Middle(FrameworkElement element) => element.TranslatePoint(new Point(0, element.ActualHeight / 2), bar).Y;
            double Left(FrameworkElement element) => element.TranslatePoint(default, bar).X;
            double Right(FrameworkElement element) => element.TranslatePoint(new Point(element.ActualWidth, 0), bar).X;

            // Row 1: start here, end here and the kept length, in that order.
            Assert.Equal(Middle(markIn), Middle(markOut), precision: 0);
            Assert.True(Left(markOut) > Right(markIn) - 0.5 && Left(summary) > Right(markOut) - 0.5);
            // The typed range and the reset button always share one row: reset is never left alone.
            Assert.Equal(Middle(start), Middle(end), precision: 0);
            Assert.Equal(Middle(start), Middle(reset), precision: 0);
            Assert.True(Left(end) > Right(start) - 0.5 && Left(reset) > Right(end) - 0.5);
            if (twoRows)
            {
                Assert.True(start.TranslatePoint(default, bar).Y >= markIn.TranslatePoint(new Point(0, markIn.ActualHeight), bar).Y - 0.5,
                    "the range sits under the actions");
                // Both rows start at the same left edge.
                Assert.Equal(Left(markIn), Left((FrameworkElement)VisualTreeHelper.GetParent(start)), precision: 0);
            }
            else
            {
                Assert.Equal(Middle(markIn), Middle(start), precision: 0);
            }

            foreach (FrameworkElement control in new FrameworkElement[] { markIn, markOut, reset, start, end })
                Assert.True(Right(control) <= bar.ActualWidth + 0.5, $"{AutomationProperties.GetName(control)} leaves the bar at {width}");
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
                Descendants(panel).OfType<Button>().Where(button => button.IsVisible && button.Content is string).Select(button => (string)button.Content));
            ListBox list = Assert.Single(Descendants(panel).OfType<ListBox>());
            var item = Assert.IsType<ListBoxItem>(list.Items[0]);
            TextBlock[] lines = Descendants(item).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^\d+:\d\d\.\d{3}–\d+:\d\d\.\d{3}$", lines[1].Text);
            // Edit and delete for the selected row sit above the list.
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
            foreach (string key in new[] { "MediaExport_SaveEdits", "MediaExport_ExportFormats", "Text_9A9C87658130" })
                AssertInside(Button(layout, key), (FrameworkElement)VisualTreeHelper.GetParent(layout), ((FrameworkElement)VisualTreeHelper.GetParent(layout)).ActualHeight);
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
            // Save to library finishes the edit; play is the largest transport target but not
            // a second filled accent button competing with it.
            Button filled = Assert.Single(Descendants(layout).OfType<Button>(), button => ReferenceEquals(button.Style, primary));
            Assert.Equal(UiText.Get("MediaExport_SaveEdits"), AutomationProperties.GetName(filled));
            Button play = Button(layout, "Text_9A9C87658130");
            Assert.Same(editor.FindResource("Button.Secondary"), play.Style);
            Assert.True(play.ActualWidth >= 56 && play.ActualHeight >= 48);
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
