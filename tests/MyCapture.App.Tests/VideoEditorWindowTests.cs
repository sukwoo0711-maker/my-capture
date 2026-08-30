using System;
using System.IO;
using System.Threading;
using System.Windows;
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
public sealed class VideoEditorWindowTests
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
            var editor = new VideoEditorWindow(rec, AppPaths.CreateForRoot(dir), lf);
            editor.WindowStartupLocation = WindowStartupLocation.Manual;
            editor.Width = editor.MinWidth;
            editor.Left = -10000;
            editor.Top = -10000;
            editor.ShowActivated = false;
            editor.Show();

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

            TwoLineTimeline timeline = editor.TimelineForTest;
            Assert.True(timeline.IsEnabled, "two-line timeline stayed disabled after media became ready");
            Assert.Equal(editor.DurationMsForTest, timeline.DurationMs, precision: 1);
            Assert.Equal(2, editor.ControlRowCountForTest);
            Assert.True(
                editor.WidestControlRowWidthForTest <= editor.ControlAreaWidthForTest + 0.5,
                $"two-row controls overflowed: desired={editor.WidestControlRowWidthForTest:0.0}, available={editor.ControlAreaWidthForTest:0.0}");
            Assert.Equal(0, timeline.ViewStartMs, precision: 1);
            Assert.True(timeline.VisibleSpanMs <= timeline.CoarseIntervalMs + 0.001);
            Assert.Contains("시작", timeline.DetailRangeText, StringComparison.Ordinal);
            Assert.Contains("끝", timeline.DetailRangeText, StringComparison.Ordinal);
            Assert.Contains("프레임", timeline.DetailRangeText, StringComparison.Ordinal);

            double initialSpan = timeline.VisibleSpanMs;
            timeline.SetPlayhead(editor.DurationMsForTest / 2.0);
            Assert.InRange(timeline.PlayheadMs, timeline.ViewStartMs, timeline.ViewEndMs);
            timeline.ZoomAroundPlayhead(0.5);
            Assert.False(timeline.IsFitAll, "detail timeline did not zoom into the overview selection");
            Assert.True(timeline.VisibleSpanMs < initialSpan, "zoom did not reduce the detail span");
            Assert.InRange(timeline.PlayheadMs, timeline.ViewStartMs, timeline.ViewEndMs);

            timeline.FitAll();
            Assert.True(timeline.IsFitAll, "fit-all did not restore the complete overview");

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
        Assert.True(timeline.DesiredSize.Height >= 150, "the stronger two-line guide rendered too small");

        timeline.SeekFromOverview(6_000);
        Assert.InRange(timeline.PlayheadMs, timeline.ViewStartMs, timeline.ViewEndMs);
        Assert.InRange(6_000, timeline.ViewStartMs, timeline.ViewEndMs);
        Assert.DoesNotContain("시작 00:00.000", timeline.DetailRangeText, StringComparison.Ordinal);
    });
}
