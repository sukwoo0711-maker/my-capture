using System.Windows;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ThemeChromeTests
{
    [Fact]
    public void EveryPaletteRecoloursSharedTokensIncludingGlass() => StaTestHost.Run(() =>
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = new FrameworkElement();
        var resources = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MyCapture;component/Themes/Tokens.xaml", UriKind.Absolute),
        };

        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            ThemeService.ApplyResources(theme, resources);
            ThemeColor expected = ThemeCatalog.ColorsFor(theme)["Surface.Base"];
            var brush = Assert.IsType<SolidColorBrush>(resources["Surface.Base"]);
            Assert.Equal(Color.FromArgb(expected.A, expected.R, expected.G, expected.B), brush.Color);
        }
    });

    [Theory]
    [InlineData("glass", false, true)]
    [InlineData("glass-light", true, false)]
    [InlineData("daylight", true, false)]
    [InlineData("midnight", false, true)]
    [InlineData("workspace", false, false)]
    [InlineData("workspace", true, true)]
    public void CaptionDarknessFollowsPaletteAndWorkspaceRole(string themeId, bool videoEditor, bool dark)
    {
        AppTheme theme = AppThemeNames.Parse(themeId);
        WorkspaceRole role = videoEditor ? WorkspaceRole.VideoEditor : WorkspaceRole.Gallery;
        Assert.Equal(dark, ModernWindowChrome.UseDarkCaption(theme, role));
        Assert.Equal(theme is AppTheme.Glass or AppTheme.GlassLight, AppThemeNames.UsesBackdrop(theme));
    }

    [Fact]
    public void ColorRefUsesWindowsBgrOrder()
    {
        Assert.Equal(0x00503A2B, ModernWindowChrome.ToColorRef(ThemeColor.Rgb(0x2B, 0x3A, 0x50)));
        Assert.Equal(0x00FCF8F6, ModernWindowChrome.ToColorRef(ThemeColor.Rgb(0xF6, 0xF8, 0xFC)));
    }
}
