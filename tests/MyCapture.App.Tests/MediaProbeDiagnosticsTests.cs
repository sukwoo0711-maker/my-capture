using System.IO;
using MyCapture.App.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MediaProbeDiagnosticsTests
{
    [Fact]
    public void StrictTailFailureReportsTheValuesItCompares()
    {
        var last = new DecodedVideoFrame([], 64, 48, 256, 966.6667, 33.3333);
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            VideoFrameRenderPipeline.ValidateDecodedTail(1100, last));
        Assert.Contains("container=1100.000 ms", failure.Message);
        Assert.Contains("lastPts=966.667 ms", failure.Message);
        Assert.Contains("sampleDuration=33.333 ms", failure.Message);
        Assert.Contains("inferredDuration=False", failure.Message);
        Assert.Contains("decodedEnd=1000.000 ms", failure.Message);
        Assert.Contains("tolerance=8.333 ms", failure.Message);
    }

    [Fact]
    public void MissingFinalDurationCanCurrentlyCoverAnAudioOnlyTail()
    {
        // Preserve and document the current MF-reader fallback until device diagnostics show
        // whether Win10 omitted the duration or itself supplied an extended final duration.
        double duration = MediaFoundationVideoFrameReader.ResolveMissingSampleDuration(966.6667, null, 1100, 33.3333);
        Assert.Equal(133.3333, duration, precision: 4);
        var inferred = new DecodedVideoFrame([], 64, 48, 256, 966.6667, duration) { SampleDurationInferred = true };
        VideoFrameRenderPipeline.ValidateDecodedTail(1100, inferred);
        Assert.Contains("inferredDuration=True", VideoFrameRenderPipeline.DescribeDecodedTail(1100, inferred));
    }
}
