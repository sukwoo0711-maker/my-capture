using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingStartupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SlowFirstCapture_IsExcludedFromTimelineAndPreservesPause(int startupPause)
    {
        var time = new ManualTime();
        var sink = new Sink();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int captures = 0;
        var grabber = Grabber();
        grabber.FrameCaptureTimed = _ =>
        {
            if (Interlocked.Increment(ref captures) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        using var recorder = new RegionRecorder(grabber, _ => sink, NullLogger.Instance, time);
        try
        {
            Start(recorder);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(recorder.IsReady);
            time.Advance(2000);
            Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
            if (startupPause != 0) Assert.True(recorder.Pause());
            if (startupPause == 2)
            {
                time.Advance(3000);
                Assert.True(recorder.Resume());
                Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
            }
            release.Set();
            sink.Wait(1);
            if (startupPause == 1)
            {
                Assert.True(recorder.WaitUntilCaptureParked(TimeSpan.FromSeconds(5)));
                time.Advance(3000);
                Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
                Assert.True(recorder.Resume());
            }
            time.Advance(100);
            sink.Wait(2);
            RecordingResult result = recorder.Stop();
            Assert.Equal(new[] { 0d, 100d }, sink.Timestamps);
            Assert.Equal(100, result.DurationMs);
            Assert.Equal(startupPause == 1 ? 3000 : 0, result.PausedMs);
            Assert.Equal(startupPause != 0 ? 1 : 0, result.PauseCount);
            Assert.Equal(0, result.DroppedFrames);
            Assert.True(sink.Completed);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void StopDuringFirstCapture_WritesOnlyZeroFrameAndFinalizes()
    {
        var time = new ManualTime();
        var sink = new Sink();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var grabber = Grabber();
        grabber.FrameCaptureTimed = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        using var recorder = new RegionRecorder(grabber, _ => sink, NullLogger.Instance, time);
        try
        {
            Start(recorder);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            time.Advance(2000);
            RecordingResult? result = null;
            Exception? failure = null;
            var stopping = new Thread(() =>
            {
                try { result = recorder.Stop(); }
                catch (Exception ex) { failure = ex; }
            });
            stopping.Start();
            // Stop's only wait is its join of the capture thread: observe that wait before
            // allowing the first grab to finish, rather than racing a scheduled Task.
            Assert.True(SpinWait.SpinUntil(
                () => (stopping.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            release.Set();
            Assert.True(stopping.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.Equal(new[] { 0d }, sink.Timestamps);
            Assert.Equal(0, Assert.IsType<RecordingResult>(result).DurationMs);
            Assert.True(sink.Completed);
            Assert.True(sink.Disposed);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void LaterCaptureStall_IsStillRepresentedInTheTimeline()
    {
        var time = new ManualTime();
        var sink = new Sink();
        int captures = 0;
        var grabber = Grabber();
        grabber.FrameCaptureTimed = _ =>
        {
            if (Interlocked.Increment(ref captures) is 1 or 3) time.Advance(2000);
        };
        using var recorder = new RegionRecorder(grabber, _ => sink, NullLogger.Instance, time);
        Start(recorder);
        sink.Wait(1);
        time.Advance(100);
        sink.Wait(2);
        time.Advance(100);
        sink.Wait(4);
        RecordingResult result = recorder.Stop();
        Assert.Equal(new[] { 0d, 100d, 200d, 2200d }, sink.Timestamps);
        Assert.Equal(2200, result.DurationMs);
        Assert.True(result.DroppedFrames > 0);
    }

    [Fact]
    public void SlowFirstWrite_IsNotRemovedFromTheAlreadyCapturedTimeline()
    {
        var time = new ManualTime();
        int writes = 0;
        var sink = new Sink
        {
            BeforeWrite = () => { if (Interlocked.Increment(ref writes) == 1) time.Advance(2000); },
        };
        using var recorder = new RegionRecorder(Grabber(), _ => sink, NullLogger.Instance, time);
        Start(recorder);
        sink.Wait(2);
        RecordingResult result = recorder.Stop();
        Assert.Equal(new[] { 0d, 2000d }, sink.Timestamps);
        Assert.Equal(2000, result.DurationMs);
        Assert.True(result.DroppedFrames > 0);
    }

    [Fact]
    public void InitialCaptureFailure_DoesNotStartClockAndAllowsFreshSession()
    {
        var time = new ManualTime();
        var sink = new Sink();
        var grabber = Grabber();
        grabber.FrameCaptureTimed = _ => { time.Advance(2000); throw new IOException("initial capture failure"); };
        using var recorder = new RegionRecorder(grabber, _ => sink, NullLogger.Instance, time);
        Start(recorder);
        Assert.True(SpinWait.SpinUntil(() => !recorder.IsRecording, TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        Assert.False(recorder.IsReady);
        Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
        Assert.Empty(sink.Timestamps);
        grabber.FrameCaptureTimed = null;
        Start(recorder);
        sink.Wait(1);
        time.Advance(100);
        sink.Wait(2);
        Assert.Equal(100, recorder.Stop().DurationMs);
        Assert.Equal(new[] { 0d, 100d }, sink.Timestamps);
    }

    [Fact]
    public void GapDiagnostic_DistinguishesStartupResumeAndActiveStalls()
    {
        double[] timestamps = [0, 2000, 2100, 3100];
        Assert.Contains("first-frame boundary", RecordingSelfTest.DescribeTimestampGap(timestamps, 1, 3, true));
        string resume = RecordingSelfTest.DescribeTimestampGap(timestamps, 3, 3, true);
        Assert.Contains("resume boundary", resume);
        Assert.Contains("2100 -> 3100ms", resume);
        Assert.Contains("active recording", RecordingSelfTest.DescribeTimestampGap(timestamps, 3, 3, false));
    }

    [Theory]
    [InlineData(1, 2500, 2500)]
    [InlineData(40, 67, 67)]
    [InlineData(2, 3067, 133.334)]
    public void VideoEditorSource_RejectsDegenerateRecording(long frames, double duration, double containerDuration)
    {
        var recording = new RecordingResult("unused.mp4", duration, 15, frames, 64, 48);
        Assert.Throws<InvalidOperationException>(() => VideoEditorResponsivenessSelfTest.ValidateSourceClip(recording, containerDuration));
    }

    [Fact]
    public void VideoEditorSource_AcceptsSustainedMultipleFrameRecording() =>
        VideoEditorResponsivenessSelfTest.ValidateSourceClip(new RecordingResult("unused.mp4", 2600, 15, 38, 64, 48), 2533.334);

    private static RegionFrameGrabber Grabber() => new(
        new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false, backend: RecordingCaptureBackend.Gdi);

    private static void Start(RegionRecorder recorder) => recorder.Start(new RectD(0, 0, 64, 48),
        "unused-startup.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps10 });

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(int milliseconds) => Interlocked.Add(ref _ticks, milliseconds * TimeSpan.TicksPerMillisecond);
    }

    private sealed class Sink : IVideoEncoder
    {
        private readonly List<double> _timestamps = [];
        public int Width => 64;
        public int Height => 48;
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }
        public Action? BeforeWrite { get; init; }
        internal double[] Timestamps { get { lock (_timestamps) return _timestamps.ToArray(); } }
        public void WriteFrame(in EncoderFrame frame)
        {
            BeforeWrite?.Invoke();
            lock (_timestamps) { _timestamps.Add(frame.TimestampMs); Monitor.PulseAll(_timestamps); }
        }
        internal void Wait(int count)
        {
            long deadline = Environment.TickCount64 + 5000;
            lock (_timestamps)
            {
                while (_timestamps.Count < count)
                {
                    long remaining = deadline - Environment.TickCount64;
                    Assert.True(remaining > 0, $"Expected {count} frames, got {_timestamps.Count}.");
                    Monitor.Wait(_timestamps, (int)remaining);
                }
            }
        }
        public void Complete() => Completed = true;
        public void Dispose() => Disposed = true;
    }
}
