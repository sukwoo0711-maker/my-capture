using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class RecordingCaptureBackendTests
{
    [Theory]
    [InlineData("gdi", RecordingCaptureBackend.Gdi)]
    [InlineData(" GDI ", RecordingCaptureBackend.Gdi)]
    [InlineData("wgc", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData(" WGC ", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData("Wgc", RecordingCaptureBackend.WindowsGraphicsCapture)]
    public void ParseOverride_AcceptsOnlyTheTwoBackendNames(string value, RecordingCaptureBackend expected)
    {
        Assert.Equal(expected, RecordingCaptureBackendSelection.ParseOverride(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("wgc2")]
    [InlineData("dxgi")]
    [InlineData("1")]
    public void ParseOverride_AnythingElseForcesNothing(string? value)
    {
        Assert.Null(RecordingCaptureBackendSelection.ParseOverride(value));
    }

    [Theory]
    // No override: the setting decides, and the product default (Auto) asks for WGC.
    [InlineData(RecordingCaptureMode.Auto, null, RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData(RecordingCaptureMode.Gdi, null, RecordingCaptureBackend.Gdi)]
    [InlineData(RecordingCaptureMode.Auto, "typo", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData(RecordingCaptureMode.Gdi, "typo", RecordingCaptureBackend.Gdi)]
    // The diagnostic environment switch wins over the setting in both directions.
    [InlineData(RecordingCaptureMode.Auto, "gdi", RecordingCaptureBackend.Gdi)]
    [InlineData(RecordingCaptureMode.Gdi, "wgc", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData(RecordingCaptureMode.Auto, "wgc", RecordingCaptureBackend.WindowsGraphicsCapture)]
    [InlineData(RecordingCaptureMode.Gdi, "gdi", RecordingCaptureBackend.Gdi)]
    public void Resolve_EnvironmentOverrideWinsOverTheSetting(
        RecordingCaptureMode mode, string? environmentValue, RecordingCaptureBackend expected)
    {
        Assert.Equal(expected, RecordingCaptureBackendSelection.Resolve(mode, environmentValue));
    }

    [Fact]
    public void Resolve_UndefinedModeBehavesLikeAuto()
    {
        Assert.Equal(
            RecordingCaptureBackend.WindowsGraphicsCapture,
            RecordingCaptureBackendSelection.Resolve((RecordingCaptureMode)42, null));
    }

    [Fact]
    public void Defaults_AutoModeAndTheDocumentedSwitchName()
    {
        Assert.Equal(RecordingCaptureMode.Auto, default(RecordingCaptureMode));
        Assert.Equal(RecordingCaptureMode.Auto, new RecordingSettings().CaptureMode);
        Assert.Equal("MYCAPTURE_RECORDING_BACKEND", RecordingCaptureBackendSelection.EnvironmentVariable);
    }

    [Fact]
    public void FromEnvironment_UsesTheProcessSwitchOverTheGivenMode()
    {
        string? previous = Environment.GetEnvironmentVariable(RecordingCaptureBackendSelection.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(RecordingCaptureBackendSelection.EnvironmentVariable, "gdi");
            Assert.Equal(RecordingCaptureBackend.Gdi, RecordingCaptureBackendSelection.FromEnvironment());
            Environment.SetEnvironmentVariable(RecordingCaptureBackendSelection.EnvironmentVariable, "wgc");
            Assert.Equal(
                RecordingCaptureBackend.WindowsGraphicsCapture,
                RecordingCaptureBackendSelection.FromEnvironment(RecordingCaptureMode.Gdi));
            Environment.SetEnvironmentVariable(RecordingCaptureBackendSelection.EnvironmentVariable, null);
            Assert.Equal(RecordingCaptureBackend.WindowsGraphicsCapture, RecordingCaptureBackendSelection.FromEnvironment());
            Assert.Equal(RecordingCaptureBackend.Gdi, RecordingCaptureBackendSelection.FromEnvironment(RecordingCaptureMode.Gdi));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RecordingCaptureBackendSelection.EnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData("auto", RecordingCaptureMode.Auto)]
    [InlineData("gdi", RecordingCaptureMode.Gdi)]
    [InlineData(" Gdi ", RecordingCaptureMode.Gdi)]
    [InlineData("wgc", RecordingCaptureMode.Auto)]
    [InlineData("", RecordingCaptureMode.Auto)]
    [InlineData(null, RecordingCaptureMode.Auto)]
    public void ModeNames_ParseUnknownAsAuto(string? value, RecordingCaptureMode expected)
    {
        Assert.Equal(expected, RecordingCaptureModeNames.Parse(value));
    }

    [Fact]
    public void ModeNames_RoundTrip()
    {
        Assert.Equal("auto", RecordingCaptureModeNames.ToSetting(RecordingCaptureMode.Auto));
        Assert.Equal("gdi", RecordingCaptureModeNames.ToSetting(RecordingCaptureMode.Gdi));
        Assert.Equal("auto", RecordingCaptureModeNames.ToSetting((RecordingCaptureMode)42));
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
