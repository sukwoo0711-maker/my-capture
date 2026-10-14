using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using Xunit;
using static MyCapture.App.Tests.VideoEditorUxTrimTests;

namespace MyCapture.App.Tests;

public sealed class VideoTimelineFeedbackTests
{
    [Fact]
    public void LayersShareZoomedMappingAndHideOffscreenBars() => StaTestHost.Run(() =>
    {
        var timeline = new VideoLayerTimeline();
        timeline.Initialize(10000);
        var visible = new TimedTextOverlay { StartMs = 2000, EndMs = 3000 };
        var hidden = new TimedTextOverlay { StartMs = 8000, EndMs = 9000 };
        timeline.SetLayers([visible, hidden], []);
        Arrange(timeline, 482);
        var viewport = new TimelineViewport(10000, 30);
        viewport.SetView(2000, 4000);
        timeline.SetViewport(viewport);
        timeline.SelectLayer(visible.Id);
        Assert.Equal(new Rect(82, 8, 200, 20), timeline.SelectedBarBounds);
        timeline.SelectLayer(hidden.Id);
        Assert.True(timeline.SelectedBarBounds.IsEmpty);
        visible.StartMs = 1500;
        visible.EndMs = 2010;
        timeline.SelectLayer(visible.Id);
        Assert.Equal(2, timeline.SelectedBarBounds.Width, 6); // Clipped bars keep the real visible span.
        timeline.SelectLayer(hidden.Id);
        viewport.FitAll();
        timeline.SetViewport(viewport);
        Assert.False(timeline.SelectedBarBounds.IsEmpty);
        Assert.Equal(402, timeline.SelectedBarBounds.Left, 6);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void HoverUsesSourceTimeAndTheSharedMillisecondFormatter(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10000, 30);
        Arrange(timeline, 400);
        timeline.HoverForTest(timeline.TrimStripForTest.ActualWidth / 2, 30);
        Assert.Equal("0:05.000", timeline.HoverText);
        timeline.Viewport.SetView(2000, 4000);
        timeline.HoverForTest(timeline.TrimStripForTest.ActualWidth / 4, 30, overview: false);
        Assert.Equal("0:02.500", timeline.HoverText);
        timeline.ClearHoverForTest();
        Assert.Equal(string.Empty, timeline.HoverText);

        var layers = new VideoLayerTimeline();
        layers.Initialize(10000);
        layers.SetViewport(timeline.Viewport);
        Arrange(layers, 482);
        layers.HoverForTest(182);
        Assert.Equal("0:02.500", layers.HoverText);
        layers.HoverForTest(20);
        Assert.Equal(string.Empty, layers.HoverText);
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void GrabZoneShowsTheLocalizedTrimHintAndShortcut(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10000, 30);
        timeline.SetIn(2000);
        timeline.SetOut(8000);
        Arrange(timeline, 400);
        timeline.HoverForTest(90, 30);
        Assert.Equal(UiText.Get("VideoEditorUx_DragStart"), timeline.TrimStripForTest.ToolTip);
        Assert.Contains("I:", Assert.IsType<string>(timeline.TrimStripForTest.ToolTip));
        timeline.HoverForTest(310, 30);
        Assert.Equal(UiText.Get("VideoEditorUx_DragEnd"), timeline.TrimStripForTest.ToolTip);
        Assert.Contains("O:", Assert.IsType<string>(timeline.TrimStripForTest.ToolTip));
        timeline.ClearHoverForTest();
        Assert.Null(timeline.TrimStripForTest.ToolTip);
    });

