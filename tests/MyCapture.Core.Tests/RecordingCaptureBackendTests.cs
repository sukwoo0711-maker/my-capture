using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingCaptureBackendTests
{
    [Theory]
    [InlineData(null, RecordingCaptureBackend.Gdi)]
    [InlineData("", RecordingCaptureBackend.Gdi)]
    [InlineData("gdi", RecordingCaptureBackend.Gdi)]
    [InlineData("wgc", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData(" WGC ", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData("Wgc", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData("wgc2", RecordingCaptureBackend.Gdi)]
    [InlineData("dxgi", RecordingCaptureBackend.Gdi)]
    [InlineData("1", RecordingCaptureBackend.Gdi)]
    public void Parse_OnlyExactWgcOptsIn(string? value, RecordingCaptureBackend expected)
    {
        Assert.Equal(expected, RecordingCaptureBackendSelection.Parse(value));
    }

    [Fact]
    public void Default_IsGdi()
    {
        Assert.Equal(RecordingCaptureBackend.Gdi, default(RecordingCaptureBackend));
        Assert.Equal("MYCAPTURE_RECORDING_BACKEND", RecordingCaptureBackendSelection.EnvironmentVariable);
    }

    private static readonly RectD[] Displays =
    [
        new(0, 0, 3440, 1440),
        new(3440, 0, 768, 1366),
        new(-1920, 200, 1920, 1080),
    ];

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0)]
    [InlineData(0, 0, 3440, 1440, 0)]
    [InlineData(3500, 100, 640, 480, 1)]
    [InlineData(-1900, 300, 800, 600, 2)]
    [InlineData(3000, 100, 800, 600, -1)]
    [InlineData(-100, 300, 400, 300, -1)]
    [InlineData(3440, 1300, 200, 100, -1)]
    [InlineData(5000, 0, 100, 100, -1)]
    [InlineData(100, 100, 0, 50, -1)]
    public void FindContainingMonitor_RequiresOneDisplayToHoldTheWholeRegion(
        double x, double y, double width, double height, int expected)
    {
        Assert.Equal(expected, RecordingCaptureBackendSelection.FindContainingMonitor(new RectD(x, y, width, height), Displays));
    }

    [Fact]
    public void FindContainingMonitor_NormalizesNegativeSizes()
    {
        // Dragged up-left: same pixels as (100,100,200,150).
        Assert.Equal(0, RecordingCaptureBackendSelection.FindContainingMonitor(new RectD(300, 250, -200, -150), Displays));
    }

    [Fact]
    public void FindContainingMonitor_NoDisplays_ReturnsMinusOne()
    {
        Assert.Equal(-1, RecordingCaptureBackendSelection.FindContainingMonitor(new RectD(0, 0, 10, 10), []));
    }
}
