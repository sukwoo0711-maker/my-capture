using MyCapture.Core.Primitives;
using System.IO;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentSyntheticRecordingTests
{
    [Fact]
    public void SyntheticFrameUsesBorrowedEvenBgraBufferAndNoNativeBackend()
    {
        byte[]? borrowed = null;
        int calls = 0, timings = 0;
        using var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), true, true,
            RecordingCaptureBackend.WindowsGraphicsCapture, null, (buffer, width, height, stride) =>
            {
                Assert.Equal(8, width); Assert.Equal(6, height); Assert.Equal(32, stride);
                Assert.Equal(stride * height, buffer.Length);
                if (borrowed is not null) Assert.Same(borrowed, buffer);
                borrowed = buffer;
                Array.Fill(buffer, (byte)++calls);
            });
        grabber.FrameCaptureTimed = elapsed => { Assert.True(elapsed >= 0); timings++; };
        Assert.Throws<InvalidOperationException>(() => grabber.GrabInto());
        grabber.Open(new RectD(0, 0, 9, 7));
        Assert.Throws<InvalidOperationException>(() => grabber.Open(new RectD(0, 0, 8, 6)));
        Assert.All(grabber.GrabInto(), value => Assert.Equal((byte)1, value));
        Assert.All(grabber.GrabInto(), value => Assert.Equal((byte)2, value));
        Assert.True(grabber.UsesSyntheticFrames); Assert.Null(grabber.ActiveBackend);
        Assert.Null(grabber.WgcDiagnostics); Assert.Null(grabber.FallbackReason);
        Assert.Equal(2, calls); Assert.Equal(2, timings);
        grabber.Dispose();
        Assert.Throws<ObjectDisposedException>(() => grabber.GrabInto());
        Assert.Throws<ObjectDisposedException>(() => grabber.Open(new RectD(0, 0, 8, 6)));
    }

    [Fact]
    public void SyntheticSuppliersCannotBeCombined()
    {
        using var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false, false, null, null,
            (_, _, _, _) => throw new InvalidOperationException("must not run"))
        { CaptureFrameForTest = _ => throw new InvalidOperationException("must not run") };
        Assert.Throws<InvalidOperationException>(() => grabber.Open(new RectD(0, 0, 8, 6)));
        Assert.Null(grabber.ActiveBackend);
    }

    [Fact]
    public void SyntheticFailurePropagatesWithoutSuccessfulFrameTiming()
    {
        var failure = new IOException("synthetic supplier failed");
        int timings = 0;
        using var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), true, true,
            RecordingCaptureBackend.WindowsGraphicsCapture, null, (_, _, _, _) => throw failure);
        grabber.FrameCaptureTimed = _ => timings++;
        grabber.Open(new RectD(0, 0, 8, 6));
        Assert.Same(failure, Assert.Throws<IOException>(() => grabber.GrabInto()));
        Assert.Equal(0, timings); Assert.Null(grabber.ActiveBackend); Assert.Null(grabber.WgcDiagnostics);
    }
}
