using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class OcrQualityMigrationTests
{
    [Fact]
    public void LegacyEnumHasTheEntireAccurateProfile()
    {
        Assert.Equal(3, (int)OcrQuality.Enhanced);
        Assert.Equal(OcrQuality.Accurate, OcrQualityNames.Normalize(OcrQuality.Enhanced));
        Assert.Equal("accurate", OcrQualityNames.ToSetting(OcrQuality.Enhanced));
        Assert.Equal(4.0, OcrQualityProfile.UpscaleFactor(OcrQuality.Enhanced));
        Assert.Equal(OcrQualityProfile.SearchRotatedOrientations(OcrQuality.Accurate),
            OcrQualityProfile.SearchRotatedOrientations(OcrQuality.Enhanced));
        Assert.Equal(OcrQualityProfile.EnhanceContrast(OcrQuality.Accurate),
            OcrQualityProfile.EnhanceContrast(OcrQuality.Enhanced));
        Assert.Equal(OcrQualityProfile.UseNeuralModel(OcrQuality.Accurate),
            OcrQualityProfile.UseNeuralModel(OcrQuality.Enhanced));
        Assert.False(OcrQualityProfile.LocalCorrection(OcrQuality.Enhanced));
        Assert.False(OcrQualityProfile.UseSuperResolution(OcrQuality.Enhanced));
        Assert.Equal(OcrQuality.Accurate, OcrQualityProfile.ResolveLoaded(OcrQuality.Enhanced, 2.0));
        var settings = new OcrSettings { Quality = OcrQuality.Enhanced, UpscaleFactor = 2.0 };
        OcrQualityProfile.Synchronize(settings);
        Assert.Equal(OcrQuality.Accurate, settings.Quality);
        Assert.Equal(4.0, settings.UpscaleFactor);
    }

    [Fact]
    public void NormalizationPreservesUnknownValueFallbacks()
    {
        var unknown = (OcrQuality)99;
        Assert.Equal(unknown, OcrQualityNames.Normalize(unknown));
        Assert.Equal(OcrQuality.Balanced, OcrQualityNames.Parse("unknown"));
        Assert.Equal("balanced", OcrQualityNames.ToSetting(unknown));
        Assert.Equal(2.0, OcrQualityProfile.UpscaleFactor(unknown));
    }

    [Theory]
    [InlineData(1.0, OcrQuality.Fast)]
    [InlineData(2.0, OcrQuality.Balanced)]
    [InlineData(4.0, OcrQuality.Accurate)]
    public void LegacyUpscaleOnlyProducesCurrentQualities(double upscale, OcrQuality expected) =>
        Assert.Equal(expected, OcrQualityProfile.FromUpscale(upscale));

    [Fact]
    public void CloneAndDraftNormalizeLegacyValueWithoutChangingSource()
    {
        var source = new AppSettings { Ocr = { Quality = OcrQuality.Enhanced, UpscaleFactor = 2.0 } };
        AppSettings clone = source.DeepClone();
        Assert.Equal(OcrQuality.Accurate, clone.Ocr.Quality);
        Assert.Equal(4.0, clone.Ocr.UpscaleFactor);
        var draft = new SettingsDraft(source);
        Assert.Equal("accurate", draft.OcrQuality);
        Assert.Equal(OcrQuality.Accurate, draft.ToAppSettings().Ocr.Quality);
        Assert.Equal(4.0, draft.ToAppSettings().Ocr.UpscaleFactor);
        Assert.Equal(OcrQuality.Enhanced, source.Ocr.Quality);
        Assert.Equal(2.0, source.Ocr.UpscaleFactor);
    }

    [Theory]
    [InlineData("\"enhanced\"")]
    [InlineData("\"ENHANCED\"")]
    [InlineData("3")]
    public void LoadAndSaveMigrateLegacyDiskValue(string legacyJsonValue)
    {
        AppPaths paths = CreateRetainedPaths();
        File.WriteAllText(paths.SettingsFile,
            $$$"""{"schemaVersion":3,"ocr":{"quality":{{{legacyJsonValue}}},"upscaleFactor":2}}""");
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();
        Assert.Equal(OcrQuality.Accurate, loaded.Ocr.Quality);
        Assert.Equal(4.0, loaded.Ocr.UpscaleFactor);
        store.Save(loaded);
        AssertCanonicalDiskValue(paths.SettingsFile);
        Assert.Equal(OcrQuality.Accurate, store.Load().Ocr.Quality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveCanonicalizesDirectLegacyEnumInForegroundAndBackground(bool background)
    {
        AppPaths paths = CreateRetainedPaths();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var source = new AppSettings { Ocr = { Quality = OcrQuality.Enhanced, UpscaleFactor = 2.0 } };
        if (background)
        {
            store.SaveInBackground(source);
            Assert.True(store.WaitForPendingSaves(TimeSpan.FromSeconds(5)));
        }
        else store.Save(source);
        AssertCanonicalDiskValue(paths.SettingsFile);
        Assert.Equal(OcrQuality.Enhanced, source.Ocr.Quality);
        Assert.Equal(2.0, source.Ocr.UpscaleFactor);
    }

    [Fact]
    public void ImportAndExportNormalizeLegacyQuality()
    {
        AppPaths paths = CreateRetainedPaths();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        string importPath = Path.Combine(paths.DataRoot, "legacy-import.json");
        File.WriteAllText(importPath, """{"ocr":{"quality":"enhanced","upscaleFactor":2}}""");
        AppSettings imported = store.ImportFrom(importPath);
        Assert.Equal(OcrQuality.Accurate, imported.Ocr.Quality);
        Assert.Equal(4.0, imported.Ocr.UpscaleFactor);
        string exportPath = Path.Combine(paths.DataRoot, "export.json");
        var source = new AppSettings { Ocr = { Quality = OcrQuality.Enhanced, UpscaleFactor = 2.0 } };
        store.ExportTo(source, exportPath);
        AssertCanonicalDiskValue(exportPath);
        Assert.Equal(OcrQuality.Enhanced, source.Ocr.Quality);
    }

    private static void AssertCanonicalDiskValue(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement ocr = document.RootElement.GetProperty("ocr");
        Assert.Equal("accurate", ocr.GetProperty("quality").GetString());
        Assert.Equal(4.0, ocr.GetProperty("upscaleFactor").GetDouble());
    }

    private static AppPaths CreateRetainedPaths()
    {
        // Keep this synthetic fixture: the existing shared cleanup helper permanently deletes it.
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(), "ocr-quality-migration-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        return paths;
    }
}
