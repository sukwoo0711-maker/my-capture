using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opens the REAL <see cref="VideoEditorWindow"/> on a genuinely recorded clip and confirms it
/// reaches the ready state — directly guarding the field report that "a 2-second video is not
/// loaded successfully". Runs on an STA thread with a dispatcher pump, like the app.
/// </summary>
public sealed class VideoEditorWindowTests : KoreanCaptionTest
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private static RecordingResult RecordClip(string path, int ms)
    {
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        var recorder = new RegionRecorder(
            grabber,
            options => new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance),
            NullLogger.Instance);
        recorder.Start(new RectD(0, 0, 320, 240), path, new RecordingSettings { FrameRate = RecordingFrameRate.Fps15 });
        Thread.Sleep(ms);
        RecordingResult r = recorder.Stop();
        recorder.Dispose();
        return r;
    }

    private static void PumpFor(TimeSpan d)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = d };
        timer.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void TwoSecondClip_OpensAndBecomesReady_WithControlsEnabled() => RunSta(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "mc-vew-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string clip = Path.Combine(dir, "twosec.mp4");
        try
        {
            RecordingResult rec = RecordClip(clip, 2000); // ~2 seconds — the reported failing case
            Assert.True(File.Exists(clip) && new FileInfo(clip).Length > 1000, "test clip was not produced");

            using ILoggerFactory lf = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
            var initialEdits = VideoEditDocument.CreateFor(rec.Width, rec.Height, rec.DurationMs);
            initialEdits.TextOverlays.Add(new TimedTextOverlay { Text = "visible timeline caption", StartMs = 0, EndMs = rec.DurationMs });
            var editor = new VideoEditorWindow(rec, AppPaths.CreateForRoot(dir), lf, initialEdits);
            // Reuse production sizing tokens without creating a process-global WPF Application
            // in this isolated STA test. Default WPF buttons are smaller than the actual app.
            var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml") };
            editor.FontSize = (double)theme["FontSize.Body"];
            foreach (Button button in Descendants((DependencyObject)editor.Content).OfType<Button>())
            {
                button.MinHeight = (double)theme["Size.HitTarget"];
                button.Padding = (Thickness)theme["Padding.Control"];
                button.BorderThickness = new Thickness(1);
                button.FontWeight = FontWeights.SemiBold;
            }
            editor.WindowStartupLocation = WindowStartupLocation.Manual;
            editor.Width = 770;
            editor.Height = 555;
            editor.Left = -10000;
            editor.Top = -10000;
            editor.ShowActivated = false;
            editor.Show();
            editor.UpdateLayout();
            Grid layout = Assert.IsType<Grid>(editor.Content);
            Border preview = layout.Children.OfType<Border>().Single(child => Grid.GetRow(child) == 0);
            Border status = layout.Children.OfType<Border>().Single(child => Grid.GetRow(child) == 3);
            ScrollViewer timelineTools = Assert.Single(layout.Children.OfType<ScrollViewer>());
            Assert.True(preview.ActualHeight >= 112, "compact video editor lost its usable preview");
            Assert.True(status.TranslatePoint(new Point(0, status.ActualHeight), layout).Y <= layout.ActualHeight + 0.5,
                "compact video editor clipped the processing status");
            Assert.Equal(ScrollBarVisibility.Disabled, timelineTools.HorizontalScrollBarVisibility);
            var layerTracks = Descendants(layout).OfType<VideoLayerTimeline>().Single();
            layerTracks.SelectLayer(initialEdits.TextOverlays[0].Id);
            editor.UpdateLayout();
            Rect selectedBar = layerTracks.SelectedBarBounds;
            Assert.False(selectedBar.IsEmpty);
            Assert.InRange(layerTracks.TranslatePoint(selectedBar.TopLeft, timelineTools).Y, 0, timelineTools.ViewportHeight);
            Assert.InRange(layerTracks.TranslatePoint(selectedBar.BottomRight, timelineTools).Y, 0, timelineTools.ViewportHeight);
            foreach (TextBlock caption in Descendants(editor.TimelineForTest).OfType<TextBlock>())
            {
                Assert.InRange(caption.TranslatePoint(new Point(0, 0), timelineTools).Y, 0, timelineTools.ViewportHeight);
                Assert.InRange(caption.TranslatePoint(new Point(0, caption.ActualHeight), timelineTools).Y, 0, timelineTools.ViewportHeight);
            }
            foreach (FrameworkElement strip in Descendants(editor.TimelineForTest).OfType<TimelineRenderSurface>())
            {
                Assert.InRange(strip.TranslatePoint(new Point(0, 0), timelineTools).Y, 0, timelineTools.ViewportHeight);
                Assert.InRange(strip.TranslatePoint(new Point(0, strip.ActualHeight), timelineTools).Y, 0, timelineTools.ViewportHeight);
            }
            foreach (string label in new[] { "사각형", "원", "이미지" })
            {
                Button add = Descendants(layout).OfType<Button>().Single(button => Equals(button.Content, label));
                Point bottom = add.TranslatePoint(new Point(0, add.ActualHeight), layout);
                Assert.True(add.IsVisible && bottom.Y <= layout.ActualHeight, $"{label} add tool is hidden in compact editor");
            }

            // Pump the dispatcher until ready or failed, bounded. The editor guarantees it
            // resolves within its own open-timeout fallback (~5s) even if MediaOpened is slow.
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!editor.IsMediaReadyForTest && !editor.HasMediaFailedForTest && DateTime.UtcNow < deadline)
            {
                PumpFor(TimeSpan.FromMilliseconds(25));
            }

            Assert.False(editor.HasMediaFailedForTest, "editor reported media failure: " + editor.MediaFailureForTest);
            Assert.True(editor.IsMediaReadyForTest, "editor did not become ready for the 2s clip");
            Assert.True(editor.DurationMsForTest > 0, "editor reported a non-positive duration");
            Button shape = Descendants(layout).OfType<Button>().Single(button => Equals(button.Content, "사각형"));
            shape.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.UpdateLayout();
            Assert.InRange(layerTracks.TranslatePoint(layerTracks.SelectedBarBounds.BottomRight, timelineTools).Y, 0, timelineTools.ViewportHeight);
            var canvas = Descendants(layout).OfType<VideoLayerCanvas>().Single();
            Assert.NotNull(canvas.SelectedId);
            var documentField = typeof(VideoEditorWindow).GetField("_editDocument", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var spatialDocument = (VideoEditDocument)documentField.GetValue(editor)!;
            Assert.Single(spatialDocument.FrameEditLayers);
            VideoLayerBounds before = spatialDocument.FrameEditLayers[0].Bounds!;
            canvas.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(editor), 0, System.Windows.Input.Key.Right) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Assert.True(spatialDocument.FrameEditLayers[0].Bounds!.X > before.X);
            typeof(VideoEditorWindow).GetMethod("RestoreEdit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, [false]);
            Assert.Equal(before, ((VideoEditDocument)documentField.GetValue(editor)!).FrameEditLayers[0].Bounds);
            DateTime layerSeekDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while ((editor.PreviewSeekCoordinatorForTest.HasPendingForTest || editor.PreviewSeekCoordinatorForTest.IsInFlightForTest)
                && DateTime.UtcNow < layerSeekDeadline)
            {
                PumpFor(TimeSpan.FromMilliseconds(10));
            }

            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.True(timeline.IsEnabled, "two-line timeline stayed disabled after media became ready");
            Assert.Equal(editor.DurationMsForTest, timeline.DurationMs, precision: 1);
            Assert.Equal(9, timeline.FixedVisualCountForTest);
            Assert.Equal(2, editor.ControlRowCountForTest);
            Assert.True(
                editor.WidestControlRowContentWidthForTest <= editor.ControlAreaWidthForTest + 0.5,
                $"two-row controls overflowed: content={editor.WidestControlRowContentWidthForTest:0.0}, available={editor.ControlAreaWidthForTest:0.0}");
            Assert.Equal(0, timeline.ViewStartMs, precision: 1);
            Assert.True(timeline.VisibleSpanMs <= timeline.CoarseIntervalMs + 0.001);
            // Native MediaOpened callbacks use the application's language rather than the
            // caller's context-local test override. Dedicated localization tests cover both
            // catalogs; this integration test verifies the range/frame information survives.
            Assert.Matches("(?:시작|Start)", timeline.DetailRangeText);
            Assert.Matches("(?:끝|End)", timeline.DetailRangeText);
            Assert.Matches("(?:프레임|[Ff]rames?)", timeline.DetailRangeText);

            // Exercise the real timeline -> latest-wins coordinator -> MediaElement adapter path
            // against the real MP4. The dispatcher is pumped rather than synchronously waiting,
            // because MediaElement is UI-affine by contract.
            double previewTarget = editor.DurationMsForTest * 0.35;
            timeline.SeekFromOverview(previewTarget);
            PreviewSeekCoordinator coordinator = editor.PreviewSeekCoordinatorForTest;
            DateTime previewDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while ((Math.Abs(coordinator.RequestedPreviewPositionMs - previewTarget) > 0.1
                || coordinator.HasPendingForTest || coordinator.IsInFlightForTest) && DateTime.UtcNow < previewDeadline)
            {
                PumpFor(TimeSpan.FromMilliseconds(10));
            }

            Assert.True(coordinator.PresentedGeneration > 0, "real MediaElement preview seek did not complete");
            Assert.Equal(previewTarget, coordinator.IntentPositionMs, precision: 1);
            Assert.Equal(previewTarget, coordinator.RequestedPreviewPositionMs, precision: 1);

            double exactTarget = editor.DurationMsForTest * 0.65;
            long exactGeneration = coordinator.RequestExact(exactTarget);
            DateTime exactDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (coordinator.PresentedGeneration != exactGeneration && DateTime.UtcNow < exactDeadline)
            {
                PumpFor(TimeSpan.FromMilliseconds(10));
            }

            Assert.Equal(exactGeneration, coordinator.PresentedGeneration);
            Assert.Equal(PreviewSeekMode.Exact, coordinator.PresentedMode);
            Assert.Equal(exactTarget, coordinator.RequestedPreviewPositionMs, precision: 1);

            double initialSpan = timeline.VisibleSpanMs;
            timeline.SetPlayhead(editor.DurationMsForTest / 2.0);
            Assert.InRange(timeline.PlayheadMs, timeline.ViewStartMs, timeline.ViewEndMs);
            timeline.ZoomAroundPlayhead(0.5);
            Assert.False(timeline.IsFitAll, "detail timeline did not zoom into the overview selection");
            Assert.True(timeline.VisibleSpanMs < initialSpan, "zoom did not reduce the detail span");
            Assert.InRange(timeline.PlayheadMs, timeline.ViewStartMs, timeline.ViewEndMs);

            timeline.FitAll();
            Assert.True(timeline.IsFitAll, "fit-all did not restore the complete overview");

            Button play = Descendants(layout).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "재생 또는 일시정지");
            var playbackTimer = (DispatcherTimer)typeof(VideoEditorWindow).GetField("_playbackTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor)!;
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DateTime playbackDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (!playbackTimer.IsEnabled && DateTime.UtcNow < playbackDeadline) { PumpFor(TimeSpan.FromMilliseconds(10)); }
            Assert.True(playbackTimer.IsEnabled, "playback did not start after pending seek reconciliation");
            timeline.SeekFromOverview(0);
            Assert.False(playbackTimer.IsEnabled, "scrubbing left the paused playback timer running");
            PumpFor(TimeSpan.FromMilliseconds(100));
            Assert.False(playbackTimer.IsEnabled, "paused playback timer restarted without a play request");
            double compactPreviewHeight = preview.ActualHeight;
            double compactTimelineHeight = timelineTools.ActualHeight;
            double compactLayoutHeight = layout.ActualHeight;
            editor.Width = 1200;
            editor.Height = 900;
            editor.UpdateLayout();
            // Windows may cap a requested 900px window to the CI desktop's work area.
            // Assert how the actual available space is allocated, not the requested size.
            double availableHeightGrowth = layout.ActualHeight - compactLayoutHeight;
            Assert.True(availableHeightGrowth > 0, "the native host did not provide any additional layout height");
            Assert.True(preview.ActualHeight >= compactPreviewHeight + availableHeightGrowth - 0.5,
                $"larger window left spare height outside preview: available growth={availableHeightGrowth:0.0}, preview growth={preview.ActualHeight - compactPreviewHeight:0.0}");
            Assert.Equal(compactTimelineHeight, timelineTools.ActualHeight, 1);
            Button export = Descendants(layout).OfType<Button>().Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "내보내기 · MP4 / GIF");
            Assert.True(export.IsVisible && export.IsEnabled);
            Exception? exportFailure = null;
            editor.Dispatcher.BeginInvoke(new Action(() =>
            {
                VideoExportDialog? dialog = editor.OwnedWindows.OfType<VideoExportDialog>().SingleOrDefault();
                try
                {
                    Assert.NotNull(dialog);
                    ComboBox format = Descendants(dialog).OfType<ComboBox>().Single(combo => combo.Items.Contains("MP4"));
                    Assert.Equal("GIF", format.SelectedItem);
                    Button save = Descendants(dialog).OfType<Button>().Single(button =>
                        System.Windows.Automation.AutomationProperties.GetName(button) == "다른 이름으로 저장");
                    Assert.False(save.IsEnabled, "GIF shortcut must open settings before any calculation/save");
                    typeof(VideoEditorWindow).GetMethod("ExportGif", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
                    Assert.Single(editor.OwnedWindows.OfType<VideoExportDialog>());
                }
                catch (Exception error) { exportFailure = error; }
                finally { dialog?.Close(); }
            }));
            typeof(VideoEditorWindow).GetMethod("ExportGif", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
            Assert.Null(exportFailure);
            editor.Close();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    });

    [Fact]
    public void LongClip_TimelineStartsWithOneCoarseInterval_AndShowsExplicitRange() => RunSta(() =>
    {
        var timeline = new TwoLineTimeline();
        timeline.Initialize(durationMs: 12_000, fps: 15);
        timeline.Measure(new Size(900, double.PositiveInfinity));
        timeline.Arrange(new Rect(0, 0, 900, timeline.DesiredSize.Height));
        timeline.UpdateLayout();

        Assert.False(timeline.IsFitAll);
        Assert.Equal(0, timeline.ViewStartMs, precision: 1);
        Assert.Equal(timeline.CoarseIntervalMs, timeline.VisibleSpanMs, precision: 1);
        Assert.Contains("굵은 눈금", timeline.OverviewRangeText, StringComparison.Ordinal);
        Assert.Contains("시작 00:00.000", timeline.DetailRangeText, StringComparison.Ordinal);
        Assert.Contains("끝 00:01.000", timeline.DetailRangeText, StringComparison.Ordinal);
        Assert.Contains("15프레임", timeline.DetailRangeText, StringComparison.Ordinal);
        Assert.InRange(timeline.DesiredSize.Height, 100, 155); // Compact guide leaves room for spatial-layer tracks.

        timeline.SeekFromOverview(6_000);
        Assert.InRange(timeline.PlayheadMs, timeline.ViewStartMs, timeline.ViewEndMs);
        Assert.InRange(6_000, timeline.ViewStartMs, timeline.ViewEndMs);
        Assert.DoesNotContain("시작 00:00.000", timeline.DetailRangeText, StringComparison.Ordinal);
    });

    [Fact]
    public void TrimMode_UsesDeletionHandlesAndKeepsPlayheadInsideRetainedRange() => RunSta(() =>
    {
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(durationMs: 10_000, fps: 20);

        Assert.False(timeline.TrimModeEnabled);
        timeline.SetTrimMode(true);
        timeline.SetPlayhead(1_000);
        timeline.SetIn(2_500);
        Assert.True(timeline.TrimModeEnabled);
        Assert.Equal(2_500, timeline.PlayheadMs, precision: 1);

        timeline.SetPlayhead(9_000);
        timeline.SetOut(7_500);
        Assert.Equal(7_500, timeline.PlayheadMs, precision: 1);

        timeline.SetPlayhead(0);
        Assert.Equal(2_500, timeline.PlayheadMs, precision: 1);
        timeline.SetPlayhead(10_000);
        Assert.Equal(7_500, timeline.PlayheadMs, precision: 1);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) { yield return nested; }
        }
    }
}
