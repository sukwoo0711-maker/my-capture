using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The recorder's inter-frame wait: a per-handle high-resolution timer on Windows 11, with the
/// previous whole-millisecond sleep as the fallback, and both keep the stop/pause contract.
/// </summary>
public sealed class RecordingFrameDelayTests
{
    [Fact]
    public void HighResolutionTimer_IsAvailableOnTheSupportFloorAndWaitsFinerThanTheSystemTick()
    {
        using RecordingFrameDelay delay = RecordingFrameDelay.Create();
        Assert.True(delay.IsHighResolution);

        var samples = new double[21];
        for (int index = 0; index < samples.Length; index++)
        {
            long started = Stopwatch.GetTimestamp();
            delay.Wait(5);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        // A waitable timer never fires early; a 15.6 ms sleep tick would put the median near 15.6.
        Assert.True(samples[0] >= 4.5, $"woke after {samples[0]:0.00} ms");
        Assert.True(samples[samples.Length / 2] < 10, $"median {samples[samples.Length / 2]:0.00} ms");
        Assert.True(delay.IsHighResolution);
    }

    [Fact]
    public void Fallback_KeepsThePreviousWholeMillisecondSleep()
    {
        using RecordingFrameDelay delay = RecordingFrameDelay.Create(allowHighResolution: false);
        Assert.False(delay.IsHighResolution);

        long started = Stopwatch.GetTimestamp();
        delay.Wait(0.5);
        delay.Wait(0);
        delay.Wait(-3);
        delay.Wait(double.NaN);
        Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds < 5, "sub-millisecond waits must return at once");

        started = Stopwatch.GetTimestamp();
        delay.Wait(3);
        Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2.5);
    }

    [Fact]
    public void HighResolutionTimer_IgnoresNegligibleOrInvalidWaits()
    {
        using RecordingFrameDelay delay = RecordingFrameDelay.Create();
        long started = Stopwatch.GetTimestamp();
        delay.Wait(0.01);
        delay.Wait(0);
        delay.Wait(double.NaN);
        delay.Wait(double.NegativeInfinity);
        Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds < 5);
        delay.Dispose();
        delay.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recorder_ReportsItsPacingTimerAndStopsPromptlyEitherWay(bool highResolution) => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        var sink = new CountingEncoder();
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ => sink, NullLogger.Instance)
        {
            FrameDelayFactory = () => RecordingFrameDelay.Create(highResolution),
        };
        // 10 fps: each wait is capped at 50 ms, so Stop must not wait for a whole 100 ms interval.
        recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps10, IncludeCursor = false });
        var wait = Stopwatch.StartNew();
        while (sink.Frames < 3 && wait.Elapsed < TimeSpan.FromSeconds(10)) SyntheticCaptureFixture.Pump(10);
        Assert.True(sink.Frames >= 3, "the recorder produced no frames");

        long stopping = Stopwatch.GetTimestamp();
        RecordingResult result = recorder.Stop();
        double stopMs = Stopwatch.GetElapsedTime(stopping).TotalMilliseconds;

        Assert.Equal(highResolution, result.Performance!.HighResolutionTimer);
        Assert.True(stopMs < 1000, $"stop took {stopMs:0} ms");
        Assert.False(recorder.IsRecording);
        Assert.True(sink.Completed);
    });

    private sealed class CountingEncoder : IVideoEncoder
    {
        private int _frames;

        public int Width => 0;

        public int Height => 0;

        internal int Frames => Volatile.Read(ref _frames);

        internal bool Completed { get; private set; }

        public void WriteFrame(in EncoderFrame frame) => Interlocked.Increment(ref _frames);

        public void Complete() => Completed = true;

        public void Dispose()
        {
        }
    }
}
