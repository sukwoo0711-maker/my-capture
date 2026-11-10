using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentFreeformProbeTests
{
    [Theory]
    [InlineData("selector-freeform")]
    [InlineData("selector-freeform-once")]
    public void FreeformMeasurementsAreOptInAndAvailableForBothRenderers(string variant)
    {
        Assert.DoesNotContain(variant, ResidentProbeOptions.ReadVariants([]));
        Assert.Contains(variant, ResidentProbeOptions.ReadVariants(["--resident-variant", variant]));
        var wpf = ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-wpf," + variant]);
        Assert.Contains(variant, wpf);
        Assert.False(ResidentProbeOptions.UsesNativeSelector(wpf));
    }

    [Fact]
    public void EveryCaptureAndFirstCaptureCannotBeRequestedTogether() =>
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(
            ["--resident-variant", "selector-freeform,selector-freeform-once"]));
}
