using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.App.Capture;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SystemThemeTests
{
    [Theory]
    [InlineData(null, AppTheme.GlassLight)]
    [InlineData(0, AppTheme.Glass)]
    [InlineData(1, AppTheme.GlassLight)]
    public void RawWindowsAppModeDefaultsMissingValuesToLight(int? rawValue, AppTheme expected)
    {
        int reads = 0;
        AppTheme resolved = ThemeService.ResolveTheme(AppTheme.System, () =>
            ThemeService.ReadWindowsLightTheme(() => { reads++; return rawValue; }));

        Assert.Equal(expected, resolved);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(SecurityException))]
    public void WindowsAppModeReadFailuresKeepTheDarkFallback(Type exceptionType)
    {
        var failure = (Exception)Activator.CreateInstance(exceptionType)!;
        int reads = 0;
        AppTheme resolved = ThemeService.ResolveTheme(AppTheme.System, () =>
            ThemeService.ReadWindowsLightTheme(() => { reads++; throw failure; }));

        Assert.Equal(AppTheme.Glass, resolved);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void InvalidStoredAppModeKeepsTheExistingDarkFallback()
    {
        Assert.False(ThemeService.ReadWindowsLightTheme(() => "1"));
        Assert.Throws<InvalidOperationException>(() =>
            ThemeService.ReadWindowsLightTheme(() => throw new InvalidOperationException("Unexpected reader failure.")));
    }

    [Theory]
    [InlineData(true, AppTheme.GlassLight)]
    [InlineData(false, AppTheme.Glass)]
    public void SystemResolvesUsingTheInjectedWindowsAppMode(bool light, AppTheme expected)
    {
        Assert.Equal(expected, ThemeService.ResolveTheme(AppTheme.System, () => light));
        foreach (AppTheme explicitTheme in Enum.GetValues<AppTheme>().Where(value => value != AppTheme.System))
            Assert.Equal(explicitTheme, ThemeService.ResolveTheme(explicitTheme, () => throw new InvalidOperationException("Explicit choices must not read Windows.")));
    }

    [Fact]
    public void PreferenceRefreshFollowsLightAndDarkWithoutChangingTheSavedSelection() => StaTestHost.Run(() =>
    {
        AppTheme previous = ThemeService.Current;
        Func<bool>? previousReader = ThemeService.SystemLightThemeReaderForTest;
        bool light = true;
        ThemeService.SystemLightThemeReaderForTest = () => light;
        ThemeService.SystemHighContrastOverrideForTest = false;
        try
        {
            ThemeService.Apply(AppTheme.System);
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.System, resources, systemHighContrast: false);
            AssertPalette(AppTheme.GlassLight);
            var original = (SolidColorBrush)resources["Surface.Base"];
            Brush follower = ThemeBrushes.Follow(resources, "Surface.Base", Brushes.Black);
            light = false;
            Refresh(false);
            AssertPalette(AppTheme.Glass);
            Assert.NotSame(original, resources["Surface.Base"]);
            Assert.Equal(ToColor(ThemeCatalog.ColorsFor(AppTheme.GlassLight)["Surface.Base"]), original.Color);
            Assert.Equal(((SolidColorBrush)resources["Surface.Base"]).Color, ((SolidColorBrush)follower).Color);
            light = true;
            Refresh(false);
            AssertPalette(AppTheme.GlassLight);
            Assert.Equal(AppTheme.System, ThemeService.Current);
            Assert.Equal("system", AppThemeNames.ToSetting(ThemeService.Current));

            ThemeService.SystemColorsOverrideForTest = role => SystemHighContrastThemeTests.NightSky[role];
            Refresh(true);
            Assert.Equal(SystemHighContrastThemeTests.NightSky[SystemColorRole.Window], ((SolidColorBrush)resources["Surface.Base"]).Color);
            Assert.Equal(SystemHighContrastThemeTests.NightSky[SystemColorRole.WindowText], ((SolidColorBrush)resources["Border.Focus"]).Color);
            Assert.Equal(AppTheme.System, ThemeService.Current);
            Refresh(false);
            AssertPalette(AppTheme.GlassLight);

            void Refresh(bool highContrast)
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                DispatcherOperation operation = ThemeService.ScheduleSystemThemeRefresh(dispatcher, resources, () => highContrast);
                dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                Assert.Equal(DispatcherOperationStatus.Completed, operation.Status);
            }
            void AssertPalette(AppTheme expected)
            {
                Assert.Equal(expected, ThemeService.EffectiveTheme);
                Assert.Equal(ToColor(ThemeCatalog.ColorsFor(expected)["Surface.Base"]), ((SolidColorBrush)resources["Surface.Base"]).Color);
                Assert.Equal(new Thickness(2), resources["Focus.RingThickness"]);
                Assert.Equal(ToColor(ThemeCatalog.ColorsFor(expected)["Recording.Live"]), ((SolidColorBrush)RecordingSelectionCue.Resolve()).Color);
                Assert.Equal(ToColor(ThemeCatalog.ColorsFor(expected)["Text.Primary"]), ((SolidColorBrush)CaptureGuidePalette.Resolve()["CaptureGuide.Text"]).Color);
            }
        }
        finally
        {
            ThemeService.SystemColorsOverrideForTest = null;
            ThemeService.SystemHighContrastOverrideForTest = null;
            ThemeService.SystemLightThemeReaderForTest = previousReader;
            ThemeService.Apply(previous);
        }
    });

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
