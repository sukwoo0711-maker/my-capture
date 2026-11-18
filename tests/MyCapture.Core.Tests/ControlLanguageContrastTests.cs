using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ControlLanguageContrastTests
{
    [Theory]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void InputAndCheckEdgesMeetThreeToOneOnBothSurfaces(AppTheme theme)
    {
        var colors = ThemeCatalog.ColorsFor(theme);
        foreach (string surfaceKey in new[] { "Surface.Base", "Surface.Raised" })
        foreach (ThemeColor backdrop in new[] { ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255) })
        {
            ThemeColor surface = ThemeContrast.Composite(colors[surfaceKey], backdrop);
            foreach (string edgeKey in new[] { "Stroke.InputEdge", "Stroke.CheckEdge" })
            {
                double ratio = ThemeContrast.Ratio(ThemeContrast.Composite(colors[edgeKey], surface), surface);
                Assert.True(ratio >= 3, $"{theme}/{edgeKey}/{surfaceKey}: {ratio:F3}:1");
            }
        }
        ThemeColor baseColor = colors["Surface.Base"];
        ThemeColor mark = ThemeColor.Rgb(baseColor.R, baseColor.G, baseColor.B);
        Assert.True(ThemeContrast.Ratio(colors["Text.Primary"], mark) >= 4.5);
        Assert.Equal((byte)255, mark.A);
    }

    [Fact]
    public void HighContrastNewTokensRetainTheExistingControlRoles()
    {
        var colors = ThemeCatalog.ColorsFor(AppTheme.HighContrast);
        foreach (string key in new[] { "Fill.Input", "Fill.InputHover", "Fill.InputActive" })
            Assert.Equal(colors["Surface.Sunken"], colors[key]);
        foreach (string key in new[] { "Stroke.InputEdge", "Stroke.CheckEdge" })
            Assert.Equal(colors["Border.Control"], colors[key]);
        Assert.Equal(colors["Text.Primary"], colors["Stroke.Selected"]);
        Assert.Equal(colors["Border.Strong"], colors["Fill.ScrollThumb"]);
        Assert.Equal(colors["Text.Muted"], colors["Fill.ScrollThumbHover"]);
        Assert.Equal(colors["Text.Primary"], colors["Fill.ScrollThumbPressed"]);
    }
}
