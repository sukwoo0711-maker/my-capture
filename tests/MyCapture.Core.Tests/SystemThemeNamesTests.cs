using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class SystemThemeNamesTests
{
    [Theory]
    [InlineData("system", AppTheme.System, "system")]
    [InlineData("SYSTEM", AppTheme.System, "system")]
    [InlineData("workspace", AppTheme.Glass, "glass")]
    [InlineData("midnight", AppTheme.Glass, "glass")]
    [InlineData("daylight", AppTheme.GlassLight, "glass-light")]
    [InlineData("light", AppTheme.GlassLight, "glass-light")]
    [InlineData("glass", AppTheme.Glass, "glass")]
    [InlineData("mica", AppTheme.Glass, "glass")]
    [InlineData("acrylic", AppTheme.Glass, "glass")]
    [InlineData("mica-light", AppTheme.GlassLight, "glass-light")]
    [InlineData("light-glass", AppTheme.GlassLight, "glass-light")]
    [InlineData("highcontrast", AppTheme.HighContrast, "high-contrast")]
    [InlineData("unknown", AppTheme.Midnight, "glass")]
    [InlineData(null, AppTheme.Midnight, "glass")]
    public void LegacyNamesParseAndSaveUsingTheFourCanonicalNames(string? name, AppTheme expected, string canonical)
    {
        Assert.Equal(expected, AppThemeNames.Parse(name));
        Assert.Equal(canonical, AppThemeNames.ToSetting(AppThemeNames.Parse(name)));
    }

    [Fact]
    public void SystemIsAppendedWithoutChangingPersistedEnumValues()
    {
        Assert.Equal(0, (int)AppTheme.Midnight);
        Assert.Equal(1, (int)AppTheme.Daylight);
        Assert.Equal(2, (int)AppTheme.HighContrast);
        Assert.Equal(3, (int)AppTheme.Workspace);
        Assert.Equal(4, (int)AppTheme.Glass);
        Assert.Equal(5, (int)AppTheme.GlassLight);
        Assert.Equal(6, (int)AppTheme.System);
        Assert.Equal("glass", AppThemeNames.ToSetting(AppTheme.Workspace));
        Assert.Equal("glass-light", AppThemeNames.ToSetting(AppTheme.Daylight));
    }
}
