using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ThemeOpacityTests
{
    [Fact]
    public void HighContrastRefreshReadsSystemStateAfterHigherPriorityNotifications() => StaTestHost.Run(() =>
    {
        var resources = new ResourceDictionary();
        ThemeService.ApplyCropResources(resources, false);
        var surface = new System.Windows.Controls.Border { Resources = resources };
        surface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Crop.Scrim");
        bool highContrast = false, colorsUpdated = false, read = false;
        var dispatcher = Dispatcher.CurrentDispatcher;
        var operation = ThemeService.ScheduleCropResourceRefresh(dispatcher, resources, () =>
        {
            Assert.True(colorsUpdated); read = true; return highContrast;
        });
        Assert.False(read);
        Assert.Equal(Color.FromArgb(153, 8, 12, 18), ((SolidColorBrush)surface.Background).Color);
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { highContrast = true; colorsUpdated = true; }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Assert.Equal(DispatcherOperationStatus.Completed, operation.Status);
        Assert.True(read); Assert.Equal(SystemColors.WindowBrush.Color, ((SolidColorBrush)surface.Background).Color);
        Assert.Same(SystemColors.WindowTextBrush, resources["Crop.Frame"]);
    });

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
        Color before = original.Color;
        ThemeService.ApplyResources(AppTheme.Midnight, resources);
        var updated = (SolidColorBrush)resources["Overlay.Dimmer"];
        Assert.Equal(opacity, updated.Opacity);
        // Token brushes are swapped for frozen brushes, never recoloured in place (ThemeBrushes).
        Assert.True(updated.IsFrozen);
        Assert.NotSame(original, updated);
        Assert.Equal(before, original.Color);
        ThemeService.ApplyResources(AppTheme.Daylight, resources);
        Assert.Equal(opacity, ((SolidColorBrush)resources["Overlay.Dimmer"]).Opacity);
        ThemeService.ApplyResources(AppTheme.Glass, resources);
        Assert.Equal(opacity, ((SolidColorBrush)resources["Overlay.Dimmer"]).Opacity);
        ThemeService.ApplyResources(AppTheme.GlassLight, resources);
        Assert.Equal(opacity, ((SolidColorBrush)resources["Overlay.Dimmer"]).Opacity);
    });
}
