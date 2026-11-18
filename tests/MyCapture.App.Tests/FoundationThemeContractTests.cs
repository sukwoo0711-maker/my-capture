using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FoundationThemeContractTests
{
    private static readonly string[] SurfaceKeys =
        ["Surface.Canvas", "Surface.Base", "Surface.Raised", "Surface.Floating"];

    [Fact]
    public void StartupAndLivePalettesUseTheSameCanonicalColourValues() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        foreach ((string key, ThemeColor expected) in ThemeCatalog.ColorsFor(AppTheme.Midnight))
            Assert.Equal(ToColor(expected), Assert.IsType<SolidColorBrush>(resources[key]).Color);

        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            // System selection is covered by the existing injected preference-reader tests.
            AppTheme resolved = ThemeService.ResolveTheme(theme, () => false);
            ThemeService.ApplyResources(resolved, resources, systemHighContrast: false);
            foreach ((string key, ThemeColor expected) in ThemeCatalog.ColorsFor(resolved))
                Assert.Equal(ToColor(expected), Assert.IsType<SolidColorBrush>(resources[key]).Color);
            foreach (string key in SurfaceKeys)
                Assert.Equal(byte.MaxValue, Assert.IsType<SolidColorBrush>(resources[key]).Color.A);
            if (resolved != AppTheme.HighContrast)
                Assert.Equal(Assert.IsType<SolidColorBrush>(resources["Surface.Floating"]).Color,
                    Assert.IsType<SolidColorBrush>(resources["Surface.Overlay"]).Color);
        }
    });

    [Fact]
    public void WorkspaceRolesShareTheCanonicalLightAndDarkPalettes() => StaTestHost.Run(() =>
    {
        foreach ((WorkspaceRole role, AppTheme palette) in new[]
                 { (WorkspaceRole.Gallery, AppTheme.Daylight), (WorkspaceRole.VideoEditor, AppTheme.Midnight) })
        {
            ResourceDictionary resources = LoadTokens();
            WorkspaceTheme.Apply(resources, role, AppTheme.Workspace, systemHighContrast: false);
            foreach ((string key, ThemeColor expected) in ThemeCatalog.ColorsFor(palette))
                Assert.Equal(ToColor(expected), Assert.IsType<SolidColorBrush>(resources[key]).Color);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothSystemContrastDirectionsKeepEveryPaletteRolePaired(bool light) => StaTestHost.Run(() =>
    {
        Color Read(SystemColorRole role) => role switch
        {
            SystemColorRole.Window or SystemColorRole.ButtonFace => light ? Colors.White : Colors.Black,
            SystemColorRole.Highlight => light ? Colors.Navy : Colors.Yellow,
            SystemColorRole.HighlightText => light ? Colors.White : Colors.Black,
            SystemColorRole.GrayText => light ? Colors.DarkGray : Colors.LightGray,
            _ => light ? Colors.Black : Colors.White,
        };
        Func<SystemColorRole, Color>? previous = ThemeService.SystemColorsOverrideForTest;
        ThemeService.SystemColorsOverrideForTest = Read;
        try
        {
            ResourceDictionary resources = LoadTokens();
            ThemeService.ApplyResources(AppTheme.Glass, resources, systemHighContrast: true);
            foreach (string key in ThemeCatalog.ColorsFor(AppTheme.Glass).Keys)
                Assert.Equal(Read(HighContrastPalette.RoleFor(key)), Assert.IsType<SolidColorBrush>(resources[key]).Color);
            foreach (string key in SurfaceKeys)
                Assert.Equal(byte.MaxValue, Assert.IsType<SolidColorBrush>(resources[key]).Color.A);
            Assert.Equal(new Thickness(2), resources["Focus.RingThickness"]);
            Assert.Equal(new Thickness(2), resources["Selection.Thickness"]);
        }
        finally
        {
            ThemeService.SystemColorsOverrideForTest = previous;
        }
    });

    [Fact]
    public void CodeAndXamlShareTheSameFoundationDimensions() => StaTestHost.Run(() =>
    {
        ResourceDictionary resources = LoadTokens();
        Assert.Equal(FoundationMetrics.ControlHeight, resources["Size.HitTarget"]);
        Assert.Equal(FoundationMetrics.CompactControlHeight, resources["Size.HitTarget.Compact"]);
        Assert.Equal(FoundationMetrics.ControlCornerRadius, resources["Radius.Control"]);
        Assert.Equal(resources["Radius.Control"], resources["Radius.Sm"]);
        Assert.Equal(FoundationMetrics.FloatingCornerRadius, resources["Radius.Floating"]);
        Assert.Equal(resources["Radius.Floating"], resources["Radius.Md"]);
        Assert.Equal(FoundationMetrics.ContentCornerRadius, resources["Radius.Content"]);
        Assert.Equal(resources["Radius.Content"], resources["Radius.Lg"]);
        Assert.Equal(FontWeights.Normal, resources["FontWeight.Regular"]);
        Assert.Equal(FontWeights.SemiBold, resources["FontWeight.Emphasis"]);
    });

    private static Color ToColor(ThemeColor value) => Color.FromArgb(value.A, value.R, value.G, value.B);

    private static ResourceDictionary LoadTokens()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        return new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute),
        };
    }
}