    [Fact]
    public void SharedProgressWarmsUpEstimatesAndClearsThemDuringFinalization() => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage("en-US");
        var progress = new MediaExportProgressRow();
        progress.Start("Saving");
        progress.Report(new VideoFrameRenderProgress(4, 100, 0), TimeSpan.FromSeconds(1));
        Assert.Empty(progress.RemainingText);
        progress.Report(new VideoFrameRenderProgress(25, 100, 0), TimeSpan.FromSeconds(2));
        Assert.Equal(25, progress.Value);
        Assert.Equal(UiText.Format("MediaExport_Remaining", 6d), progress.RemainingText);
        progress.FinalizeProgress();
        Assert.Empty(progress.RemainingText);
        progress.Stop();
        Assert.Equal(Visibility.Collapsed, progress.Visibility);
        progress.Start("Saving");
        Assert.Equal(0, progress.Value);
        Assert.Empty(progress.RemainingText);
        Assert.True(progress.CancelButton.IsEnabled);
    });

    [Fact]
    public void LayerHoverChipStaysInsideTheScrolledViewport() => StaTestHost.Run(() =>
    {
        string root = CreateFixtureDirectory();
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10000, show: true);
            var scroll = Field<ScrollViewer>(editor, "_layerTracksScroll");
            var layers = Assert.IsType<VideoLayerTimeline>(scroll.Content);
            layers.SetLayers(Enumerable.Range(0, 8)
                .Select(_ => new TimedTextOverlay { StartMs = 1000, EndMs = 9000 }).ToArray(), []);
            scroll.Visibility = Visibility.Visible;
            editor.UpdateLayout();
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
            scroll.ScrollToVerticalOffset(96);
            scroll.UpdateLayout();
            Assert.Equal(96, scroll.VerticalOffset);
            layers.HoverForTest((VideoLayerTimeline.LabelWidth + layers.ActualWidth) / 2);
            Assert.NotEmpty(layers.HoverText);

            AssertChipVisible();
            // Scrolling again must move the chip even without another pointer move.
            scroll.ScrollToVerticalOffset(160);
            scroll.UpdateLayout();
            Assert.Equal(160, scroll.VerticalOffset);
            AssertChipVisible();

            void AssertChipVisible()
            {
                layers.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                DrawingGroup drawing = VisualTreeHelper.GetDrawing(layers)!;
                var chip = (RectangleGeometry)drawing.Children.OfType<GeometryDrawing>()
                    .Last(item => item.Geometry is RectangleGeometry { RadiusX: 3 }).Geometry;
                Assert.InRange(chip.Bounds.Top, scroll.VerticalOffset, scroll.VerticalOffset + scroll.ViewportHeight);
                Assert.InRange(chip.Bounds.Bottom, scroll.VerticalOffset, scroll.VerticalOffset + scroll.ViewportHeight);
            }
        }
        finally
        {
            editor?.Close();
        }
    });

    [Fact]
    public void NestedBusyStateDuringDraftRetirementPreservesFinalizedSaveProgress() => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage("en-US");
        string root = CreateFixtureDirectory();
        VideoEditorWindow? editor = null;
        try
        {
            editor = OpenReadyEditor(root, 10000);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo busy = typeof(VideoEditorWindow).GetMethod("SetOperationRunning", flags)!;
            var progress = Field<MediaExportProgressRow>(editor, "_saveProgress");
            var bar = progress.Children.OfType<ProgressBar>().Single();
            string operation = UiText.Get("VideoEditorUx_LibrarySaving");
            busy.Invoke(editor, [true]);
            progress.Start(operation);
            progress.Report(new VideoFrameRenderProgress(100, 100, 0), TimeSpan.FromSeconds(2));
            progress.FinalizeProgress();

            // Exercise both nested busy calls in the real retirement path.
            var retirement = (Task<bool>)typeof(VideoEditorWindow)
                .GetMethod("RetireDraftBeforeCloseAsync", flags)!.Invoke(editor, null)!;
            Assert.True(retirement.GetAwaiter().GetResult());
            Assert.Equal(100, progress.Value);
            Assert.Equal(UiText.Format("MediaExport_Percent", 100), progress.PercentText);
            Assert.Empty(progress.RemainingText);
            Assert.Equal(operation, AutomationProperties.GetName(bar));
            Assert.True(bar.IsIndeterminate);
            Assert.Equal(Visibility.Visible, progress.Visibility);

            busy.Invoke(editor, [false]);
            Assert.Equal(Visibility.Collapsed, progress.Visibility);
            busy.Invoke(editor, [true]);
            Assert.Equal(0, progress.Value);
            Assert.Equal(UiText.Get("VideoEdit_ImageLayerPreparing"), AutomationProperties.GetName(bar));
            busy.Invoke(editor, [false]);
        }
        finally
        {
            editor?.Close();
        }
    });

    [Fact]
    public void ResourceReplacementRefreshesFrozenTimelineDrawings() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        var layers = new VideoLayerTimeline();
        var host = new StackPanel();
        host.Resources["Surface.Base"] = Brushes.Black;
        host.Resources["Surface.Sunken"] = Brushes.Black;
        host.Resources["Timeline.Background"] = Brushes.Black;
        host.Children.Add(timeline);
        host.Children.Add(layers);
        timeline.Initialize(10000, 30);
        timeline.SetDetailVisible(false);
        Arrange(host, 482);
        host.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert.Equal(Colors.Black, Pixel(layers, 12, 2));
        Assert.Equal(Colors.Black, Pixel(timeline.TrimStripForTest, 123, 28));
        long before = timeline.TransientDrawCountForTest;
        host.Resources["Surface.Base"] = Brushes.White;
        host.Resources["Surface.Sunken"] = Brushes.White;
        host.Resources["Timeline.Background"] = Brushes.White;
        host.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        timeline.FlushRenderForTest();
        Assert.True(timeline.TransientDrawCountForTest > before);
        Assert.Equal(Colors.White, Pixel(layers, 12, 2));
        Assert.Equal(Colors.White, Pixel(timeline.TrimStripForTest, 123, 28));
    });

    private static Color Pixel(FrameworkElement element, int x, int y)
    {
        FrameworkElement root = element;
        while (VisualTreeHelper.GetParent(root) is FrameworkElement parent) root = parent;
        root.UpdateLayout();
        Point point = element.TranslatePoint(new Point(x, y), root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    // Preserve fixtures in the worktree; the shared cleanup helper permanently deletes them.
    private static string CreateFixtureDirectory() => Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "video-feedback-fixtures", Guid.NewGuid().ToString("N"))).FullName;

    private static void Arrange(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();
    }
}
