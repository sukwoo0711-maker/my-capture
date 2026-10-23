using System.Text.Json;
using MyCapture.Core.Pin;
using MyCapture.Core.Serialization;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinSessionProvenanceSerializationTests
{
    [Fact]
    public void PinRecordJsonRoundTripPreservesProvenanceAndTheSeparateExportUrl()
    {
        var original = new PinSessionPin(Guid.NewGuid(), PinSessionContentKind.Image, null,
            "https://legacy.example/export", false, 160, 90, 1.25, 0.8, null, 7,
            SourceUrl: "https://example.test/spec?token=private#section",
            SourceDocumentPath: @"C:\specs\사양서.xlsx", SourceAppName: "Excel",
            SourceAppExecutablePath: @"C:\Office\EXCEL.EXE", SourceWindowTitle: "사양서 - Excel");

        string json = JsonSerializer.Serialize(original, JsonDefaults.Compact);
        PinSessionPin restored = Assert.IsType<PinSessionPin>(
            JsonSerializer.Deserialize<PinSessionPin>(json, JsonDefaults.Compact));

        Assert.Equal(original, restored);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(original.SourcePageUrl, document.RootElement.GetProperty("sourcePageUrl").GetString());
        Assert.Equal(original.SourceUrl, document.RootElement.GetProperty("sourceUrl").GetString());
        Assert.Equal(original.SourceDocumentPath, document.RootElement.GetProperty("sourceDocumentPath").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.test/legacy?query=value#section")]
    public void LegacyPinRecordJsonDefaultsMissingProvenanceFieldsToNull(string sourcePageUrl)
    {
        string json = $$"""
            {
                "id": "ae2b68fe-507a-4d75-9f8e-0fe5b087b8df",
                "kind": "Image",
                "originalText": null,
                "sourcePageUrl": {{JsonSerializer.Serialize(sourcePageUrl)}},
                "usesDeviceIndependentPixels": false,
                "imageWidthDip": 160,
                "imageHeightDip": 90,
                "zoom": 1.25,
                "opacity": 0.8,
                "placement": null
            }
            """;

        PinSessionPin restored = Assert.IsType<PinSessionPin>(
            JsonSerializer.Deserialize<PinSessionPin>(json, JsonDefaults.Compact));

        Assert.Equal(sourcePageUrl, restored.SourcePageUrl);
        Assert.Equal(PinSessionContentKind.Image, restored.Kind);
        Assert.Null(restored.SourceUrl);
        Assert.Null(restored.SourceDocumentPath);
        Assert.Null(restored.SourceAppName);
        Assert.Null(restored.SourceAppExecutablePath);
        Assert.Null(restored.SourceWindowTitle);
    }
}
