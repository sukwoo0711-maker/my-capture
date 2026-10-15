using MyCapture.Core.Platform;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class WindowsFeatureSupportTests
{
    [Theory]
    [InlineData(19045, false, false, 0.0)]
    [InlineData(21999, false, false, 0.0)]
    [InlineData(22000, true, false, 8.0)]
    [InlineData(22620, true, false, 8.0)]
    [InlineData(22621, true, true, 8.0)]
    public void OptionalFeatures_AreIndependentOfHostSupport(int build, bool chrome, bool backdrop, double radius)
    {
        Assert.True(WindowsSupportPolicy.IsSupportedBuild(build));
        Assert.Equal(chrome, WindowsFeatureSupport.HasRoundedWindowCorners(build));
        Assert.Equal(chrome, WindowsFeatureSupport.HasDwmCaptionColors(build));
        Assert.Equal(chrome, WindowsFeatureSupport.HasBorderlessCapture(build));
        Assert.Equal(chrome, WindowsFeatureSupport.HasAccessibilitySettingsName(build));
        Assert.Equal(backdrop, WindowsFeatureSupport.HasSystemBackdrop(build));
        Assert.Equal(radius, WindowsFeatureSupport.WindowCornerRadiusDip(build));
    }
}
