using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ThemeOpacityTests
{
    [Fact]
    public void CropMaskFollowsWindowsHighContrastAndUpdatesLiveResources() => StaTestHost.Run(() =>
    {
        var resources = new ResourceDictionary();
        ThemeService.ApplyResources(AppTheme.HighContrast, resources);
        Assert.Equal(SystemParameters.HighContrast ? SystemColors.WindowBrush.Color : Color.FromArgb(153, 8, 12, 18),
            ((SolidColorBrush)resources["Crop.Scrim"]).Color);
        var surface = new System.Windows.Controls.Border { Resources = resources };
        surface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Crop.Scrim");
        ThemeService.ApplyCropResources(resources, true);
        Assert.Equal(SystemColors.WindowBrush.Color, ((SolidColorBrush)surface.Background).Color);
        Assert.Same(SystemColors.WindowTextBrush, resources["Crop.Frame"]);
        ThemeService.ApplyCropResources(resources, false);
        Assert.Equal(Color.FromArgb(153, 8, 12, 18), ((SolidColorBrush)surface.Background).Color);
        Assert.Same(Brushes.White, resources["Crop.Frame"]);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyingPalette_PreservesCaptureDimmerTransparency(bool frozen) => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute),
        };
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
        ThemeService.ApplyResources(AppTheme.Glass, resources);
        Assert.Equal(opacity, ((SolidColorBrush)resources["Overlay.Dimmer"]).Opacity);
        ThemeService.ApplyResources(AppTheme.GlassLight, resources);
        Assert.Equal(opacity, ((SolidColorBrush)resources["Overlay.Dimmer"]).Opacity);
    });
}
