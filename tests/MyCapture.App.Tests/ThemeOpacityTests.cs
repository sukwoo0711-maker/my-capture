using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ThemeOpacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyingPalette_PreservesCaptureDimmerTransparency(bool frozen) => StaTestHost.Run(() =>
    {
        var resources = new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml") };
        var original = ((SolidColorBrush)resources["Overlay.Dimmer"]).CloneCurrentValue();
        Assert.InRange(original.Opacity, 0.01, 0.99);
        double opacity = original.Opacity;
        if (frozen) original.Freeze();
        resources["Overlay.Dimmer"] = original;
        ThemeService.ApplyResources(AppTheme.Midnight, resources);
        var updated = (SolidColorBrush)resources["Overlay.Dimmer"];
        Assert.Equal(opacity, updated.Opacity);
        Assert.False(updated.IsFrozen);
        if (frozen) Assert.NotSame(original, updated);
        else Assert.Same(original, updated);
        ThemeService.ApplyResources(AppTheme.Daylight, resources);
        Assert.Equal(opacity, ((SolidColorBrush)resources["Overlay.Dimmer"]).Opacity);
    });
}
