using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Windows.Graphics.Capture recording backend: the default request, compatibility mode, same
/// frame contract as GDI, GDI fallback, and the managed cursor ring. Native cases need desktop
/// access like the other capture tests.
/// </summary>
public sealed class RecordingWgcBackendTests
{
    [Fact]
    public void Grabber_WithoutExplicitBackend_DefaultsToWgcUnlessTheEnvironmentSaysGdi()
    {
        var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false);
        RecordingCaptureBackend? forced = RecordingCaptureBackendSelection.ParseOverride(
            RecordingCaptureBackendSelection.EnvironmentOverrideValue());
        Assert.Equal(forced ?? RecordingCaptureBackend.WindowsGraphicsCapture, grabber.RequestedBackend);
        Assert.Null(grabber.ActiveBackend);
        grabber.Dispose();
    }

    [Fact]
    public void Grabber_ExplicitBackendIsNotOverriddenByTheEnvironment()
    {
        // Callers that pass a backend (the recording coordinator, benchmarks) already resolved
        // the switch; the grabber must not re-read it and undo the user's compatibility choice.
        using var gdi = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false,
            backend: RecordingCaptureBackend.Gdi);
        using var wgc = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false,
            backend: RecordingCaptureBackend.WindowsGraphicsCapture);
        Assert.Equal(RecordingCaptureBackend.Gdi, gdi.RequestedBackend);
        Assert.Equal(RecordingCaptureBackend.WindowsGraphicsCapture, wgc.RequestedBackend);
    }

    [Fact]
    public void CompatibilityModeGrabber_NeverOpensWgc() => StaTestHost.Run(() =>
    {
        using var fixture = new PatternFixture();
        RecordingCaptureBackend backend = RecordingCaptureBackendSelection.Resolve(RecordingCaptureMode.Gdi, null);
        using var grabber = new RegionFrameGrabber(fixture.Engine, false, backend: backend);
        grabber.Open(fixture.Region);

        Assert.Equal(grabber.Width * grabber.Height * 4, grabber.GrabInto().Length);

        Assert.Equal(RecordingCaptureBackend.Gdi, grabber.RequestedBackend);
        Assert.Equal(RecordingCaptureBackend.Gdi, grabber.ActiveBackend);
        Assert.Null(grabber.FallbackReason);
        Assert.Null(grabber.WgcDiagnostics);
    });

    [Fact]
    public void WgcGrabber_CopiesTheSameRegionPixelsAsGdi() => StaTestHost.Run(() =>
    {
        using var fixture = new PatternFixture();
        var gdi = new RegionFrameGrabber(fixture.Engine, false, backend: RecordingCaptureBackend.Gdi);
        var wgc = new RegionFrameGrabber(fixture.Engine, false, backend: RecordingCaptureBackend.WindowsGraphicsCapture);
        try
        {
            gdi.Open(fixture.Region);
            wgc.Open(fixture.Region);
            byte[] expected = (byte[])gdi.GrabInto().Clone();
            byte[] actual = wgc.GrabInto();
            Assert.Equal(RecordingCaptureBackend.Gdi, gdi.ActiveBackend);
            if (!WgcRegionCaptureSession.IsSupported())
            {
                Assert.Equal(RecordingCaptureBackend.Gdi, wgc.ActiveBackend);
                Assert.NotNull(wgc.FallbackReason);
                return;
            }

            Assert.Equal(RecordingCaptureBackend.WindowsGraphicsCapture, wgc.ActiveBackend);
            Assert.Null(wgc.FallbackReason);
            Assert.Equal(gdi.Width, wgc.Width);
            Assert.Equal(gdi.Height, wgc.Height);
            Assert.Equal(expected.Length, actual.Length);
            // The markers make a wrong crop offset or row pitch visible, not just a wrong colour.
            Assert.Equal(0, CountColourMismatches(expected, actual));
            Assert.True(CountColour(actual, PatternFixture.Marker) > 1000);
            WgcCaptureDiagnostics diagnostics = wgc.WgcDiagnostics!;
            Assert.Equal(1, diagnostics.FramesCopied);
            Assert.False(diagnostics.CursorCaptureEnabled);
        }
        finally
        {
            gdi.Dispose();
            wgc.Dispose();
        }
    });

    [Fact]
    public void WgcGrabber_RegionAcrossADisplayEdge_FallsBackToGdi() => StaTestHost.Run(() =>
    {
        var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
        MonitorInfo primary = MonitorEnumerator.GetPrimary();
        var region = new RectD(primary.Bounds.Left - 16, primary.Bounds.Top + 16, 64, 48);
        using var grabber = new RegionFrameGrabber(engine, false, backend: RecordingCaptureBackend.WindowsGraphicsCapture);
        grabber.Open(region);

        byte[] pixels = grabber.GrabInto();

        Assert.Equal(RecordingCaptureBackend.Gdi, grabber.ActiveBackend);
        Assert.NotNull(grabber.FallbackReason);
        Assert.Equal(64 * 48 * 4, pixels.Length);
        Assert.Same(pixels, grabber.GrabInto());
    });

    [Fact]
    public void WgcGrabber_OpenedButNeverCaptured_DisposesCleanly() => StaTestHost.Run(() =>
    {
        using var fixture = new PatternFixture();
        var grabber = new RegionFrameGrabber(fixture.Engine, false, backend: RecordingCaptureBackend.WindowsGraphicsCapture);
        grabber.Open(fixture.Region);
        grabber.Dispose();
        grabber.Dispose();

        using var next = new RegionFrameGrabber(fixture.Engine, false, backend: RecordingCaptureBackend.WindowsGraphicsCapture);
        next.Open(fixture.Region);
        Assert.Equal(next.Width * next.Height * 4, next.GrabInto().Length);
    });

    [Fact]
    public void WgcRecorder_EncodesFramesAndReleasesTheSessionOnTheCaptureThread() => StaTestHost.Run(() =>
    {
        using var fixture = new PatternFixture();
        var grabber = new RegionFrameGrabber(fixture.Engine, false, backend: RecordingCaptureBackend.WindowsGraphicsCapture);
        var encoder = new CountingEncoder();
        using var recorder = new RegionRecorder(grabber, _ => encoder, NullLogger.Instance);
        recorder.Start(fixture.Region, "unused.mp4", new RecordingSettings { FrameRate = RecordingFrameRate.Fps30, IncludeCursor = false });
        PatternFixture.Pump(1200);

        RecordingResult result = recorder.Stop();

        Assert.True(result.EmittedFrames >= 10, $"only {result.EmittedFrames} frame(s)");
        Assert.Equal(result.EmittedFrames, encoder.Frames);
        Assert.Equal(0, encoder.WrongSizedFrames);
        Assert.True(encoder.Completed);
        Assert.Equal(WgcRegionCaptureSession.IsSupported()
            ? RecordingCaptureBackend.WindowsGraphicsCapture
            : RecordingCaptureBackend.Gdi, grabber.ActiveBackend);
        Assert.False(recorder.IsRecording);
    });

    [Fact]
    public void CursorRing_MatchesTheGdiRingGeometryAndClipsAtTheEdges()
    {
        const int Size = 64;
        const int Stride = Size * 4;
        var pixels = new byte[Stride * Size];

        RegionFrameGrabber.PaintRing(pixels, Stride, Size, Size, 32, 32, pressed: false);
        Assert.Equal(new byte[] { 0, 199, 255, 255 }, Pixel(pixels, Stride, 32 + 13, 32));
        Assert.Equal(new byte[] { 0, 199, 255, 255 }, Pixel(pixels, Stride, 32, 32 - 13));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, Stride, 32, 32));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, Stride, 32 + 18, 32));

        Array.Clear(pixels);
        RegionFrameGrabber.PaintRing(pixels, Stride, Size, Size, 32, 32, pressed: true);
        Assert.Equal(new byte[] { 64, 64, 255, 255 }, Pixel(pixels, Stride, 32 + 18, 32));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, Stride, 32 + 13, 32));

        // Partly or fully outside the frame: clipped, never out of range.
        RegionFrameGrabber.PaintRing(pixels, Stride, Size, Size, 0, 0, pressed: true);
        RegionFrameGrabber.PaintRing(pixels, Stride, Size, Size, Size - 1, Size - 1, pressed: false);
        Array.Clear(pixels);
        RegionFrameGrabber.PaintRing(pixels, Stride, Size, Size, -500, -500, pressed: true);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    private static byte[] Pixel(byte[] pixels, int stride, int x, int y) => pixels.AsSpan((y * stride) + (x * 4), 4).ToArray();

    private static int CountColourMismatches(byte[] expected, byte[] actual)
    {
        int mismatched = 0;
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            // GDI leaves alpha undefined; compare colour only.
            if (Math.Abs(expected[offset] - actual[offset]) > 2
                || Math.Abs(expected[offset + 1] - actual[offset + 1]) > 2
                || Math.Abs(expected[offset + 2] - actual[offset + 2]) > 2)
            {
                mismatched++;
            }
        }

        return mismatched;
    }

    private static int CountColour(byte[] pixels, Color colour)
    {
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] == colour.B && pixels[offset + 1] == colour.G && pixels[offset + 2] == colour.R)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Opaque topmost window with asymmetric markers inside a 320x240 region.</summary>
    private sealed class PatternFixture : IDisposable
    {
        internal static readonly Color Marker = Color.FromRgb(220, 40, 40);
        private readonly Window _window;

        internal PatternFixture()
        {
            var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(18, 90, 166)) };
            AddRectangle(canvas, 50, 40, 60, 30, Marker);
            AddRectangle(canvas, 230, 170, 40, 50, Color.FromRgb(40, 200, 90));
            AddRectangle(canvas, 20, 20, 2, 240, Color.FromRgb(250, 250, 250));
            _window = new Window
            {
                Style = null,
                Width = 700,
                Height = 500,
                Left = 100,
                Top = 100,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                Content = canvas,
            };
            _window.Show();
            _window.UpdateLayout();
            Pump(200);
            Point origin = _window.PointToScreen(new Point(20, 20));
            Region = new RectD(origin.X, origin.Y, 320, 240);
        }

        internal ScreenCaptureEngine Engine { get; } = new(NullLogger<ScreenCaptureEngine>.Instance);

        internal RectD Region { get; }

        internal static void Pump(int milliseconds) => SyntheticCaptureFixture.Pump(milliseconds);

        private static void AddRectangle(Canvas canvas, double left, double top, double width, double height, Color colour)
        {
            var rectangle = new Rectangle { Width = width, Height = height, Fill = new SolidColorBrush(colour) };
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            canvas.Children.Add(rectangle);
        }

        public void Dispose() => _window.Close();
    }

    private sealed class CountingEncoder : IVideoEncoder
    {
        public int Width => 0;

        public int Height => 0;

        internal long Frames { get; private set; }

        internal int WrongSizedFrames { get; private set; }

        internal bool Completed { get; private set; }

        public void WriteFrame(in EncoderFrame frame)
        {
            Frames++;
            if (frame.Pixels.Length < frame.Stride * frame.Height || frame.Stride != frame.Width * 4)
            {
                WrongSizedFrames++;
            }
        }

        public void Complete() => Completed = true;

        public void Dispose()
        {
        }
    }
}
