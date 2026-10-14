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
            Assert.Contains(texts, text => text.Text == "동영상 편집");
            Assert.Contains(texts, text => text.Text == "동영상 0:10.000 · 640×360 · 30fps");
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
        editor.FitCommandBarForTest(1360);
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

            // Undo/redo, then trim · split · add text · frame image, then export · save at the right.
            double[] order =
            [
                X(UiText.Get("Text_CE706412FA75")), X(UiText.Get("Text_078C44D5A66E")),
                X(UiText.Get("VideoEditorUx_Trim")), X(UiText.Get("VideoMulticut_Split")),
                X(UiText.Get("VideoEditorUx_AddText")), X(UiText.Get("VideoEditorUx_FrameImageEdit")),
                X(UiText.Get("MediaExport_ExportFormats")), X(UiText.Get("MediaExport_SaveEdits")),
            ];
            Assert.Equal(order.Order().ToArray(), order);
            // Saving into the library finishes the edit, so it carries the emphasis, as Done does.
            Button saveEdits = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_SaveEdits"));
            Button exportFile = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_ExportFormats"));
            Assert.Same(saveEdits.FindResource("Button.Primary"), saveEdits.Style);
            Assert.Same(exportFile.FindResource("Button.Secondary"), exportFile.Style);
            foreach (var (name, gesture) in new[]
            {
                ("VideoEditorUx_Trim", "X"), ("VideoMulticut_Split", "Ctrl+K"),
                ("VideoEditorUx_AddText", "Ctrl+T"), ("VideoEditorUx_FrameImageEdit", "E"),
            })
            {
                Button tool = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(name));
                Assert.Equal(gesture, AutomationProperties.GetAcceleratorKey(tool));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(tool)), name);
                Assert.Contains(Descendants(tool).OfType<TextBlock>(), text => text.IsVisible && text.Text == UiText.Get(name));
            }

            Assert.Equal(0, editor.CommandBarLevelForTest);
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
    public void RemovedSegmentOffersRestoreInTheSplitSlotWithoutHidingOtherTools(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        string root = OwnedTestDirectory.Create("mc-video-ux-cut-tools-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true, 760, 555);
            var layout = (Grid)editor.Content;
            Grid bar = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoEditorCommandBar");
            Button split = Field<Button>(editor, "_splitToolButton");
            Button restore = Field<Button>(editor, "_restoreSegmentButton");
            Assert.True(split.IsVisible);
            Assert.False(restore.IsVisible);
            foreach (double position in new[] { 2000d, 4000d })
            {
                editor.TimelineForTest.SetPlayhead(position);
                Assert.True(editor.HandleShortcut(Key.K, ModifierKeys.Control));
            }
            editor.TimelineForTest.SelectSegmentAt(3000);
            Assert.True(editor.HandleShortcut(Key.Delete, ModifierKeys.None));
            editor.TimelineForTest.SelectSegmentAt(3000);
            editor.UpdateLayout();
            Assert.False(split.IsVisible);
            Assert.True(restore.IsVisible);
            Assert.True(restore.IsEnabled);
            AssertToolsFit();

            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.UpdateLayout();
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").RemovedRanges);
            Assert.True(split.IsVisible);
            Assert.False(restore.IsVisible);
            AssertToolsFit();

            void AssertToolsFit()
            {
                Assert.InRange(editor.CommandBarLevelForTest, 0, 3);
                Assert.True(Field<Button>(editor, "_trimToolButton").IsVisible);
                Assert.True(Field<Button>(editor, "_addTextToolButton").IsVisible);
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

            // Frame image edit, save and export never leave; trim and add text keep X, Ctrl+T,
            // the trim bar and the layer menu when the narrowest bar drops them.
            foreach (string key in new[] { "VideoEditorUx_FrameImageEdit", "MediaExport_SaveEdits", "MediaExport_ExportFormats" })
                Assert.True(Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(key)).IsVisible, key);
            if (width <= 760) Assert.True(editor.CommandBarLevelForTest >= 3, $"level {editor.CommandBarLevelForTest} at {width}");
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
            int wide = editor.CommandBarLevelForTest;
            Assert.InRange(wide, 0, 2);
            int narrowest = wide;
            foreach (var (width, height) in new[] { (760, 555), (960, 516), (1180, 760), (760, 555), (1400, 820) })
            {
                editor.MinHeight = Math.Min(editor.MinHeight, height);
                editor.Width = width;
                editor.Height = height;
                editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));
                Button save = Assert.Single(Descendants(bar).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get("MediaExport_SaveEdits"));
                AssertInside(save, layout, layout.ActualHeight);
                if (width == 760) narrowest = Math.Max(narrowest, editor.CommandBarLevelForTest);
            }

            Assert.True(narrowest > wide, $"760 DIP kept level {narrowest}, the wide window had {wide}");
            Assert.Equal(wide, editor.CommandBarLevelForTest);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void LayerPanelOpensWithTheFirstLayerAndClosesWithTheLast() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-panel-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var layout = (Grid)editor.Content;
            Border panel = Assert.Single(Descendants(layout).OfType<Border>(), border => border.Name == "VideoLayersPanel");
            Grid viewport = Assert.Single(Descendants(layout).OfType<Grid>(), grid => grid.Name == "VideoPreviewViewport");
            void Settle() => editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(editor.UpdateLayout));

            // A plain trim keeps the whole width for the preview: no empty layer panel.
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.False(editor.IsLayerPanelVisibleForTest);
            double wide = viewport.ActualWidth;

            void AddShape() => typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            void Undo() => typeof(VideoEditorWindow).GetMethod("RestoreEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            AddShape();
            Settle();
            Assert.True(editor.IsLayerPanelVisibleForTest);
            Assert.True(viewport.ActualWidth < wide - 200, $"preview {viewport.ActualWidth} of {wide}");
            Undo();
            Settle();
            Assert.False(editor.IsLayerPanelVisibleForTest);
            Assert.Equal(wide, viewport.ActualWidth, precision: 0);

            // The switch in the command bar is the user's choice and wins over the automatic rule.
            var toggle = Assert.Single(Descendants(layout).OfType<System.Windows.Controls.Primitives.ToggleButton>(),
                t => AutomationProperties.GetName(t) == UiText.Get("VideoEditorUx_LayerPanel"));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(toggle)));
            toggle.IsChecked = true;
            Settle();
            Assert.True(editor.IsLayerPanelVisibleForTest);
            AddShape();
            Undo();
            Settle();
            Assert.True(editor.IsLayerPanelVisibleForTest);
            toggle.IsChecked = false;
            Settle();
            Assert.False(editor.IsLayerPanelVisibleForTest);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void TimelineViewToolsSitAtTheRightOfACentredTransport() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-transport-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            var layout = (Grid)editor.Content;
            StackPanel transport = Assert.Single(Descendants(layout).OfType<StackPanel>(), panel => panel.Name == "VideoTransport");
            StackPanel view = Assert.Single(Descendants(layout).OfType<StackPanel>(), panel => panel.Name == "VideoTimelineViewTools");
            var row = (Grid)VisualTreeHelper.GetParent(transport);
            double rowCentre = row.ActualWidth / 2;
            double transportCentre = transport.TranslatePoint(new Point(transport.ActualWidth / 2, 0), row).X;
            Assert.Equal(rowCentre, transportCentre, precision: 0);
            double transportRight = transport.TranslatePoint(new Point(transport.ActualWidth, 0), row).X;
            double viewLeft = view.TranslatePoint(new Point(), row).X;
            Assert.True(viewLeft > transportRight, $"view tools at {viewLeft}, transport ends at {transportRight}");
            foreach (string key in new[] { "Text_48D137437347", "Text_C5176D8C3041", "Text_6CA53DEDFF4A" })
                Assert.Contains(Descendants(view).OfType<Button>(), b => AutomationProperties.GetName(b) == UiText.Get(key));
            Assert.Contains(Descendants(view).OfType<System.Windows.Controls.Primitives.ToggleButton>(),
                t => AutomationProperties.GetName(t) == UiText.Get("VideoEditorUx_DetailTimeline"));
            Assert.Equal(2, editor.ControlRowCountForTest);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void StatusLineHintsTheNextStepInsteadOfListingCounts() => StaTestHost.Run(() =>
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
            Assert.Equal("손잡이를 끌거나 I·O로 남길 구간을 정합니다", status.Text);
            Assert.DoesNotContain("녹화 드롭", status.Text);
            Assert.True(editor.TimelineForTest.SetTrimRange(2_000, 8_000));
            typeof(VideoEditorWindow).GetMethod("UpdateStatusForMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.Equal("라이브러리에 저장 또는 내보내기로 적용합니다", status.Text);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            typeof(VideoEditorWindow).GetMethod("UpdateStatusForMode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.Equal("라이브러리에 저장 또는 내보내기로 적용합니다", status.Text);
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
    public void LayerTracksAppearOnlyOnceThereIsALayer() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        string root = OwnedTestDirectory.Create("mc-video-ux-tracks-");
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10_000, show: true);
            ScrollViewer tracks = Field<ScrollViewer>(editor, "_layerTracksScroll");
            Assert.Equal(Visibility.Collapsed, tracks.Visibility);
            typeof(VideoEditorWindow).GetMethod("AddShapeLayer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Equal(Visibility.Visible, tracks.Visibility);
            typeof(VideoEditorWindow).GetMethod("RestoreEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Empty(Field<VideoEditDocument>(editor, "_editDocument").FrameEditLayers);
            Assert.Equal(Visibility.Collapsed, tracks.Visibility);
        }
        finally
        {
            editor?.Close();
            OwnedTestDirectory.Delete(root);
        }
    });
}
