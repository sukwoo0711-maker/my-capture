using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// End-to-end recorder scenarios that mirror real user actions the automated suite had NOT
/// covered: start → record → stop, then start a SECOND recording. Reproduces the reported
/// "stop then it records again from 0" class of bug at the recorder-orchestration level
/// (real RegionRecorder + real screen grabber + a spy encoder, so timing is real but no
/// Media Foundation is needed).
/// </summary>
public sealed class RecordingScenarioTests
{
    private sealed class SpyEncoder : IVideoEncoder
    {
        public int Width { get; }

        public int Height { get; }

        public SpyEncoder(int w, int h) { Width = w; Height = h; }

        public List<double> Timestamps { get; } = [];

        public bool Completed { get; private set; }

        public bool Disposed { get; private set; }

        public void WriteFrame(in EncoderFrame frame)
        {
            lock (Timestamps) { Timestamps.Add(frame.TimestampMs); }
        }

        public void Complete() => Completed = true;

        public void Dispose() => Disposed = true;
    }

    private static ScreenCaptureEngine NewEngine() =>
        new(NullLogger<ScreenCaptureEngine>.Instance);

    private static RectD SmallRegion() => new(0, 0, 64, 48);

    [Fact]
    public async Task StopDuringEncoderWarmup_StillWritesTimestampZeroFrame()
    {
        var engine = NewEngine();
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        using var factoryEntered = new ManualResetEventSlim(false);
        using var allowFactoryToFinish = new ManualResetEventSlim(false);
        SpyEncoder? spy = null;
        var recorder = new RegionRecorder(
            grabber,
            _ =>
            {
                factoryEntered.Set();
                Assert.True(allowFactoryToFinish.Wait(TimeSpan.FromSeconds(5)));
                return spy = new SpyEncoder(64, 48);
            },
            NullLogger.Instance);

        recorder.Start(SmallRegion(), "warmup-stop.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps15 });
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(5)), "encoder factory did not start");
        Task<RecordingResult> stopping = Task.Run(recorder.Stop);
        await Task.Delay(50);
        allowFactoryToFinish.Set();
        RecordingResult result = await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(spy);
        Assert.True(spy!.Completed);
        Assert.Single(spy.Timestamps);
        Assert.Equal(0, spy.Timestamps[0], precision: 3);
        Assert.Equal(1, result.EmittedFrames);
        recorder.Dispose();
    }

    [Fact]
    public void RealMediaFoundation_StartStop_ProducesPlayableClip()
    {
        // The spy-encoder tests never exercise the real MF encoder inside the recorder loop.
        // This runs the full real pipeline (grabber -> MediaFoundationVideoEncoder) exactly as
        // the app does, surfacing any real-device failure the field report hit.
        var engine = NewEngine();
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-realrec-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "real.mp4");

        var recorder = new RegionRecorder(
            grabber,
            options => new MediaFoundationVideoEncoder(options, NullLogger<MediaFoundationVideoEncoder>.Instance),
            NullLogger.Instance);

        try
        {
            recorder.Start(new RectD(0, 0, 320, 240), path, new RecordingSettings { FrameRate = RecordingFrameRate.Fps15 });
            Thread.Sleep(700);
            RecordingResult result = recorder.Stop(); // throws with inner detail if the loop failed

            Assert.False(recorder.IsRecording);
            Assert.True(result.EmittedFrames >= 1, "no frames emitted");
            Assert.True(System.IO.File.Exists(path), "no output file");
            Assert.True(new System.IO.FileInfo(path).Length > 1000, "output implausibly small");
        }
        finally
        {
            recorder.Dispose();
            try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void StartThenStop_ProducesResult_AndEncoderCompletes()
    {
        var engine = NewEngine();
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        SpyEncoder? spy = null;
        var recorder = new RegionRecorder(
            grabber,
            _ => (spy = new SpyEncoder(64, 48)),
            NullLogger.Instance);

        recorder.Start(SmallRegion(), "scenario1.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps15 });
        Assert.True(recorder.IsRecording);
        Thread.Sleep(400); // ~6 frames at 15fps
        RecordingResult result = recorder.Stop();

        Assert.False(recorder.IsRecording);          // truly stopped
        Assert.NotNull(spy);
        Assert.True(spy!.Completed);                 // encoder finalised exactly once
        Assert.True(result.EmittedFrames >= 1);
        Assert.True(spy.Timestamps.Count >= 1);

        // Frame timestamps must start at 0 and be monotonically increasing (real clock).
        Assert.Equal(0, spy.Timestamps[0], 3);
        for (int i = 1; i < spy.Timestamps.Count; i++)
        {
            Assert.True(spy.Timestamps[i] > spy.Timestamps[i - 1]);
        }

        recorder.Dispose();
    }

    [Fact]
    public void SecondStartOnFreshRecorder_RecordsIndependently_NoLeakFromFirst()
    {
        // Mirrors the coordinator: each session builds a NEW recorder + encoder (BuildRecorder).
        var engine = NewEngine();

        SpyEncoder first = RunOneSession(engine, "sessionA.mp4", 400);
        SpyEncoder second = RunOneSession(engine, "sessionB.mp4", 250);

        // Two independent clips: both finalised, both start at timestamp 0, and the second
        // did not inherit or continue the first's frames.
        Assert.True(first.Completed);
        Assert.True(second.Completed);
        Assert.Equal(0, first.Timestamps[0], 3);
        Assert.Equal(0, second.Timestamps[0], 3);
        Assert.True(first.Timestamps.Count >= 1);
        Assert.True(second.Timestamps.Count >= 1);
    }

    private static SpyEncoder RunOneSession(ScreenCaptureEngine engine, string path, int recordMs)
    {
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        SpyEncoder? spy = null;
        var recorder = new RegionRecorder(
            grabber,
            _ => (spy = new SpyEncoder(64, 48)),
            NullLogger.Instance);
        recorder.Start(SmallRegion(), path, new RecordingSettings { FrameRate = RecordingFrameRate.Fps15 });
        Thread.Sleep(recordMs);
        recorder.Stop();
        recorder.Dispose();
        return spy!;
    }

    [Fact]
    public void DoubleStop_OnSameRecorder_Throws_NotRestart()
    {
        // After Stop(), the recorder's thread is gone; a second Stop must throw rather than
        // silently restart or resurrect the capture loop.
        var engine = NewEngine();
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        var recorder = new RegionRecorder(grabber, _ => new SpyEncoder(64, 48), NullLogger.Instance);

        recorder.Start(SmallRegion(), "scenario-double.mp4", new RecordingSettings());
        Thread.Sleep(200);
        _ = recorder.Stop();

        Assert.False(recorder.IsRecording);
        Assert.Throws<InvalidOperationException>(() => recorder.Stop());

        recorder.Dispose();
    }

    [Fact]
    public void StartWhileRecording_Throws_NoSecondLoop()
    {
        var engine = NewEngine();
        var grabber = new RegionFrameGrabber(engine, includeCursor: false);
        var recorder = new RegionRecorder(grabber, _ => new SpyEncoder(64, 48), NullLogger.Instance);

        recorder.Start(SmallRegion(), "scenario-restart.mp4", new RecordingSettings());
        try
        {
            Thread.Sleep(120);
            // A second Start on an already-running recorder must be rejected, never spawn a
            // second capture loop against the same encoder.
            Assert.Throws<InvalidOperationException>(() =>
                recorder.Start(SmallRegion(), "scenario-restart-2.mp4", new RecordingSettings()));
        }
        finally
        {
            _ = recorder.Stop();
            recorder.Dispose();
        }
    }
}
