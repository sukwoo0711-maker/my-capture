using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingCaptureLifecycleTests
{
    [Fact]
    public void SlowInitialization_StopStillEmitsZeroFrameAndExcludesStartupFromDuration() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        using var initializing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var encoder = new Encoder();
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ =>
        {
            initializing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return encoder;
        }, NullLogger.Instance);
        recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings());
        Assert.True(initializing.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(recorder.IsReady);
        Assert.Equal(TimeSpan.Zero, recorder.RecordedElapsed);
        Thread.Sleep(250);
        Task<RecordingResult> stopping = Task.Run(recorder.Stop);
        Thread.Sleep(30);
        release.Set();
        Assert.True(stopping.Wait(TimeSpan.FromSeconds(5)));
        RecordingResult result = stopping.Result;
        Assert.Equal(new[] { 0d }, encoder.Timestamps);
        Assert.True(encoder.Completed);
        Assert.True(encoder.Disposed);
        Assert.Equal(encoder.CreatedThread, encoder.DisposedThread);
        Assert.True(result.Performance!.InitializationMs >= 250);
        Assert.True(result.DurationMs < result.Performance.InitializationMs);
        Assert.False(recorder.IsReady);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusableSurface_PreservesPixelsAndHandlesAcrossFailureAndFreshSessions(bool includeCursor) => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        byte[] expected = new byte[320 * 240 * 4];
        fixture.Engine.CaptureRegionInto(fixture.Region, includeCursor, expected, 1280);
        // Warm both paths before counting GDI objects.
        using (var warm = fixture.Engine.CreateSession(fixture.Region, includeCursor)) warm.CaptureInto(expected, 1280);
        uint before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (int sessionIndex = 0; sessionIndex < 8; sessionIndex++)
        {
            var session = fixture.Engine.CreateSession(fixture.Region, includeCursor);
            byte[] actual = new byte[expected.Length];
            Assert.Throws<ArgumentException>(() => session.CaptureInto(new byte[1], 1280));
            session.CaptureInto(actual, 1280);
            if (!includeCursor) Assert.Equal(expected, actual);
            Assert.Equal(255, actual[3]);
            session.Dispose();
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => session.CaptureInto(actual, 1280));
        }
        uint after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        Assert.InRange((long)after - before, -2, 2);
    });

    [Fact]
    public void EncodeFailure_CleansNativeSessionAndAllowsRecorderRestart() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        bool fail = true;
        using var written = new ManualResetEventSlim();
        var encoder = new Encoder { OnWrite = () => { written.Set(); if (fail) throw new IOException("synthetic encoder failure"); } };
        using var recorder = new RegionRecorder(new RegionFrameGrabber(fixture.Engine, false), _ => encoder, NullLogger.Instance);
        recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings());
        Assert.True(written.Wait(TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        Assert.True(encoder.Disposed);
        fail = false;
        written.Reset();
        recorder.Start(fixture.Region, "unused-again.mp4", new RecordingSettings());
        Assert.True(written.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(recorder.Stop().EmittedFrames >= 1);
    });

    [Fact]
    public void EditorExclusionFailure_NeverShowsEditorAndReleasesBusyState() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        var coordinator = new CaptureOverlayCoordinator(fixture.Engine,
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            RequiresCaptureExclusion = () => true,
        };
        bool attempted = false;
        Exception? failure = null;
        coordinator.ApplyCaptureExclusion = window => { attempted = true; Assert.False(window.IsVisible); return false; };
        coordinator.TransitionFailed += error => failure = error;
        var frame = new FrozenFrame(fixture.Engine.CaptureRegion(fixture.Region, false), fixture.Region, null, 0);
        coordinator.StartWithSelection(frame, new RectD(0, 0, 50, 50));
        Assert.True(coordinator.LastTransitionForTest.IsCompletedSuccessfully);
        Assert.True(attempted);
        Assert.NotNull(failure);
        Assert.False(coordinator.IsActive);
    });

    private sealed class Encoder : IVideoEncoder
    {
        public List<double> Timestamps { get; } = [];
        public Action? OnWrite { get; init; }
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }
        public int CreatedThread { get; private set; }
        public int DisposedThread { get; private set; }
        public int Width => 320;
        public int Height => 240;
        public void WriteFrame(in EncoderFrame frame) { CreatedThread = Environment.CurrentManagedThreadId; Timestamps.Add(frame.TimestampMs); OnWrite?.Invoke(); }
        public void Complete() => Completed = true;
        public void Dispose() { Disposed = true; DisposedThread = Environment.CurrentManagedThreadId; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}

internal sealed class SyntheticCaptureFixture : IDisposable
{
    private readonly Window _window;
    internal ScreenCaptureEngine Engine { get; } = new(NullLogger<ScreenCaptureEngine>.Instance);
    internal RectD Region { get; }

    internal SyntheticCaptureFixture()
    {
        _window = new Window { Width = 700, Height = 500, Left = 100, Top = 100,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Topmost = true, ShowActivated = false, Background = new SolidColorBrush(Color.FromRgb(18, 90, 166)) };
        _window.Show();
        _window.UpdateLayout();
        Pump(120);
        Point origin = _window.PointToScreen(new Point(20, 20));
        Region = new RectD(origin.X, origin.Y, 320, 240);
    }

    internal static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public void Dispose() => _window.Close();
}
