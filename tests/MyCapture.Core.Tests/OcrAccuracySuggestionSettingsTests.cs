using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class OcrAccuracySuggestionSettingsTests
{
    [Fact]
    public void NewAndLegacySettingsOfferSuggestionWithoutChangingFastDefault()
    {
        var fresh = new AppSettings();
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.False(fresh.Ocr.AccuracySuggestionDismissed);
        Assert.False(legacy.Ocr.AccuracySuggestionDismissed);
        Assert.Equal(OcrQuality.Fast, fresh.Ocr.Quality);
        Assert.Equal(OcrQuality.Fast, legacy.Ocr.Quality);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloneAndSettingsDraftPreserveDismissal(bool dismissed)
    {
        var source = new AppSettings { Ocr = { AccuracySuggestionDismissed = dismissed, Quality = OcrQuality.Balanced } };
        AppSettings clone = source.DeepClone();
        Assert.Equal(dismissed, clone.Ocr.AccuracySuggestionDismissed);
        Assert.Equal(dismissed, new SettingsDraft(source).ToAppSettings().Ocr.AccuracySuggestionDismissed);
        clone.Ocr.AccuracySuggestionDismissed = !dismissed;
        Assert.Equal(dismissed, source.Ocr.AccuracySuggestionDismissed);
        Assert.Equal(OcrQuality.Balanced, source.Ocr.Quality);
    }

    [Fact]
    public void DismissalSurvivesSettingsStoreSaveAndReload()
    {
        var paths = AppPaths.CreateForRoot(Path.Combine(Path.GetTempPath(),
            "ocr-accuracy-settings-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var settings = new AppSettings { Ocr = { AccuracySuggestionDismissed = true } };

        store.Save(settings);

        AppSettings reloaded = new SettingsStore(paths, NullLogger<SettingsStore>.Instance).Load();
        Assert.True(reloaded.Ocr.AccuracySuggestionDismissed);
        Assert.Equal(OcrQuality.Fast, reloaded.Ocr.Quality);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile));
        Assert.True(saved.RootElement.GetProperty("ocr").GetProperty("accuracySuggestionDismissed").GetBoolean());
        // Retain this isolated fixture; cleanup is owned by the host.
    }
}
