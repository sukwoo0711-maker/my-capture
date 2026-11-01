using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsGuideCatalogTests
{
    [Fact]
    public void AllThirteenIdsAndSettingMappingsAreStableAndLocalized()
    {
        Assert.Equal(Enumerable.Range(1, 13).Select(n => $"G{n:00}"), SettingsGuideCatalog.All.Select(g => g.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => SettingsGuideCatalog.Get("../other"));
        Assert.Empty(SettingsGuideCatalog.ForSetting("Unknown"));
        foreach (string language in new[] { "ko", "en" })
        {
            using var culture = UiText.UseLanguage(language);
            foreach (var guide in SettingsGuideCatalog.All)
            {
                Assert.Same(guide, SettingsGuideCatalog.Get(guide.Id));
                Assert.False(string.IsNullOrWhiteSpace(guide.Title));
                Assert.Equal(UiText.Get(guide.Id == "G09" ? "SettingsGuide.CompareResults" : "SettingsGuide.SeeDifference"), guide.ButtonLabel);
                foreach (string setting in guide.SettingKeys) Assert.Contains(guide, SettingsGuideCatalog.ForSetting(setting));
                foreach (var comparison in guide.Comparisons)
                {
                    Assert.False(string.IsNullOrWhiteSpace(comparison.Title));
                    Assert.NotEqual(comparison.Before, comparison.After);
                    Assert.False(string.IsNullOrWhiteSpace(comparison.Description));
                }
            }
        }
    }

    [Theory]
    [InlineData("G01")]
    [InlineData("G02")]
    [InlineData("G03")]
    [InlineData("G04")]
    [InlineData("G05")]
    [InlineData("G06")]
    [InlineData("G07")]
    [InlineData("G08")]
    [InlineData("G09")]
    [InlineData("G10")]
    [InlineData("G11")]
    [InlineData("G12")]
    [InlineData("G13")]
    public void EveryComparisonResolvesToPackagedPngs(string id)
    {
        foreach (var comparison in SettingsGuideCatalog.Get(id).Comparisons)
        foreach (string language in new[] { "ko", "en" })
        foreach (string frame in new[] { "before", "after" }.Concat(Enumerable.Range(0, comparison.MotionFrames).Select(n => $"motion{n}")))
        {
            string path = Path.Combine(SettingsGuideCatalog.AssetDirectory, comparison.AssetFile(language, frame));
            Assert.True(File.Exists(path), $"Missing required guide asset: {path}");
            using var stream = File.OpenRead(path);
            var png = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.True(png.Frames[0].PixelWidth > 200 && png.Frames[0].PixelHeight > 100);
        }
    }
}
