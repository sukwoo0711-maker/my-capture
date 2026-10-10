using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Recording pause: the recorder must neither grab nor encode while paused, the paused span
/// must vanish from the clip timeline and the metrics, Stop (including Ctrl+Shift+X) must
/// work while paused, and a close while paused must keep the clip for startup recovery.
/// </summary>
public sealed class RecordingPauseTests : KoreanCaptionTest
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Recorder_PausedSpanIsExcludedFromTimestampsDurationAndMetrics()
    {
        // A manual time source makes the timeline exact: 1 s recorded, 5 s paused, 1 s recorded.
        var time = new ManualTimeProvider();
        var sink = new TimestampSink(64, 48);
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(NewEngine(), includeCursor: false), _ => sink, NullLogger.Instance, time);

        recorder.Start(new RectD(0, 0, 64, 48), "pause.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps10 });
        sink.WaitForCount(1);
        AdvanceFrames(time, sink, 10);

        Assert.True(recorder.Pause());
        Assert.True(recorder.IsPaused);
        Assert.False(recorder.Pause());
        Assert.True(recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)));
        time.Advance(5_000);
        Thread.Sleep(300); // several capture-loop wake-ups while paused
        Assert.Equal(11, sink.Count);
        Assert.Equal(1_000, recorder.RecordedElapsed.TotalMilliseconds, 3);

        Assert.True(recorder.Resume());
        Assert.False(recorder.Resume());
        Assert.False(recorder.IsPaused);
        AdvanceFrames(time, sink, 10);
        RecordingResult result = recorder.Stop();

        Assert.Equal(Enumerable.Range(0, 21).Select(i => i * 100.0), sink.Timestamps);
        Assert.True(sink.Completed);
        Assert.Equal(2_000, result.DurationMs, 3);
        Assert.Equal(5_000, result.PausedMs, 3);
        Assert.Equal(1, result.PauseCount);
        Assert.Equal(21, result.EmittedFrames);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(10.5, result.EffectiveFps, 3);
        Assert.Equal(2_000, recorder.RecordedElapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void Recorder_StopWhilePausedFinalizesWithoutCapturingThePausedPeriod()
    {
        var time = new ManualTimeProvider();
        var sink = new TimestampSink(64, 48);
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(NewEngine(), includeCursor: false), _ => sink, NullLogger.Instance, time);

        recorder.Start(new RectD(0, 0, 64, 48), "pause-stop.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps10 });
        sink.WaitForCount(1);
        AdvanceFrames(time, sink, 5);
        Assert.True(recorder.Pause());
        Assert.True(recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)));
        // Long idle: the encoder receives nothing while paused, however much time passes.
        time.Advance(3_600_000);
        Thread.Sleep(200);

        RecordingResult result = recorder.Stop();

        Assert.False(recorder.IsRecording);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => i * 100.0), sink.Timestamps);
        Assert.True(sink.Completed);
        Assert.Equal(500, result.DurationMs, 3);
        Assert.Equal(3_600_000, result.PausedMs, 3);
        Assert.Equal(1, result.PauseCount);
        Assert.Equal(0, result.DroppedFrames);
        Assert.False(recorder.WaitUntilCaptureParked(TimeSpan.Zero));
    }

    [Fact]
    public void Recorder_PauseDuringEncoderStartupStillWritesTheZeroFrameThenParks()
    {
        var time = new ManualTimeProvider();
        var sink = new TimestampSink(64, 48);
        using var factoryEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(NewEngine(), includeCursor: false),
            _ =>
            {
                factoryEntered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                return sink;
            },
            NullLogger.Instance,
            time);

        recorder.Start(new RectD(0, 0, 64, 48), "pause-startup.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps10 });
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(recorder.Pause());
        release.Set();

        sink.WaitForCount(1);
        Assert.True(recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)));
        time.Advance(2_000);
        Thread.Sleep(200);
        Assert.Equal(1, sink.Count);

        Assert.True(recorder.Resume());
        AdvanceFrames(time, sink, 1);
        RecordingResult result = recorder.Stop();

        Assert.Equal(new[] { 0d, 100d }, sink.Timestamps);
        Assert.Equal(100, result.DurationMs, 3);
        Assert.Equal(2_000, result.PausedMs, 3);
    }

    [Fact]
    public void Recorder_PauseAndResumeAreRejectedWithoutARecording()
    {
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(NewEngine(), includeCursor: false), _ => new TimestampSink(4, 4), NullLogger.Instance);

        Assert.False(recorder.Pause());
        Assert.False(recorder.Resume());
        Assert.False(recorder.IsPaused);
        Assert.False(recorder.WaitUntilCaptureParked(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
    }

    [Fact]
    public void RecordingResult_PausedTimeIsNotCountedAsDroppedFrames()
    {
        // 20 s recorded at 15 fps; the 5 s pause is reported separately.
        var result = new RecordingResult("capture.mp4", 20_000, 15, 300, 1280, 720) { PausedMs = 5_000, PauseCount = 1 };

        Assert.Equal(300, result.ExpectedFrames);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(15, result.EffectiveFps, 3);
        Assert.Equal(0, result.DropRate, 3);
    }

    [Fact]
    public void ControlWindow_PauseResumeUpdatesCuesAndStopWhilePausedKeepsTheClip() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        var sink = new TimestampSink(320, 240);
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ => sink, NullLogger.Instance);
        var window = new RecordingControlWindow(
            fixture.Region,
            new RecordingSettings { UseStartDelay = false, FrameRate = RecordingFrameRate.Fps15 },
            () => recorder,
            () => "unused.mp4",
            NullLogger<RecordingControlWindow>.Instance) { ShowActivated = false };
        RecordingResult? finished = null;
        Exception? failure = null;
        window.RecordingFinished += (_, result) => finished = result;
        window.Failed += (_, args) => failure = args.Exception;
        try
        {
            window.Show();
            Button pause = Field<Button>(window, "_pauseButton");
            Border frame = Field<Border>(window, "_regionFrame");
            TextBlock status = Field<TextBlock>(window, "_statusText");
            Assert.Equal(Visibility.Collapsed, pause.Visibility);

            Invoke(window, "OnPrimaryClicked");
            sink.WaitForCount(1);
            Assert.Equal(Visibility.Visible, pause.Visibility);
            Assert.True(pause.IsEnabled && pause.Focusable && pause.IsTabStop);
            Assert.Equal("일시정지", pause.Content);
            Assert.Equal("녹화 일시정지", AutomationProperties.GetName(pause));
            Assert.Contains("동영상에 포함되지 않습니다", AutomationProperties.GetHelpText(pause), StringComparison.Ordinal);
            Brush recordingBrush = frame.BorderBrush;

            Field<CheckBox>(window, "_clockCheckBox").IsChecked = true;
            RecordingWallClockWindow clock = Field<RecordingWallClockWindow>(window, "_wallClock");
            Assert.False(clock.IsPaused);

            pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => window.IsPaused && !window.IsPauseTransitionPending);
            Assert.True(recorder.IsPaused);
            Assert.True(window.IsRecording); // Ctrl+Shift+X routes to RequestStop while paused
            Assert.Equal("재개", pause.Content);
            Assert.Equal("녹화 재개", AutomationProperties.GetName(pause));
            Assert.StartsWith("일시정지됨", status.Text, StringComparison.Ordinal);
            Assert.Equal(status.Text, AutomationProperties.GetName(status));
            Assert.Equal("일시정지됨", AutomationProperties.GetItemStatus(frame));
            Assert.IsType<LinearGradientBrush>(frame.BorderBrush);
            Assert.NotSame(recordingBrush, frame.BorderBrush);
            Assert.True(clock.IsPaused);
            Assert.Equal(Visibility.Visible, clock.PausedBadge.Visibility);

            int framesAtPause = sink.Count;
            TimeSpan recordedAtPause = recorder.RecordedElapsed;
            SyntheticCaptureFixture.Pump(600);
            Assert.Equal(framesAtPause, sink.Count);
            Assert.Equal(recordedAtPause, recorder.RecordedElapsed);

            pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => !window.IsPaused && !window.IsPauseTransitionPending);
            Assert.False(recorder.IsPaused);
            Assert.False(clock.IsPaused);
            Assert.Equal("일시정지", pause.Content);
            Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(frame));
            Assert.IsNotType<LinearGradientBrush>(frame.BorderBrush);
            sink.WaitForCount(framesAtPause + 2);

            pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => window.IsPaused && !window.IsPauseTransitionPending);
            window.RequestStop();
            PumpUntil(() => finished is not null || failure is not null);

            Assert.Null(failure);
            Assert.NotNull(finished);
            Assert.Equal(2, finished!.PauseCount);
            Assert.True(finished.PausedMs >= 550, $"paused {finished.PausedMs:0}ms");
            Assert.True(sink.Completed);
            Assert.False(pause.IsEnabled);
            double[] timestamps = sink.Timestamps;
            for (int i = 1; i < timestamps.Length; i++)
            {
                Assert.True(timestamps[i] > timestamps[i - 1]);
                // The 600 ms pause never appears as a hole in the timeline.
                Assert.True(timestamps[i] - timestamps[i - 1] < 450, $"gap {timestamps[i] - timestamps[i - 1]:0}ms at {i}");
            }

            Assert.True(finished.DurationMs >= timestamps[^1]);
        }
        finally
        {
            if (window.IsRecording)
            {
                window.RequestStop();
                PumpUntil(() => finished is not null || failure is not null);
            }

            window.CompleteAndClose();
        }
    });

    [Fact]
    public void ControlWindow_CloseWhilePausedKeepsTheFinalizedClipForStartupRecovery() => StaTestHost.Run(() =>
    {
        // Application exit (tray Exit, update restart, Windows sign-out) closes every window.
        // The recorder finalises the private MP4 in OnClosed; the coordinator must keep it
        // so the next startup publishes it instead of the clip being deleted.
        using var fixture = new SyntheticCaptureFixture();
        string root = OwnedTestDirectory.Create("mc-recording-pause-close-");
        try
        {
            AppPaths paths = AppPaths.CreateForRoot(root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            queue.Load();
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            var coordinator = new RegionRecordingCoordinator(
                fixture.Engine,
                paths,
                () => new RecordingSettings
                {
                    UseStartDelay = false,
                    FrameRate = RecordingFrameRate.Fps15,
                    IncludeCursor = false,
                },
                NullLoggerFactory.Instance,
                library);

            typeof(RegionRecordingCoordinator).GetMethod("OpenControls", Flags)!.Invoke(coordinator, [fixture.Region]);
            var controls = Field<RecordingControlWindow>(coordinator, "_controls");
            Invoke(controls, "OnPrimaryClicked");
            PumpUntil(() => controls.CanCaptureStill);
            SyntheticCaptureFixture.Pump(400);
            Invoke(controls, "TogglePause");
            PumpUntil(() => controls.IsPaused && !controls.IsPauseTransitionPending);
            double recordedMs = Field<RegionRecorder>(controls, "_recorder").RecordedElapsed.TotalMilliseconds;
            SyntheticCaptureFixture.Pump(300);

            controls.Close();

            Assert.True(controls.FinalizedOnClose);
            Assert.False(coordinator.IsActive);
            string pending = Assert.Single(Directory.GetFiles(
                paths.CapturesRoot, CaptureFileNames.VideoPending, SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(pending)!, CaptureFileNames.VideoWriting)));

            // Next startup: recovery validates the finalised file and publishes it.
            var reloadedQueue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            reloadedQueue.Load();
            _ = new VideoLibraryService(reloadedQueue, paths, NullLogger<VideoLibraryService>.Instance);
            CaptureRecord recovered = Assert.Single(reloadedQueue.Records);
            Assert.True(recovered.IsVideo);
            Assert.Equal(320, recovered.Width);
            Assert.Equal(240, recovered.Height);
            // The paused span after the recorded part is not in the clip.
            Assert.InRange(recovered.DurationMs, 1, recordedMs + 250);
            Assert.True(File.Exists(reloadedQueue.GetFilePath(recovered, CaptureFileNames.VideoSource)));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void WallClock_PausedBadgeIsShownOnlyWhilePaused() => StaTestHost.Run(() =>
    {
        var region = new RectD(100, 100, 1280, 720);
        var window = new RecordingWallClockWindow(region, dpiScale: 1.0, targetFps: 30);
        try
        {
            Assert.False(window.IsPaused);
            Assert.Equal(Visibility.Collapsed, window.PausedBadge.Visibility);
            double runningWidth = window.Width;

            window.SetPaused(true);
            Assert.True(window.IsPaused);
            Assert.Equal(Visibility.Visible, window.PausedBadge.Visibility);
            Assert.Equal("일시정지됨", window.PausedBadge.Text);
            Assert.Equal("일시정지됨", AutomationProperties.GetItemStatus(window.Container));
            Assert.Equal("녹화 시계", AutomationProperties.GetName(window.Container));
            Assert.True(window.Width > runningWidth);
            Assert.True(window.PhysicalWidth <= region.Width);
            window.AdjustFontSize(2);
            Assert.Equal(window.TextBlock.FontSize, window.PausedBadge.FontSize);

            window.SetPaused(false);
            Assert.False(window.IsPaused);
            Assert.Equal(Visibility.Collapsed, window.PausedBadge.Visibility);
            Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(window.Container));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void PausedOutline_UsesAStripePatternNotOnlyAColour() => StaTestHost.Run(() =>
    {
        var brush = Assert.IsType<LinearGradientBrush>(RecordingControlWindow.CreatePausedFrameBrush());

        Assert.True(brush.IsFrozen);
        Assert.Equal(GradientSpreadMethod.Repeat, brush.SpreadMethod);
        Assert.Equal(BrushMappingMode.Absolute, brush.MappingMode);
        Assert.Equal(4, brush.GradientStops.Count);
        Assert.NotEqual(brush.GradientStops[0].Color, brush.GradientStops[2].Color);
    });

    private static ScreenCaptureEngine NewEngine() => new(NullLogger<ScreenCaptureEngine>.Instance);

    private static void AdvanceFrames(ManualTimeProvider time, TimestampSink sink, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            int expected = sink.Count + 1;
            time.Advance(100);
            sink.WaitForCount(expected);
        }
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;

    private static void Invoke(object owner, string method) =>
        owner.GetType().GetMethod(method, Flags)!.Invoke(owner, null);

    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            SyntheticCaptureFixture.Pump(10);
        }

        Assert.True(predicate(), "The recording controls did not reach the expected state.");
    }

    /// <summary>Monotonic test clock; starts away from zero, which the recorder treats as "not started".</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        internal void Advance(double milliseconds) =>
            Interlocked.Add(ref _ticks, (long)(milliseconds * TimeSpan.TicksPerMillisecond));
    }

    private sealed class TimestampSink(int width, int height) : IVideoEncoder
    {
        private readonly List<double> _timestamps = [];

        public int Width => width;

        public int Height => height;

        public bool Completed { get; private set; }

        internal int Count
        {
            get
            {
                lock (_timestamps)
                {
                    return _timestamps.Count;
                }
            }
        }

        internal double[] Timestamps
        {
            get
            {
                lock (_timestamps)
                {
                    return _timestamps.ToArray();
                }
            }
        }

        public void WriteFrame(in EncoderFrame frame)
        {
            lock (_timestamps)
            {
                _timestamps.Add(frame.TimestampMs);
                Monitor.PulseAll(_timestamps);
            }
        }

        internal void WaitForCount(int count)
        {
            long deadline = Environment.TickCount64 + 5_000;
            lock (_timestamps)
            {
                while (_timestamps.Count < count)
                {
                    long remaining = deadline - Environment.TickCount64;
                    Assert.True(remaining > 0, $"Expected {count} frame(s), got {_timestamps.Count}.");
                    Monitor.Wait(_timestamps, (int)remaining);
                }
            }
        }

        public void Complete() => Completed = true;

        public void Dispose()
        {
        }
    }
}
