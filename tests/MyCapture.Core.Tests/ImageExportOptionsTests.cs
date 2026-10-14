using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ImageExportOptionsTests
{
    [Theory]
    [InlineData(4000, 2000, 1280, 1280, 640)]
    [InlineData(2000, 4000, 1920, 960, 1920)]
    [InlineData(640, 480, 800, 640, 480)]
    [InlineData(800, 1, 800, 800, 1)]
    [InlineData(10000, 1, 800, 800, 1)]
    [InlineData(4000, 2000, 0, 4000, 2000)]
    public void LongEdgeNeverUpscales(int width, int height, int edge, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ImageExportOptions.Fit(width, height, edge));

    [Theory]
    [InlineData(0)] [InlineData(24)] [InlineData(128)]
    public void StyledSizeAddsExactlyTwoMargins(int margin) =>
        Assert.Equal((1280 + 2 * margin, 640 + 2 * margin),
            new ImageExportOptions { LongEdge = 1280, Margin = margin, Shadow = true, CornerRadius = 48 }.OutputSize(4000, 2000));

    [Theory]
    [InlineData(ImageExportPreset.Original, ImageExportFormat.Automatic, 0)]
    [InlineData(ImageExportPreset.Messenger, ImageExportFormat.Jpeg, 1280)]
    [InlineData(ImageExportPreset.Document, ImageExportFormat.Png, 1920)]
    public void PresetsRoundTripAndClone(ImageExportPreset preset, ImageExportFormat format, int edge)
    {
        var settings = new AppSettings();
        settings.Export.LastImageExport = ImageExportOptions.FromPreset(preset) with { IncludeSourceUrl = true, Margin = 25, Shadow = true };
        string json = JsonSerializer.Serialize(settings, JsonDefaults.Readable);
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;
        Assert.Equal(settings.Export.LastImageExport, restored.Export.LastImageExport);
        Assert.Equal(format, restored.Export.LastImageExport.Format);
        Assert.Equal(edge, restored.Export.LastImageExport.LongEdge);
        AppSettings clone = restored.DeepClone();
        Assert.Equal(restored.Export.LastImageExport, clone.Export.LastImageExport);
        Assert.Equal(restored.Export.LastImageExport, new SettingsDraft(restored).ToAppSettings().Export.LastImageExport);
        clone.Export.LastImageExport = new();
        Assert.True(restored.Export.LastImageExport.IncludeSourceUrl);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":4,\"export\":{\"fileNamePattern\":\"legacy\"}}")]
    [InlineData("{\"schemaVersion\":4,\"export\":{\"fileNamePattern\":\"legacy\",\"lastImageExport\":null}}")]
    public void LegacySettingsUseAdditiveMigrationWithoutLosingExistingPreferences(string json)
    {
        AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!;
        Assert.Equal(4, settings.SchemaVersion);
        Assert.Equal("legacy", settings.Export.FileNamePattern);
        Assert.Equal(new ImageExportOptions(), settings.Export.LastImageExport);
    }

    [Fact]
    public void InvalidPersistedValuesAreClamped()
    {
        const string json = "{\"export\":{\"lastImageExport\":{\"longEdge\":-3,\"margin\":999,\"cornerRadius\":-5,\"reductionPercent\":200,\"format\":123}}}";
        ImageExportOptions options = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Readable)!.Export.LastImageExport;
        Assert.Equal(0, options.LongEdge);
        Assert.Equal(128, options.Margin);
        Assert.Equal(0, options.CornerRadius);
        Assert.Equal(90, options.ReductionPercent);
        Assert.Equal(ImageExportFormat.Automatic, options.Format);
    }
}
