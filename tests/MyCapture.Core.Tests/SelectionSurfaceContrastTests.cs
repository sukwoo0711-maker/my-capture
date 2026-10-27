using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class SelectionSurfaceContrastTests
{
    [Fact]
    public void SelectedSurfaceIsStrongerWhileEveryTextRoleRemainsReadable()
    {
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var colors = ThemeCatalog.ColorsFor(theme);
            ThemeColor selected = colors["Surface.Selected"];
            ThemeColor previous = AppThemeNames.IsLight(theme)
                ? ThemeColor.Rgb(0xDD, 0xE3, 0xEB) : ThemeColor.Rgb(0x30, 0x36, 0x42);
            foreach (ThemeColor backdrop in new[] { ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255) })
            {
                ThemeColor background = ThemeContrast.Composite(colors["Surface.Base"], backdrop);
                Assert.True(ThemeContrast.Ratio(selected, background) > ThemeContrast.Ratio(previous, background), theme.ToString());
                foreach (string role in new[] { "Text.Primary", "Text.Secondary", "Text.Muted" })
                    Assert.True(ThemeContrast.Ratio(colors[role], selected) >= 4.5, $"{theme}/{role}");
            }
            int step = AppThemeNames.IsLight(theme) ? -1 : 1;
            ThemeColor stronger = ThemeColor.Rgb((byte)(selected.R + step), (byte)(selected.G + step), (byte)(selected.B + step));
            Assert.True(ThemeContrast.Ratio(colors["Text.Muted"], stronger) < 4.5,
                $"{theme}: selected surface stops at the muted-text contrast boundary");
        }
    }
}
