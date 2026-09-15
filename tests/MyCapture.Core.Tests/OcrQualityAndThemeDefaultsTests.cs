using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class OcrQualityAndThemeDefaultsTests
{
    [Theory]
    [InlineData("fast", OcrQuality.Fast, 1.0, false, false, false)]
    [InlineData("balanced", OcrQuality.Balanced, 2.0, true, false, false)]
    [InlineData("normal", OcrQuality.Balanced, 2.0, true, false, false)]
    [InlineData("accurate", OcrQuality.Accurate, 4.0, true, true, true)]
    [InlineData("slow", OcrQuality.Accurate, 4.0, true, true, true)]
    [InlineData("enhanced", OcrQuality.Enhanced, 2.0, true, true, true)]
    [InlineData("careful", OcrQuality.Enhanced, 2.0, true, true, true)]
    [InlineData("document", OcrQuality.Enhanced, 2.0, true, true, true)]
    [InlineData("precise", OcrQuality.Enhanced, 2.0, true, true, true)]
    public void QualityStageDerivesUpscaleRotationAndContrast(
        string id,
        OcrQuality expected,
        double upscale,
        bool rotate,
        bool contrast,
        bool neural)
    {
        OcrQuality quality = OcrQualityNames.Parse(id);
        Assert.Equal(expected, quality);
        Assert.Equal(upscale, OcrQualityProfile.UpscaleFactor(quality));
        Assert.Equal(rotate, OcrQualityProfile.SearchRotatedOrientations(quality));
        Assert.Equal(contrast, OcrQualityProfile.EnhanceContrast(quality));
        Assert.Equal(neural, OcrQualityProfile.UseNeuralModel(quality));
        Assert.Equal(OcrQualityNames.ToSetting(expected), OcrQualityNames.ToSetting(quality));
    }

    [Fact]
    public void Load_InfersLegacyUpscaleAsQualityStage()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        store.Save(new AppSettings { Ocr = { Quality = OcrQuality.Fast, UpscaleFactor = 4.0 } });
        AppSettings accurate = store.Load();
        Assert.Equal(OcrQuality.Accurate, accurate.Ocr.Quality);
        Assert.Equal(4.0, accurate.Ocr.UpscaleFactor);

        store.Save(new AppSettings { Ocr = { Quality = OcrQuality.Fast, UpscaleFactor = 2.0 } });
        AppSettings balanced = store.Load();
        Assert.Equal(OcrQuality.Balanced, balanced.Ocr.Quality);
        Assert.Equal(2.0, balanced.Ocr.UpscaleFactor);

        store.Save(new AppSettings { Ocr = { Quality = OcrQuality.Balanced, UpscaleFactor = 1.0 } });
        AppSettings fast = store.Load();
        Assert.Equal(OcrQuality.Fast, fast.Ocr.Quality);
        Assert.Equal(1.0, fast.Ocr.UpscaleFactor);
    }

    [Fact]
    public void Draft_MapsQualityStagesAndKeepsLaunchAtLoginOn()
    {
        var draft = new SettingsDraft(new AppSettings());
        Assert.True(draft.LaunchAtLogin);
        Assert.Equal(AppThemeNames.Glass, draft.Theme);
        Assert.Equal(OcrQualityNames.Fast, draft.OcrQuality);

        draft.OcrQuality = "balanced";
        draft.Theme = "glass-light";
        AppSettings mapped = draft.ToAppSettings();
        Assert.Equal(OcrQuality.Balanced, mapped.Ocr.Quality);
        Assert.Equal(2.0, mapped.Ocr.UpscaleFactor);
        Assert.Equal(AppThemeNames.GlassLight, mapped.General.Theme);
        Assert.True(mapped.General.LaunchAtLogin);

        draft.OcrQuality = "accurate";
        mapped = draft.ToAppSettings();
        Assert.Equal(OcrQuality.Accurate, mapped.Ocr.Quality);
        Assert.Equal(4.0, mapped.Ocr.UpscaleFactor);
        Assert.True(OcrQualityProfile.EnhanceContrast(mapped.Ocr.Quality));
        Assert.False(OcrQualityProfile.LocalCorrection(mapped.Ocr.Quality));

        draft.OcrQuality = "enhanced";
        mapped = draft.ToAppSettings();
        Assert.Equal(OcrQuality.Enhanced, mapped.Ocr.Quality);
        Assert.Equal(2.0, mapped.Ocr.UpscaleFactor);
        Assert.True(OcrQualityProfile.EnhanceContrast(mapped.Ocr.Quality));
        Assert.True(OcrQualityProfile.LocalCorrection(mapped.Ocr.Quality));
        Assert.True(OcrQualityProfile.UseNeuralModel(mapped.Ocr.Quality));
        Assert.True(OcrQualityProfile.UseSuperResolution(mapped.Ocr.Quality));
        Assert.False(OcrQualityProfile.UseSuperResolution(OcrQuality.Accurate));
        Assert.False(OcrQualityProfile.UseSuperResolution(OcrQuality.Fast));
    }
}
