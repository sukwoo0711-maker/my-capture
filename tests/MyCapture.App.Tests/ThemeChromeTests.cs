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
            ThemeColor expected = ThemeCatalog.ColorsFor(ThemeService.ResolveTheme(theme))["Surface.Base"];
            var brush = Assert.IsType<SolidColorBrush>(resources["Surface.Base"]);
            Assert.Equal(Color.FromArgb(expected.A, expected.R, expected.G, expected.B), brush.Color);
        }
    });

    [Theory]
    [InlineData("glass", false, true)]
    [InlineData("glass-light", true, false)]
    [InlineData("daylight", true, false)]
    [InlineData("midnight", false, true)]
    [InlineData("workspace", false, true)]
    [InlineData("workspace", true, true)]
    public void CaptionDarknessFollowsPaletteAndWorkspaceRole(string themeId, bool videoEditor, bool dark)
    {
        AppTheme theme = AppThemeNames.Parse(themeId);
        WorkspaceRole role = videoEditor ? WorkspaceRole.VideoEditor : WorkspaceRole.Gallery;
        Assert.Equal(dark, ModernWindowChrome.UseDarkCaption(theme, role));
        Assert.Equal(theme is AppTheme.Glass or AppTheme.GlassLight, AppThemeNames.UsesBackdrop(theme));
    }

    [Theory]
    [InlineData("glass", false, 0x15, 0x17, 0x1F)]
    [InlineData("glass-light", false, 0xF0, 0xF1, 0xF4)]
    [InlineData("midnight", false, 0x15, 0x17, 0x1F)]
    [InlineData("daylight", false, 0xF0, 0xF1, 0xF4)]
    [InlineData("high-contrast", false, 0x00, 0x00, 0x00)]
    [InlineData("workspace", false, 0x15, 0x17, 0x1F)]
    [InlineData("workspace", true, 0x15, 0x17, 0x1F)]
    public void CaptionColourIsTheBodyColourTheWindowActuallyShows(string themeId, bool videoEditor, int r, int g, int b)
    {
        AppTheme theme = AppThemeNames.Parse(themeId);
        WorkspaceRole role = videoEditor ? WorkspaceRole.VideoEditor : WorkspaceRole.Gallery;
        WindowChromePlan plan = ModernWindowChrome.ResolveChrome(theme, role, systemHighContrast: false);

        // Glass surfaces are translucent and WPF clears the client to black under them.
        Assert.Equal(ModernWindowChrome.ToColorRef(ThemeColor.Rgb((byte)r, (byte)g, (byte)b)), plan.CaptionColor);
        Assert.Equal(ModernWindowChrome.UseDarkCaption(theme, role), plan.DarkMode);
        ThemeColor caption = FromColorRef(plan.CaptionColor!.Value);
        Assert.True(ThemeContrast.Ratio(FromColorRef(plan.TextColor!.Value), caption) >= 7.0, themeId + " caption text");
        Assert.NotNull(plan.BorderColor);
    }

    [Fact]
    public void EveryPaletteAndRoleColoursTheCaptionLikeItsBody()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        foreach (WorkspaceRole role in Enum.GetValues<WorkspaceRole>())
        {
            WindowChromePlan plan = ModernWindowChrome.ResolveChrome(theme, role, systemHighContrast: false);
            ThemeColor body = ThemeContrast.Composite(ModernWindowChrome.ClientColors(theme, role)["Surface.Base"],
                ThemeColor.Rgb(0, 0, 0));
            Assert.Equal(ModernWindowChrome.ToColorRef(body), plan.CaptionColor);
            // A dark caption flag on a light caption colour would draw unreadable caption buttons.
            Assert.Equal(ThemeContrast.RelativeLuminance(body) < 0.18, plan.DarkMode);
        }
    }

    [Fact]
    public void SystemHighContrastKeepsTheUsersCaptionPalette()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            WindowChromePlan plan = ModernWindowChrome.ResolveChrome(theme, WorkspaceRole.Gallery, systemHighContrast: true);
            Assert.Null(plan.CaptionColor);
            Assert.Null(plan.TextColor);
            Assert.Null(plan.BorderColor);
        }
    }

    private static ThemeColor FromColorRef(int value) =>
        ThemeColor.Rgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));

    [Fact]
    public void ColorRefUsesWindowsBgrOrder()
    {
        Assert.Equal(0x00503A2B, ModernWindowChrome.ToColorRef(ThemeColor.Rgb(0x2B, 0x3A, 0x50)));
        Assert.Equal(0x00FCF8F6, ModernWindowChrome.ToColorRef(ThemeColor.Rgb(0xF6, 0xF8, 0xFC)));
    }
}
